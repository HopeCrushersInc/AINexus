using System.IO;
using System.Text.Json;
using AINexus.App;

namespace AINexus.Checks;

internal static class ModelMaintenanceChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var bytes = "GGUF model maintenance fixture"u8.ToArray();
            var models = Path.Combine(root, "models");
            Directory.CreateDirectory(models);
            var report = AcquisitionPlanChecks.FixtureReport(models, bytes);
            var plan = ModelAcquisitionPlanner.Create(report, report.Catalog.Items.Single().Id, models);
            await File.WriteAllBytesAsync(plan.DestinationPath, bytes);
            var evidence = await ModelArtifactVerifier.VerifyExistingAsync(plan);
            var receipt = new ArtifactAcquisitionReceipt(1, plan, new(evidence, true, DateTimeOffset.UtcNow), DateTimeOffset.UtcNow);
            var receiptPath = Path.Combine(root, "receipt.json");
            await File.WriteAllTextAsync(receiptPath, JsonSerializer.Serialize(receipt));
            var receiptBytes = await File.ReadAllBytesAsync(receiptPath);

            var result = await ModelMaintenance.CheckAsync(plan.DestinationPath, receiptPath);
            check(result.StatusDisplay == "Matches receipt" && result.Integrity.Matches
                && result.ModelPath == plan.DestinationPath && result.ReceiptPath == receiptPath,
                "Library maintenance binds current size/hash evidence to the explicitly selected model and receipt");
            check(result.Summary.Contains("not publisher authentication or runtime/inference verification")
                && !Directory.Exists(Path.Combine(models, ".ainexus"))
                && (await File.ReadAllBytesAsync(plan.DestinationPath)).SequenceEqual(bytes)
                && (await File.ReadAllBytesAsync(receiptPath)).SequenceEqual(receiptBytes),
                "A maintenance match preserves model and receipt bytes and makes no runtime-success claim");

            var unrelated = Path.Combine(root, "unrelated.gguf");
            // A locked unrelated file proves wrong-receipt rejection occurs before model-byte access.
            await File.WriteAllBytesAsync(unrelated, bytes);
            using (var locked = new FileStream(unrelated, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                check(await RejectsAsync<InvalidDataException>(() => ModelMaintenance.CheckAsync(unrelated, receiptPath)),
                    "A receipt for another model is rejected before reading the selected model bytes");

            await File.WriteAllBytesAsync(plan.DestinationPath, bytes[..^1]);
            result = await ModelMaintenance.CheckAsync(plan.DestinationPath, receiptPath);
            check(result.StatusDisplay == "Size mismatch" && !result.Integrity.Matches
                && result.Integrity.ActualSha256 is null && new FileInfo(plan.DestinationPath).Length == bytes.Length - 1,
                "Maintenance reports a truncated model without overwriting it or inventing a hash");

            var changed = bytes.ToArray();
            changed[^1] ^= 1;
            await File.WriteAllBytesAsync(plan.DestinationPath, changed);
            result = await ModelMaintenance.CheckAsync(plan.DestinationPath, receiptPath);
            check(result.StatusDisplay == "Hash mismatch" && !result.Integrity.Matches
                && result.Integrity.ActualSha256 is not null
                && (await File.ReadAllBytesAsync(plan.DestinationPath)).SequenceEqual(changed),
                "Maintenance detects changed same-size model bytes and preserves the file");

            File.Delete(plan.DestinationPath);
            result = await ModelMaintenance.CheckAsync(plan.DestinationPath, receiptPath);
            check(result.StatusDisplay == "Missing file" && !result.Integrity.Exists && !File.Exists(plan.DestinationPath),
                "Maintenance reports a missing model without acquiring or recreating it");

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            check(await RejectsAsync<OperationCanceledException>(() => ModelMaintenance.CheckAsync("unused", "unused", cancelled.Token)),
                "Precancelled maintenance performs no receipt or model access");
            await File.WriteAllTextAsync(receiptPath, JsonSerializer.Serialize(receipt with { Plan = plan with { Name = "Changed receipt" } }));
            check(await RejectsAsync<InvalidDataException>(() => ModelMaintenance.CheckAsync(plan.DestinationPath, receiptPath)),
                "Maintenance rejects altered acquisition evidence rather than trusting historical success");

            foreach (var path in new[] { "relative.gguf", @"\\server\share\model.gguf", @"\\?\F:\model.gguf" })
                check(await RejectsAsync<ArgumentException>(() => ModelMaintenance.CheckAsync(path, receiptPath)),
                    "Maintenance rejects relative, network and device model paths before receipt access");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task<bool> RejectsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); return false; }
        catch (T) { return true; }
    }
}
