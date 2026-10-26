using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AINexus.App;
using Microsoft.Web.WebView2.Wpf;

namespace AINexus.Checks;

internal static class EmbeddedChecks
{
    public static void RunPolicy(Action<bool, string> check)
    {
        var origin = new Uri("http://127.0.0.1:8123/app/");
        foreach (var value in new[] { "http://localhost:3000/app", "https://127.0.0.1/ui?tab=models", "http://[::1]:4000/" })
            check(EmbeddedNavigationPolicy.ValidateEndpoint(new(value)).IsAbsoluteUri, $"Embedded endpoint accepts loopback URL: {value}");
        foreach (var value in new[] { "https://example.com", "file:///C:/Windows/win.ini", "ftp://127.0.0.1/data", "http://user:secret@localhost/", "http://localhost.example.com/", "http://192.168.1.1/", "http://0.0.0.0/", "app/relative" })
        {
            var rejected = false;
            try { EmbeddedNavigationPolicy.ValidateEndpoint(new(value, UriKind.RelativeOrAbsolute)); }
            catch (ArgumentException) { rejected = true; }
            check(rejected, $"Embedded endpoint rejects {new Uri(value, UriKind.RelativeOrAbsolute).SchemeOrRelative()}");
        }
        check(EmbeddedNavigationPolicy.IsAllowed(new("http://127.0.0.1:8123/other?x=1#section"), origin), "Embedded same-origin paths and fragments are allowed");
        foreach (var value in new[] { "http://127.0.0.1:8124/", "https://127.0.0.1:8123/", "http://localhost:8123/", "http://user@127.0.0.1:8123/", "file:///C:/", "data:text/html,fixture", "about:blank", "https://example.com/" })
            check(!EmbeddedNavigationPolicy.IsAllowed(new(value), origin), "Embedded navigation rejects changed origin, credentials, or unsafe scheme");
        check(EmbeddedNavigationPolicy.IsResourceAllowed(new("data:image/png;base64,AA=="), origin), "Inline data resources do not require a network origin");
        check(EmbeddedNavigationPolicy.IsResourceAllowed(new("blob:http://127.0.0.1:8123/fixture"), origin), "Same-origin blob resources are allowed");
        check(!EmbeddedNavigationPolicy.IsResourceAllowed(new("blob:https://example.com/fixture"), origin), "Foreign-origin blobs are blocked");
        check(!EmbeddedNavigationPolicy.IsResourceAllowed(new("blob:null/fixture"), origin), "Opaque-origin blobs are blocked");
        check(!EmbeddedNavigationPolicy.IsResourceAllowed(new("file:///C:/fixture.gguf"), origin), "Embedded resources cannot read local files");
    }

    public static void RunLive(MainWindow window, Action<bool, string> check)
    {
        check(Application.Current is not null, "Embedded live checks reuse the existing WPF Application");
        var host = window.FindName("EmbeddedHost") as EmbeddedAppHost
            ?? throw new InvalidOperationException("EmbeddedHost was not found in the main window.");
        var tab = window.FindName("EmbeddedTab") as TabItem
            ?? throw new InvalidOperationException("EmbeddedTab was not found in the main window.");
        window.Opacity = 0;
        window.ShowInTaskbar = false;
        window.ShowActivated = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -32000;
        window.Top = -32000;
        tab.IsSelected = true;
        window.Show();

        using var fixture = new LocalFixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(43));
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resourceBlocked = false;
        var navigationBlocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnStatus(string status)
        {
            if (status.Contains("loaded inside", StringComparison.Ordinal)) loaded.TrySetResult();
            if (status.Contains("Blocked a resource", StringComparison.Ordinal)) resourceBlocked = true;
            if (status.Contains("Blocked navigation", StringComparison.Ordinal)) navigationBlocked.TrySetResult();
        }
        host.StatusChanged += OnStatus;
        try
        {
            Pump(async () =>
            {
                await host.NavigateAsync(fixture.Endpoint);
                await loaded.Task.WaitAsync(deadline.Token);
                check(host.Content is WebView2, "Installed WebView2 runtime initializes inside WPF");
                var browser = (WebView2)host.Content;
                var title = await browser.CoreWebView2.ExecuteScriptAsync("document.title");
                check(title == "\"AINexus embedded runtime fixture\"", "Embedded runtime renders the served loopback document");
                var marker = await browser.CoreWebView2.ExecuteScriptAsync("document.getElementById('ready').textContent");
                check(marker == "\"Script ready\"", "Local application JavaScript runs inside the embedded browser");
                var settings = browser.CoreWebView2.Settings;
                check(!settings.AreHostObjectsAllowed && !settings.IsWebMessageEnabled && !settings.AreDevToolsEnabled
                    && !settings.AreDefaultScriptDialogsEnabled, "Embedded runtime applies host integration and dialog restrictions");
                check(resourceBlocked && fixture.BlockedRequests == 0, "Off-origin fixture resources are blocked before reaching the server");

                browser.CoreWebView2.Navigate(fixture.ForeignOrigin + "/escape");
                await navigationBlocked.Task.WaitAsync(deadline.Token);
                check(fixture.EscapeRequests == 0, "Off-origin top-level navigation is cancelled before reaching the server");
            }, TimeSpan.FromSeconds(45));
        }
        finally
        {
            host.StatusChanged -= OnStatus;
            window.Hide();
        }
    }

    private static void Pump(Func<Task> operation, TimeSpan timeout)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var previous = SynchronizationContext.Current;
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Send, dispatcher) { Interval = timeout };
        timer.Tick += (_, _) => frame.Continue = false;
        Task? task = null;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            task = operation();
            _ = task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
            timer.Start();
            if (!task.IsCompleted) Dispatcher.PushFrame(frame);
            if (!task.IsCompleted) throw new TimeoutException("Embedded runtime validation exceeded its 45-second deadline.");
            task.GetAwaiter().GetResult();
        }
        finally
        {
            timer.Stop();
            SynchronizationContext.SetSynchronizationContext(previous);
            // Observe any failure that completes just after the deadline.
            if (task is { IsCompleted: false })
                _ = task.ContinueWith(completed => _ = completed.Exception, TaskContinuationOptions.OnlyOnFaulted);
        }
    }

    private static string SchemeOrRelative(this Uri uri) => uri.IsAbsoluteUri ? uri.Scheme + " endpoint" : "a relative endpoint";

    /// <summary>All fixture content is served from memory; no model/settings fixture files are created.</summary>
    private sealed class LocalFixture : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource lifetime = new();
        private readonly Task server;
        private int blockedRequests;
        private int escapeRequests;
        public Uri Endpoint { get; }
        public string ForeignOrigin { get; }
        public int BlockedRequests => Volatile.Read(ref blockedRequests);
        public int EscapeRequests => Volatile.Read(ref escapeRequests);

        public LocalFixture()
        {
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Endpoint = new Uri($"http://127.0.0.1:{port}/fixture");
            ForeignOrigin = $"http://localhost:{port}";
            server = ServeAsync();
        }

        private async Task ServeAsync()
        {
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    using var client = await listener.AcceptTcpClientAsync(lifetime.Token).ConfigureAwait(false);
                    using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    requestTimeout.CancelAfter(TimeSpan.FromSeconds(3));
                    try
                    {
                        await using var stream = client.GetStream();
                        var request = new StringBuilder();
                        var buffer = new byte[1024];
                        while (request.Length < 8192 && !request.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                        {
                            var count = await stream.ReadAsync(buffer, requestTimeout.Token).ConfigureAwait(false);
                            if (count == 0) break;
                            request.Append(Encoding.ASCII.GetString(buffer, 0, count));
                        }
                        var line = request.ToString().Split('\n')[0];
                        if (line.Contains(" /blocked ", StringComparison.Ordinal)) Interlocked.Increment(ref blockedRequests);
                        if (line.Contains(" /escape ", StringComparison.Ordinal)) Interlocked.Increment(ref escapeRequests);
                        var html = "<!doctype html><html><head><title>AINexus embedded runtime fixture</title></head>" +
                            "<body><div id='ready'>Waiting</div><script>document.getElementById('ready').textContent='Script ready';</script>" +
                            $"<img alt='' src='{ForeignOrigin}/blocked'></body></html>";
                        var content = Encoding.UTF8.GetBytes(html);
                        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {content.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
                        await stream.WriteAsync(header, requestTimeout.Token).ConfigureAwait(false);
                        await stream.WriteAsync(content, requestTimeout.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException) { }
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (SocketException) when (lifetime.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (lifetime.IsCancellationRequested) { }
        }

        public void Dispose()
        {
            lifetime.Cancel();
            listener.Stop();
            server.GetAwaiter().GetResult();
            lifetime.Dispose();
        }
    }
}
