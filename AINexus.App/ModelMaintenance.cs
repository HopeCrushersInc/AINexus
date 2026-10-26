using System.IO;

namespace AINexus.App;

public sealed record ModelMaintenanceResult(string ModelPath, string ReceiptPath, DateTimeOffset CheckedAt,
    ModelIntegrityEvidence Integrity, string StatusDisplay, string Summary);

/// <summary>Read-only, point-in-time comparison with explicitly selected historical acquisition evidence.</summary>
public static class ModelMaintenance
{
    public static async Task<ModelMaintenanceResult> CheckAsync(string modelPath, string receiptPath,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        OfflineModelVerifier.CheckNoReparsePath(modelPath);
        OfflineModelVerifier.CheckNoReparsePath(receiptPath);
        var selected = Path.GetFullPath(modelPath);
        var receiptFile = Path.GetFullPath(receiptPath);
        var receipt = await OfflineModelVerifier.ReadReceiptAsync(receiptFile, token).ConfigureAwait(false);
        if (!string.Equals(selected, receipt.Plan.DestinationPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The selected acquisition receipt belongs to a different model path. No model bytes were read.");

        var integrity = await ModelArtifactVerifier.VerifyExistingAsync(receipt.Plan, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var status = !integrity.Exists ? "Missing file"
            : integrity.ActualBytes != receipt.Plan.Artifact.SizeBytes ? "Size mismatch"
            : !integrity.Matches ? "Hash mismatch" : "Matches receipt";
        var summary = $"{status}. {integrity.Description} " +
            "This is a point-in-time comparison with historical acquisition evidence, not publisher authentication or runtime/inference verification. " +
            "No download, repair, deletion or runtime launch was performed.";
        return new(selected, receiptFile, integrity.CheckedAt, integrity, status, summary);
    }
}
