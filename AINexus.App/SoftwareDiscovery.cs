using System.Diagnostics;
using System.IO;
using System.Security;
using Microsoft.Win32;

namespace AINexus.App;

public sealed record SoftwareInstallation(string? ExecutablePath, string? Version, string Source)
{
    public bool IsVerified => ExecutablePath is not null;
}

public sealed record SoftwareApplication(
    string Id,
    string Name,
    string Category,
    IReadOnlyList<SoftwareInstallation> Installations)
{
    public bool IsDetected => Installations.Count > 0;
    public bool IsInstalled => Installations.Any(installation => installation.IsVerified);
    public string Status => IsInstalled ? "Executable found" : IsDetected ? "Registered; executable unverified" : "Not detected";
    public string Version => Installations.FirstOrDefault(installation => installation.IsVerified && installation.Version is not null)?.Version
        ?? Installations.FirstOrDefault(installation => installation.Version is not null)?.Version ?? "Version unavailable";
    public string Location => Installations.FirstOrDefault(installation => installation.IsVerified)?.ExecutablePath
        ?? (IsDetected ? "Registry entry only" : "No executable found in scanned locations");
    public string Evidence => string.Join("; ", Installations.Select(installation => installation.Source).Distinct());
}

public sealed record SoftwareDiscoverySnapshot(
    DateTimeOffset ScannedAt,
    IReadOnlyList<SoftwareApplication> Applications,
    IReadOnlyList<string> Warnings,
    string Scope);

/// <summary>
/// Read-only, bounded discovery. Executables are inspected, never launched. Registry
/// registration alone is deliberately distinguished from a verified executable.
/// </summary>
public static class SoftwareDiscovery
{
    public const string ScanScope = "Checks PATH, common installation locations, and current-user/machine uninstall registrations. " +
        "Portable or custom installations may be missed. Detection does not establish that an application is running or healthy. " +
        "Python Windows Store execution aliases are excluded.";

    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    private sealed record Definition(string Id, string Name, string Category, string[] Executables, string[] RelativeLocations)
    {
        public bool MatchesRegistration(string name) => Id switch
        {
            "ollama" => MatchesName(name, "Ollama"),
            "lm-studio" => MatchesName(name, "LM Studio"),
            "gpt4all" => MatchesName(name, "GPT4All"),
            "jan" => MatchesName(name, "Jan"),
            "anythingllm" => MatchesName(name, "AnythingLLM"),
            "docker" => MatchesName(name, "Docker Desktop"),
            "python" => name.StartsWith("Python 3.", StringComparison.OrdinalIgnoreCase)
                && !name.Contains("Launcher", StringComparison.OrdinalIgnoreCase),
            "git" => name.Equals("Git", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("Git version ", StringComparison.OrdinalIgnoreCase)
                || (name.StartsWith("Git ", StringComparison.OrdinalIgnoreCase) && name.Length > 4 && char.IsDigit(name[4])),
            _ => false
        };

        private static bool MatchesName(string value, string expected) =>
            value.Equals(expected, StringComparison.OrdinalIgnoreCase)
            || value.StartsWith(expected + " ", StringComparison.OrdinalIgnoreCase);
    }

    private static readonly Definition[] Catalog =
    [
        new("ollama", "Ollama", "Model runtime", ["ollama.exe"], [@"Ollama\ollama.exe", @"Programs\Ollama\ollama.exe"]),
        new("lm-studio", "LM Studio", "AI workspace", ["LM Studio.exe", "lm-studio.exe", "lms.exe"],
            [@"LM Studio\LM Studio.exe", @"Programs\LM Studio\LM Studio.exe", @"LM Studio\lm-studio.exe", @"Programs\LM Studio\lm-studio.exe"]),
        new("gpt4all", "GPT4All", "AI workspace", ["gpt4all.exe", "gpt4all-chat.exe"],
            [@"GPT4All\bin\gpt4all.exe", @"GPT4All\bin\gpt4all-chat.exe", @"Programs\GPT4All\bin\gpt4all.exe"]),
        new("jan", "Jan", "AI workspace", ["Jan.exe", "jan.exe"], [@"Jan\Jan.exe", @"Programs\Jan\Jan.exe"]),
        new("anythingllm", "AnythingLLM", "AI workspace", ["AnythingLLM.exe"],
            [@"AnythingLLM\AnythingLLM.exe", @"Programs\AnythingLLM\AnythingLLM.exe"]),
        new("docker", "Docker", "Container tooling", ["Docker Desktop.exe", "docker.exe"],
            [@"Docker\Docker\Docker Desktop.exe", @"Docker\Docker\resources\bin\docker.exe"]),
        new("python", "Python", "Development dependency", ["python.exe", "python3.exe"], []),
        new("git", "Git", "Development dependency", ["git.exe"], [@"Git\cmd\git.exe", @"Git\bin\git.exe", @"Programs\Git\cmd\git.exe"])
    ];

    public static Task<SoftwareDiscoverySnapshot> ScanAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => Scan(cancellationToken), cancellationToken);

    internal static SoftwareDiscoverySnapshot ScanLocations(
        IEnumerable<string> searchPathDirectories,
        IEnumerable<string> installationRoots,
        CancellationToken token = default) => Scan(token, searchPathDirectories.ToArray(), installationRoots.ToArray(), false);

    private static SoftwareDiscoverySnapshot Scan(CancellationToken token, string[]? searchPaths = null,
        string[]? installationRoots = null, bool readRegistry = true)
    {
        var warnings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var found = Catalog.ToDictionary(definition => definition.Id, _ => new List<SoftwareInstallation>());
        var pathDirectories = searchPaths ?? (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(directory => Environment.ExpandEnvironmentVariables(directory.Trim('"')))
            .Where(Path.IsPathFullyQualified).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var roots = installationRoots ?? new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        }.Where(root => !string.IsNullOrWhiteSpace(root)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        foreach (var definition in Catalog)
        {
            token.ThrowIfCancellationRequested();
            foreach (var directory in pathDirectories)
            foreach (var executable in definition.Executables)
                ProbeExecutable(definition, Path.Combine(directory, executable), "PATH", null, found, warnings, token);
            foreach (var root in roots)
            foreach (var relativePath in definition.RelativeLocations)
                ProbeExecutable(definition, Path.Combine(root, relativePath), "Common installation location", null, found, warnings, token);
        }

        // Python versioned directories are bounded to immediate Python* children,
        // rather than recursively searching user files or unrelated drives.
        var python = Catalog.Single(definition => definition.Id == "python");
        var pythonRoots = installationRoots is null
            ? roots.Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Python"))
            : roots;
        foreach (var root in pythonRoots)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (!Directory.Exists(root)) continue;
                foreach (var directory in Directory.EnumerateDirectories(root, "Python*", SearchOption.TopDirectoryOnly))
                    ProbeExecutable(python, Path.Combine(directory, "python.exe"), "Common Python installation location", null, found, warnings, token);
            }
            catch (Exception ex) when (IsLocationError(ex))
            {
                warnings.Add($"Could not inspect installation directory: {root} ({ex.GetType().Name}).");
            }
        }

        if (readRegistry)
        {
            foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                ReadRegistrations(hive, view, found, warnings, token);
        }

        token.ThrowIfCancellationRequested();
        var applications = Catalog.Select(definition => new SoftwareApplication(definition.Id, definition.Name, definition.Category,
            found[definition.Id].OrderByDescending(installation => installation.IsVerified)
                .ThenBy(installation => installation.ExecutablePath, StringComparer.OrdinalIgnoreCase).ToList().AsReadOnly()))
            .ToList().AsReadOnly();
        return new SoftwareDiscoverySnapshot(DateTimeOffset.Now, applications, warnings.Order().ToList().AsReadOnly(), ScanScope);
    }

    private static void ReadRegistrations(RegistryHive hive, RegistryView view,
        Dictionary<string, List<SoftwareInstallation>> found, HashSet<string> warnings, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = baseKey.OpenSubKey(UninstallKey);
            if (uninstall is null) return;
            foreach (var keyName in uninstall.GetSubKeyNames())
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    using var registration = uninstall.OpenSubKey(keyName);
                    if (registration?.GetValue("DisplayName") is not string displayName) continue;
                    foreach (var definition in Catalog.Where(definition => definition.MatchesRegistration(displayName)))
                    {
                        var source = $"Windows registration ({hive}, {view}): {displayName}";
                        var version = registration.GetValue("DisplayVersion") as string;
                        var verified = false;
                        if (registration.GetValue("InstallLocation") is string installLocation && !string.IsNullOrWhiteSpace(installLocation))
                        {
                            var location = Environment.ExpandEnvironmentVariables(installLocation.Trim().Trim('"'));
                            if (Path.IsPathFullyQualified(location))
                            {
                                foreach (var executable in definition.Executables)
                                foreach (var relative in new[] { executable, Path.Combine("bin", executable), Path.Combine("cmd", executable) })
                                    verified |= ProbeExecutable(definition, Path.Combine(location, relative), source, version, found, warnings, token);
                            }
                        }
                        if (registration.GetValue("DisplayIcon") is string displayIcon)
                        {
                            var iconPath = ReadIconPath(displayIcon);
                            if (definition.Executables.Contains(Path.GetFileName(iconPath), StringComparer.OrdinalIgnoreCase))
                                verified |= ProbeExecutable(definition, iconPath, source, version, found, warnings, token);
                        }
                        if (!verified)
                        {
                            var evidence = new SoftwareInstallation(null, EmptyToNull(version), source);
                            if (!found[definition.Id].Contains(evidence)) found[definition.Id].Add(evidence);
                        }
                    }
                }
                catch (Exception ex) when (IsLocationError(ex))
                {
                    warnings.Add($"Could not inspect an uninstall entry ({hive}, {view}; {ex.GetType().Name}).");
                }
            }
        }
        catch (Exception ex) when (IsLocationError(ex))
        {
            warnings.Add($"Could not read uninstall registrations ({hive}, {view}; {ex.GetType().Name}).");
        }
    }

    private static bool ProbeExecutable(Definition definition, string candidate, string source, string? registeredVersion,
        Dictionary<string, List<SoftwareInstallation>> found, HashSet<string> warnings, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            if (!Path.IsPathFullyQualified(candidate)) return false;
            var path = Path.GetFullPath(candidate);
            if (definition.Id == "python" && path.Contains(@"\Microsoft\WindowsApps\", StringComparison.OrdinalIgnoreCase)) return false;
            var attributes = File.GetAttributes(path);
            if (attributes.HasFlag(FileAttributes.Directory)) return false;
            var existing = found[definition.Id].FindIndex(installation => string.Equals(installation.ExecutablePath, path, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0)
            {
                var previous = found[definition.Id][existing];
                if (!previous.Source.Contains(source, StringComparison.Ordinal))
                    found[definition.Id][existing] = previous with { Source = previous.Source + "; " + source, Version = previous.Version ?? EmptyToNull(registeredVersion) };
                return true;
            }
            string? version = EmptyToNull(registeredVersion);
            try
            {
                var metadata = FileVersionInfo.GetVersionInfo(path);
                version = EmptyToNull(metadata.ProductVersion) ?? EmptyToNull(metadata.FileVersion) ?? version;
            }
            catch (Exception ex) when (IsLocationError(ex))
            {
                warnings.Add($"Could not read executable version: {path} ({ex.GetType().Name}).");
            }
            found[definition.Id].Add(new SoftwareInstallation(path, version, source));
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (Exception ex) when (IsLocationError(ex))
        {
            warnings.Add($"Could not inspect executable location: {candidate} ({ex.GetType().Name}).");
            return false;
        }
    }

    private static string ReadIconPath(string displayIcon)
    {
        var value = Environment.ExpandEnvironmentVariables(displayIcon.Trim());
        if (value.StartsWith('"'))
        {
            var closingQuote = value.IndexOf('"', 1);
            return closingQuote > 0 ? value[1..closingQuote] : "";
        }
        var comma = value.LastIndexOf(',');
        return comma > 0 && int.TryParse(value[(comma + 1)..].Trim(), out _) ? value[..comma].Trim() : value;
    }

    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static bool IsLocationError(Exception exception) => exception is IOException or UnauthorizedAccessException
        or SecurityException or ArgumentException or NotSupportedException or System.ComponentModel.Win32Exception;
}
