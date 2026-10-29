using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace AINexus.App;

public partial class MainWindow
{
    private readonly ComfyServerController _imageServer = new();
    private bool _imageBusy;
    private bool _closeAfterImages;
    private CancellationTokenSource? _imageOperation;
    private ComfyInventory? _imageInventory;
    private Uri? _imageConnectedEndpoint;
    private ComfyImageResult? _imageResult;
    internal Func<HttpClient>? ImageHttpFactoryOverride { get; set; }
    internal Func<CancellationToken, Task<ResourceAdmission>>? ImageAdmissionOverride { get; set; }

    private void InitializeImages()
    {
        Closing += (_, args) =>
        {
            if (args.Cancel) return;
            if (_imageBusy)
            {
                args.Cancel = true; _closeAfterImages = true; _imageOperation?.Cancel();
                ImageStatusText.Text = "Cancelling the current image operation before closing...";
                return;
            }
            try { _imageServer.Stop(); }
            catch (Exception ex) { args.Cancel = true; ImageStatusText.Text = "The owned engine could not stop: " + ex.Message; }
        };
        Closed += (_, _) => { _imageOperation?.Cancel(); _imageServer.Dispose(); };
        RefreshImageControls();
    }
    private void RefreshImageControls()
    {
        var selected = _selectedImageTool is not null;
        ImageToolLanding.Visibility = selected ? Visibility.Collapsed : Visibility.Visible;
        ImageAdapterPanel.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        ImageConnectButton.IsEnabled = ImageStartButton.IsEnabled = ImageEndpointText.IsEnabled = ImageEnginePathText.IsEnabled = !_imageBusy && selected;
        ImageStopButton.IsEnabled = !_imageBusy && _imageServer.IsRunning;
        ImageModelSelector.IsEnabled = !_imageBusy && _imageInventory is not null;
        var dimensionsValid = TryReadImageDimensions(out _, out _, out var dimensionMessage);
        ImageWidthText.IsEnabled = ImageHeightText.IsEnabled = !_imageBusy;
        ImageDimensionHintText.Text = dimensionMessage;
        ImageGenerateButton.IsEnabled = selected && !_imageBusy && dimensionsValid && _imageConnectedEndpoint is not null && ImageModelSelector.SelectedItem is string;
        ImageCancelButton.IsEnabled = _imageBusy;
        ImageSaveButton.IsEnabled = !_imageBusy && _imageResult is not null;
        RefreshComfyCheckpointFolderControls();
    }
    private void ImageInputsChanged(object sender, RoutedEventArgs e)
    {
        if (ImageGenerateButton is null) return;
        _imageInventory = null; _imageConnectedEndpoint = null; ImageModelSelector.ItemsSource = null;
        ImageStatusText.Text = "Engine address changed. Check the connection before generating.";
        RefreshImageControls();
    }
    private void ImageModelChanged(object sender, SelectionChangedEventArgs e) { if (ImageGenerateButton is not null) RefreshImageControls(); }
    private async Task RunImageOperationAsync(Func<CancellationToken, Task> operation)
    {
        if (_imageBusy || _lifetime.IsCancellationRequested) return;
        _imageBusy = true;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _imageOperation = cancel;
        RefreshImageControls();
        try { await operation(cancel.Token); }
        catch (OperationCanceledException ex) { if (!_lifetime.IsCancellationRequested) ImageStatusText.Text = ex.Message; }
        catch (Exception ex) { if (!_lifetime.IsCancellationRequested) ImageStatusText.Text = "Could not complete: " + ex.Message; }
        finally
        {
            _imageOperation = null; _imageBusy = false;
            if (!_lifetime.IsCancellationRequested) RefreshImageControls();
            if (_closeAfterImages && !_lifetime.IsCancellationRequested) { _closeAfterImages = false; Close(); }
        }
    }
    private async Task ConnectImageEngineAsync(CancellationToken token)
    {
        if (_selectedImageTool is null) throw new InvalidOperationException("Choose an image tool before connecting an engine.");
        _imageInventory = null; _imageConnectedEndpoint = null; ImageModelSelector.ItemsSource = null;
        var endpoint = new Uri(ImageEndpointText.Text.Trim());
        ComfyImageClient.ValidateEndpoint(endpoint);
        using var http = OllamaClient.CreateLocalHttpClient();
        var inventory = await new ComfyImageClient(http).InspectAsync(endpoint, token);
        _imageInventory = inventory; _imageConnectedEndpoint = endpoint;
        ImageModelSelector.ItemsSource = inventory.Checkpoints;
        ImageModelSelector.SelectedIndex = -1;
        ImageStatusText.Text = $"Connected to ComfyUI {inventory.Version}. {inventory.Checkpoints.Count} checkpoint(s) found. " +
            (inventory.Checkpoints.Count == 0 ? "An image checkpoint must be installed before you can generate." : "Choose a standard diffusion checkpoint to get started.");
    }
    private async void ImageConnectButton_Click(object sender, RoutedEventArgs e) => await RunImageOperationAsync(async token =>
    { ImageStatusText.Text = "Checking the local engine and its installed checkpoints..."; await ConnectImageEngineAsync(token); });
    private async void ImageStartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_imageBusy || _selectedImageTool is null) return;
        if (string.IsNullOrWhiteSpace(ImageEnginePathText.Text))
        {
            var dialog = new OpenFolderDialog { Title = "Choose an already installed ComfyUI portable folder", Multiselect = false };
            if (dialog.ShowDialog(this) != true) return;
            ImageEnginePathText.Text = dialog.FolderName;
        }
        await RunImageOperationAsync(async token =>
        {
            var endpoint = new Uri(ImageEndpointText.Text.Trim());
            ComfyImageClient.ValidateEndpoint(endpoint);
            if (endpoint.Host != "127.0.0.1") throw new ArgumentException("The owned engine starts on 127.0.0.1. Set that address before starting.");
            ImageStatusText.Text = "Starting your local image engine. This does not load a checkpoint or generate an image...";
            await _imageServer.StartAsync(ImageEnginePathText.Text.Trim(), endpoint.Port, token);
            await ConnectImageEngineAsync(token);
            await _preferencesReady.Task.WaitAsync(token);
            _settings = _settings with { ComfyUIRoot = ImageEnginePathText.Text.Trim() };
            await _settingsStore.SaveAsync(_settings, token);
        });
    }
    private void ImageStopButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _imageServer.Stop(); _imageInventory = null; _imageConnectedEndpoint = null; ImageModelSelector.ItemsSource = null;
            ImageStatusText.Text = "Stopped the image engine started by this window. Other servers were left alone.";
        }
        catch (Exception ex) { ImageStatusText.Text = "The owned engine could not stop: " + ex.Message; }
        RefreshImageControls();
    }
    private async void ImageGenerateButton_Click(object sender, RoutedEventArgs e) => await GenerateSelectedImageAsync();
    internal async Task GenerateSelectedImageAsync()
    {
        if (_imageBusy || _selectedImageTool is null || _lifetime.IsCancellationRequested) return;
        if (!TryReadImageDimensions(out var width, out var height, out var dimensionMessage))
        {
            ImageStatusText.Text = dimensionMessage;
            return;
        }
        if (_imageConnectedEndpoint is not { } endpoint || ImageModelSelector.SelectedItem is not string checkpoint) return;
        var prompt = ImagePromptText.Text.Trim(); var negative = ImageNegativeText.Text.Trim();
        var seed = RandomNumberGenerator.GetInt32(int.MaxValue);
        await RunImageOperationAsync(async token =>
        {
            _imageResult = null; ImagePreview.Source = null;
            ImageStatusText.Text = "Checking available resources before starting one local image...";
            ResourceAdmission admission;
            if (ImageAdmissionOverride is { } inspect) admission = await inspect(token);
            else
            {
                var monitor = new ResourceMonitor(); await monitor.SampleAsync(token); await Task.Delay(250, token);
                admission = new ResourcePolicy(MinimumAvailableRamGb: 2).Evaluate(await monitor.SampleAsync(token));
            }
            if (!admission.Allowed) throw new InvalidOperationException(admission.Reason);
            ImageStatusText.Text = "Generating on your local engine. The first image may take longer while the checkpoint loads.";
            using var http = ImageHttpFactoryOverride is { } createHttp ? createHttp() : OllamaClient.CreateLocalHttpClient();
            var result = await new ComfyImageClient(http).GenerateAsync(endpoint, checkpoint, prompt, negative, width, height, 20, seed, token);
            RenderImageResult(result);
        });
    }
    internal void RenderImageResult(ComfyImageResult result)
    {
        ComfyImageClient.ValidatePng(result.Png);
        using var stream = new MemoryStream(result.Png);
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream; image.EndInit(); image.Freeze();
        ImagePreview.Source = image; _imageResult = result;
        ImageStatusText.Text = $"Image ready · {image.PixelWidth} × {image.PixelHeight} · seed {result.Seed} · {result.Checkpoint}. Generated locally; output is also kept by your image engine.";
    }
    private void ImageSaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_imageResult is not { } result) return;
        var dialog = new SaveFileDialog { Title = "Save your generated image", Filter = "PNG image|*.png", FileName = "AINexus-" + result.JobId + ".png", OverwritePrompt = true };
        if (dialog.ShowDialog(this) == true)
        {
            try { File.WriteAllBytes(dialog.FileName, result.Png); ImageStatusText.Text = "Saved image to " + dialog.FileName; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ImageStatusText.Text = "Could not save image: " + ex.Message; }
        }
    }
    private void ImageCancelButton_Click(object sender, RoutedEventArgs e) => _imageOperation?.Cancel();
}
