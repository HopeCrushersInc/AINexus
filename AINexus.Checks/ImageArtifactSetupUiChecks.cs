using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Controls;
using AINexus.App;

namespace AINexus.Checks;

internal static class ImageArtifactSetupUiChecks
{
    private static readonly byte[] Bytes = "UI image artifact fixture; not a complete or executable checkpoint"u8.ToArray();

    internal static void RunNative(Action<bool, string> check)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            SelectionReviewAndAcquisition(root, check);
            ReviewMutationAndCache(root, check);
            ClosingCancelsPartial(root, check);
        }
        finally
        {
            var fixtureRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "fixtures")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(fixtureRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe image UI fixture cleanup.");
            Directory.Delete(root, recursive: true);
        }
    }

    private static void SelectionReviewAndAcquisition(string root, Action<bool, string> check)
    {
        var first = NewFolder(root, "first");
        var second = NewFolder(root, "second");
        var report = FixtureReport(first);
        var item = report.Catalog.Items.Single();
        var window = new MainWindow { GuidedPersistPreferences = false };
        var transfers = 0;
        var hardware = 0;
        try
        {
            GuidedInstallChecks.Pump(async () =>
            {
                window.CompleteWorkloadScan(report.Machine);
                window.ApplyWorkloadSelection([DiscoveryInterest.Images]);
                window.ImageArtifactHardwareOverride = _ => { hardware++; return Task.FromResult(FixtureReport(first).Machine); };
                window.ImageArtifactHttpFactoryOverride = () => new HttpClient(new Handler((request, _) =>
                {
                    transfers++;
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Bytes) });
                }));
                var folder = Find<TextBox>(window, "ImageArtifactDestinationText");
                var approve = Find<CheckBox>(window, "ImageArtifactApprovalCheck");
                var download = Find<Button>(window, "ImageArtifactDownloadButton");
                check(folder.Text == "" && approve.IsChecked != true && !download.IsEnabled && window.ImageArtifactReviewedPlanId is null,
                    "Image acquisition starts with no selected source, destination, reviewed plan or approval");
                await window.DownloadImageArtifactAsync();
                check(transfers == 0 && hardware == 0 && !Directory.EnumerateFileSystemEntries(first).Any(),
                    "Opening image acquisition and requesting an unapproved transfer performs no scan, download or file changes");
                var gguf = item with { Artifact = item.Artifact! with { Format = "GGUF", FileName = "not-image.gguf" } };
                check(!window.TrySelectImageArtifactSetup(gguf, report.Catalog with { Items = [gguf] }),
                    "Image file acquisition cannot substitute the GGUF text-model recipe");
                check(window.TrySelectImageArtifactSetup(item, report.Catalog)
                    && Find<TabItem>(window, "ImageArtifactSetupTab").IsSelected && folder.Text == ""
                    && hardware == 0 && transfers == 0 && window.GuidedModelPath is null && !window.OwnedLlamaServerRunning,
                    "Explicit image source selection opens neutral setup without choosing a destination, scanning, approving or launching");
                folder.Text = first;
                await window.DownloadImageArtifactAsync();
                check(transfers == 0 && hardware == 0 && !download.IsEnabled,
                    "A chosen image destination alone cannot enable a transfer before exact review and approval");
                await window.ReviewImageArtifactDownloadAsync();
                var firstPlanId = window.ImageArtifactReviewedPlanId;
                var detail = Find<TextBox>(window, "ImageArtifactSetupDetailsText").Text;
                check(firstPlanId is not null && hardware == 1 && transfers == 0 && approve.IsChecked != true && !download.IsEnabled
                    && detail.Contains(item.Artifact!.Sha256!, StringComparison.OrdinalIgnoreCase)
                    && detail.Contains(item.Artifact.FileName, StringComparison.Ordinal) && detail.Contains(first, StringComparison.Ordinal)
                    && detail.Contains("hardware fit", StringComparison.OrdinalIgnoreCase),
                    "Image review binds the exact source hash, file, destination and current scan while leaving fit unverified and approval unchecked");
                await window.DownloadImageArtifactAsync();
                check(transfers == 0 && !Directory.EnumerateFileSystemEntries(first).Any(),
                    "A reviewed image download remains inert until its own explicit approval");
                approve.IsChecked = true;
                check(download.IsEnabled, "Only approval of the current exact image plan enables its acquisition action");
                folder.Text = second;
                check(approve.IsChecked != true && !download.IsEnabled && window.ImageArtifactReviewedPlanId is null,
                    "Changing the image destination revokes the prior plan and approval");
                await window.ReviewImageArtifactDownloadAsync();
                check(window.ImageArtifactReviewedPlanId != firstPlanId && approve.IsChecked != true,
                    "A new image destination requires a different exact review and a fresh approval");
                approve.IsChecked = true;
                await window.DownloadImageArtifactAsync();
                check(transfers == 1 && window.ImageArtifactResultPath == Path.Combine(second, item.Artifact!.FileName)
                    && File.ReadAllBytes(window.ImageArtifactResultPath!).SequenceEqual(Bytes)
                    && window.ImageArtifactReceiptPath is { } receiptPath && File.Exists(receiptPath)
                    && approve.IsChecked != true && !download.IsEnabled && window.ImageArtifactReviewedPlanId is null,
                    "Approved native image transfer verifies actual fixture bytes, saves a separate receipt and consumes its approval");
                var connectFolder = Find<Button>(window, "ImageArtifactConnectFolderButton");
                check(connectFolder.IsEnabled, "Verified image data exposes only an optional explicit folder-connection review action");
                connectFolder.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                check(Find<TabItem>(window, "ComfyCheckpointFoldersTab").IsSelected
                    && Find<TextBox>(window, "ComfyFolderEngineText").Text == "" && Find<TextBox>(window, "ComfyFolderPathsText").Text == second
                    && Find<CheckBox>(window, "ComfyFolderApprovalCheck").IsChecked != true && window.ComfyCheckpointFolderReviewedPlanId is null,
                    "Connecting a verified image's folder copies only its exact user-chosen location without choosing an engine, reviewing or approving configuration");
                using (var receipt = JsonDocument.Parse(await File.ReadAllTextAsync(window.ImageArtifactReceiptPath!)))
                {
                    check(receipt.RootElement.GetProperty("ArtifactKind").GetString() == "ImageArtifactCandidate"
                        && !receipt.RootElement.GetProperty("RuntimeExecutionVerified").GetBoolean()
                        && !receipt.RootElement.GetProperty("CompleteCheckpointVerified").GetBoolean()
                        && !receipt.RootElement.GetProperty("HardwareFitVerified").GetBoolean()
                        && !window.ImageArtifactReceiptPath!.Contains(Path.Combine(".ainexus", "receipts"), StringComparison.OrdinalIgnoreCase),
                        "Image-file receipts never enter the GGUF launch index or claim installed checkpoint, hardware fit or execution success");
                }
                await window.DownloadImageArtifactAsync();
                check(transfers == 1, "A completed image transfer cannot repeat using consumed approval");
                await window.ReviewImageArtifactDownloadAsync();
                approve.IsChecked = true;
                await window.DownloadImageArtifactAsync();
                check(transfers == 1 && Find<TextBox>(window, "ImageArtifactSetupDetailsText").Text.Contains("Reused matching existing file: True", StringComparison.Ordinal)
                    && !window.OwnedLlamaServerRunning,
                    "Explicit image-file reuse verifies existing bytes without another HTTP transfer, engine configuration or model launch");
                var changed = Bytes.ToArray(); changed[0] ^= 1;
                await File.WriteAllBytesAsync(Path.Combine(second, item.Artifact!.FileName), changed);
                await window.ReviewImageArtifactDownloadAsync();
                approve.IsChecked = true;
                await window.DownloadImageArtifactAsync();
                check(transfers == 1 && window.ImageArtifactResultPath is null && window.ImageArtifactReviewedPlanId is null
                    && approve.IsChecked != true && !download.IsEnabled
                    && File.ReadAllBytes(Path.Combine(second, item.Artifact.FileName)).SequenceEqual(changed),
                    "Conflicting existing image bytes are preserved and failed verification consumes approval instead of silently repairing or replacing the file");
                window.TrySelectImageArtifactSetup(item, report.Catalog);
                check(folder.Text == "" && window.ImageArtifactReviewedPlanId is null && approve.IsChecked != true,
                    "Selecting an image source again clears remembered destination and acquisition approval");
            }, TimeSpan.FromSeconds(20), () => window.Close());
        }
        finally { window.Close(); }
    }

    private static void ReviewMutationAndCache(string root, Action<bool, string> check)
    {
        var first = NewFolder(root, "mutation-first");
        var second = NewFolder(root, "mutation-second");
        var report = FixtureReport(first);
        var item = report.Catalog.Items.Single();
        var window = new MainWindow { GuidedPersistPreferences = false };
        var scans = 0;
        var transfers = 0;
        try
        {
            GuidedInstallChecks.Pump(async () =>
            {
                window.CompleteWorkloadScan(report.Machine);
                window.ApplyWorkloadSelection([DiscoveryInterest.Images]);
                window.ImageArtifactHttpFactoryOverride = () => { transfers++; throw new InvalidOperationException("No HTTP expected in review fixtures."); };
                window.ImageArtifactHardwareOverride = _ => { scans++; return Task.FromResult(FixtureReport(first).Machine); };
                var folder = Find<TextBox>(window, "ImageArtifactDestinationText");
                window.TrySelectImageArtifactSetup(item, report.Catalog with { IsCached = true });
                folder.Text = first;
                await window.ReviewImageArtifactDownloadAsync();
                check(scans == 0 && transfers == 0 && window.ImageArtifactReviewedPlanId is null
                    && Find<CheckBox>(window, "ImageArtifactApprovalCheck").IsChecked != true,
                    "Saved image metadata cannot prepare a new download or trigger hardware/HTTP work");
                window.TrySelectImageArtifactSetup(item, report.Catalog);
                folder.Text = first;
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                window.ImageArtifactHardwareOverride = async token =>
                {
                    scans++; entered.SetResult();
                    await Task.Delay(Timeout.Infinite, token);
                    return report.Machine;
                };
                var pending = window.ReviewImageArtifactDownloadAsync();
                await entered.Task;
                folder.Text = second;
                await pending;
                check(window.ImageArtifactReviewedPlanId is null && transfers == 0
                    && Find<CheckBox>(window, "ImageArtifactApprovalCheck").IsChecked != true,
                    "Destination changes during an awaited hardware scan cancel the old review and cannot restore its plan or approval");
                window.ImageArtifactHardwareOverride = _ => Task.FromResult(FixtureReport(first).Machine);
                await window.ReviewImageArtifactDownloadAsync();
                Find<CheckBox>(window, "ImageArtifactApprovalCheck").IsChecked = true;
                window.RenderDiscoveryReport(report);
                var grid = Find<DataGrid>(window, "DiscoveryResultsGrid");
                grid.SelectedIndex = 0;
                check(window.ImageArtifactReviewedPlanId is null && Find<CheckBox>(window, "ImageArtifactApprovalCheck").IsChecked != true
                    && !Find<Button>(window, "ImageArtifactDownloadButton").IsEnabled && transfers == 0,
                    "A changed Discovery selection revokes an image plan instead of retaining approval for a different source");
            }, TimeSpan.FromSeconds(15), () => window.Close());
        }
        finally { window.Close(); }
    }

    private static void ClosingCancelsPartial(string root, Action<bool, string> check)
    {
        var folder = NewFolder(root, "closing");
        var report = FixtureReport(folder);
        var item = report.Catalog.Items.Single();
        var sentinel = Path.Combine(folder, "keep.txt");
        File.WriteAllText(sentinel, "unrelated file");
        var window = new MainWindow { GuidedPersistPreferences = false };
        var closed = false;
        window.Closed += (_, _) => closed = true;
        try
        {
            GuidedInstallChecks.Pump(async () =>
            {
                window.CompleteWorkloadScan(report.Machine);
                window.ApplyWorkloadSelection([DiscoveryInterest.Images]);
                var readEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                window.ImageArtifactHardwareOverride = _ => Task.FromResult(FixtureReport(folder).Machine);
                window.ImageArtifactHttpFactoryOverride = () => new HttpClient(new Handler((_, _) => Task.FromResult(
                    new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BlockingStream(readEntered)) })));
                window.TrySelectImageArtifactSetup(item, report.Catalog);
                Find<TextBox>(window, "ImageArtifactDestinationText").Text = folder;
                await window.ReviewImageArtifactDownloadAsync();
                Find<CheckBox>(window, "ImageArtifactApprovalCheck").IsChecked = true;
                var pending = window.DownloadImageArtifactAsync();
                await readEntered.Task;
                check(Directory.EnumerateFiles(folder, ".ainexus-image-*.partial").Any() && !closed,
                    "The native close fixture reaches its own bounded image partial transfer before cancellation");
                window.Close();
                check(!closed, "Closing during image acquisition waits for cancellation and owned-partial cleanup");
                await pending;
                check(closed && !Directory.EnumerateFiles(folder, ".ainexus-image-*.partial").Any()
                    && !File.Exists(Path.Combine(folder, item.Artifact!.FileName)) && File.ReadAllText(sentinel) == "unrelated file"
                    && !window.OwnedLlamaServerRunning,
                    "Image acquisition cancels, removes only its own partial, preserves unrelated data and closes without launching a model");
            }, TimeSpan.FromSeconds(15), () => window.Close());
        }
        finally { window.Close(); }
    }

    private static DiscoveryReport FixtureReport(string folder)
    {
        var report = AcquisitionPlanChecks.FixtureReport(folder, Bytes);
        var now = DateTimeOffset.UtcNow;
        var artifact = report.Catalog.Items.Single().Artifact! with { Repository = "Fixture/UiImage", FileName = "chosen-image.safetensors", Format = "Safetensors" };
        var item = report.Catalog.Items.Single() with
        {
            Interest = DiscoveryInterest.Images, PipelineTag = "text-to-image", Artifact = artifact,
            Sources = [new("Pinned card", new Uri($"https://huggingface.co/{artifact.Repository}/blob/{artifact.Revision}/README.md"), now),
                new("Pinned file", new Uri($"https://huggingface.co/{artifact.Repository}/blob/{artifact.Revision}/{artifact.FileName}"), now)]
        };
        return DiscoveryPipeline.Evaluate(report.Catalog with { Items = [item], RetrievedAt = now }, report.Machine, report.TargetVolume);
    }

    private static string NewFolder(string root, string name) { var folder = Path.Combine(root, name); Directory.CreateDirectory(folder); return folder; }
    private static T Find<T>(MainWindow window, string name) => (T)window.FindName(name);

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await send(request, cancellationToken);
            response.RequestMessage = request;
            return response;
        }
    }

    private sealed class BlockingStream(TaskCompletionSource entered) : MemoryStream([])
    {
        public override bool CanSeek => false;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }
}
