using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AINexus.App;

namespace AINexus.Checks;

internal static class NativeAcquisitionChecks
{
    public static void Run(Action<bool, string> check)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Pump(async () =>
            {
                var bytes = "GGUF native acquisition fixture"u8.ToArray();
                var destination = Path.Combine(root, "success");
                var calls = 0;
                using var http = new HttpClient(new FixtureHandler((_, _) =>
                {
                    calls++;
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
                }));
                var window = Prepare(new ModelAcquisitionService(http), destination, bytes);
                try
                {
                    var download = Find<Button>(window, "AcquisitionDownloadButton");
                    download.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    check(calls == 0 && !Directory.Exists(destination), "Native acquisition cannot be invoked before exact-plan approval");
                    Find<CheckBox>(window, "AcquisitionApprovalCheck").IsChecked = true;
                    download.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await WaitUntilAsync(() => Find<Button>(window, "AcquisitionLibraryButton").IsEnabled);
                    var file = Path.Combine(destination, "fixture.gguf");
                    check(calls == 1 && File.ReadAllBytes(file).SequenceEqual(bytes), "Approved native acquisition publishes actual verified bytes to the reviewed folder");
                    check(Find<CheckBox>(window, "AcquisitionApprovalCheck").IsChecked == false && !download.IsEnabled,
                        "A completed native acquisition consumes its approval instead of silently reusing it");
                    var receipt = Directory.GetFiles(Path.Combine(destination, ".ainexus", "receipts"), "*.json").Single();
                    using var evidence = JsonDocument.Parse(await File.ReadAllTextAsync(receipt));
                    check(!evidence.RootElement.GetProperty("RuntimeExecutionVerified").GetBoolean(),
                        "Native completion saves integrity evidence without inventing an offline inference result");
                }
                finally { window.Close(); }

                var cancelledDestination = Path.Combine(root, "close-cancel");
                using var slow = new HttpClient(new FixtureHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new PendingStream(bytes))
                })));
                var cancelledWindow = Prepare(new ModelAcquisitionService(slow), cancelledDestination, bytes);
                var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                cancelledWindow.Closed += (_, _) => closed.TrySetResult();
                try
                {
                    Find<CheckBox>(cancelledWindow, "AcquisitionApprovalCheck").IsChecked = true;
                    Find<Button>(cancelledWindow, "AcquisitionDownloadButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await WaitUntilAsync(() => Directory.Exists(cancelledDestination)
                        && Directory.GetFiles(cancelledDestination, "*.partial").Length > 0);
                    cancelledWindow.Close();
                    await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    check(!File.Exists(Path.Combine(cancelledDestination, "fixture.gguf"))
                        && Directory.GetFiles(cancelledDestination, "*.partial").Length == 0,
                        "Closing during native acquisition cancels and cleans its own partial before the window closes");
                }
                finally { cancelledWindow.Close(); }
            });
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static MainWindow Prepare(ModelAcquisitionService service, string destination, byte[] bytes)
    {
        var window = new MainWindow(service);
        window.RenderDiscoveryReport(AcquisitionPlanChecks.FixtureReport(destination, bytes));
        Find<TextBox>(window, "DiscoveryDestinationText").Text = destination;
        Find<Button>(window, "DiscoveryPlanButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (!Find<CheckBox>(window, "AcquisitionApprovalCheck").IsEnabled)
            throw new InvalidOperationException("Native fixture plan was not prepared: " + Find<TextBlock>(window, "DiscoveryStatusText").Text);
        return window;
    }
    private static T Find<T>(MainWindow window, string name) where T : class => (T)window.FindName(name);
    private static async Task WaitUntilAsync(Func<bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!ready()) await Task.Delay(20, timeout.Token);
    }
    private static void Pump(Func<Task> action)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var previous = SynchronizationContext.Current;
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(25) };
        timer.Tick += (_, _) => frame.Continue = false;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var task = action();
            _ = task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
            timer.Start();
            if (!task.IsCompleted) Dispatcher.PushFrame(frame);
            if (!task.IsCompleted) throw new TimeoutException("Native acquisition checks exceeded their deadline.");
            task.GetAwaiter().GetResult();
        }
        finally { timer.Stop(); SynchronizationContext.SetSynchronizationContext(previous); }
    }
    private sealed class FixtureHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
    private sealed class PendingStream(byte[] bytes) : MemoryStream(bytes)
    {
        private bool _read;
        public override bool CanSeek => false;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_read) return new(WaitAsync(cancellationToken));
            _read = true;
            return base.ReadAsync(buffer[..4], cancellationToken);
        }
        private static async Task<int> WaitAsync(CancellationToken token) { await Task.Delay(Timeout.Infinite, token); return 0; }
    }
}
