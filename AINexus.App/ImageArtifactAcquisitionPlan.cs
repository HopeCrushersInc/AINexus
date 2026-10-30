using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AINexus.App;

public sealed record ImageArtifactAcquisitionPlan(string PlanId, string ItemId, string Name, ModelArtifact Artifact,
    Uri DownloadUrl, string DestinationDirectory, string DestinationPath, string TargetVolume, string DeclaredLicense,
    Uri LicenseReference, DateTimeOffset MetadataRetrievedAt, DateTimeOffset HardwareScannedAt, DateTimeOffset CreatedAt,
    bool ExistingFileAtReview, long RequiredAdditionalBytes, long AvailableBytesAtReview);

/// <summary>Reviews acquisition of one explicitly selected image DATA artifact. No compatibility, complete-checkpoint,
/// runtime installation, configuration or execution approval is inferred from this review.</summary>
public static class ImageArtifactAcquisitionPlanner
{
    internal const long ReceiptReserveBytes = 1_048_576;

    public static ImageArtifactAcquisitionPlan Create(DiscoveryReport report, string itemId, string existingDestinationDirectory,
        DateTimeOffset? currentTime = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        var now = currentTime ?? DateTimeOffset.UtcNow;
        if (report.Catalog.IsCached || !Fresh(report.Catalog.RetrievedAt, TimeSpan.FromHours(24), now))
            throw new InvalidOperationException("Refresh image source evidence online before reviewing an artifact download.");
        if (!Fresh(report.Machine.ScannedAt, TimeSpan.FromMinutes(5), now))
            throw new InvalidOperationException("Rescan hardware and selected-volume space before reviewing image data acquisition.");
        var matches = report.Catalog.Items.Where(item => item.Id == itemId).ToArray();
        if (matches.Length != 1) throw new ArgumentException("Select exactly one image artifact from this discovery report.", nameof(itemId));
        var selected = matches[0];
        if (selected.Interest != DiscoveryInterest.Images || selected.Kind != "Model" || selected.PipelineTag != "text-to-image")
            throw new InvalidDataException("Select source-backed text-to-image artifact metadata; this is not a tool or text-model recipe.");
        ValidateArtifact(selected.Artifact);
        var artifact = selected.Artifact!;
        var license = RequireLicense(selected.License);
        var card = CardUrl(artifact);
        var file = ArtifactUrl(artifact);
        var evidence = selected.Sources?.Where(source => source is not null && (source.Url == card || source.Url == file)).ToArray() ?? [];
        if (!evidence.Any(source => source.Url == card) || !evidence.Any(source => source.Url == file)
            || evidence.Any(source => !Fresh(source.RetrievedAt, TimeSpan.FromHours(24), now)))
            throw new InvalidDataException("Fresh pinned model-card and exact-file source references are required for image acquisition.");
        var directory = RuntimeDownloadPlanner.ResolveDirectory(existingDestinationDirectory);
        var destination = ResolveDestination(directory, artifact.FileName);
        var volumePath = Path.GetPathRoot(destination)!;
        if (!string.Equals(NormalizeVolume(report.TargetVolume), volumePath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Select and rescan the destination's actual volume before reviewing another drive.");
        var volume = report.Machine.Volumes.SingleOrDefault(candidate =>
            string.Equals(NormalizeVolume(candidate.Path), volumePath, StringComparison.OrdinalIgnoreCase));
        if (volume is null || volume.TotalBytes <= 0 || volume.AvailableBytes < 0 || volume.AvailableBytes > volume.TotalBytes)
            throw new InvalidOperationException("The selected volume has no valid scanned storage evidence.");
        var existing = File.Exists(destination);
        if (existing && new FileInfo(destination).Length != artifact.SizeBytes)
            throw new InvalidDataException("An existing file has a different byte count and was preserved. Select another folder.");
        var required = RequiredBytes(artifact, existing);
        var drive = new DriveInfo(volumePath);
        if (!drive.IsReady) throw new IOException("The selected local volume is unavailable.");
        var available = Math.Min(volume.AvailableBytes, drive.AvailableFreeSpace);
        if (available < required) throw new IOException("The selected volume lacks the reviewed image-data and receipt-space budget.");
        var metadataTime = evidence.Select(source => source.RetrievedAt).Append(report.Catalog.RetrievedAt).Min();
        var plan = new ImageArtifactAcquisitionPlan("", selected.Id, selected.Name, artifact, DownloadUrl(artifact), directory,
            destination, volumePath, license, card, metadataTime, report.Machine.ScannedAt, now, existing, required, available);
        ValidateIdentity(plan, requireFingerprint: false);
        return plan with { PlanId = Fingerprint(plan) };
    }

    public static void ValidateForAcquisition(ImageArtifactAcquisitionPlan plan, string approvedPlanId)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ValidateIdentity(plan);
        if (string.IsNullOrWhiteSpace(approvedPlanId) || plan.PlanId != approvedPlanId)
            throw new InvalidOperationException("Approval must match this exact image artifact, source, license and destination review.");
        var now = DateTimeOffset.UtcNow;
        if (!Fresh(plan.CreatedAt, TimeSpan.FromMinutes(30), now) || !Fresh(plan.MetadataRetrievedAt, TimeSpan.FromHours(24), now)
            || !Fresh(plan.HardwareScannedAt, TimeSpan.FromMinutes(5), now))
            throw new InvalidOperationException("The image data review expired. Refresh source/hardware evidence and review again.");
    }

    internal static void ValidateIdentity(ImageArtifactAcquisitionPlan plan, bool requireFingerprint = true)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ValidateArtifact(plan.Artifact);
        RequireLicense(plan.DeclaredLicense);
        if (string.IsNullOrWhiteSpace(plan.ItemId) || plan.ItemId.Length > 1024 || plan.ItemId.Any(char.IsControl)
            || string.IsNullOrWhiteSpace(plan.Name) || plan.Name.Length > 512 || plan.Name.Any(char.IsControl))
            throw new InvalidDataException("Image review identity/name is missing or oversized.");
        if (plan.DownloadUrl != DownloadUrl(plan.Artifact) || plan.LicenseReference != CardUrl(plan.Artifact))
            throw new InvalidDataException("Image source and terms must remain bound to the exact pinned public artifact.");
        if (!string.Equals(RuntimeDownloadPlanner.ResolveDirectory(plan.DestinationDirectory), plan.DestinationDirectory, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(ResolveDestination(plan.DestinationDirectory, plan.Artifact.FileName), plan.DestinationPath, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(NormalizeVolume(plan.TargetVolume), Path.GetPathRoot(plan.DestinationPath), StringComparison.OrdinalIgnoreCase)
            || plan.RequiredAdditionalBytes != RequiredBytes(plan.Artifact, plan.ExistingFileAtReview)
            || plan.AvailableBytesAtReview < plan.RequiredAdditionalBytes)
            throw new InvalidDataException("Image destination or storage budget differs from the reviewed identity.");
        if (requireFingerprint && plan.PlanId != Fingerprint(plan))
            throw new InvalidDataException("Image artifact review content changed after approval.");
    }

    internal static string Fingerprint(ImageArtifactAcquisitionPlan plan) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(plan with { PlanId = "" })));

    internal static string ResolveDestination(string directory, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 240 || !name.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(Path.GetFileNameWithoutExtension(name)) || name.Contains('/') || name.Contains('\\') || name.Contains('#')
            || Regex.IsMatch(name.Split('.')[0].TrimEnd(' '), @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            throw new InvalidDataException("Choose one safe root safetensors artifact filename, not a component path or device name.");
        return RuntimeDownloadPlanner.ResolveArchivePath(directory, name);
    }

    private static void ValidateArtifact(ModelArtifact? artifact)
    {
        if (artifact is null || artifact.Repository is null
            || !Regex.IsMatch(artifact.Repository, @"^[A-Za-z0-9][A-Za-z0-9._-]{0,95}/[A-Za-z0-9][A-Za-z0-9._-]{0,95}\z", RegexOptions.CultureInvariant)
            || artifact.Revision is null || !Hex(artifact.Revision, 40) || artifact.Sha256 is null || !Hex(artifact.Sha256, 64)
            || artifact.SizeBytes is not > 0 || artifact.SizeBytes > long.MaxValue - ReceiptReserveBytes || artifact.Gated || artifact.AdditionalFiles is not null
            || !string.Equals(artifact.Format, "Safetensors", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A public safetensors artifact with exact repository, pinned revision, positive bytes and SHA-256 is required.");
    }

    private static string RequireLicense(string? license)
    {
        if (string.IsNullOrWhiteSpace(license) || license.Length > 256 || license.Any(char.IsControl)
            || new[] { "unknown", "unavailable", "not specified", "none", "other", "noassertion" }.Contains(license.Trim(), StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("A reviewable source-declared image artifact license is required; missing terms cannot be guessed.");
        return license.Trim();
    }
    private static bool Hex(string text, int length) => text.Length == length && text.All(Uri.IsHexDigit);
    private static bool Fresh(DateTimeOffset time, TimeSpan age, DateTimeOffset now) => time != DateTimeOffset.MinValue && time <= now.AddMinutes(1) && now - time <= age;
    private static string NormalizeVolume(string path)
    {
        OfflineModelVerifier.RequireLocalDrivePath(path);
        var full = Path.GetFullPath(path);
        if (!string.Equals(Path.TrimEndingDirectorySeparator(full), Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full)!), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Storage evidence must identify a local volume root.");
        return Path.GetPathRoot(full)!;
    }
    private static long RequiredBytes(ModelArtifact artifact, bool existing) => (existing ? 0 : artifact.SizeBytes!.Value) + ReceiptReserveBytes;
    private static string Encode(string path) => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
    private static Uri DownloadUrl(ModelArtifact artifact) => new($"https://huggingface.co/{Encode(artifact.Repository)}/resolve/{artifact.Revision}/{Encode(artifact.FileName)}");
    private static Uri CardUrl(ModelArtifact artifact) => new($"https://huggingface.co/{Encode(artifact.Repository)}/blob/{artifact.Revision}/README.md");
    private static Uri ArtifactUrl(ModelArtifact artifact) => new($"https://huggingface.co/{Encode(artifact.Repository)}/blob/{artifact.Revision}/{Encode(artifact.FileName)}");

    public static string Describe(ImageArtifactAcquisitionPlan plan)
    {
        ValidateIdentity(plan);
        return $"IMAGE ARTIFACT DATA REVIEW — SEPARATE APPROVAL REQUIRED\nPlan: {plan.PlanId}\n{plan.Name}\n"
            + $"Source: {plan.DownloadUrl}\nRevision: {plan.Artifact.Revision}\nFile: {plan.Artifact.FileName}\nBytes: {plan.Artifact.SizeBytes:N0}\nSHA-256: {plan.Artifact.Sha256}\n"
            + $"Destination: {plan.DestinationPath}\nDeclared license: {plan.DeclaredLicense}\nPinned terms: {plan.LicenseReference}\n"
            + $"Source evidence: {plan.MetadataRetrievedAt:o}\nHardware/storage scan: {plan.HardwareScannedAt:o}\n"
            + $"Additional storage budget: {plan.RequiredAdditionalBytes:N0} bytes, including a {ReceiptReserveBytes:N0}-byte receipt allowance; measured available: {plan.AvailableBytesAtReview:N0} bytes.\n"
            + (plan.ExistingFileAtReview ? "An existing file is reviewed for hash-verified reuse only. If it disappears, review again; no automatic replacement download.\n"
                : "Approve downloading only these exact data bytes. Matching existing files may be rehashed for reuse; different files are never overwritten.\n")
            + "This approval is DATA acquisition only. A safetensors file is not proven to be a complete checkpoint. Architecture, companion assets, runtime recipe, hardware fit and successful image generation remain unverified. "
            + "No engine is installed, configured, started or switched. No GPU power, Windows settings or services are changed. Image-specific integrity receipts are not GGUF launch approvals. Source hashes identify bytes, not publisher authenticity.";
    }
}
