using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AINexus.App;

public sealed record ModelAcquisitionPlan(
    string PlanId, string ItemId, string Name, ModelArtifact Artifact, Uri DownloadUrl,
    string DestinationPath, string TargetVolume, string DeclaredLicense, Uri LicenseReference,
    DateTimeOffset MetadataRetrievedAt, DateTimeOffset HardwareScannedAt, DateTimeOffset CreatedAt,
    IReadOnlyList<string> Actions, IReadOnlyList<string> Limitations);

/// <summary>Produces an immutable review document. It has no download, process or installer capability.</summary>
public static partial class ModelAcquisitionPlanner
{
    public static ModelAcquisitionPlan Create(DiscoveryReport report, string itemId, string destinationDirectory,
        DateTimeOffset? currentTime = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        var now = currentTime ?? DateTimeOffset.UtcNow;
        if (report.Catalog.IsCached) throw new InvalidOperationException("Refresh the search online before preparing an acquisition plan from saved metadata.");
        if (report.Catalog.RetrievedAt > now.AddMinutes(1) || now - report.Catalog.RetrievedAt > TimeSpan.FromHours(24))
            throw new InvalidOperationException("Source metadata is too old or has an invalid timestamp. Refresh the search.");
        if (report.Machine.ScannedAt > now.AddMinutes(1) || now - report.Machine.ScannedAt > TimeSpan.FromMinutes(5))
            throw new InvalidOperationException("Refresh hardware and available space before preparing this plan.");
        var item = report.Catalog.Items.SingleOrDefault(candidate => candidate.Id == itemId)
            ?? throw new ArgumentException("Select an item from this discovery report.", nameof(itemId));
        var assessment = HardwareCompatibilityEngine.Evaluate(item, report.Machine, report.TargetVolume);
        if (assessment.Status != RecommendationStatus.ResourceCandidate)
            throw new InvalidOperationException("Resolve the compatibility assessment's missing evidence or resource constraints before preparing a download plan.");
        var artifact = item.Artifact ?? throw new InvalidOperationException("No model artifact is attached.");
        if (!RepositoryId().IsMatch(artifact.Repository) || !Hex40().IsMatch(artifact.Revision) ||
            artifact.Sha256 is null || !Hex64().IsMatch(artifact.Sha256) || artifact.SizeBytes is not > 0 || artifact.Gated)
            throw new InvalidDataException("A public artifact, pinned revision, exact byte size and SHA-256 are required.");
        if (string.IsNullOrWhiteSpace(item.License)) throw new InvalidOperationException("Review the source license before acquiring this artifact.");
        var destination = ResolveDestination(destinationDirectory, artifact.FileName);
        var root = Path.GetPathRoot(destination)!;
        if (!string.Equals(root.TrimEnd('\\', '/'), report.TargetVolume.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The destination must be on the volume assessed in this report. Select another volume and rescan to change drives.");
        var repository = EncodePath(artifact.Repository);
        var download = new Uri($"https://huggingface.co/{repository}/resolve/{artifact.Revision}/{EncodePath(artifact.FileName)}");
        var licenseReference = new Uri($"https://huggingface.co/{repository}/blob/{artifact.Revision}/README.md");
        var actions = new[]
        {
            "Review the source and its declared license terms before approving acquisition.",
            $"Download exactly {artifact.SizeBytes.Value:N0} bytes of model data from the pinned source to the selected destination.",
            "Verify the complete file's byte count and SHA-256 before exposing it as an acquired model.",
            "If a destination already exists, verify it for reuse; never overwrite a different file.",
            "Select an existing compatible runtime or prepare a separate runtime installation/build plan.",
            "Run an explicit local functionality test before claiming offline execution is verified."
        };
        var limitations = new[]
        {
            "This is a review document, not an approved or executed download. No directories or files were created by planning.",
            "No Windows environment variables, drivers, services, registry keys or system-wide settings are changed by this model-data plan.",
            "The license is source-declared; review the pinned source terms and applicable use restrictions.",
            "Model architecture, companion assets, tokenizer/template behavior and runtime compatibility still need actual verification.",
            "Memory estimates are heuristics and do not reserve resources or guarantee model fit or performance."
        };
        var plan = new ModelAcquisitionPlan("", item.Id, item.Name, artifact, download, destination, report.TargetVolume,
            item.License, licenseReference, report.Catalog.RetrievedAt, report.Machine.ScannedAt, now, actions, limitations);
        return plan with { PlanId = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(plan))) };
    }

    internal static string ResolveDestination(string directory, string artifactPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactPath);
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("Choose an absolute destination folder.", nameof(directory));
        var segments = artifactPath.Split('/');
        if (artifactPath.Contains('\\') || segments.Any(segment => !SafeSegment(segment)))
            throw new InvalidDataException("The source artifact path is not safe for a Windows model destination.");
        if (!artifactPath.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("This acquisition recipe supports GGUF model files only.");
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (string.Equals(folder, Path.GetPathRoot(folder), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose a model folder inside the selected volume, rather than its root.", nameof(directory));
        var path = Path.GetFullPath(Path.Combine(folder, Path.Combine(segments)));
        if (!path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The destination escapes the selected model folder.");
        for (var ancestor = new DirectoryInfo(Path.GetDirectoryName(path)!); ancestor is not null; ancestor = ancestor.Parent)
        {
            if (ancestor.Exists && ancestor.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException("Choose a destination without symbolic links or junctions in its path.");
        }
        if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException("The destination is a symbolic link or reparse point; choose a different model location.");
        return path;
    }

    private static bool SafeSegment(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment) || segment is "." or ".." || segment.EndsWith('.') || segment.EndsWith(' ')
            || segment.Any(character => char.IsControl(character) || Path.GetInvalidFileNameChars().Contains(character))) return false;
        var name = segment.Split('.')[0];
        return !ReservedDeviceName().IsMatch(name);
    }
    private static string EncodePath(string path) => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,95}/[A-Za-z0-9][A-Za-z0-9._-]{0,95}$", RegexOptions.CultureInvariant)] private static partial Regex RepositoryId();
    [GeneratedRegex("^[a-fA-F0-9]{40}$", RegexOptions.CultureInvariant)] private static partial Regex Hex40();
    [GeneratedRegex("^[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant)] private static partial Regex Hex64();
    [GeneratedRegex(@"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex ReservedDeviceName();
}

public sealed record ModelIntegrityEvidence(string Path, bool Exists, bool Matches, long? ActualBytes, string? ActualSha256,
    DateTimeOffset CheckedAt, string Description);

public static class ModelArtifactVerifier
{
    /// <summary>Reads an existing file only. Hash matching does not claim runtime compatibility or successful inference.</summary>
    public static async Task<ModelIntegrityEvidence> VerifyExistingAsync(ModelAcquisitionPlan plan, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        token.ThrowIfCancellationRequested();
        ModelAcquisitionPlanner.ResolveDestination(Path.GetDirectoryName(plan.DestinationPath)!, Path.GetFileName(plan.DestinationPath));
        if (!File.Exists(plan.DestinationPath))
            return new(plan.DestinationPath, false, false, null, null, DateTimeOffset.UtcNow, "No existing artifact is present. No download was started.");
        if (File.GetAttributes(plan.DestinationPath).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException("An existing reparse-point destination cannot be verified for reuse.");
        await using var stream = new FileStream(plan.DestinationPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var bytes = stream.Length;
        if (bytes != plan.Artifact.SizeBytes)
            return new(plan.DestinationPath, true, false, bytes, null, DateTimeOffset.UtcNow, "Existing file has a different byte count. It was preserved and must not be overwritten.");
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
        var matches = string.Equals(hash, plan.Artifact.Sha256, StringComparison.OrdinalIgnoreCase);
        return new(plan.DestinationPath, true, matches, bytes, hash, DateTimeOffset.UtcNow, matches
            ? "Existing artifact matches source-reported size and SHA-256. Runtime execution has not been verified by this check."
            : "Existing file does not match the expected SHA-256. It was preserved and must not be overwritten.");
    }
}
