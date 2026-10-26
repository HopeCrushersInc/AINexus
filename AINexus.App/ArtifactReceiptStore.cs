using System.IO;
using System.Text.Json;

namespace AINexus.App;

public sealed record ArtifactAcquisitionReceipt(int SchemaVersion, ModelAcquisitionPlan Plan,
    ModelAcquisitionResult Acquisition, DateTimeOffset RecordedAt)
{
    public bool RuntimeExecutionVerified => false;
}

/// <summary>Saves artifact identity evidence only, never a claim that offline inference works.</summary>
public static class ArtifactReceiptStore
{
    public static async Task<string> SaveAsync(ModelAcquisitionPlan plan, ModelAcquisitionResult result, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(result);
        if (!string.Equals(plan.PlanId, ModelAcquisitionPlanner.ComputeFingerprint(plan), StringComparison.Ordinal)
            || !result.Evidence.Matches || !result.Evidence.Exists
            || !string.Equals(result.Evidence.Path, plan.DestinationPath, StringComparison.OrdinalIgnoreCase)
            || result.Evidence.ActualBytes != plan.Artifact.SizeBytes
            || !string.Equals(result.Evidence.ActualSha256, plan.Artifact.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only matching acquisition integrity evidence can be recorded for this exact plan.");
        ModelAcquisitionPlanner.ResolveDestination(Path.GetDirectoryName(plan.DestinationPath)!, Path.GetFileName(plan.DestinationPath));
        var directory = Path.Combine(Path.GetDirectoryName(plan.DestinationPath)!, ".ainexus", "receipts");
        CheckParents(directory);
        Directory.CreateDirectory(directory);
        CheckParents(directory);
        var path = Path.Combine(directory, $"{plan.PlanId}-{Guid.NewGuid():N}.json");
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.Asynchronous);
        await JsonSerializer.SerializeAsync(stream, new ArtifactAcquisitionReceipt(1, plan, result, DateTimeOffset.UtcNow),
            new JsonSerializerOptions { WriteIndented = true }, token);
        await stream.FlushAsync(token);
        return path;
    }

    private static void CheckParents(string directory)
    {
        for (var parent = new DirectoryInfo(directory); parent is not null; parent = parent.Parent)
            if (parent.Exists && parent.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException("Receipt storage cannot follow a symbolic link or junction.");
    }
}
