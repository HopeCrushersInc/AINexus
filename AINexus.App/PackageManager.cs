using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace AINexus.App;

public enum ManagedPackage { Ollama, Gpt4All }
public enum PackageAction { Install, Update }

public sealed record PackageCatalogEntry(
    ManagedPackage Package, string Name, string PackageId, bool CanModify, string Restriction);

public sealed record PackageOperationResult(
    bool Started, int? ExitCode, string Output, string Error, bool TimedOut = false, bool Cancelled = false)
{
    public bool Succeeded => Started && ExitCode == 0 && !TimedOut && !Cancelled;
    public string ExitCodeDisplay => ExitCode is { } code ? $"{code} (0x{unchecked((uint)code):X8})" : "Not available";
}

/// <summary>
/// Narrow WinGet boundary. No shell, elevation, source modification, arbitrary IDs,
/// or cancellation of installers. Only explicit execution accepts agreements.
/// </summary>
public static class PackageManager
{
    // Verified against Microsoft's community manifests on 2026-10-24:
    // https://github.com/microsoft/winget-pkgs/blob/master/manifests/o/Ollama/Ollama/0.40.10/Ollama.Ollama.installer.yaml
    // https://github.com/microsoft/winget-pkgs/blob/master/manifests/n/nomic/gpt4all/3.10.0/nomic.gpt4all.installer.yaml
    // Command semantics: https://learn.microsoft.com/windows/package-manager/winget/install
    // https://learn.microsoft.com/windows/package-manager/winget/upgrade
    public static IReadOnlyList<PackageCatalogEntry> Catalog { get; } = Array.AsReadOnly(new[]
    {
        new PackageCatalogEntry(ManagedPackage.Ollama, "Ollama", "Ollama.Ollama", true,
            "Current-user installation only. No model downloads or dependency installations. WinGet verifies the installer hash. " +
            "The installer may register background services or startup tasks according to its own behavior."),
        new PackageCatalogEntry(ManagedPackage.Gpt4All, "GPT4All (preview only)", "nomic.gpt4all", false,
            "Installation and updates are disabled: the reviewed manifest does not declare user scope and upgrades uninstall the previous version.")
    });

    private static readonly SemaphoreSlim OperationGate = new(1, 1);
    private static int installing;
    private const int OutputLimit = 65_536;
    public static bool IsBusy => OperationGate.CurrentCount == 0;
    public static bool IsInstalling => Volatile.Read(ref installing) != 0;

    public static Task<PackageOperationResult> PreviewAsync(ManagedPackage package, CancellationToken cancellationToken = default) =>
        RunAsync(BuildArguments(package, null), false, cancellationToken);

    /// <summary>
    /// Call only after the user explicitly reviews the package and accepts its and
    /// the source's agreements. Once started, await completion; do not terminate it.
    /// </summary>
    public static Task<PackageOperationResult> ExecuteAsync(ManagedPackage package, PackageAction action, bool acceptAgreements) =>
        RunAsync(BuildArguments(package, action, acceptAgreements), true, CancellationToken.None);

    internal static IReadOnlyList<string> BuildArguments(ManagedPackage package, PackageAction? action, bool acceptAgreements = false)
    {
        var entry = Catalog.SingleOrDefault(entry => entry.Package == package)
            ?? throw new ArgumentOutOfRangeException(nameof(package), "The package is not in the managed catalog.");
        if (action is not null && !Enum.IsDefined(action.Value))
            throw new ArgumentOutOfRangeException(nameof(action));
        if (action is not null && !entry.CanModify)
            throw new InvalidOperationException(entry.Restriction);
        if (action is not null && !acceptAgreements)
            throw new InvalidOperationException("Explicit acceptance of the package and source agreements is required.");

        var arguments = new List<string>
        {
            action switch { PackageAction.Install => "install", PackageAction.Update => "upgrade", _ => "show" },
            "--id", entry.PackageId, "--exact", "--source", "winget", "--disable-interactivity"
        };
        if (action is not null)
        {
            arguments.AddRange(["--scope", "user", "--silent", "--skip-dependencies",
                "--accept-package-agreements", "--accept-source-agreements"]);
            if (action == PackageAction.Install) arguments.Add("--no-upgrade");
        }
        return arguments.AsReadOnly();
    }

    private static async Task<PackageOperationResult> RunAsync(IReadOnlyList<string> arguments, bool modifiesSystem, CancellationToken token)
    {
        if (!await OperationGate.WaitAsync(0, token).ConfigureAwait(false))
            return new PackageOperationResult(false, null, "", "A package operation is already running. Wait for it to finish.");
        var started = false;
        try
        {
            if (modifiesSystem) Interlocked.Exchange(ref installing, 1);
            // Resolve the known App Installer alias directly, never through the
            // current directory or an arbitrary PATH executable named winget.
            var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WindowsApps", "winget.exe");
            if (!File.Exists(executable))
                return new PackageOperationResult(false, null, "", "WinGet is unavailable. Install or repair Microsoft's App Installer before using package operations.");

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
            token.ThrowIfCancellationRequested();
            if (!process.Start())
                return new PackageOperationResult(false, null, "", "WinGet could not be started.");
            started = true;
            try { process.StandardInput.Close(); }
            catch (IOException) { /* The child may already have closed its input. */ }

            using var previewTimeout = modifiesSystem ? null : new CancellationTokenSource(TimeSpan.FromSeconds(45));
            using var previewCancellation = modifiesSystem ? null : CancellationTokenSource.CreateLinkedTokenSource(token, previewTimeout!.Token);
            var processToken = previewCancellation?.Token ?? CancellationToken.None;
            var outputTask = ReadBoundedAsync(process.StandardOutput, processToken);
            var errorTask = ReadBoundedAsync(process.StandardError, processToken);
            try
            {
                await process.WaitForExitAsync(processToken).ConfigureAwait(false);
                var output = await outputTask.ConfigureAwait(false);
                var error = await errorTask.ConfigureAwait(false);
                return new PackageOperationResult(true, process.ExitCode, output, error);
            }
            catch (OperationCanceledException) when (!modifiesSystem)
            {
                // Only the read-only show process may be stopped. Installation
                // has no timeout or cancellation token and always runs to exit.
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                catch (Win32Exception) { }
                var output = await ObserveOutputAsync(outputTask).ConfigureAwait(false);
                var error = await ObserveOutputAsync(errorTask).ConfigureAwait(false);
                var timedOut = previewTimeout!.IsCancellationRequested && !token.IsCancellationRequested;
                var message = timedOut ? "WinGet preview timed out after 45 seconds." : "WinGet preview was cancelled.";
                return new PackageOperationResult(true, null, output, JoinError(error, message), timedOut, !timedOut);
            }
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new PackageOperationResult(started, null, "", $"WinGet operation failed: {ex.Message}");
        }
        finally
        {
            if (modifiesSystem) Interlocked.Exchange(ref installing, 0);
            OperationGate.Release();
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken token)
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        var truncated = false;
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
            if (count == 0) break;
            var remaining = OutputLimit - text.Length;
            if (remaining > 0) text.Append(buffer, 0, Math.Min(remaining, count));
            if (count > remaining) truncated = true;
            // Continue draining even after the display limit to avoid blocking WinGet.
        }
        if (truncated) text.AppendLine().Append("[Additional output omitted]");
        return text.ToString().Trim();
    }

    private static async Task<string> ObserveOutputAsync(Task<string> task)
    {
        try { return await task.ConfigureAwait(false); }
        catch (OperationCanceledException) { return ""; }
        catch (IOException) { return ""; }
    }

    private static string JoinError(string error, string message) => string.IsNullOrWhiteSpace(error) ? message : error + Environment.NewLine + message;
}
