using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using AINexus.App;

namespace AINexus.Checks;

internal static class OfflineVerificationChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var modelBytes = "GGUF offline verification fixture"u8.ToArray();
            var models = Path.Combine(root, "models");
            Directory.CreateDirectory(models);
            var report = AcquisitionPlanChecks.FixtureReport(models, modelBytes);
            var plan = ModelAcquisitionPlanner.Create(report, report.Catalog.Items.Single().Id, models);
            await File.WriteAllBytesAsync(plan.DestinationPath, modelBytes);
            var integrity = await ModelArtifactVerifier.VerifyExistingAsync(plan);
            var receipt = new ArtifactAcquisitionReceipt(1, plan, new(integrity, true, DateTimeOffset.UtcNow), DateTimeOffset.UtcNow);
            var receiptPath = Path.Combine(root, "receipt.json");
            await SaveReceiptAsync(receiptPath, receipt);

            var audited = await OfflineModelVerifier.AuditReceiptAsync(receiptPath);
            check(audited.Integrity.Matches && audited.Integrity.ActualBytes == modelBytes.Length,
                "Offline receipt audit rereads actual model bytes without a model server or online source");
            check(!Directory.Exists(Path.Combine(models, ".ainexus", "verification")),
                "An integrity audit does not launch inference or create runtime-success evidence");

            var historicalPlan = plan with
            {
                MetadataRetrievedAt = DateTimeOffset.Parse("2020-01-01T00:00:00Z"),
                HardwareScannedAt = DateTimeOffset.Parse("2020-01-01T00:00:00Z"),
                CreatedAt = DateTimeOffset.Parse("2020-01-01T00:00:00Z")
            };
            historicalPlan = historicalPlan with { PlanId = ModelAcquisitionPlanner.ComputeFingerprint(historicalPlan) };
            await SaveReceiptAsync(receiptPath, receipt with { Plan = historicalPlan });
            var historical = await OfflineModelVerifier.AuditReceiptAsync(receiptPath);
            check(historical.Integrity.Matches && historical.Receipt.Plan.MetadataRetrievedAt.Year == 2020,
                "Historical acquisition evidence can be audited offline without inventing fresh metadata dates");

            await SaveReceiptAsync(receiptPath, receipt with { SchemaVersion = 99 });
            check(await RejectsAsync<InvalidDataException>(() => OfflineModelVerifier.AuditReceiptAsync(receiptPath)),
                "Unknown acquisition-receipt schemas cannot authorize runtime verification");
            await SaveReceiptAsync(receiptPath, receipt with { Plan = plan with { Name = "Tampered after approval" } });
            check(await RejectsAsync<InvalidDataException>(() => OfflineModelVerifier.AuditReceiptAsync(receiptPath)),
                "Offline audits reject altered plan contents rather than trusting a recorded success flag");
            await SaveReceiptAsync(receiptPath, receipt with { Acquisition = receipt.Acquisition with { Evidence = integrity with { Matches = false } } });
            check(await RejectsAsync<InvalidDataException>(() => OfflineModelVerifier.AuditReceiptAsync(receiptPath)),
                "A receipt with failed acquisition integrity is not treated as verified model data");
            await File.WriteAllTextAsync(receiptPath, JsonSerializer.Serialize(receipt).Replace("\"RuntimeExecutionVerified\":false", "\"RuntimeExecutionVerified\":true", StringComparison.Ordinal));
            check(await RejectsAsync<InvalidDataException>(() => OfflineModelVerifier.AuditReceiptAsync(receiptPath)),
                "An acquisition receipt cannot forge a runtime-execution claim");
            await File.WriteAllTextAsync(receiptPath, "{bad fixture");
            check(await RejectsAsync<JsonException>(() => OfflineModelVerifier.AuditReceiptAsync(receiptPath)),
                "Malformed offline receipt JSON is rejected without launching a process");
            await File.WriteAllBytesAsync(receiptPath, new byte[OfflineModelVerifier.MaximumReceiptBytes + 1]);
            check(await RejectsAsync<InvalidDataException>(() => OfflineModelVerifier.AuditReceiptAsync(receiptPath)),
                "Offline receipt parsing enforces its one-MiB bound");
            foreach (var unsafePath in new[] { @"\\server\share\receipt.json", @"\\?\F:\receipt.json", @"\\.\pipe\fixture", "relative-receipt.json" })
                check(await RejectsAsync<ArgumentException>(() => OfflineModelVerifier.AuditReceiptAsync(unsafePath)),
                    "Offline verification rejects UNC, device and relative paths before opening files");

            await SaveReceiptAsync(receiptPath, receipt);
            var changed = modelBytes.ToArray();
            changed[^1] ^= 1;
            await File.WriteAllBytesAsync(plan.DestinationPath, changed);
            var mismatch = await OfflineModelVerifier.AuditReceiptAsync(receiptPath);
            check(!mismatch.Integrity.Matches && File.ReadAllBytes(plan.DestinationPath).SequenceEqual(changed),
                "Offline audit reports changed model bytes and preserves the file");
            check(await RejectsAsync<InvalidDataException>(() => OfflineModelVerifier.VerifyAsync(receiptPath, Path.Combine(root, "missing", "llama-server.exe")))
                && !Directory.Exists(Path.Combine(models, ".ainexus", "verification")),
                "Changed model data blocks verification before any runtime is probed or launched");
            await File.WriteAllBytesAsync(plan.DestinationPath, modelBytes);
            check(await RejectsAsync<FileNotFoundException>(() => OfflineModelVerifier.VerifyAsync(receiptPath, Path.Combine(root, "missing", "llama-server.exe"))),
                "A missing local runtime is reported without installing or inventing one");

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            check(await RejectsAsync<OperationCanceledException>(() => OfflineModelVerifier.VerifyAsync(receiptPath, "not-used", cancellationToken: cancelled.Token)),
                "Precancelled offline verification starts no runtime process");

            check(LlamaRuntimeCapabilities.AdvertisesOffline("Usage\n--offline    prevents network access\n")
                && !LlamaRuntimeCapabilities.AdvertisesOffline("--offline-other flag\nexample: use --offline when supported"),
                "Offline mode is accepted only when the runtime help advertises the actual flag");
            var ordinary = LlamaServerController.CreateStartInfo(Path.Combine(root, "llama-server.exe"), plan.DestinationPath, 8081, 4096);
            var offline = LlamaServerController.CreateStartInfo(Path.Combine(root, "llama-server.exe"), plan.DestinationPath, 8081, 4096, offlineMode: true);
            check(!ordinary.ArgumentList.Contains("--offline") && offline.ArgumentList.Contains("--offline")
                && offline.ArgumentList.Contains("127.0.0.1") && offline.CreateNoWindow,
                "Offline verification adds its explicit offline flag while preserving existing hidden loopback launch defaults");

            await CheckRuntimeIdentityAsync(root, check);
            var capacity = OfflineModelVerifier.EvaluateCapacity(receipt, report.Machine);
            check(capacity.Status == RecommendationStatus.ResourceCandidate,
                "Offline capacity planning reuses the known GGUF recipe and current resource evidence");
            var existingStorage = report.Machine with
            {
                Volumes = report.Machine.Volumes.Select(volume => volume with { AvailableBytes = 64L * 1024 * 1024 }).ToArray()
            };
            check(OfflineModelVerifier.EvaluateCapacity(receipt, existingStorage).Status == RecommendationStatus.ResourceCandidate,
                "Verification budgets space for evidence without demanding a second download of an existing model");
            var pressured = report.Machine with { Resources = report.Machine.Resources with { RamUsedGb = report.Machine.Resources.RamTotalGb, Gpus = [] } };
            check(OfflineModelVerifier.EvaluateCapacity(receipt, pressured).Status == RecommendationStatus.InsufficientResources,
                "Changed available memory blocks a model load rather than trusting acquisition-time capacity");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    public static async Task RunLiveAsync(string receiptPath, string executablePath, Action<bool, string> check)
    {
        var audit = await OfflineModelVerifier.AuditReceiptAsync(receiptPath);
        check(audit.Integrity.Matches, "Live offline audit verifies the real acquired artifact against its saved pinned identity");
        var evidence = await OfflineModelVerifier.VerifyAsync(receiptPath, executablePath);
        check(evidence.OfflineModeRequested && evidence.Arguments.Contains("--offline") && !evidence.OSNetworkIsolationVerified,
            "Actual local verification requests runtime offline mode without claiming a Windows firewall isolation test");
        check(!string.IsNullOrWhiteSpace(evidence.Response) && evidence.RuntimeFiles.Any(file => file.RelativePath.Equals("llama-server.exe", StringComparison.OrdinalIgnoreCase))
            && evidence.RuntimeFiles.All(file => file.Sha256.Length == 64),
            "Actual offline-mode generation returns real text tied to model and runtime file identities");
        using (var client = new TcpClient())
        {
            var closed = false;
            try { await client.ConnectAsync("127.0.0.1", evidence.Endpoint.Port); }
            catch (SocketException) { closed = true; }
            check(closed, "The owned offline smoke-test server releases its port before successful evidence is returned");
        }
        check(File.Exists(evidence.SavedEvidencePath), "Actual successful offline-mode verification saves its evidence beside the model");
        Console.WriteLine($"Offline-mode smoke-test response: {evidence.Response}");
        Console.WriteLine($"Offline verification evidence: {evidence.SavedEvidencePath}");
    }

    private static async Task CheckRuntimeIdentityAsync(string root, Action<bool, string> check)
    {
        var directory = Path.Combine(root, "runtime");
        Directory.CreateDirectory(directory);
        var executable = Path.Combine(directory, "llama-server.exe");
        await File.WriteAllBytesAsync(executable, "Executable identity fixture; never execute"u8.ToArray());
        var library = Path.Combine(directory, "fixture.dll");
        await File.WriteAllBytesAsync(library, "Library identity fixture"u8.ToArray());
        await File.WriteAllTextAsync(Path.Combine(directory, "unrelated.txt"), "Unrelated fixture");
        var fingerprints = await OfflineModelVerifier.FingerprintRuntimeAsync(executable);
        check(fingerprints.Count == 2 && fingerprints.All(file => file.RelativePath != "unrelated.txt")
            && fingerprints.Single(file => file.RelativePath == "fixture.dll").Sha256 == Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(library))),
            "Runtime identity reads actual executable/DLL bytes and excludes unrelated files without executing fixtures");
        await File.WriteAllTextAsync(library, "Changed fixture DLL");
        var changed = await OfflineModelVerifier.FingerprintRuntimeAsync(executable);
        check(changed.Single(file => file.RelativePath == "fixture.dll").Sha256 != fingerprints.Single(file => file.RelativePath == "fixture.dll").Sha256,
            "Runtime DLL changes produce different verification identities even when the executable is unchanged");
        for (var i = 0; i < 256; i++) await File.WriteAllTextAsync(Path.Combine(directory, $"extra-{i}.dll"), "");
        check(await RejectsAsync<InvalidDataException>(() => OfflineModelVerifier.FingerprintRuntimeAsync(executable)),
            "Runtime identity inspection enforces a bounded local file count");
    }
    private static Task SaveReceiptAsync(string path, ArtifactAcquisitionReceipt receipt) =>
        File.WriteAllTextAsync(path, JsonSerializer.Serialize(receipt));
    private static async Task<bool> RejectsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return true; }
        return false;
    }
}
