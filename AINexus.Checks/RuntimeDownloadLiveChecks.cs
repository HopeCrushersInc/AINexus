using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using AINexus.App;

namespace AINexus.Checks;

/// <summary>Opt-in public CPU ZIP acquisition and read-only inspection; never extracts or executes.</summary>
internal static class RuntimeDownloadLiveChecks
{
    public static async Task RunAsync(string tag, string archiveDirectory, Action<bool, string> check)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var token = deadline.Token;
        archiveDirectory = RuntimeSetupPlanner.ValidateNewDirectory(archiveDirectory);
        Directory.CreateDirectory(archiveDirectory);
        using var metadataHttp = VerifiedOnlineDiscoveryProvider.CreateHttpClient();
        var snapshot = await new VerifiedOnlineDiscoveryProvider(metadataHttp).SearchAsync(
            new(DiscoveryInterest.LocalAssistants, "", 1, tag), token);
        var runtime = snapshot.Items.Single(item => item.RuntimeRelease?.Tag == tag);
        var machine = await HardwareCapabilityScanner.ScanAsync(token);
        var report = DiscoveryPipeline.Evaluate(snapshot, machine, Path.GetPathRoot(archiveDirectory)!);
        var candidates = runtime.RuntimeRelease!.Archives.Select(asset => RuntimeSetupPlanner.Assess(asset, tag, machine))
            .Where(assessment => assessment is { Role: "Runtime archive", Backend: "cpu", Status: RuntimeArchiveStatus.PlatformMatch }
                && assessment.Asset.Sha256 is not null).ToArray();
        check(candidates.Length == 1, "Live source metadata identifies one exact native CPU ZIP with an upstream digest");
        var selected = candidates.Single().Asset;
        if (selected.SizeBytes > 512L * 1024 * 1024) throw new InvalidOperationException("The opt-in live check limits its CPU archive download to 512 MiB.");
        var uncreatedRuntime = Path.Combine(archiveDirectory, "NotInstalled-" + Guid.NewGuid().ToString("N"));
        var files = RuntimeFilePlanner.Create(report, runtime.Id, uncreatedRuntime, selected.Name);
        var plan = RuntimeDownloadPlanner.Create(report, files, archiveDirectory);
        using var downloadHttp = RuntimeDownloadService.CreateHttpClient();
        var service = new RuntimeDownloadService(downloadHttp);
        var approval = new RuntimeDownloadApproval(plan.PlanId, true, true, true);
        var result = await service.AcquireAsync(plan, approval, cancellationToken: token);
        await using (var archive = new FileStream(result.ArchivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous))
        {
            check(archive.Length == selected.SizeBytes
                && Convert.ToHexString(await SHA256.HashDataAsync(archive, token)).Equals(selected.Sha256, StringComparison.OrdinalIgnoreCase),
                "Actual runtime ZIP independently matches upstream byte count and SHA-256 after acquisition");
        }
        using (var receipt = JsonDocument.Parse(await File.ReadAllTextAsync(result.ReceiptPath, token)))
            check(!receipt.RootElement.GetProperty("ExtractionPerformed").GetBoolean()
                && !receipt.RootElement.GetProperty("RuntimeExecutionVerified").GetBoolean(),
                "Actual runtime acquisition saves a historical byte-verification receipt without installation or execution claims");
        var reused = await service.AcquireAsync(plan, approval, cancellationToken: token);
        check(reused.ReusedExisting && reused.ArchivePath == result.ArchivePath,
            "A new explicit acquisition attempt can reuse the existing matching archive after rehashing");
        var inspection = await RuntimeArchiveInspector.InspectAsync(files, archiveDirectory, token);
        check(inspection.HasServer && inspection.Entries.Count > 0 && !Directory.Exists(uncreatedRuntime),
            "Actual downloaded runtime ZIP passes read-only inspection while the installation destination remains absent");
        var path = Path.Combine(archiveDirectory, "live-download-report-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            Plan = plan, Download = result, Reused = reused, Inspection = inspection,
            ExtractionPerformed = false, RuntimeExecutionVerified = false
        }, new JsonSerializerOptions { WriteIndented = true }), token);
        Console.WriteLine("Actual runtime download report: " + path);
    }
}
