using System.IO;
using System.Net.Http;
using System.Windows;
using Microsoft.Win32;

namespace AINexus.App;

public partial class MainWindow
{
    private readonly PortalSettingsStore _settingsStore = PortalSettingsStore.ForCurrentUser();
    private PortalSettings _settings = PortalSettings.Default;
    private readonly HttpClient _modelHttp = OllamaClient.CreateLocalHttpClient();
    private bool _libraryBusy;
    private bool _ollamaBusy;
    private readonly TaskCompletionSource _preferencesReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void InitializeModels()
    {
        Loaded += async (_, _) =>
        {
            try
            {
                var loaded = await _settingsStore.LoadAsync(_lifetime.Token);
                _settings = loaded.Settings;
                OllamaEndpointText.Text = _settings.OllamaEndpoint;
                LlamaCppEndpointText.Text = _settings.LlamaCppEndpoint;
                LlamaCppExecutableText.Text = _settings.LlamaCppExecutablePath;
                LlamaCppModelPathText.Text = _settings.LlamaCppModelPath;
                LlamaCppContextText.Text = _settings.LlamaCppContextSize.ToString();
                LlamaCppPortText.Text = new Uri(_settings.LlamaCppEndpoint).Port.ToString();
                OfflineRuntimeText.Text = _settings.LlamaCppExecutablePath;
                OfflineContextText.Text = _settings.LlamaCppContextSize.ToString();
                ImageEnginePathText.Text = _settings.ComfyUIRoot;
                _preferencesReady.TrySetResult();
                RefreshLibraryRoots();
                if (loaded.Warning is not null) LibraryNoticeText.Text = loaded.Warning;
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            finally { _preferencesReady.TrySetResult(); }
        };
        Closed += (_, _) => _modelHttp.Dispose();
    }

    private void RefreshLibraryRoots()
    {
        LibraryRootSelector.ItemsSource = _settings.ModelRoots;
        if (_settings.ModelRoots.Count > 0) LibraryRootSelector.SelectedIndex = 0;
    }

    private async void LibraryAddButton_Click(object sender, RoutedEventArgs e)
    {
        if (_libraryBusy) return;
        if (_settings.ModelRoots.Count >= 64)
        {
            LibraryStatusText.Text = "The library supports up to 64 tracked folders.";
            return;
        }
        var dialog = new OpenFolderDialog { Title = "Select a model folder to index", Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        var selected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dialog.FolderName));
        if (string.Equals(selected, Path.TrimEndingDirectorySeparator(Path.GetPathRoot(selected)!), StringComparison.OrdinalIgnoreCase))
        {
            LibraryStatusText.Text = "Select a model folder within the drive, rather than the drive root.";
            return;
        }
        _settings = _settings with { ModelRoots = _settings.ModelRoots.Append(selected).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() };
        RefreshLibraryRoots();
        await SaveSettingsAsync();
        await ScanLibraryAsync();
    }

    private async void LibraryRemoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_libraryBusy || LibraryRootSelector.SelectedItem is not string root) return;
        _settings = _settings with { ModelRoots = _settings.ModelRoots.Where(path => !string.Equals(path, root, StringComparison.OrdinalIgnoreCase)).ToArray() };
        RefreshLibraryRoots();
        await SaveSettingsAsync();
        await ScanLibraryAsync();
    }

    private async Task SaveSettingsAsync()
    {
        try { await _settingsStore.SaveAsync(_settings, _lifetime.Token); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LibraryNoticeText.Text = "Preferences could not be saved. Changes apply to this session only.";
        }
    }

    private async void LibraryRescanButton_Click(object sender, RoutedEventArgs e) => await ScanLibraryAsync();

    private async Task ScanLibraryAsync()
    {
        if (_libraryBusy || _lifetime.IsCancellationRequested) return;
        ResetMaintenanceEvidence();
        _libraryBusy = true;
        RefreshMaintenanceControls();
        LibraryAddButton.IsEnabled = LibraryRemoveButton.IsEnabled = LibraryRescanButton.IsEnabled = false;
        LibraryStatusText.Text = "Reading model file metadata...";
        try
        {
            var snapshot = await ModelLibrary.ScanAsync(_settings.ModelRoots, _lifetime.Token);
            if (_lifetime.IsCancellationRequested) return;
            ModelFileGrid.ItemsSource = snapshot.Files;
            LibraryStatusText.Text = $"{snapshot.Files.Count} model files indexed from {_settings.ModelRoots.Count} tracked folders at {snapshot.ScannedAt.ToLocalTime():HH:mm:ss}" +
                (snapshot.Truncated ? " - scan limit reached; results are partial" : "");
            LibraryNoticeText.Text = snapshot.Warnings.Count > 0 ? string.Join(Environment.NewLine, snapshot.Warnings)
                : "File metadata only; extensions do not validate contents. Stop tracking changes this list without deleting files. Sharded files are listed individually.";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { LibraryStatusText.Text = $"Library scan failed: {ex.Message}"; }
        finally
        {
            _libraryBusy = false;
            LibraryAddButton.IsEnabled = LibraryRemoveButton.IsEnabled = LibraryRescanButton.IsEnabled = true;
            RefreshMaintenanceControls();
        }
    }

    private async void OllamaRefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (_ollamaBusy || _lifetime.IsCancellationRequested) return;
        if (!Uri.TryCreate(OllamaEndpointText.Text.Trim(), UriKind.Absolute, out var endpoint))
        {
            OllamaStatusText.Text = "Enter an absolute loopback HTTP or HTTPS endpoint.";
            return;
        }
        try { OllamaClient.ValidateEndpoint(endpoint); }
        catch (ArgumentException ex) { OllamaStatusText.Text = ex.Message; return; }
        _ollamaBusy = true;
        OllamaRefreshButton.IsEnabled = OllamaEndpointText.IsEnabled = false;
        OllamaStatusText.Text = "Querying the existing local Ollama server...";
        try
        {
            var snapshot = await new OllamaClient(_modelHttp).ScanAsync(endpoint, _lifetime.Token);
            if (_lifetime.IsCancellationRequested) return;
            RenderOllama(snapshot);
            UpdateAgentModels(snapshot, endpoint);
            _settings = _settings with { OllamaEndpoint = endpoint.AbsoluteUri };
            await SaveSettingsAsync();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            OllamaAvailableGrid.ItemsSource = OllamaRunningGrid.ItemsSource = null;
            OllamaStatusText.Text = $"Local server query failed: {ex.Message}";
        }
        finally
        {
            _ollamaBusy = false;
            OllamaRefreshButton.IsEnabled = OllamaEndpointText.IsEnabled = true;
        }
    }

    internal void RenderOllama(OllamaSnapshot snapshot)
    {
        OllamaAvailableGrid.ItemsSource = snapshot.AvailableModels.Select(ModelRow).ToList();
        OllamaRunningGrid.ItemsSource = snapshot.RunningModels.Select(ModelRow).ToList();
        OllamaStatusText.Text = $"Available: {(snapshot.AvailableModelsSucceeded ? snapshot.AvailableModels.Count.ToString() : "unavailable")} | " +
            $"Loaded: {(snapshot.RunningModelsSucceeded ? snapshot.RunningModels.Count.ToString() : "unavailable")} | Queried {snapshot.ScannedAt.ToLocalTime():HH:mm:ss}";
        OllamaNoticeText.Text = snapshot.Notes.Count > 0 ? string.Join(Environment.NewLine, snapshot.Notes)
            : "Read-only inventory. Disk size, loaded size, and VRAM usage are distinct readings. No models were downloaded or started.";
    }

    private static object ModelRow(OllamaModelInfo model) => new
    {
        model.Name,
        SizeDisplay = ByteSizeDisplay.ModelSize(model.SizeBytes),
        VramDisplay = ByteSizeDisplay.Format(model.VramSizeBytes),
        ParameterDisplay = model.ParameterSize ?? "Unavailable",
        QuantizationDisplay = model.QuantizationLevel ?? "Unavailable",
        ExpiresDisplay = model.ExpiresAt?.ToLocalTime().ToString("g") ?? "Unavailable"
    };
}
