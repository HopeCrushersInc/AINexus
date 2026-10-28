using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Controls;
using AINexus.App;

namespace AINexus.Checks;

internal static class ImageArtifactSetupLiveChecks
{
    internal static void Run(string repository, string fileName, string existingFolder, Action<bool, string> check)
    {
        if (!Directory.Exists(existingFolder)) throw new ArgumentException("Choose an existing explicit image-data folder.");
        var window = new MainWindow { GuidedPersistPreferences = false };
        var transferRequests = 0;
        try
        {
            GuidedInstallChecks.Pump(async () =>
            {
                using var metadata = VerifiedOnlineDiscoveryProvider.CreateHttpClient();
                var snapshot = await new VerifiedOnlineDiscoveryProvider(metadata).SearchAsync(new(DiscoveryInterest.Images, repository, 12));
                var item = snapshot.Items.Single(candidate => candidate.Artifact is { } artifact
                    && artifact.Repository == repository && artifact.FileName == fileName);
                var expectedPath = Path.GetFullPath(Path.Combine(existingFolder, item.Artifact!.FileName));
                if (File.Exists(expectedPath) || Directory.Exists(expectedPath))
                    throw new InvalidOperationException("This fresh-download live check requires an unoccupied exact destination; existing data was preserved.");
                window.CompleteWorkloadScan(await HardwareCapabilityScanner.ScanAsync());
                window.ApplyWorkloadSelection([DiscoveryInterest.Images]);
                window.ImageArtifactHttpFactoryOverride = () => new HttpClient(new CountingHandler(() => Interlocked.Increment(ref transferRequests)))
                    { Timeout = Timeout.InfiniteTimeSpan };
                check(window.TrySelectImageArtifactSetup(item, snapshot)
                    && string.IsNullOrEmpty(((TextBox)window.FindName("ImageArtifactDestinationText")).Text)
                    && window.ImageArtifactReviewedPlanId is null && transferRequests == 0,
                    "Live exact image-source selection opens data setup without choosing a folder or starting transfer");
                ((TextBox)window.FindName("ImageArtifactDestinationText")).Text = Path.GetFullPath(existingFolder);
                await window.ReviewImageArtifactDownloadAsync();
                if (window.ImageArtifactReviewedPlanId is null)
                    throw new InvalidOperationException("Live image review failed: " + ((TextBlock)window.FindName("ImageArtifactSetupStatusText")).Text);
                var approval = (CheckBox)window.FindName("ImageArtifactApprovalCheck");
                check(approval.IsChecked != true && transferRequests == 0 && !window.OwnedLlamaServerRunning,
                    "Live image data review requires separate exact approval and starts no engine or download");
                approval.IsChecked = true;
                await window.DownloadImageArtifactAsync();
                if (window.ImageArtifactReceiptPath is not { } receiptPath || window.ImageArtifactResultPath is not { } artifactPath)
                    throw new InvalidOperationException("Live image acquisition failed: " + ((TextBlock)window.FindName("ImageArtifactSetupStatusText")).Text);
                using var receipt = JsonDocument.Parse(await File.ReadAllTextAsync(receiptPath));
                check(File.Exists(artifactPath) && new FileInfo(artifactPath).Length == item.Artifact!.SizeBytes
                    && receipt.RootElement.GetProperty("ActualSha256").GetString()!.Equals(item.Artifact.Sha256, StringComparison.OrdinalIgnoreCase)
                    && !receipt.RootElement.GetProperty("ReusedExisting").GetBoolean() && transferRequests > 0
                    && approval.IsChecked != true,
                    "Explicitly approved live image acquisition publishes exact source bytes/hash and consumes approval");
                check(!receipt.RootElement.GetProperty("RuntimeExecutionVerified").GetBoolean()
                    && !receipt.RootElement.GetProperty("CompleteCheckpointVerified").GetBoolean()
                    && !receipt.RootElement.GetProperty("HardwareFitVerified").GetBoolean(),
                    "A real downloaded image artifact receipt does not invent runtime, hardware or complete-checkpoint evidence");
                var firstReceipt = receiptPath;
                var requestsAfterFirst = transferRequests;
                await window.ReviewImageArtifactDownloadAsync();
                if (window.ImageArtifactReviewedPlanId is null) throw new InvalidOperationException("Live image reuse review did not prepare a plan.");
                approval.IsChecked = true;
                await window.DownloadImageArtifactAsync();
                if (window.ImageArtifactReceiptPath is not { } reuseReceipt) throw new InvalidOperationException("Live image reuse failed.");
                using var reused = JsonDocument.Parse(await File.ReadAllTextAsync(reuseReceipt));
                check(transferRequests == requestsAfterFirst && reused.RootElement.GetProperty("ReusedExisting").GetBoolean()
                    && window.ImageArtifactResultPath == artifactPath && approval.IsChecked != true,
                    "Separately approved live image reuse rehashes the existing file without another transfer");
                var evidenceRoot = Path.Combine(AppContext.BaseDirectory, "Reports", "ImageArtifactSetupLive");
                Directory.CreateDirectory(evidenceRoot);
                await File.WriteAllTextAsync(Path.Combine(evidenceRoot, "last-run.json"), JsonSerializer.Serialize(new
                {
                    CompletedAt = DateTimeOffset.UtcNow, item.Artifact, ArtifactPath = artifactPath, FirstReceipt = firstReceipt,
                    ReuseReceipt = reuseReceipt, TransferRequests = requestsAfterFirst,
                    FirstReusedExisting = false,
                    RuntimeExecutionVerified = false, HardwareFitVerified = false, CompleteCheckpointVerified = false,
                    Limitation = "Actual data acquisition/reuse only. No image-engine configuration, model loading or generation occurred."
                }, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine("Live image data verified: " + artifactPath);
            }, TimeSpan.FromMinutes(30), () => window.Close());
        }
        finally { window.Close(); }
        check(!window.OwnedLlamaServerRunning, "Live image data acquisition leaves no model engine running");
    }

    private sealed class CountingHandler(Action request) : DelegatingHandler(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseCookies = false, Credentials = null, DefaultProxyCredentials = null,
        AutomaticDecompression = DecompressionMethods.None, ConnectTimeout = TimeSpan.FromSeconds(10), MaxResponseHeadersLength = 64
    })
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken token)
        { request(); return base.SendAsync(message, token); }
    }
}
