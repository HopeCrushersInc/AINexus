using System.IO;
using System.Net.Http;
using System.Text.Json;
using AINexus.App;

namespace AINexus.Checks;

internal static class AcquisitionLiveChecks
{
    public static async Task RunAsync(string previouslyApprovedModel, bool exerciseDownload, Action<bool, string> check)
    {
        var original = Path.GetFullPath(previouslyApprovedModel);
        if (!File.Exists(original)) throw new FileNotFoundException("The previously approved local model must exist for live acquisition checks.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        var machine = await HardwareCapabilityScanner.ScanAsync(deadline.Token);
        var target = machine.Volumes.Single(volume => string.Equals(volume.Path, Path.GetPathRoot(original), StringComparison.OrdinalIgnoreCase));
        using var metadata = VerifiedOnlineDiscoveryProvider.CreateHttpClient();
        var catalog = await new VerifiedOnlineDiscoveryProvider(metadata).SearchAsync(
            new(DiscoveryInterest.LocalAssistants, "Qwen2.5-1.5B-Instruct-GGUF", 3), deadline.Token);
        var item = catalog.Items.Single(candidate => candidate.Artifact?.Repository == "Qwen/Qwen2.5-1.5B-Instruct-GGUF"
            && candidate.Artifact.FileName == Path.GetFileName(original));
        var report = DiscoveryPipeline.Evaluate(catalog, machine, target.Path);
        var plan = ModelAcquisitionPlanner.Create(report, item.Id, Path.GetDirectoryName(original)!);
        using (var noDownload = new HttpClient(new RejectNetworkHandler()))
        {
            var reused = await new ModelAcquisitionService(noDownload).AcquireAsync(plan, new(plan.PlanId, true, true), cancellationToken: deadline.Token);
            check(reused.ReusedExisting && reused.Evidence.Matches, "Live acquisition verifies the previously approved real model without an artifact HTTP request");
            var receipt = await ArtifactReceiptStore.SaveAsync(plan, reused, deadline.Token);
            check(File.Exists(receipt), "Live reuse saves pinned source and actual model integrity evidence on the model's drive");
            Console.WriteLine($"Live verified model: {reused.Evidence.ActualBytes:N0} bytes; SHA-256 {reused.Evidence.ActualSha256}");
        }
        if (!exerciseDownload) return;

        // A duplicate of the SAME already-approved artifact, not a different model or runtime.
        // Its checksum was just verified against the original before any download begins.
        var directory = Path.Combine(AppContext.BaseDirectory, "Reports", "Acquisition", Guid.NewGuid().ToString("N"));
        if (!string.Equals(Path.GetPathRoot(directory), target.Path, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Live download evidence must stay on the same approved drive.");
        var downloadPlan = ModelAcquisitionPlanner.Create(report, item.Id, directory);
        using var transport = ModelAcquisitionService.CreateHttpClient();
        long lastPrinted = -1;
        var progress = new InlineProgress(update =>
        {
            if (lastPrinted < 0 || update.CompletedBytes == update.ExpectedBytes || update.CompletedBytes - lastPrinted >= 134_217_728)
            {
                Console.WriteLine($"Live artifact transfer: {update.CompletedBytes:N0}/{update.ExpectedBytes:N0} bytes; {update.Stage}");
                lastPrinted = update.CompletedBytes;
            }
        });
        var result = await new ModelAcquisitionService(transport).AcquireAsync(downloadPlan,
            new(downloadPlan.PlanId, true, true), progress, deadline.Token);
        check(!result.ReusedExisting && result.Evidence.Matches && File.Exists(result.Evidence.Path),
            "Real public-source download publishes only the complete approved artifact after size and SHA-256 verification");
        var independent = await ModelArtifactVerifier.VerifyExistingAsync(downloadPlan, deadline.Token);
        check(independent.Matches && independent.ActualSha256 == result.Evidence.ActualSha256,
            "An independent file reread confirms the real downloaded artifact's integrity");
        var resultReceipt = await ArtifactReceiptStore.SaveAsync(downloadPlan, result, deadline.Token);
        using var receiptDocument = JsonDocument.Parse(await File.ReadAllTextAsync(resultReceipt, deadline.Token));
        check(!receiptDocument.RootElement.GetProperty("RuntimeExecutionVerified").GetBoolean(),
            "A real download receipt does not falsely claim offline model execution was tested");
        check(!Directory.GetFiles(directory, "*.partial").Any(), "A completed real download leaves no unchecked temporary file");
        Console.WriteLine($"Real download evidence receipt: {resultReceipt}");
    }

    private sealed class RejectNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Existing-model reuse must not issue an artifact HTTP request.");
    }
    private sealed class InlineProgress(Action<AcquisitionProgress> report) : IProgress<AcquisitionProgress>
    {
        public void Report(AcquisitionProgress value) => report(value);
    }
}
