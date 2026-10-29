using System.Windows;
using System.Windows.Controls;

namespace AINexus.App;

public partial class MainWindow
{
    private DiscoveredItem? _reviewedTool;
    private DiscoveredItem? _selectedImageTool;
    private void ResetToolSelection()
    {
        _reviewedTool = _selectedImageTool = null;
        _imageInventory = null; _imageConnectedEndpoint = null; ImageModelSelector.ItemsSource = null;
        _imageResult = null; ImagePreview.Source = null;
        ImageEnginePathText.Text = ImageEndpointText.Text = "";
        _comfyPortableRelease = null;
        ComfyPortableAssetSelector.ItemsSource = null;
        ComfyPortableDestinationText.Text = ComfyPortableTagText.Text = "";
        InvalidateComfyPortablePlan();
        InvalidateComfyCheckpointFolderReview();
        SelectedToolTitleText.Text = "No tool selected";
        SelectedToolDetailsText.Text = "Choose a tool from discovery. No tool is preinstalled or selected by Nexus.";
        SelectedToolUseExistingButton.IsEnabled = SelectedToolArchiveButton.IsEnabled = false;
        SelectedToolUseExistingButton.Visibility = SelectedToolArchiveButton.Visibility = Visibility.Collapsed;
        RefreshImageControls();
        RefreshComfyPortableControls();
    }
    private void ImageFindTools_Click(object sender, RoutedEventArgs e)
    {
        if (!_workspaceConfigured || _workspaceProfile?.HasImages != true) return;
        DiscoveryInterestSelector.SelectedItem = ((IEnumerable<DiscoveryWorkflow>)DiscoveryInterestSelector.ItemsSource)
            .FirstOrDefault(workflow => workflow.Interest == DiscoveryInterest.Images);
        DiscoveryTab.IsSelected = true;
        DiscoveryStatusText.Text = "Search online, then select the image tool you want. No tool is installed, required or selected by Nexus.";
    }

    internal bool SelectDiscoveredTool(DiscoveredItem item)
    {
        if (item.Kind != "Tool" || item.Interest != DiscoveryInterest.Images || !_workspaceConfigured || _workspaceProfile?.HasImages != true
            || _imageBusy || _imageServer.IsRunning || _comfyPortableBusy || _comfyFolderBusy) return false;
        ResetToolSelection();
        _reviewedTool = item;
        SelectedToolTitleText.Text = item.Name;
        SelectedToolDetailsText.Text = $"You selected: {item.Name}\nPublisher: {item.Publisher}\nSource-declared license: {item.License ?? "Unknown"}\n"
            + string.Join("\n", item.Sources.Select(source => source.Url.AbsoluteUri))
            + (IsComfyPortableTool(item)
                ? "\n\nOptional adapter available. Choose an existing installation yourself, or review an archive download. Neither action installs or starts a tool automatically."
                : "\n\nNo managed setup or execution adapter is available for this tool yet. Its source page provides manual setup instructions. No alternative tool was substituted.");
        SelectedToolUseExistingButton.Visibility = SelectedToolArchiveButton.Visibility = IsComfyPortableTool(item) ? Visibility.Visible : Visibility.Collapsed;
        SelectedToolUseExistingButton.IsEnabled = SelectedToolArchiveButton.IsEnabled = IsComfyPortableTool(item);
        RefreshImageControls();
        ToolDetailsTab.IsSelected = true;
        return true;
    }

    internal bool UseSelectedImageTool()
    {
        if (!_workspaceConfigured || _workspaceProfile?.HasImages != true || _reviewedTool is not { } tool
            || !IsComfyPortableTool(tool) || _imageBusy || _imageServer.IsRunning) return false;
        _selectedImageTool = tool;
        ImageEnginePathText.Text = "";
        ImageEndpointText.Text = "http://127.0.0.1:8188";
        ImageStatusText.Text = "You selected ComfyUI. Choose its existing installation or explicitly check a local server. No installation has been found, started or assumed.";
        RefreshImageControls();
        ImageStudioTab.IsSelected = true;
        return true;
    }
    private void SelectedToolUseExisting_Click(object sender, RoutedEventArgs e) => UseSelectedImageTool();
    private void SelectedToolArchive_Click(object sender, RoutedEventArgs e) => ComfyPortableOpen_Click(sender, e);
    private void SelectedToolSource_Click(object sender, RoutedEventArgs e)
    {
        if (_reviewedTool is not { } item || DiscoverySourceUri(item) is not { } source) return;
        try
        {
            if (DiscoverySourceOpenOverride is { } open) open(source);
            else System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(source.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) { SelectedToolDetailsText.Text += "\nCould not open the source: " + ex.Message; }
    }
}
