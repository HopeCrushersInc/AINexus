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
        ModelFileSet.Validate(artifact);
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
            $"Acquire exactly {ModelFileSet.TotalBytes(artifact):N0} bytes across {1 + (artifact.AdditionalFiles?.Count ?? 0)} model file(s) from the pinned source to the selected destination.",
            "Verify every file's byte count and SHA-256 before exposing the complete inventory as an acquired model.",
            "If a destination already exists, verify it for reuse; never overwrite a different file.",
            "Save a local acquisition receipt beside the model with its pinned identity and integrity evidence.",
            "Select an existing compatible runtime or prepare a separate runtime installation/build plan.",
            "Run an explicit local functionality test before claiming offline execution is verified."
        };
        var limitations = new[]
        {
            "Planning alone does not approve or execute acquisition. Approval must match this exact plan; planning creates no directories or files.",
            "No Windows environment variables, drivers, services, registry keys or system-wide settings are changed by this model-data plan.",
            "The license is source-declared; review the pinned source terms and applicable use restrictions.",
            "Model architecture, companion assets, tokenizer/template behavior and runtime compatibility still need actual verification.",
            "Memory estimates are heuristics and do not reserve resources or guarantee model fit or performance."
        };
        var plan = new ModelAcquisitionPlan("", item.Id, item.Name, artifact, download, destination, report.TargetVolume,
            item.License, licenseReference, report.Catalog.RetrievedAt, report.Machine.ScannedAt, now, actions, limitations);
        return plan with { PlanId = ComputeFingerprint(plan) };
    }

    internal static string ComputeFingerprint(ModelAcquisitionPlan plan) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(plan with { PlanId = "" })));

    /// <summary>Bind approval to the exact review document before any acquisition side effects.</summary>
    public static void ValidateForAcquisition(ModelAcquisitionPlan plan, string approvedPlanId)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (string.IsNullOrWhiteSpace(approvedPlanId) || !string.Equals(approvedPlanId, plan.PlanId, StringComparison.Ordinal)
            || !string.Equals(plan.PlanId, ComputeFingerprint(plan), StringComparison.Ordinal))
            throw new InvalidOperationException("Approval does not match this exact plan. Prepare and review a new plan after any change.");
        var now = DateTimeOffset.UtcNow;
        if (plan.CreatedAt > now.AddMinutes(1) || now - plan.CreatedAt > TimeSpan.FromMinutes(30)
            || plan.MetadataRetrievedAt > now.AddMinutes(1) || now - plan.MetadataRetrievedAt > TimeSpan.FromHours(24))
            throw new InvalidOperationException("The acquisition plan has expired or has invalid dates. Refresh source evidence and prepare a new plan.");
        ValidateIdentity(plan);
    }

    /// <summary>Historical receipt validation without granting a fresh acquisition approval.</summary>
    internal static void ValidateIdentity(ModelAcquisitionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!string.Equals(plan.PlanId, ComputeFingerprint(plan), StringComparison.Ordinal))
            throw new InvalidDataException("The recorded plan fingerprint does not match its content.");
        var artifact = plan.Artifact ?? throw new InvalidDataException("The acquisition plan has no artifact identity.");
        ModelFileSet.Validate(artifact);
        if (artifact.Repository is null || !RepositoryId().IsMatch(artifact.Repository)
            || artifact.Revision is null || !Hex40().IsMatch(artifact.Revision)
            || artifact.Sha256 is null || !Hex64().IsMatch(artifact.Sha256) || artifact.SizeBytes is not > 0
            || artifact.Gated || string.IsNullOrWhiteSpace(artifact.FileName)
            || artifact.FileName.Contains('\\') || artifact.FileName.Split('/').Any(segment => !SafeSegment(segment))
            || !string.Equals(artifact.Format, "GGUF", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(plan.DeclaredLicense))
            throw new InvalidDataException("Acquisition requires a complete public GGUF artifact identity and declared license.");
        var expectedDownload = new Uri($"https://huggingface.co/{EncodePath(artifact.Repository)}/resolve/{artifact.Revision}/{EncodePath(artifact.FileName)}");
        var expectedTerms = new Uri($"https://huggingface.co/{EncodePath(artifact.Repository)}/blob/{artifact.Revision}/README.md");
        if (plan.DownloadUrl != expectedDownload || plan.LicenseReference != expectedTerms)
            throw new InvalidDataException("The plan source or license reference does not match the pinned artifact identity.");
        var absolute = Path.GetFullPath(plan.DestinationPath);
        if (!Path.IsPathFullyQualified(plan.DestinationPath) || !string.Equals(absolute, plan.DestinationPath, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetPathRoot(absolute)?.TrimEnd('\\', '/'), plan.TargetVolume?.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The destination must remain on the reviewed storage volume.");
        ResolveDestination(Path.GetDirectoryName(absolute)!, Path.GetFileName(absolute));
        if (!absolute.EndsWith(Path.DirectorySeparatorChar + artifact.FileName.Replace('/', Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The destination filename does not match the reviewed model artifact.");
        foreach (var protectedRoot in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.SystemDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
        }.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            if (absolute.StartsWith(Path.TrimEndingDirectorySeparator(protectedRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Model-data acquisition cannot write into Windows or system-wide application directories. Choose a model folder; system-wide setup needs a separate approved plan.");
        }
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
    DateTimeOffset CheckedAt, string Description,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<ModelIntegrityEvidence>? AdditionalFiles = null);

public static class ModelArtifactVerifier
{
    /// <summary>Reads an existing file only. Hash matching does not claim runtime compatibility or successful inference.</summary>
    public static async Task<ModelIntegrityEvidence> VerifyExistingAsync(ModelAcquisitionPlan plan, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        token.ThrowIfCancellationRequested();
        if (plan.Artifact.AdditionalFiles is not null)
        {
            var files = ModelFileSet.FilePlans(plan);
            var evidence = new List<ModelIntegrityEvidence>();
            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();
                evidence.Add(await VerifyExistingAsync(file, token).ConfigureAwait(false));
            }
            var complete = evidence.All(file => file.Exists && file.Matches);
            return evidence[0] with
            {
                Matches = complete,
                AdditionalFiles = evidence.Skip(1).ToArray(),
                Description = complete
                    ? "All model files match their source-reported sizes and SHA-256 hashes. Runtime execution has not been verified by this check."
                    : "The model file set is incomplete or has different bytes. Existing files were preserved; no download or runtime was started."
            };
        }
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
