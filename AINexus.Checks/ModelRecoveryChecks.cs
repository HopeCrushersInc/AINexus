using System.IO;
using System.Text.Json;
using AINexus.App;

namespace AINexus.Checks;

internal static class ModelRecoveryChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        const long storageFixtureBytes = 123;
        var exactRequired = storageFixtureBytes + ModelRecoveryPlanner.StorageHeadroom;
        check(ModelRecoveryPlanner.ValidateStorage(storageFixtureBytes, exactRequired) == exactRequired,
            "Recovery storage admits an exact artifact-plus-headroom boundary");
        check(Rejects<IOException>(() => ModelRecoveryPlanner.ValidateStorage(storageFixtureBytes, exactRequired - 1)),
            "Recovery storage rejects even one byte below required capacity");
        check(Rejects<OverflowException>(() => ModelRecoveryPlanner.ValidateStorage(long.MaxValue, long.MaxValue)),
            "Recovery storage rejects expected-size overflow instead of wrapping the required capacity");
        check(Rejects<ArgumentOutOfRangeException>(() => ModelRecoveryPlanner.ValidateStorage(0, exactRequired))
            && Rejects<ArgumentOutOfRangeException>(() => ModelRecoveryPlanner.ValidateStorage(-1, exactRequired))
            && Rejects<ArgumentOutOfRangeException>(() => ModelRecoveryPlanner.ValidateStorage(storageFixtureBytes, -1)),
            "Recovery storage rejects nonpositive expected bytes and negative available bytes");
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var bytes = "GGUF recovery review fixture"u8.ToArray();
            var models = Path.Combine(root, "models");
            var recovery = Path.Combine(root, "recovery");
            Directory.CreateDirectory(models);
            Directory.CreateDirectory(recovery);
            var report = AcquisitionPlanChecks.FixtureReport(models, bytes);
            var acquisition = ModelAcquisitionPlanner.Create(report, report.Catalog.Items.Single().Id, models);
            await File.WriteAllBytesAsync(acquisition.DestinationPath, bytes);
            var evidence = await ModelArtifactVerifier.VerifyExistingAsync(acquisition);
            var historical = acquisition with
            {
                CreatedAt = DateTimeOffset.Parse("2020-01-01T00:00:00Z"),
                MetadataRetrievedAt = DateTimeOffset.Parse("2020-01-01T00:00:00Z"),
                HardwareScannedAt = DateTimeOffset.Parse("2020-01-01T00:00:00Z")
            };
            historical = historical with { PlanId = ModelAcquisitionPlanner.ComputeFingerprint(historical) };
            var receipt = new ArtifactAcquisitionReceipt(1, historical, new(evidence, true, DateTimeOffset.UtcNow), DateTimeOffset.UtcNow);
            var receiptPath = Path.Combine(root, "receipt.json");
            await File.WriteAllTextAsync(receiptPath, JsonSerializer.Serialize(receipt));
            var receiptBytes = await File.ReadAllBytesAsync(receiptPath);

            check(await RejectsAsync<InvalidOperationException>(() => ModelRecoveryPlanner.CreateAsync(acquisition.DestinationPath, receiptPath, recovery)),
                "Recovery review rejects a model that currently matches its acquisition receipt");
            File.Delete(acquisition.DestinationPath);
            var beforeFiles = Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order().ToArray();
            var beforeDirectories = Directory.GetDirectories(root, "*", SearchOption.AllDirectories).Order().ToArray();
            var plan = await ModelRecoveryPlanner.CreateAsync(acquisition.DestinationPath, receiptPath, recovery);
            check(!plan.CurrentIntegrity.Exists && plan.OriginalPath == acquisition.DestinationPath
                && plan.RecoveryDirectory == recovery
                && plan.DestinationPath == ModelAcquisitionPlanner.ResolveDestination(recovery, historical.Artifact.FileName)
                && plan.PlanId == ModelRecoveryPlanner.ComputeFingerprint(plan),
                "Missing-model recovery proposes a distinct same-basename candidate and fingerprints its exact review");
            check(plan.AvailableBytes >= plan.RequiredBytes
                && plan.RequiredBytes == plan.ExpectedBytes + ModelRecoveryPlanner.StorageHeadroom
                && plan.StorageCheckedAt <= plan.CreatedAt,
                "Recovery preserves the actual disk snapshot, required bytes and storage-check timestamp in its review");
            check(plan.SourceRepository == historical.Artifact.Repository && plan.Revision == historical.Artifact.Revision
                && plan.ExpectedBytes == bytes.Length && plan.ExpectedSha256 == historical.Artifact.Sha256
                && plan.SourceUrl == historical.DownloadUrl && plan.DeclaredLicense == historical.DeclaredLicense
                && plan.LicenseReference == historical.LicenseReference,
                "Recovery retains the historical pinned source, bytes, hash and terms without inventing fresh upstream evidence");
            check(Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order().SequenceEqual(beforeFiles)
                && Directory.GetDirectories(root, "*", SearchOption.AllDirectories).Order().SequenceEqual(beforeDirectories)
                && (await File.ReadAllBytesAsync(receiptPath)).SequenceEqual(receiptBytes),
                "Recovery review creates no files or folders and preserves its historical receipt");
            var description = ModelRecoveryPlanner.Describe(plan);
            check(description.Contains("NO DOWNLOAD OR REPLACEMENT APPROVAL") && description.Contains(plan.ExpectedSha256)
                && description.Contains("fresh acquisition review") && description.Contains("offline smoke test")
                && description.Contains("retain the original files"),
                "Recovery instructions distinguish historical review, new acquisition approval, actual verification and explicit switching");
            check(description.Contains($"Available bytes at review: {plan.AvailableBytes:N0}")
                && description.Contains(plan.StorageCheckedAt.ToString("O"))
                && description.Contains("does not reserve space"),
                "Recovery description reports its captured storage values without claiming a reservation");

            await File.WriteAllBytesAsync(acquisition.DestinationPath, bytes[..^1]);
            plan = await ModelRecoveryPlanner.CreateAsync(acquisition.DestinationPath, receiptPath, recovery);
            check(plan.CurrentIntegrity.Exists && !plan.CurrentIntegrity.Matches
                && plan.CurrentIntegrity.ActualBytes == bytes.Length - 1
                && (await File.ReadAllBytesAsync(acquisition.DestinationPath)).SequenceEqual(bytes[..^1]),
                "Size-mismatch recovery rechecks and preserves current original bytes");
            var changed = bytes.ToArray();
            changed[^1] ^= 1;
            await File.WriteAllBytesAsync(acquisition.DestinationPath, changed);
            plan = await ModelRecoveryPlanner.CreateAsync(acquisition.DestinationPath, receiptPath, recovery);
            check(!plan.CurrentIntegrity.Matches && plan.CurrentIntegrity.ActualSha256 is not null
                && (await File.ReadAllBytesAsync(acquisition.DestinationPath)).SequenceEqual(changed),
                "Hash-mismatch recovery rehashes current bytes rather than trusting earlier maintenance evidence");
            check(plan.PlanId != ModelRecoveryPlanner.ComputeFingerprint(plan with { DestinationPath = acquisition.DestinationPath }),
                "Changing the proposed recovery destination changes the review fingerprint");

            var unrelated = Path.Combine(root, "unrelated.gguf");
            await File.WriteAllBytesAsync(unrelated, bytes);
            using (var locked = new FileStream(unrelated, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                check(await RejectsAsync<InvalidDataException>(() => ModelRecoveryPlanner.CreateAsync(unrelated, receiptPath, recovery)),
                    "A receipt for another path is rejected before recovery reads selected model bytes");
            check(await RejectsAsync<InvalidOperationException>(() => ModelRecoveryPlanner.CreateAsync(acquisition.DestinationPath, receiptPath, models)),
                "Recovery cannot propose the original path even when its current bytes are damaged");

            await File.WriteAllBytesAsync(plan.DestinationPath, bytes);
            check(await RejectsAsync<InvalidOperationException>(() => ModelRecoveryPlanner.CreateAsync(acquisition.DestinationPath, receiptPath, recovery))
                && (await File.ReadAllBytesAsync(plan.DestinationPath)).SequenceEqual(bytes),
                "Recovery refuses an existing candidate file without changing it");
            File.Delete(plan.DestinationPath);
            Directory.CreateDirectory(plan.DestinationPath);
            check(await RejectsAsync<InvalidOperationException>(() => ModelRecoveryPlanner.CreateAsync(acquisition.DestinationPath, receiptPath, recovery)),
                "Recovery refuses a directory occupying the proposed candidate filename");
            Directory.Delete(plan.DestinationPath);

            var missingDirectory = Path.Combine(root, "not-created");
            check(await RejectsAsync<DirectoryNotFoundException>(() => ModelRecoveryPlanner.CreateAsync(acquisition.DestinationPath, receiptPath, missingDirectory))
                && !Directory.Exists(missingDirectory),
                "Recovery requires an existing folder without creating a missing destination");
            check(await RejectsAsync<ArgumentException>(() => ModelRecoveryPlanner.CreateAsync(acquisition.DestinationPath, receiptPath, Path.GetPathRoot(root)!)),
                "Recovery rejects a whole-drive destination");
            foreach (var unsafePath in new[] { "relative-folder", @"\\server\share\recovery", @"\\?\F:\recovery" })
                check(await RejectsAsync<ArgumentException>(() => ModelRecoveryPlanner.CreateAsync(acquisition.DestinationPath, receiptPath, unsafePath)),
                    "Recovery rejects relative, network and device destination paths");

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            check(await RejectsAsync<OperationCanceledException>(() => ModelRecoveryPlanner.CreateAsync("unused", "unused", "unused", cancelled.Token)),
                "Precancelled recovery review reads no receipt or model");

            var nestedOriginalRoot = Path.Combine(root, "nested-models");
            var nestedReport = AcquisitionPlanChecks.FixtureReport(nestedOriginalRoot, bytes);
            var nestedItem = nestedReport.Catalog.Items.Single();
            nestedItem = nestedItem with { Artifact = nestedItem.Artifact! with { FileName = "quant/recovery.gguf" } };
            nestedReport = nestedReport with { Catalog = nestedReport.Catalog with { Items = new[] { nestedItem } } };
            var nestedAcquisition = ModelAcquisitionPlanner.Create(nestedReport, nestedItem.Id, nestedOriginalRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(nestedAcquisition.DestinationPath)!);
            await File.WriteAllBytesAsync(nestedAcquisition.DestinationPath, bytes);
            var nestedEvidence = await ModelArtifactVerifier.VerifyExistingAsync(nestedAcquisition);
            var nestedReceipt = new ArtifactAcquisitionReceipt(1, nestedAcquisition,
                new(nestedEvidence, true, DateTimeOffset.UtcNow), DateTimeOffset.UtcNow);
            var nestedReceiptPath = Path.Combine(root, "nested-receipt.json");
            await File.WriteAllTextAsync(nestedReceiptPath, JsonSerializer.Serialize(nestedReceipt));
            File.Delete(nestedAcquisition.DestinationPath);
            var nestedRecovery = await ModelRecoveryPlanner.CreateAsync(nestedAcquisition.DestinationPath, nestedReceiptPath, recovery);
            check(nestedRecovery.DestinationPath == ModelAcquisitionPlanner.ResolveDestination(recovery, nestedAcquisition.Artifact.FileName)
                && nestedRecovery.DestinationPath == Path.Combine(recovery, "quant", "recovery.gguf")
                && !Directory.Exists(Path.Combine(recovery, "quant")),
                "Recovery preserves nested artifact paths exactly as a later acquisition would, without creating their directories");
            check(await RejectsAsync<InvalidOperationException>(() => ModelRecoveryPlanner.CreateAsync(
                nestedAcquisition.DestinationPath, nestedReceiptPath, nestedOriginalRoot)),
                "A nested artifact cannot be recovered into its original acquisition root");

            await File.WriteAllTextAsync(receiptPath, JsonSerializer.Serialize(receipt with { Plan = historical with { Name = "Altered" } }));
            check(await RejectsAsync<InvalidDataException>(() => ModelRecoveryPlanner.CreateAsync(acquisition.DestinationPath, receiptPath, recovery)),
                "Recovery rejects altered receipt identity instead of trusting recorded historical success");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task<bool> RejectsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); return false; }
        catch (T) { return true; }
    }

    private static bool Rejects<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }
}
