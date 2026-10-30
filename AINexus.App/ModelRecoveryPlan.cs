using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AINexus.App;

public sealed record ModelRecoveryPlan(string PlanId, string OriginalPath, string DestinationPath,
    string ReceiptPath, string SourceRepository, string Revision, long ExpectedBytes, string ExpectedSha256,
    Uri SourceUrl, string DeclaredLicense, Uri LicenseReference, DateTimeOffset CreatedAt,
    ModelIntegrityEvidence CurrentIntegrity, IReadOnlyList<string> Actions, IReadOnlyList<string> Limitations,
    long AvailableBytes, long RequiredBytes, DateTimeOffset StorageCheckedAt, string RecoveryDirectory);

/// <summary>Read-only recovery review. There is deliberately no download, approval or execution method.</summary>
public static class ModelRecoveryPlanner
{
    internal const long StorageHeadroom = 64L * 1024 * 1024;

    public static async Task<ModelRecoveryPlan> CreateAsync(string modelPath, string receiptPath,
        string destinationDirectory, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        OfflineModelVerifier.CheckNoReparsePath(modelPath);
        OfflineModelVerifier.CheckNoReparsePath(receiptPath);
        var original = Path.GetFullPath(modelPath);
        var receiptFile = Path.GetFullPath(receiptPath);
        var receipt = await OfflineModelVerifier.ReadReceiptAsync(receiptFile, token).ConfigureAwait(false);
        if (receipt.Plan.Artifact.AdditionalFiles is not null)
            throw new InvalidOperationException("Split-model recovery requires a complete file-set repair review, which is not implemented. Preserve all parts and prepare a fresh full-inventory acquisition review in a separate folder.");
        if (!string.Equals(original, receipt.Plan.DestinationPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The selected acquisition receipt belongs to a different model path. No model bytes were read.");

        OfflineModelVerifier.CheckNoReparsePath(destinationDirectory);
        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationDirectory));
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException("Choose an existing local recovery folder. This review creates no folders.");
        if (string.Equals(Path.TrimEndingDirectorySeparator(Path.GetPathRoot(directory)!), directory,
            StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose a recovery folder within the drive, not its root.", nameof(destinationDirectory));
        var destination = ModelAcquisitionPlanner.ResolveDestination(directory, receipt.Plan.Artifact.FileName);
        OfflineModelVerifier.CheckNoReparsePath(destination);
        if (string.Equals(original, destination, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Choose a different recovery folder so the original path remains untouched.");
        if (File.Exists(destination) || Directory.Exists(destination))
            throw new InvalidOperationException("The proposed recovery destination already exists. It will not be overwritten.");

        var integrity = await ModelArtifactVerifier.VerifyExistingAsync(receipt.Plan, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (integrity.Matches)
            throw new InvalidOperationException("The current model matches its acquisition receipt; no recovery review is needed.");
        var expectedBytes = receipt.Plan.Artifact.SizeBytes
            ?? throw new InvalidDataException("The acquisition receipt lacks an expected byte count.");
        var expectedSha256 = receipt.Plan.Artifact.Sha256
            ?? throw new InvalidDataException("The acquisition receipt lacks an expected SHA-256.");
        var drive = new DriveInfo(Path.GetPathRoot(destination)!);
        var availableBytes = drive.AvailableFreeSpace;
        var storageCheckedAt = DateTimeOffset.UtcNow;
        var requiredBytes = ValidateStorage(expectedBytes, availableBytes);
        token.ThrowIfCancellationRequested();

        var actions = Array.AsReadOnly(new[]
        {
            "Preserve the original model path and any changed bytes; this review never deletes, moves or overwrites them.",
            "Refresh upstream metadata for this exact repository, immutable revision and artifact; compare its bytes, SHA-256 and license terms with this historical receipt.",
            "Rescan current hardware and the proposed destination volume; resolve compatibility and storage evidence in the existing discovery workflow.",
            "Prepare a separate fresh acquisition review for the new destination and explicitly approve its exact source, license and writes before any transfer.",
            "Verify the acquired candidate's complete byte count and SHA-256 and record acquisition evidence without claiming inference success.",
            "Run an explicit offline smoke test with the intended compatible runtime before treating the candidate as working.",
            "Switch configuration only by an explicit action after verification; retain the original files and previous configuration for inspection and recovery."
        });
        var limitations = Array.AsReadOnly(new[]
        {
            "This is historical-source recovery review only, not an executable acquisition plan or download approval. No network request, file write or runtime launch occurred.",
            "Source URLs, revision, size, hash and declared license come from the validated historical acquisition receipt; availability and current upstream terms were not refreshed.",
            "The current integrity result and available disk space are point-in-time readings. Space is not reserved, and a missing or changed model does not establish the cause of damage.",
            "Fresh source, license, hardware and destination review plus a separately approved acquisition are required. This review grants no authority to replace either path.",
            "Checksum comparison is not publisher authentication or runtime compatibility evidence; an actual separate offline test is required."
        });
        var plan = new ModelRecoveryPlan("", original, destination, receiptFile, receipt.Plan.Artifact.Repository,
            receipt.Plan.Artifact.Revision, expectedBytes, expectedSha256, receipt.Plan.DownloadUrl,
            receipt.Plan.DeclaredLicense, receipt.Plan.LicenseReference, DateTimeOffset.UtcNow, integrity, actions, limitations,
            availableBytes, requiredBytes, storageCheckedAt, directory);
        return plan with { PlanId = ComputeFingerprint(plan) };
    }

    internal static long ValidateStorage(long expectedBytes, long availableBytes)
    {
        if (expectedBytes <= 0) throw new ArgumentOutOfRangeException(nameof(expectedBytes));
        if (availableBytes < 0) throw new ArgumentOutOfRangeException(nameof(availableBytes));
        var requiredBytes = checked(expectedBytes + StorageHeadroom);
        if (availableBytes < requiredBytes)
            throw new IOException("The recovery volume currently lacks space for the expected artifact plus 64 MiB of review headroom.");
        return requiredBytes;
    }

    internal static string ComputeFingerprint(ModelRecoveryPlan plan) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(plan with { PlanId = "" })));

    public static string Describe(ModelRecoveryPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var text = new StringBuilder();
        text.AppendLine("MODEL RECOVERY REVIEW — NO DOWNLOAD OR REPLACEMENT APPROVAL");
        text.AppendLine($"Review identity: {plan.PlanId}\nReviewed at: {plan.CreatedAt:O}");
        text.AppendLine($"Original path (preserved): {plan.OriginalPath}\nChosen recovery folder: {plan.RecoveryDirectory}\nProposed separate candidate: {plan.DestinationPath}\nHistorical receipt: {plan.ReceiptPath}");
        text.AppendLine($"Current integrity: {plan.CurrentIntegrity.Description}\nIntegrity checked at: {plan.CurrentIntegrity.CheckedAt:O}");
        text.AppendLine($"Available bytes at review: {plan.AvailableBytes:N0}\nRequired bytes (artifact plus 64 MiB headroom): {plan.RequiredBytes:N0}\nStorage checked at: {plan.StorageCheckedAt:O}\nThis storage snapshot does not reserve space or establish model compatibility.");
        text.AppendLine($"Historical repository: {plan.SourceRepository}\nPinned revision: {plan.Revision}\nExpected bytes: {plan.ExpectedBytes:N0}\nExpected SHA-256: {plan.ExpectedSha256}");
        text.AppendLine($"Historical source URL: {plan.SourceUrl}\nDeclared license: {plan.DeclaredLicense}\nHistorical terms reference: {plan.LicenseReference}");
        text.AppendLine("\nRequired next steps:");
        foreach (var action in plan.Actions) text.AppendLine("- " + action);
        text.AppendLine("\nLimitations:");
        foreach (var limitation in plan.Limitations) text.AppendLine("- " + limitation);
        return text.ToString();
    }
}
