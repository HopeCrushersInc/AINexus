using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace AINexus.App;

public enum RuntimeArchiveStatus { PlatformMatch, HardwareEvidenceMissing, PlatformMismatch, Unrecognized }
public sealed record RuntimeArchiveAssessment(RuntimeReleaseAsset Asset, RuntimeArchiveStatus Status,
    string Role, string Architecture, string Backend, IReadOnlyList<string> Reasons);
public sealed record RuntimeSetupReview(string Release, string? License, string Destination,
    bool CachedMetadata, DateTimeOffset HardwareScannedAt, IReadOnlyList<RuntimeArchiveAssessment> Archives,
    IReadOnlyList<string> Actions, IReadOnlyList<string> Blockers, IReadOnlyList<DiscoverySource> Sources);

/// <summary>Pure review: no file writes, network requests, tool execution, installation or approval.</summary>
public static partial class RuntimeSetupPlanner
{
    public static RuntimeSetupReview Create(DiscoveryReport report, string itemId, string destinationDirectory)
    {
        ArgumentNullException.ThrowIfNull(report);
        var item = report.Catalog.Items.SingleOrDefault(item => item.Id == itemId)
            ?? throw new ArgumentException("Select a runtime from this discovery report.", nameof(itemId));
        var release = item.RuntimeRelease ?? throw new ArgumentException("This result has no runtime release evidence.");
        if (item.Kind != "Runtime") throw new ArgumentException("Select a runtime entry, not a model artifact.");
        var destination = ValidateNewDirectory(destinationDirectory);
        var volume = report.Machine.Volumes.SingleOrDefault(volume =>
            string.Equals(volume.Path, Path.GetPathRoot(destination), StringComparison.OrdinalIgnoreCase));
        if (volume is null || !string.Equals(volume.Path, report.TargetVolume, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose a runtime folder on the volume evaluated by this discovery report.");
        if (Directory.Exists(destination) || File.Exists(destination))
            throw new ArgumentException("Choose a new runtime folder so an existing installation or personal files cannot be overwritten.");
        var assessments = release.Archives.Select(asset => Assess(asset, release.Tag, report.Machine)).ToArray();
        var blockers = new List<string>
        {
            "Review only: this plan cannot authorize a download or install. Inspect local archives first; supported CPU extraction requires a separate explicit installation review.",
            "Archive contents, expanded storage, companion dependencies, CPU instruction requirements and driver minima are not verified by filenames. A platform match is not a working-runtime claim.",
            "Any future download/install needs explicit approval of its exact files, destination and license terms. System-wide dependency or driver changes need separate approval."
        };
        if (report.Catalog.IsCached) blockers.Add("This is saved source metadata; refresh online before preparing an executable acquisition plan.");
        if (DateTimeOffset.UtcNow - report.Machine.ScannedAt > TimeSpan.FromMinutes(5)
            || report.Machine.ScannedAt > DateTimeOffset.UtcNow.AddMinutes(1))
            blockers.Add("Hardware evidence is stale or future-dated; rescan before execution.");
        if (string.IsNullOrWhiteSpace(item.License)) blockers.Add("The upstream runtime license is unknown; review source terms first.");
        if (assessments.Length == 0) blockers.Add("This release contains no supported published archives. No nightly fallback or source build was selected.");
        if (!assessments.Any(a => a.Status == RuntimeArchiveStatus.PlatformMatch && a.Role == "Runtime archive"))
            blockers.Add("No recognized runtime archive matches the detected platform and available backend evidence.");
        if (assessments.Any(a => a.Status == RuntimeArchiveStatus.PlatformMatch && a.Asset.Sha256 is null))
            blockers.Add("A matching archive lacks an upstream SHA-256 identity; checksum-verified acquisition is blocked.");
        var matchingBytes = assessments.Where(a => a.Status == RuntimeArchiveStatus.PlatformMatch)
            .Aggregate(0m, (total, a) => total + a.Asset.SizeBytes);
        if (matchingBytes > volume.AvailableBytes) blockers.Add("Even the listed matching compressed archives exceed the selected volume's currently available bytes.");
        return new(release.Tag, item.License, destination, report.Catalog.IsCached, report.Machine.ScannedAt,
            Array.AsReadOnly(assessments), Array.AsReadOnly(new[]
            {
                "Select one runtime archive and inspect its exact release-specific requirements; do not download every matching option.",
                "Resolve any companion archives and their licenses from this same release; a CUDA support archive alone is not a server.",
                "After explicit approval, verify every acquired archive's byte count and SHA-256 before inspecting contents.",
                "Determine expanded size and reject traversal, links, duplicates and collisions before extracting into the new folder.",
                "Preserve the existing runtime. Configure the selected model and loopback endpoint only after the new runtime passes verification.",
                "Run an actual offline-mode smoke test and save evidence before reporting successful setup."
            }), blockers.AsReadOnly(), item.Sources);
    }

    public static RuntimeArchiveAssessment Assess(RuntimeReleaseAsset asset, string releaseTag, MachineCapabilities machine)
    {
        var reasons = new List<string>();
        var name = asset.Name;
        var prefix = "llama-" + releaseTag + "-bin-";
        if (name.StartsWith(prefix, StringComparison.Ordinal)) name = "llama-bin-" + name[prefix.Length..];
        var companionPrefix = "cudart-" + prefix;
        if (name.StartsWith(companionPrefix, StringComparison.Ordinal)) name = "cudart-llama-bin-" + name[companionPrefix.Length..];
        var match = WindowsArchive().Match(name);
        if (!match.Success)
            return new(asset, RuntimeArchiveStatus.Unrecognized, "Unknown archive role", "Unknown", "Unknown",
                ["No supported Windows archive naming recipe matches this publisher label. No compatibility or server contents were inferred."]);
        var architecture = match.Groups["arch"].Value;
        var backend = match.Groups["backend"].Value;
        var role = match.Groups["companion"].Success ? "CUDA support archive" : "Runtime archive";
        if (role == "CUDA support archive" && !backend.StartsWith("cuda-", StringComparison.Ordinal))
            return new(asset, RuntimeArchiveStatus.Unrecognized, "Unknown archive role", architecture, backend,
                ["The companion label does not describe a recognized CUDA dependency archive."]);
        reasons.Add($"Publisher label: Windows / {architecture} / {backend}. Detected OS: {machine.OsDescription}; architecture: {machine.OsArchitecture}.");
        if (!machine.OsDescription.Contains("Windows", StringComparison.OrdinalIgnoreCase)
            || !architecture.Equals(machine.OsArchitecture, StringComparison.OrdinalIgnoreCase))
            return new(asset, RuntimeArchiveStatus.PlatformMismatch, role, architecture, backend,
                [.. reasons, "The labeled native platform does not match this machine. Emulation was not assumed."]);
        var status = RuntimeArchiveStatus.PlatformMatch;
        if (backend.StartsWith("cuda-", StringComparison.Ordinal))
        {
            var drivers = machine.DriverEvidence.Where(d => d.Source.Contains("nvidia-smi", StringComparison.OrdinalIgnoreCase)).ToArray();
            reasons.AddRange(drivers.Select(d => $"Reported NVIDIA adapter: {d.AdapterName}; driver: {d.DriverVersion ?? "unknown"}; compute capability: {d.ComputeCapability ?? "unknown"}."));
            if (drivers.Length == 0)
            {
                status = RuntimeArchiveStatus.HardwareEvidenceMissing;
                reasons.Add("No NVIDIA driver-query evidence is available. A GPU name alone cannot establish CUDA support.");
            }
            reasons.Add("The CUDA label does not establish the minimum driver or compiled GPU targets. Those remain unresolved even when NVIDIA metadata is available.");
        }
        else if (backend == "vulkan")
        {
            status = RuntimeArchiveStatus.HardwareEvidenceMissing;
            reasons.Add("Vulkan loader, driver and required extensions have not been probed. GPU vendor alone cannot establish Vulkan support.");
        }
        else reasons.Add($"Usable CPU instruction evidence: AVX={machine.AvxSupported}, AVX2={machine.Avx2Supported}. The archive's instruction baseline still requires verification.");
        if (role == "CUDA support archive") reasons.Add("Dependency only; this is not a standalone llama-server installation.");
        reasons.Add("Filename assessment is not execution verification. No archive was downloaded, inspected or run.");
        return new(asset, status, role, architecture, backend, reasons.AsReadOnly());
    }

    internal static string ValidateNewDirectory(string directory)
    {
        OfflineModelVerifier.RequireLocalDrivePath(directory);
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var root = Path.GetPathRoot(full)!;
        if (full.Equals(root, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose a new runtime folder inside the selected volume, not the whole drive.");
        foreach (var segment in full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]))
        {
            if (string.IsNullOrWhiteSpace(segment) || segment.EndsWith('.') || segment.EndsWith(' ')
                || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || ReservedName().IsMatch(segment))
                throw new ArgumentException("The runtime folder contains a reserved or invalid Windows name.");
        }
        // Inspect parents from the root before checking the final path, so a junction
        // cannot turn a review into an unauthorized network filesystem probe.
        OfflineModelVerifier.CheckNoReparsePath(Path.Combine(full, ".ainexus-plan"));
        return full;
    }

    public static string Describe(RuntimeSetupReview review)
    {
        var text = new StringBuilder("RUNTIME SETUP REVIEW — NO INSTALLATION APPROVED\n");
        text.AppendLine($"Release: {review.Release}\nDeclared license: {review.License ?? "unknown"}\nNew destination: {review.Destination}\nSaved metadata: {review.CachedMetadata}\nHardware scanned: {review.HardwareScannedAt.ToLocalTime():g}");
        foreach (var a in review.Archives)
        {
            text.AppendLine($"\n{a.Asset.Name} | {a.Status} | {a.Role}\nCompressed bytes: {a.Asset.SizeBytes:N0}\nSHA-256: {a.Asset.Sha256 ?? "unknown"}\nSource: {a.Asset.DownloadUrl}");
            foreach (var reason in a.Reasons) text.AppendLine("• " + reason);
        }
        text.AppendLine("\nSteps required before setup can be completed:");
        foreach (var action in review.Actions) text.AppendLine("• " + action);
        text.AppendLine("\nUnresolved requirements:");
        foreach (var blocker in review.Blockers) text.AppendLine("• " + blocker);
        foreach (var source in review.Sources) text.AppendLine($"\n{source.Name}: {source.Url}\nRetrieved: {source.RetrievedAt.ToLocalTime():g}");
        return text.ToString();
    }

    [GeneratedRegex(@"^(?<companion>cudart-)?llama-bin-win-(?<backend>cpu|cuda-[0-9]+\.[0-9]+|vulkan)-(?<arch>x64|arm64)\.zip$", RegexOptions.CultureInvariant)]
    private static partial Regex WindowsArchive();

    [GeneratedRegex(@"^(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\..*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReservedName();
}
