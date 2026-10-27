using System.IO;
using System.Text.Json;
using AINexus.App;

namespace AINexus.Checks;

internal static class CatalogLibraryIndexChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
        var models = Path.Combine(root, "models");
        var modelFolder = Path.Combine(models, "nested", "model");
        Directory.CreateDirectory(modelFolder);
        try
        {
            var bytes = new byte[1024];
            "GGUF"u8.CopyTo(bytes);
            var report = AcquisitionPlanChecks.FixtureReport(modelFolder, bytes);
            var original = report.Catalog.Items.Single();
            var prefixed = original with { Id = "Coding:" + original.Id, Interest = DiscoveryInterest.Coding };
            report = DiscoveryPipeline.Evaluate(report.Catalog with { Items = [prefixed] }, report.Machine, report.TargetVolume);
            var plan = ModelAcquisitionPlanner.Create(report, prefixed.Id, modelFolder);
            await File.WriteAllBytesAsync(plan.DestinationPath, bytes);
            var integrity = await ModelArtifactVerifier.VerifyExistingAsync(plan);
            var receiptPath = await ArtifactReceiptStore.SaveAsync(plan, new(integrity, true, DateTimeOffset.UtcNow));
            var savedReceipt = await File.ReadAllBytesAsync(receiptPath);
            var initialFiles = Directory.GetFiles(root, "*", SearchOption.AllDirectories);

            // Exclusive byte access stays locked: catalog indexing only asks for file metadata.
            using (var locked = new FileStream(plan.DestinationPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var indexed = await CatalogLibraryIndex.ScanAsync([models]);
                var found = indexed.Installations.Single();
                check(found.CatalogId == CatalogDatabase.CanonicalId(original) && found.ModelPath == plan.DestinationPath
                    && found.ReceiptPath == receiptPath && found.FilePresent && found.ActualBytes == bytes.Length,
                    "Receipt catalog links a nested installed artifact to canonical identity independently of category-prefixed source IDs");
                check(found.State == "Receipt recorded; file present; integrity not rechecked" && indexed.Warnings.Count == 0,
                    "Receipt indexing uses metadata without reading locked model bytes or upgrading historical evidence into current integrity/runtime success");
            }
            check((await File.ReadAllBytesAsync(receiptPath)).SequenceEqual(savedReceipt)
                && (await File.ReadAllBytesAsync(plan.DestinationPath)).SequenceEqual(bytes)
                && Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order().SequenceEqual(initialFiles.Order()),
                "Private catalog receipt indexing creates no files and preserves model and receipt bytes");

            await File.WriteAllBytesAsync(plan.DestinationPath, bytes[..^1]);
            var changed = await CatalogLibraryIndex.ScanAsync([models]);
            check(changed.Installations.Single() is { FilePresent: true, ActualBytes: 1023, State: "Receipt recorded; file size mismatch" },
                "A changed file size is reported without trusting the receipt's earlier successful acquisition");
            File.Delete(plan.DestinationPath);
            var missing = await CatalogLibraryIndex.ScanAsync([models]);
            check(missing.Installations.Single() is { FilePresent: false, ActualBytes: null, State: "Receipt recorded; model file missing" },
                "Missing acquired models remain actionable catalog receipt references without recreation or download");

            var empty = await CatalogLibraryIndex.ScanAsync([]);
            check(empty.Installations.Count == 0 && File.Exists(receiptPath),
                "Stopping folder tracking removes catalog installation references while preserving existing acquisition receipts");

            var corruptPath = Path.Combine(Path.GetDirectoryName(receiptPath)!, "corrupt.json");
            await File.WriteAllTextAsync(corruptPath, "{invalid");
            var corrupt = await CatalogLibraryIndex.ScanAsync([models]);
            check(corrupt.Installations.Count == 1 && corrupt.Warnings.Any(w => w.Contains("corrupt.json", StringComparison.Ordinal)),
                "A malformed private receipt is reported while other valid receipt links survive");

            var outside = Path.Combine(root, "outside");
            Directory.CreateDirectory(outside);
            var outsideReport = AcquisitionPlanChecks.FixtureReport(outside, bytes);
            var outsidePlan = ModelAcquisitionPlanner.Create(outsideReport, outsideReport.Catalog.Items.Single().Id, outside);
            await File.WriteAllBytesAsync(outsidePlan.DestinationPath, bytes);
            var outsideEvidence = await ModelArtifactVerifier.VerifyExistingAsync(outsidePlan);
            var externalReceipt = new ArtifactAcquisitionReceipt(1, outsidePlan, new(outsideEvidence, true, DateTimeOffset.UtcNow), DateTimeOffset.UtcNow);
            var copiedPath = Path.Combine(Path.GetDirectoryName(receiptPath)!, "external.json");
            await File.WriteAllTextAsync(copiedPath, JsonSerializer.Serialize(externalReceipt));
            var bounded = await CatalogLibraryIndex.ScanAsync([models]);
            check(bounded.Installations.Count == 1 && bounded.Installations.All(i => i.ModelPath.StartsWith(models + Path.DirectorySeparatorChar))
                && bounded.Warnings.Any(w => w.Contains("external.json", StringComparison.Ordinal)),
                "A receipt cannot cause catalog indexing to expose a model outside explicitly tracked roots");

            var wholeDrive = await CatalogLibraryIndex.ScanAsync([Path.GetPathRoot(root)!]);
            check(wholeDrive.Installations.Count == 0 && wholeDrive.Warnings.Count > 0,
                "Private receipt indexing rejects whole-drive scans");
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            var cancelledAsExpected = false;
            try { await CatalogLibraryIndex.ScanAsync([models], cancelled.Token); }
            catch (OperationCanceledException) { cancelledAsExpected = true; }
            check(cancelledAsExpected, "Cancelled receipt indexing performs no further filesystem work");
        }
        finally
        {
            var fixturePath = Path.GetFullPath(root);
            var fixtureParent = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "fixtures")) + Path.DirectorySeparatorChar;
            if (!fixturePath.StartsWith(fixtureParent, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe fixture cleanup path.");
            Directory.Delete(fixturePath, recursive: true);
        }
    }
}
