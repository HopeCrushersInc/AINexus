using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AINexus.App;
using Microsoft.Web.WebView2.Wpf;

namespace AINexus.Checks;

/// <summary>Explicit opt-in checks using an actual selected portable server and GGUF.</summary>
internal static class LlamaCppUiChecks
{
    /// <summary>Call last, immediately before the owning check would close its window.</summary>
    public static void RunCloseCleanup(MainWindow window, Action<bool, string> check)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        Pump(async () =>
        {
            await window.StartOwnedLlamaServerAsync();
            check(window.OwnedLlamaServerRunning, "Close-cleanup check starts a real portal-owned server");
            var port = int.Parse(Find<TextBox>(window, "LlamaCppPortText").Text, System.Globalization.CultureInfo.InvariantCulture);
            window.Close();
            check(!window.OwnedLlamaServerRunning && !await IsPortOpenAsync(port, deadline.Token),
                "Closing the native portal synchronously terminates its owned server and releases its port");
            using var http = OllamaClient.CreateLocalHttpClient();
            check(await IsHealthyAsync(http, new("http://127.0.0.1:8080"), deadline.Token),
                "Closing the portal preserves the external llama.cpp server");
        }, TimeSpan.FromSeconds(60));
    }

    public static void Run(MainWindow window, string executable, string model, Action<bool, string> check)
    {
        check(Application.Current is not null, "Native llama.cpp lifecycle checks reuse the existing WPF Application");
        window.Opacity = 0;
        window.ShowInTaskbar = false;
        window.ShowActivated = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -32000;
        window.Top = -32000;
        Find<TabItem>(window, "LlamaCppTab").IsSelected = true;
        window.Show();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            Pump(async () =>
            {
                await window.RuntimePreferencesLoaded.WaitAsync(deadline.Token);
                using var http = OllamaClient.CreateLocalHttpClient();
                var external = new Uri("http://127.0.0.1:8080");
                check(await IsHealthyAsync(http, external, deadline.Token), "Pre-existing external llama.cpp server is healthy before native lifecycle checks");
                var port = ReserveAvailablePort();
                var ownedEndpoint = new Uri($"http://127.0.0.1:{port}");
                Find<TextBox>(window, "LlamaCppExecutableText").Text = executable;
                Find<TextBox>(window, "LlamaCppModelPathText").Text = model;
                Find<TextBox>(window, "LlamaCppPortText").Text = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
                Find<TextBox>(window, "LlamaCppContextText").Text = "4096";
                try
                {
                    await window.StartOwnedLlamaServerAsync();
                    check(window.OwnedLlamaServerRunning,
                        "Native Start starts an owned hidden llama.cpp server: " + Find<TextBlock>(window, "LlamaCppRuntimeStatusText").Text);
                    check(await IsHealthyAsync(http, ownedEndpoint, deadline.Token), "Native Start waits for actual owned-server health");
                    var actualIds = await new LlamaCppClient(http).GetModelsAsync(ownedEndpoint, deadline.Token);
                    var displayedIds = Find<ListBox>(window, "LlamaCppModelList").Items.Cast<string>().ToArray();
                    check(actualIds.Count > 0 && displayedIds.SequenceEqual(actualIds.Order(StringComparer.Ordinal)), "Native startup displays actual server-reported model IDs");
                    check(Find<Button>(window, "LlamaCppStopButton").IsEnabled
                        && !Find<Button>(window, "LlamaCppStartButton").IsEnabled, "Owned-runtime actions reflect the running process");

                    var runtime = Find<ComboBox>(window, "AgentRuntimeSelector");
                    runtime.SelectedIndex = 0;
                    var models = Find<ComboBox>(window, "AgentModelSelector");
                    models.SelectedItem = actualIds[0];
                    Find<TextBox>(window, "AgentNameText").Text = "Native llama.cpp lifecycle smoke test";
                    Find<TextBox>(window, "AgentSystemText").Text = "Answer briefly in plain text. No tool use.";
                    Find<TextBox>(window, "AgentPromptText").Text = "Reply with exactly these two words: Portal ready.";
                    Find<TextBox>(window, "AgentMinRamText").Text = "0";
                    Find<TextBox>(window, "AgentMaxCpuText").Text = "100";
                    Find<TextBox>(window, "AgentMinVramText").Text = "0";
                    var enqueue = Find<Button>(window, "AgentEnqueueButton");
                    check(enqueue.IsEnabled, "Actual owned-server model selection enables native task submission");
                    var grid = Find<DataGrid>(window, "AgentJobGrid");
                    var count = grid.Items.Count;
                    enqueue.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    check(grid.Items.Count == count + 1, "Native Queue Task creates a session task for the selected real model");
                    var row = grid.Items.Cast<object>().Last();
                    await WaitUntilAsync(() => ReadProperty(row, "Status") is "Completed" or "Failed" or "Blocked" or "Cancelled", deadline.Token);
                    var status = ReadProperty(row, "Status");
                    check(status == "Completed" && !string.IsNullOrWhiteSpace(ReadProperty(row, "Response")),
                        $"Native task UI receives actual completed generation ({status}: {ReadProperty(row, "Detail")})");
                    check(ReadProperty(row, "Runtime") == "llama.cpp", "Task history records its captured llama.cpp runtime");
                }
                finally
                {
                    await window.StopOwnedLlamaServerAsync();
                }
                check(!window.OwnedLlamaServerRunning && !Find<Button>(window, "LlamaCppStopButton").IsEnabled,
                    "Native Stop clears owned process state and disables owned Stop");
                check(!await IsPortOpenAsync(port, deadline.Token), "Native Stop releases the owned server's listening port");
                check(await IsHealthyAsync(http, external, deadline.Token), "Stopping the owned server preserves the pre-existing external server on port 8080");
                await CheckActualEmbeddedInterfaceAsync(window, external, check, deadline.Token);
            }, TimeSpan.FromMinutes(3));
        }
        finally
        {
            window.Hide();
        }
    }

    private static async Task CheckActualEmbeddedInterfaceAsync(MainWindow window, Uri endpoint, Action<bool, string> check, CancellationToken token)
    {
        Find<TabItem>(window, "EmbeddedTab").IsSelected = true;
        var host = Find<EmbeddedAppHost>(window, "EmbeddedHost");
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnStatus(string status)
        {
            if (status.Contains("loaded inside", StringComparison.Ordinal)) loaded.TrySetResult();
            if (status.Contains("failed to load", StringComparison.Ordinal) || status.Contains("could not open", StringComparison.Ordinal))
                loaded.TrySetException(new InvalidOperationException(status));
        }
        host.StatusChanged += OnStatus;
        string? scriptId = null;
        try
        {
            await host.NavigateAsync(endpoint);
            await loaded.Task.WaitAsync(token);
            var browser = host.Content as WebView2 ?? throw new InvalidOperationException("The embedded browser did not initialize.");
            scriptId = await browser.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(
                "window.__ainexusRuntimeErrors=[];window.addEventListener('error',e=>{if(window.__ainexusRuntimeErrors.length<20)window.__ainexusRuntimeErrors.push(String(e.message));});" +
                "window.addEventListener('unhandledrejection',e=>{if(window.__ainexusRuntimeErrors.length<20)window.__ainexusRuntimeErrors.push(String(e.reason));});");
            loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await host.NavigateAsync(endpoint);
            await loaded.Task.WaitAsync(token);
            await Task.Delay(500, token);
            var body = JsonSerializer.Deserialize<string>(await browser.CoreWebView2.ExecuteScriptAsync("document.body ? document.body.innerText : ''")) ?? "";
            check(!string.IsNullOrWhiteSpace(body), "The actual llama.cpp web interface renders nonempty text inside the native portal");
            var encodedErrors = await browser.CoreWebView2.ExecuteScriptAsync("JSON.stringify(window.__ainexusRuntimeErrors || [])");
            var errorJson = JsonSerializer.Deserialize<string>(encodedErrors) ?? "[]";
            var errors = JsonSerializer.Deserialize<string[]>(errorJson) ?? [];
            check(errors.Length == 0 && !body.Contains("Failed to fetch dynamically imported module", StringComparison.OrdinalIgnoreCase)
                && !(body.Contains(".svelte", StringComparison.OrdinalIgnoreCase) && body.Contains("error", StringComparison.OrdinalIgnoreCase)),
                "The actual embedded llama.cpp UI loads without script or Svelte-module errors");
        }
        finally
        {
            host.StatusChanged -= OnStatus;
            if (scriptId is not null && host.Content is WebView2 browser)
                browser.CoreWebView2.RemoveScriptToExecuteOnDocumentCreated(scriptId);
        }
    }

    private static T Find<T>(MainWindow window, string name) where T : class => window.FindName(name) as T
        ?? throw new InvalidOperationException($"Expected native control {name} was not found.");
    private static string? ReadProperty(object row, string name) => row.GetType().GetProperty(name)?.GetValue(row) as string;

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken token)
    {
        while (!condition()) await Task.Delay(50, token);
    }

    private static int ReserveAvailablePort()
    {
        var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        try { return ((IPEndPoint)reservation.LocalEndpoint).Port; }
        finally { reservation.Stop(); }
    }

    private static async Task<bool> IsHealthyAsync(HttpClient client, Uri endpoint, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            using var response = await client.GetAsync(new Uri(endpoint, "/health"), timeout.Token);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException) { return false; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return false; }
    }

    private static async Task<bool> IsPortOpenAsync(int port, CancellationToken token)
    {
        using var client = new TcpClient();
        try { await client.ConnectAsync(IPAddress.Loopback, port, token); return true; }
        catch (SocketException) { return false; }
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
            if (!task.IsCompleted) throw new TimeoutException("Native llama.cpp UI checks exceeded the three-minute deadline.");
            task.GetAwaiter().GetResult();
        }
        finally
        {
            timer.Stop();
            SynchronizationContext.SetSynchronizationContext(previous);
            if (task is { IsCompleted: false })
                _ = task.ContinueWith(completed => _ = completed.Exception, TaskContinuationOptions.OnlyOnFaulted);
        }
    }
}
