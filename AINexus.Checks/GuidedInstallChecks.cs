using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AINexus.App;

namespace AINexus.Checks;

internal static class GuidedInstallChecks
{
    internal static void RunNative(MainWindow _, Action<bool, string> check)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var bytes = "GGUF guided install fixture"u8.ToArray();
        var calls = 0;
        var serveBadHash = false;
        using var http = new HttpClient(new FixtureHandler((_, _) =>
        {
            calls++;
            var payload = bytes.ToArray();
            if (serveBadHash) payload[^1] ^= 1;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
        }));
        var window = new MainWindow(new ModelAcquisitionService(http)) { GuidedPersistPreferences = false };
        try
        {
            Pump(async () =>
            {
                var folder = Path.Combine(root, "models");
                Directory.CreateDirectory(folder);
                var featured = FeaturedModelCatalog.Visible(false).Single(model => model.Id == "qwen-7b");
                var cards = (ItemsControl)window.FindName("FeaturedCards");
                var nsfw = (CheckBox)window.FindName("FeaturedNsfwCheck");
                var approve = (CheckBox)window.FindName("GuidedModelApprovalCheck");
                var start = (Button)window.FindName("GuidedStartChatButton");
                var guided = (TabItem)window.FindName("GuidedSetupTab");
                var lookupCalls = 0;
                var report = FeaturedReport(featured, folder, bytes);
                window.GuidedLookupOverride = (_, _) => { lookupCalls++; return Task.FromResult(report.Catalog); };
                window.GuidedHardwareOverride = _ => Task.FromResult(report.Machine with { ScannedAt = DateTimeOffset.UtcNow });

                check(nsfw.IsChecked != true && ((IEnumerable<FeaturedModel>)cards.ItemsSource).All(model => !model.Nsfw)
                    && !((Button)window.FindName("GuidedDownloadButton")).IsEnabled && !start.IsEnabled,
                    "Guided storefront starts without adult opt-in, model approval, download or chat readiness");
                window.SelectGuidedModel(featured);
                check(!guided.IsSelected && lookupCalls == 0 && calls == 0,
                    "Selecting an install card before hardware evidence cannot start setup or metadata requests");

                window.CompleteWorkloadScan(report.Machine);
                SavePreview(window, "storefront");
                nsfw.IsChecked = true;
                check(((IEnumerable<FeaturedModel>)cards.ItemsSource).Any(model => model.Nsfw) && lookupCalls == 0,
                    "Explicit adult catalog opt-in reveals warned cards without downloading or selecting one");
                nsfw.IsChecked = false;
                window.ApplyWorkloadSelection([DiscoveryInterest.LocalAssistants]);
                window.SelectGuidedModel(featured);
                ((TextBox)window.FindName("GuidedModelFolderText")).Text = folder;
                ((TextBox)window.FindName("GuidedRuntimePathText")).Text = Path.Combine(root, "llama-server.exe");
                check(guided.IsSelected && lookupCalls == 0 && calls == 0 && approve.IsChecked != true,
                    "An explicit featured card opens guided setup after hardware scan without source fetch or approval");

                await window.ReviewGuidedModelAsync();
                check(lookupCalls == 1 && calls == 0 && approve.IsChecked != true
                    && ((TextBox)window.FindName("GuidedModelDetailsText")).Text.Contains(featured.Repository),
                    "Guided model review reads the exact source and hardware while leaving transfer approval unchecked");
                await window.DownloadGuidedModelAsync();
                check(calls == 0 && !Directory.EnumerateFileSystemEntries(folder).Any() && !start.IsEnabled,
                    "A reviewed featured model cannot download before explicit license and destination approval");

                approve.IsChecked = true;
                await window.DownloadGuidedModelAsync();
                var modelPath = Path.Combine(folder, featured.PreferredFile);
                check(calls == 1 && File.Exists(modelPath) && File.ReadAllBytes(modelPath).SequenceEqual(bytes)
                    && start.IsEnabled && approve.IsChecked != true && !window.OwnedLlamaServerRunning,
                    "Approved guided acquisition verifies actual bytes, consumes approval and enables an explicit load without starting a server");
                check(!string.IsNullOrWhiteSpace(window.GuidedReceiptPath) && File.Exists(window.GuidedReceiptPath)
                    && window.GuidedModelPath == modelPath,
                    "Guided acquisition saves an actual receipt bound to the installed model path");
                SavePreview(window, "guided-ready");

                var failedFolder = Path.Combine(root, "bad-model");
                Directory.CreateDirectory(failedFolder);
                ((TextBox)window.FindName("GuidedModelFolderText")).Text = failedFolder;
                var failedReport = FeaturedReport(featured, failedFolder, bytes);
                window.GuidedLookupOverride = (_, _) => Task.FromResult(failedReport.Catalog);
                window.GuidedHardwareOverride = _ => Task.FromResult(failedReport.Machine with { ScannedAt = DateTimeOffset.UtcNow });
                await window.ReviewGuidedModelAsync();
                approve.IsChecked = true;
                serveBadHash = true;
                await window.DownloadGuidedModelAsync();
                check(calls == 2 && !File.Exists(Path.Combine(failedFolder, featured.PreferredFile))
                    && !start.IsEnabled && !window.OwnedLlamaServerRunning && approve.IsChecked != true,
                    "A guided download hash mismatch cannot publish a model, retain approval or enable chat startup");
            }, TimeSpan.FromSeconds(45), () => window.Close());
        }
        finally { window.Close(); Directory.Delete(root, recursive: true); }
    }

    internal static void RunLive(string modelId, string existingFolder, string existingRuntime, Action<bool, string> check)
    {
        var featured = FeaturedModelCatalog.Visible(true).Single(model => model.Id == modelId);
        if (!Directory.Exists(existingFolder) || !File.Exists(existingRuntime))
            throw new ArgumentException("Live guided setup needs an existing model folder and approved local llama-server executable.");
        var window = new MainWindow { GuidedPersistPreferences = false };
        var progress = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        var status = (TextBlock)window.FindName("GuidedStatusText");
        progress.Tick += (_, _) => Console.WriteLine("Guided live progress: " + status.Text);
        var started = DateTimeOffset.UtcNow;
        try
        {
            progress.Start();
            Pump(async () =>
            {
                var machine = await HardwareCapabilityScanner.ScanAsync();
                window.CompleteWorkloadScan(machine);
                window.ApplyWorkloadSelection([DiscoveryInterest.LocalAssistants]);
                if (featured.Nsfw) ((CheckBox)window.FindName("FeaturedNsfwCheck")).IsChecked = true;
                window.SelectGuidedModel(featured);
                ((TextBox)window.FindName("GuidedModelFolderText")).Text = Path.GetFullPath(existingFolder);
                ((TextBox)window.FindName("GuidedRuntimePathText")).Text = Path.GetFullPath(existingRuntime);
                await window.ReviewGuidedModelAsync();
                if (!((TextBox)window.FindName("GuidedModelDetailsText")).Text.Contains("MODEL DATA ACQUISITION PLAN"))
                    throw new InvalidOperationException("Live guided source review failed: " + status.Text);
                ((CheckBox)window.FindName("GuidedModelApprovalCheck")).IsChecked = true;
                await window.DownloadGuidedModelAsync();
                var modelPath = window.GuidedModelPath;
                var receiptPath = window.GuidedReceiptPath;
                if (!((Button)window.FindName("GuidedStartChatButton")).IsEnabled || !File.Exists(modelPath) || !File.Exists(receiptPath))
                    throw new InvalidOperationException("Live guided acquisition failed: " + status.Text);
                check(true, "Live guided installer acquired or verified the exact model and saved its receipt through the native approval flow");
                await window.StartGuidedChatAsync();
                if (!window.OwnedLlamaServerRunning || !((Button)window.FindName("ChatSendButton")).IsEnabled)
                    throw new InvalidOperationException("Live guided chat startup failed: " + status.Text);
                check(true, "Live guided setup loaded its model, verified actual generation and enabled native local chat");
                var prompt = (TextBox)window.FindName("ChatPromptText");
                var transcript = (TextBox)window.FindName("ChatTranscriptText");
                prompt.Text = "Please reply with a short greeting.";
                await window.SendChatAsync();
                var first = transcript.Text;
                if (!first.Contains("Assistant:") || !first.Contains("You: Please reply with a short greeting.") || prompt.Text.Length != 0)
                    throw new InvalidOperationException("First actual local chat turn failed: " + ((TextBlock)window.FindName("ChatStatusText")).Text);
                check(true, "Live native chat displayed an actual first local model response");
                prompt.Text = "What was my last request?";
                await window.SendChatAsync();
                var second = transcript.Text;
                if (!second.StartsWith(first, StringComparison.Ordinal) || !second.Contains("You: What was my last request?")
                    || second.Split("Assistant:", StringSplitOptions.None).Length < 3 || prompt.Text.Length != 0)
                    throw new InvalidOperationException("Second actual local chat turn failed: " + ((TextBlock)window.FindName("ChatStatusText")).Text);
                check(true, "Live native chat retained the first turn and displayed a second actual local model response");
                var reportDirectory = Path.Combine(AppContext.BaseDirectory, "Reports", "GuidedLive");
                Directory.CreateDirectory(reportDirectory);
                var reportPath = Path.Combine(reportDirectory, $"{modelId}-{Guid.NewGuid():N}.json");
                var report = new
                {
                    ModelId = modelId, ModelPath = modelPath, ReceiptPath = receiptPath, RuntimePath = existingRuntime,
                    StartedAtUtc = started, CompletedAtUtc = DateTimeOffset.UtcNow,
                    TwoActualTurnsDisplayed = true, FirstTranscriptCharacters = first.Length, FinalTranscriptCharacters = second.Length,
                    TranscriptSaved = false,
                    Limitations = "Short setup/chat verification only; no quality, all-hardware or OS-enforced network isolation claim."
                };
                await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine("Guided live report: " + reportPath);
                SavePreview(window, "live-chat");
            }, TimeSpan.FromHours(2), () => window.Close());
        }
        finally { progress.Stop(); window.Close(); }
        check(!window.OwnedLlamaServerRunning, "Live guided window close stopped its owned model server");
    }

    private static DiscoveryReport FeaturedReport(FeaturedModel featured, string folder, byte[] bytes)
    {
        var report = AcquisitionPlanChecks.FixtureReport(folder, bytes);
        var item = report.Catalog.Items.Single();
        var artifact = item.Artifact! with { Repository = featured.Repository, FileName = featured.PreferredFile };
        var at = DateTimeOffset.UtcNow;
        item = item with
        {
            Id = $"huggingface:{artifact.Repository}@{artifact.Revision}:{artifact.FileName}", Name = featured.Name,
            Publisher = featured.Repository.Split('/')[0], Artifact = artifact, License = "apache-2.0",
            Sources = [new("Pinned fixture", new Uri($"https://huggingface.co/{artifact.Repository}/blob/{artifact.Revision}/README.md"), at)],
            PipelineTag = "text-generation"
        };
        return DiscoveryPipeline.Evaluate(report.Catalog with { Items = [item], RetrievedAt = at }, report.Machine, report.TargetVolume);
    }

    private static void SavePreview(MainWindow window, string suffix)
    {
        if (Environment.GetEnvironmentVariable("AINEXUS_RENDER_GUIDED") != "1") return;
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(1440, 1000));
        content.Arrange(new Rect(0, 0, 1440, 1000));
        content.UpdateLayout();
        var bitmap = new RenderTargetBitmap(1440, 1000, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var outputDirectory = Path.Combine(AppContext.BaseDirectory, "Reports", "Guided");
        Directory.CreateDirectory(outputDirectory);
        var path = Path.Combine(outputDirectory, $"{Guid.NewGuid():N}-{suffix}.png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        encoder.Save(file);
        Console.WriteLine("Guided preview: " + path);
    }

    private static void Pump(Func<Task> action, TimeSpan timeout, Action cancel)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var previous = SynchronizationContext.Current;
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = timeout };
        var timedOut = false;
        timer.Tick += (_, _) =>
        {
            if (timedOut) { frame.Continue = false; return; }
            timedOut = true;
            cancel();
            timer.Interval = TimeSpan.FromSeconds(30);
        };
        try
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var task = action();
            _ = task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
            timer.Start();
            if (!task.IsCompleted) Dispatcher.PushFrame(frame);
            if (!task.IsCompleted || timedOut) throw new TimeoutException("Guided checks exceeded their deadline; owned work was asked to cancel.");
            task.GetAwaiter().GetResult();
        }
        finally { timer.Stop(); SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private sealed class FixtureHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
}
