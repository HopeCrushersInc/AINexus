using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AINexus.App;

namespace AINexus.Checks;

internal static class RuntimeDownloadUiChecks
{
    public static void Run(Action<bool, string> check)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Pump(async () =>
            {
                var bytes = FixtureZip();
                var calls = 0;
                using var http = new HttpClient(new FixtureHandler((_, _) =>
                {
                    calls++;
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
                }));
                var downloads = Path.Combine(root, "downloaded");
                var installFolder = Path.Combine(root, "uncreated-runtime");
                var window = Prepare(new RuntimeDownloadService(http), downloads, installFolder, bytes);
                try
                {
                    if (Environment.GetEnvironmentVariable("AINEXUS_RENDER_INSTALL_UI") == "1") RuntimeInstallationUiChecks.SaveLayoutPreview(window);
                    var download = Find<Button>(window, "RuntimeDownloadButton");
                    download.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    check(calls == 0 && !Directory.EnumerateFiles(downloads).Any(),
                        "Native runtime review cannot download or write archives before exact-plan approval");
                    Find<CheckBox>(window, "RuntimeDownloadApprovalCheck").IsChecked = true;
                    check(download.IsEnabled, "Explicit native runtime download approval enables only its reviewed transfer");
                    download.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await WaitUntilAsync(() => Find<Button>(window, "RuntimeDownloadedInspectButton").IsEnabled);
                    var archive = Directory.GetFiles(downloads, "*.zip").Single();
                    check(calls == 1 && File.ReadAllBytes(archive).SequenceEqual(bytes) && !Directory.Exists(installFolder)
                        && !window.OwnedLlamaServerRunning,
                        "Native approved runtime transfer publishes verified ZIP bytes without extracting or starting them");
                    check(!download.IsEnabled && Find<CheckBox>(window, "RuntimeDownloadApprovalCheck").IsChecked == false
                        && Directory.GetFiles(Path.Combine(downloads, RuntimeDownloadPlanner.ReceiptDirectoryName), "*.json").Length == 1,
                        "Native runtime download consumes approval and saves a separate historical receipt");
                    Find<Button>(window, "RuntimeDownloadedInspectButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await WaitUntilAsync(() => Find<Button>(window, "RuntimeInstallReviewButton").IsEnabled);
                    check(!Directory.Exists(installFolder) && Find<TextBox>(window, "DiscoveryDetailsText").Text.Contains("LOCAL ARCHIVE INSPECTION"),
                        "Downloaded runtime ZIP feeds the existing read-only inspection step without installing it");
                    Find<Button>(window, "RuntimeInstallReviewButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await WaitUntilAsync(() => Find<StackPanel>(window, "RuntimeInstallApprovalPanel").Visibility == Visibility.Visible);
                    check(!Find<Button>(window, "RuntimeInstallButton").IsEnabled && !Directory.Exists(installFolder),
                        "Downloaded archive installation still requires separate bundled-terms and destination approval");
                    Find<TextBox>(window, "RuntimeDownloadDirectoryText").Text = downloads + "-changed";
                    check(!download.IsEnabled && !Find<Button>(window, "RuntimeDownloadedInspectButton").IsEnabled
                        && Find<StackPanel>(window, "RuntimeInstallApprovalPanel").Visibility == Visibility.Collapsed
                        && File.Exists(archive),
                        "Changing the runtime download folder revokes old actions while preserving the acquired archive");
                }
                finally { window.Close(); }

                var slowDownloads = Path.Combine(root, "cancelled-download");
                var slowCalls = 0;
                using var slow = new HttpClient(new FixtureHandler((_, _) =>
                {
                    slowCalls++;
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new PendingStream(bytes)) });
                }));
                var cancelled = Prepare(new RuntimeDownloadService(slow), slowDownloads, Path.Combine(root, "cancelled-runtime"), bytes);
                var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                cancelled.Closed += (_, _) => closed.TrySetResult();
                try
                {
                    Find<CheckBox>(cancelled, "RuntimeDownloadApprovalCheck").IsChecked = true;
                    Find<Button>(cancelled, "RuntimeDownloadButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await WaitUntilAsync(() => slowCalls > 0 && Directory.GetFiles(slowDownloads, "*.partial").Length > 0);
                    cancelled.Close();
                    await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    check(!Directory.GetFiles(slowDownloads, "*.partial").Any() && !Directory.GetFiles(slowDownloads, "*.zip").Any(),
                        "Closing during runtime download waits for cancellation and removes only its own partial before closing");
                }
                finally { cancelled.Close(); }
            });
        }
        finally
        {
            OfflineModelVerifier.CheckNoReparsePath(root);
            Directory.Delete(root, recursive: true);
        }
    }

    private static MainWindow Prepare(RuntimeDownloadService service, string downloads, string installFolder, byte[] bytes)
    {
        Directory.CreateDirectory(downloads);
        var architecture = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
        var name = $"llama-bin-win-cpu-{architecture}.zip";
        var asset = new RuntimeReleaseAsset(name, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)),
            new Uri("https://github.com/ggml-org/llama.cpp/releases/download/fixture/" + name));
        var machine = RuntimeSetupChecks.Machine();
        var original = RuntimeSetupChecks.Report(machine, asset);
        var item = original.Catalog.Items.Single() with
        {
            Sources = [new("Fixture release metadata", new Uri("https://api.github.com/repos/ggml-org/llama.cpp/releases/tags/fixture"), DateTimeOffset.UtcNow)]
        };
        var report = DiscoveryPipeline.Evaluate(original.Catalog with { Items = [item], RetrievedAt = DateTimeOffset.UtcNow }, machine, original.TargetVolume);
        var window = new MainWindow(null, service);
        window.CompleteWorkloadScan(machine);
        window.ApplyWorkloadSelection([DiscoveryInterest.LocalAssistants]);
        Find<CheckBox>(window, "DiscoveryShowUnassessedCheck").IsChecked = false; // Include runtimes whose requirements still need separate review.
        window.RenderDiscoveryReport(report);
        if (Find<DataGrid>(window, "DiscoveryResultsGrid").SelectedIndex != -1)
            throw new InvalidOperationException("Runtime download fixture unexpectedly selected its source without user action.");
        Find<DataGrid>(window, "DiscoveryResultsGrid").SelectedIndex = 0;
        Find<TextBox>(window, "RuntimeDestinationText").Text = installFolder;
        Find<ComboBox>(window, "RuntimeArchiveSelector").SelectedIndex = 0;
        Find<Button>(window, "RuntimeFileReviewButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Find<TextBox>(window, "RuntimeDownloadDirectoryText").Text = downloads;
        Find<Button>(window, "RuntimeDownloadReviewButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (!Find<CheckBox>(window, "RuntimeDownloadApprovalCheck").IsEnabled)
            throw new InvalidOperationException("Native runtime download fixture review failed: " + Find<TextBlock>(window, "DiscoveryStatusText").Text);
        return window;
    }

    private static byte[] FixtureZip()
    {
        using var data = new MemoryStream();
        using (var zip = new ZipArchive(data, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var text = new StreamWriter(zip.CreateEntry("llama-server.exe").Open())) text.Write("Inert fixture; never execute.");
            using (var text = new StreamWriter(zip.CreateEntry("LICENSE").Open())) text.Write("Fixture license for native workflow checks.");
        }
        return data.ToArray();
    }

    private static T Find<T>(MainWindow window, string name) where T : class => (T)window.FindName(name);
    private static async Task WaitUntilAsync(Func<bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!ready()) await Task.Delay(20, timeout.Token);
    }
    private static void Pump(Func<Task> action)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var previous = SynchronizationContext.Current;
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(35) };
        timer.Tick += (_, _) => frame.Continue = false;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var task = action();
            _ = task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
            timer.Start();
            if (!task.IsCompleted) Dispatcher.PushFrame(frame);
            if (!task.IsCompleted) throw new TimeoutException("Native runtime download checks exceeded their deadline.");
            task.GetAwaiter().GetResult();
        }
        finally { timer.Stop(); SynchronizationContext.SetSynchronizationContext(previous); }
    }
    private sealed class FixtureHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
    private sealed class PendingStream(byte[] bytes) : MemoryStream(bytes)
    {
        private bool _read;
        public override bool CanSeek => false;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (_read) return new(WaitAsync(token));
            _read = true;
            return base.ReadAsync(buffer[..4], token);
        }
        private static async Task<int> WaitAsync(CancellationToken token) { await Task.Delay(Timeout.Infinite, token); return 0; }
    }
}
