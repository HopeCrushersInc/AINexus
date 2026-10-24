using System.Windows;
using Microsoft.Win32;

namespace AINexus.App;

public partial class MainWindow
{
    private bool _llamaCppBusy;
    private bool _llamaLifecycleInitialized;
    private bool _llamaLifecycleBusy;
    private readonly LlamaServerController _llamaServer = new();
    private Uri? _ownedLlamaEndpoint;
    private int _llamaQueryGeneration;
    internal bool OwnedLlamaServerRunning => _llamaServer.IsRunning;
    internal Task RuntimePreferencesLoaded => _preferencesReady.Task;

    private void LlamaCppTab_Loaded(object sender, RoutedEventArgs e) => InitializeLlamaCppLifecycle();

    private void InitializeLlamaCppLifecycle()
    {
        if (_llamaLifecycleInitialized) return;
        _llamaLifecycleInitialized = true;
        _llamaServer.StatusChanged += status =>
        {
            if (_lifetime.IsCancellationRequested || Dispatcher.HasShutdownStarted) return;
            Dispatcher.BeginInvoke(() =>
            {
                if (_lifetime.IsCancellationRequested) return;
                LlamaCppRuntimeStatusText.Text = status;
                RefreshLlamaRuntimeControls();
                if (!_llamaServer.IsRunning && _ownedLlamaEndpoint is not null)
                {
                    if (_agentModelCatalogs.TryGetValue(LocalGenerationRuntime.LlamaCpp, out var catalog)
                        && catalog.Endpoint == _ownedLlamaEndpoint)
                        SetAgentModelCatalog(LocalGenerationRuntime.LlamaCpp, _ownedLlamaEndpoint, null);
                    _ownedLlamaEndpoint = null;
                }
            });
        };
        Closed += (_, _) =>
        {
            // WPF exits immediately after the final window closes. Complete the
            // controller's bounded shutdown here so an owned child cannot outlive
            // an abandoned async Closed handler. Controller awaits do not use UI context.
            try { _llamaServer.Dispose(); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                System.Diagnostics.Debug.WriteLine($"Owned llama.cpp cleanup failed: {ex.GetType().Name}");
            }
        };
        RefreshLlamaRuntimeControls();
    }

    private async void LlamaCppBrowseExecutableButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select your portable llama.cpp server executable",
            Filter = "llama.cpp server (llama-server.exe)|llama-server.exe",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;
        LlamaCppExecutableText.Text = dialog.FileName;
        await PersistLlamaCppPreferencesAsync();
    }

    private async void LlamaCppBrowseModelButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select an existing GGUF model to load",
            Filter = "GGUF model (*.gguf)|*.gguf",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;
        LlamaCppModelPathText.Text = dialog.FileName;
        await PersistLlamaCppPreferencesAsync();
    }

    private async void LlamaCppStartButton_Click(object sender, RoutedEventArgs e) => await StartOwnedLlamaServerAsync();
    private async void LlamaCppStopButton_Click(object sender, RoutedEventArgs e) => await StopOwnedLlamaServerAsync();
    private void LlamaCppLogButton_Click(object sender, RoutedEventArgs e) => RefreshLlamaRuntimeControls();

    internal async Task StartOwnedLlamaServerAsync()
    {
        InitializeLlamaCppLifecycle();
        if (_llamaLifecycleBusy || _llamaCppBusy || _lifetime.IsCancellationRequested || _llamaServer.IsRunning) return;
        if (!int.TryParse(LlamaCppPortText.Text, out var port) || port is < 1024 or > 65535
            || !int.TryParse(LlamaCppContextText.Text, out var context) || context is < 512 or > 32768)
        {
            LlamaCppRuntimeStatusText.Text = "Enter a port from 1024 to 65535 and a context token count from 512 to 32768.";
            return;
        }
        _llamaLifecycleBusy = true;
        _ownedLlamaEndpoint = null;
        RefreshLlamaRuntimeControls();
        LlamaCppRuntimeStatusText.Text = "Starting the selected portable server hidden and loading the selected model...";
        try
        {
            await _llamaServer.StartAsync(LlamaCppExecutableText.Text.Trim(), LlamaCppModelPathText.Text.Trim(), port, context, _lifetime.Token);
            if (_lifetime.IsCancellationRequested) return;
            _ownedLlamaEndpoint = _llamaServer.Endpoint
                ?? throw new InvalidOperationException("The started server did not report its local endpoint.");
            LlamaCppEndpointText.Text = _ownedLlamaEndpoint.GetLeftPart(UriPartial.Authority);
            LlamaCppRuntimeStatusText.Text = $"Portal-owned server is ready (PID {_llamaServer.Pid}). It stops when this portal closes.";
            await PersistLlamaCppPreferencesAsync();
            await QueryLlamaCppAsync(allowDuringStart: true);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!_lifetime.IsCancellationRequested)
                LlamaCppRuntimeStatusText.Text = $"Server startup failed: {ex.Message}";
        }
        finally
        {
            _llamaLifecycleBusy = false;
            if (!_lifetime.IsCancellationRequested) RefreshLlamaRuntimeControls();
        }
    }

    internal async Task StopOwnedLlamaServerAsync()
    {
        if (_llamaLifecycleBusy || !_llamaServer.IsRunning || _lifetime.IsCancellationRequested) return;
        _llamaLifecycleBusy = true;
        RefreshLlamaRuntimeControls();
        try
        {
            _llamaQueryGeneration++;
            CancelLlamaCppAgentTasks();
            if (_ownedLlamaEndpoint is { } endpoint)
                SetAgentModelCatalog(LocalGenerationRuntime.LlamaCpp, endpoint, null);
            LlamaCppModelList.ItemsSource = null;
            await _llamaServer.StopAsync();
            _ownedLlamaEndpoint = null;
            LlamaCppRuntimeStatusText.Text = "Portal-owned server stopped. Other local servers were not stopped.";
            LlamaCppStatusText.Text = "The portal-owned server has stopped. Query a running local server to refresh model IDs.";
        }
        catch (Exception ex)
        {
            LlamaCppRuntimeStatusText.Text = $"Could not stop the portal-owned server: {ex.Message}";
        }
        finally
        {
            _llamaLifecycleBusy = false;
            if (!_lifetime.IsCancellationRequested) RefreshLlamaRuntimeControls();
        }
    }

    private void RefreshLlamaRuntimeControls()
    {
        var editable = !_llamaLifecycleBusy && !_llamaCppBusy && !_llamaServer.IsRunning;
        LlamaCppStartButton.IsEnabled = editable;
        LlamaCppStopButton.IsEnabled = !_llamaLifecycleBusy && _llamaServer.IsRunning;
        LlamaCppExecutableText.IsEnabled = LlamaCppModelPathText.IsEnabled = editable;
        LlamaCppPortText.IsEnabled = LlamaCppContextText.IsEnabled = editable;
        LlamaCppBrowseExecutableButton.IsEnabled = LlamaCppBrowseModelButton.IsEnabled = editable;
        LlamaCppRefreshButton.IsEnabled = !_llamaLifecycleBusy && !_llamaCppBusy;
        LlamaCppRuntimeLogText.Text = _llamaServer.RecentOutput;
    }

    private async void LlamaCppRefreshButton_Click(object sender, RoutedEventArgs e) => await QueryLlamaCppAsync();

    private async Task QueryLlamaCppAsync(bool allowDuringStart = false)
    {
        if (_llamaCppBusy || (_llamaLifecycleBusy && !allowDuringStart) || _lifetime.IsCancellationRequested) return;
        if (!Uri.TryCreate(LlamaCppEndpointText.Text.Trim(), UriKind.Absolute, out var endpoint))
        {
            LlamaCppStatusText.Text = "Enter an absolute loopback HTTP or HTTPS server address.";
            return;
        }
        try { OllamaClient.ValidateEndpoint(endpoint); }
        catch (ArgumentException ex) { LlamaCppStatusText.Text = ex.Message; return; }

        _llamaCppBusy = true;
        var generation = ++_llamaQueryGeneration;
        RefreshLlamaRuntimeControls();
        LlamaCppRefreshButton.IsEnabled = LlamaCppEndpointText.IsEnabled = false;
        LlamaCppModelList.ItemsSource = null;
        SetAgentModelCatalog(LocalGenerationRuntime.LlamaCpp, endpoint, null);
        LlamaCppStatusText.Text = "Querying model IDs reported by the existing local llama.cpp server...";
        try
        {
            var models = await new LlamaCppClient(_modelHttp).GetModelsAsync(endpoint, _lifetime.Token);
            if (_lifetime.IsCancellationRequested || generation != _llamaQueryGeneration) return;
            RenderLlamaCppModels(models, endpoint);
            await PersistLlamaCppPreferencesAsync();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (_lifetime.IsCancellationRequested || generation != _llamaQueryGeneration) return;
            LlamaCppStatusText.Text = $"Local llama.cpp query failed: {ex.Message}";
            if (SelectedAgentRuntime == LocalGenerationRuntime.LlamaCpp)
                AgentStatusText.Text = "llama.cpp model discovery failed. Check its endpoint and that a local server is running, then refresh.";
        }
        finally
        {
            _llamaCppBusy = false;
            if (!_lifetime.IsCancellationRequested)
            {
                LlamaCppRefreshButton.IsEnabled = LlamaCppEndpointText.IsEnabled = true;
                RefreshLlamaRuntimeControls();
            }
        }
    }

    internal void RenderLlamaCppModels(IReadOnlyList<string> models, Uri endpoint)
    {
        var ids = models.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        LlamaCppModelList.ItemsSource = ids;
        LlamaCppStatusText.Text = $"{ids.Length} model IDs reported by {endpoint.GetLeftPart(UriPartial.Authority)} at {DateTime.Now:HH:mm:ss}. " +
            "Select llama.cpp in Agent Tasks to submit a prompt to one of these IDs.";
        SetAgentModelCatalog(LocalGenerationRuntime.LlamaCpp, endpoint, ids);
    }
}
