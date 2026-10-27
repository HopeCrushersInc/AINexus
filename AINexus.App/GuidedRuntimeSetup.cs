using System.IO;
using System.Runtime.InteropServices;

namespace AINexus.App;

/// <summary>Short guided route to the existing approved CPU-runtime workflow. These methods only
/// review metadata/local archives; downloading, bundled-terms acceptance and extraction stay separate.</summary>
public static class GuidedRuntimeSetup
{
    public const string ReleaseTag = "b11669";

    public static async Task<RuntimeDownloadPlan> ReviewDownloadAsync(VerifiedOnlineDiscoveryProvider provider,
        MachineCapabilities machine, string existingFolder, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(machine);
        var folder = RuntimeDownloadPlanner.ResolveDirectory(existingFolder);
        var volume = Path.GetPathRoot(folder)!;
        if (!machine.Volumes.Any(candidate => string.Equals(candidate.Path, volume, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Rescan hardware with the selected runtime folder's volume available.");
        var snapshot = await provider.SearchAsync(new(DiscoveryInterest.LocalAssistants, "", 1, ReleaseTag), token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var runtimes = snapshot.Items.Where(item => item.Kind == "Runtime" && item.RuntimeRelease?.Tag == ReleaseTag).ToArray();
        if (runtimes.Length != 1 || snapshot.IsCached)
            throw new InvalidOperationException("The explicit CPU runtime release could not be reviewed online. No alternate release was selected. "
                + string.Join(" ", snapshot.Notes));
        var runtime = runtimes[0];
        var native = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
        var candidates = runtime.RuntimeRelease!.Archives
            .Select(asset => RuntimeSetupPlanner.Assess(asset, ReleaseTag, machine))
            .Where(assessment => assessment.Status == RuntimeArchiveStatus.PlatformMatch
                && assessment.Role == "Runtime archive" && assessment.Backend == "cpu"
                && assessment.Architecture.Equals(native, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (candidates.Length != 1)
            throw new NotSupportedException("This release has no unambiguous native Windows CPU runtime recipe for this PC. No GPU or alternate architecture was substituted.");
        var destination = Path.Combine(folder, $"llama-{ReleaseTag}-cpu-{Guid.NewGuid():N}");
        var report = DiscoveryPipeline.Evaluate(snapshot, machine, volume);
        var files = RuntimeFilePlanner.Create(report, runtime.Id, destination, candidates[0].Asset.Name);
        return RuntimeDownloadPlanner.Create(report, files, folder);
    }

    public static async Task<RuntimeInstallationPlan> ReviewDownloadedAsync(RuntimeDownloadPlan download,
        RuntimeDownloadResult result, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(download);
        ArgumentNullException.ThrowIfNull(result);
        // Acquisition already enforced freshness when it began. A slow completed transfer can
        // still be inspected against its exact historical hashes without authorizing new HTTP.
        if (download.PlanId != RuntimeDownloadPlanner.Fingerprint(download)
            || download.Files.PlanId != RuntimeFilePlanner.Fingerprint(download.Files) || download.Files.Files.Count != 1)
            throw new InvalidDataException("The guided runtime review identity changed after acquisition.");
        if (!string.Equals(RuntimeDownloadPlanner.ResolveDirectory(download.ArchiveDirectory), download.ArchiveDirectory, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(RuntimeDownloadPlanner.ResolveArchivePath(download.ArchiveDirectory, download.Files.Files[0].Name),
                download.ArchivePath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The guided runtime archive destination no longer matches its exact review.");
        OfflineModelVerifier.CheckNoReparsePath(result.ArchivePath);
        OfflineModelVerifier.CheckNoReparsePath(result.ArchiveDirectory);
        if (!string.Equals(Path.GetFullPath(result.ArchivePath), download.ArchivePath, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(result.ArchiveDirectory)),
                download.ArchiveDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The downloaded archive does not match this exact guided runtime plan.");
        var inspection = await RuntimeArchiveInspector.InspectAsync(download.Files, download.ArchiveDirectory, token).ConfigureAwait(false);
        return await RuntimeInstallationPlanner.CreateAsync(inspection, download.ArchiveDirectory, token).ConfigureAwait(false);
    }
}
