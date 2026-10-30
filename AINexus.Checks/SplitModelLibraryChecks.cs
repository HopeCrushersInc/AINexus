using System.IO;
using System.Security.Cryptography;
using System.Text;
using AINexus.App;

namespace AINexus.Checks;

internal static class SplitModelLibraryChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(root, "models");
        Directory.CreateDirectory(folder);
        try
        {
            var first = Encoding.UTF8.GetBytes("GGUF inert first part");
            var second = Encoding.UTF8.GetBytes("GGUF inert second part");
            var hash1 = Convert.ToHexString(SHA256.HashData(first));
            var hash2 = Convert.ToHexString(SHA256.HashData(second));
            var revision = new string('a', 40);
            var artifact = new ModelArtifact("Fixture/Split", revision, "model-00001-of-00002.gguf", first.Length, hash1, "GGUF", false,
                [new("model-00002-of-00002.gguf", second.Length, hash2)]);
            var now = DateTimeOffset.UtcNow;
            var item = new DiscoveredItem("fixture", "Fixture split", "Fixture", DiscoveryInterest.LocalAssistants, "Model", "MIT", artifact,
                [new("Pinned card", new Uri($"https://huggingface.co/Fixture/Split/blob/{revision}/README.md"), now)], "Inert file-set fixture.", "text-generation");
            item = item with { Id = CatalogDatabase.CanonicalId(item) };
            var path1 = Path.Combine(folder, artifact.FileName);
            var path2 = Path.Combine(folder, artifact.AdditionalFiles![0].FileName);
            var plan = new ModelAcquisitionPlan("", item.Id, item.Name, artifact,
                new Uri($"https://huggingface.co/Fixture/Split/resolve/{revision}/{artifact.FileName}"), path1,
                Path.GetPathRoot(path1)!, "MIT", item.Sources[0].Url, now, now, now, [], []);
            plan = plan with { PlanId = ModelAcquisitionPlanner.ComputeFingerprint(plan) };
            await File.WriteAllBytesAsync(path1, first);
            await File.WriteAllBytesAsync(path2, second);
            var evidence1 = new ModelIntegrityEvidence(path1, true, true, first.Length, hash1, now, "Inert fixture.");
            var evidence2 = new ModelIntegrityEvidence(path2, true, true, second.Length, hash2, now, "Inert fixture.");
            check(await RejectsAsync(() => ArtifactReceiptStore.SaveAsync(plan, new(evidence1, false, now)))
                && !Directory.Exists(Path.Combine(folder, ".ainexus")),
                "Split-model receipt storage rejects first-file-only evidence before creating receipt folders");
            var evidence = evidence1 with { AdditionalFiles = [evidence2] };
            var receiptPath = await ArtifactReceiptStore.SaveAsync(plan, new(evidence, false, now));
            var read = await OfflineModelVerifier.ReadReceiptAsync(receiptPath);
            check(ModelFileSet.SameIdentity(read.Plan.Artifact, artifact),
                "A complete split-model receipt retains structural identity after JSON roundtrip");
            var index = await CatalogLibraryIndex.ScanAsync([folder]);
            var installation = index.Installations.Single();
            var record = new CatalogRecord(item.Id, item, [DiscoveryInterest.LocalAssistants], now);
            var row = new MainWindow.CatalogLibraryRow(record, [installation], "Fixture only");
            check(installation.FilePresent && installation.InventorySizeMatches && installation.ActualBytes == first.Length + second.Length
                && MainWindow.CatalogInstallationMatches(row, installation),
                "Library indexing and local review require all split-model files and compare aggregate bytes with structural receipt identity");
            var maintenance = await ModelMaintenance.CheckAsync(path1, receiptPath);
            check(maintenance.Integrity.Matches && maintenance.StatusDisplay == "Matches receipt",
                "Model maintenance verifies every part in a complete split-model inventory");
            await File.WriteAllBytesAsync(path1, [.. first, 0]);
            await File.WriteAllBytesAsync(path2, second[..^1]);
            index = await CatalogLibraryIndex.ScanAsync([folder]);
            installation = index.Installations.Single();
            row = new(record, [installation], "Fixture only");
            check(installation.FilePresent && !installation.InventorySizeMatches && installation.ActualBytes == first.Length + second.Length
                && !MainWindow.CatalogInstallationMatches(row, installation),
                "Opposing per-part size changes cannot cancel out into a ready split model even when aggregate bytes are unchanged");
            await File.WriteAllBytesAsync(path1, first);
            File.Delete(path2);
            index = await CatalogLibraryIndex.ScanAsync([folder]);
            maintenance = await ModelMaintenance.CheckAsync(path1, receiptPath);
            check(!index.Installations.Single().FilePresent && index.Installations.Single().ActualBytes is null
                && maintenance.StatusDisplay == "Incomplete file set" && !maintenance.Integrity.Matches,
                "A missing later part marks both library and maintenance as incomplete despite an intact first file");
            var recoveryFolder = Path.Combine(root, "recovery");
            Directory.CreateDirectory(recoveryFolder);
            check(await RejectsAsync(() => ModelRecoveryPlanner.CreateAsync(path1, receiptPath, recoveryFolder))
                && !Directory.EnumerateFileSystemEntries(recoveryFolder).Any(),
                "The single-file recovery recipe blocks split-model repair instead of producing a misleading first-part-only plan");
            var database = new CatalogDatabase(Path.Combine(root, "catalog", "catalog.db"));
            await database.UpsertDiscoveryAsync(new([item], [], now));
            var changed = item with { Artifact = artifact with { AdditionalFiles = [artifact.AdditionalFiles[0] with { Sha256 = new string('b', 64) }] } };
            check(await RejectsAsync(() => database.UpsertDiscoveryAsync(new([changed], [], now.AddSeconds(1))))
                && ModelFileSet.SameIdentity((await database.ReadAsync()).Single().Item.Artifact, artifact),
                "A pinned catalog entry rejects a changed later-part hash and preserves the prior complete inventory");
            check(await RejectsAsync(() => database.UpsertDiscoveryAsync(new([item with { Artifact = artifact with { AdditionalFiles = null } }], [], now.AddSeconds(1))))
                && ModelFileSet.SameIdentity((await database.ReadAsync()).Single().Item.Artifact, artifact),
                "A catalog refresh cannot silently drop a pinned split-model inventory to its first file");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task<bool> RejectsAsync(Func<Task> action)
    {
        try { await action(); return false; }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException) { return true; }
    }
}
