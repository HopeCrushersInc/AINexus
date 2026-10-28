using System.Windows;

namespace AINexus.App;

public partial class MainWindow
{
    private void DiscoveryReviewSelected_Click(object sender, RoutedEventArgs e)
        => ReviewSelectedDiscoveryItem();

    internal void ReviewSelectedDiscoveryItem()
    {
        if (_discoveryBusy || DiscoveryResultsGrid.SelectedItem is not DiscoveryRow row)
        {
            DiscoveryStatusText.Text = "Select a result yourself before reviewing it. Nothing was selected or installed automatically.";
            return;
        }
        var item = row.Recommendation.Item;
        if (item is { Interest: DiscoveryInterest.Images, Kind: "Model", Artifact: { Format: "Safetensors" } imageArtifact })
        {
            DiscoveryDetailsText.Text = DescribeRecommendation(row)
                + $"\n\nIMAGE FILE CANDIDATE\nExact file bytes: {imageArtifact.SizeBytes?.ToString("N0") ?? "Unknown"}\n"
                + "Hardware fit, complete checkpoint contents, companion assets and runtime compatibility are unverified. "
                + "Automatic image-artifact download, installation and checkpoint configuration are not implemented. "
                + "This review grants no download or launch approval and starts no software.";
            DiscoveryStatusText.Text = "Image source and pinned file reviewed. Automatic installation is not supported; no file was downloaded or model loaded.";
            DiscoveryDetailsExpander.IsExpanded = true;
            DiscoveryTab.IsSelected = true;
            return;
        }
        if (item.Kind == "Model" && item.Artifact is { Format: "GGUF" })
        {
            _guidedModel = null;
            _guidedUserSelection = item;
            _guidedUserSnapshot = row.Report.Catalog;
            _guidedModelPlan = null;
            _guidedReceipt = null;
            _guidedInstalledOnly = false;
            GuidedModelApprovalCheck.IsChecked = false;
            InvalidateGuidedRuntimeReview();
            GuidedModelTitleText.Text = item.Name;
            GuidedModelFolderText.Text = DiscoveryDestinationText.Text;
            GuidedRuntimePathText.Text = _settings.LlamaCppExecutablePath;
            GuidedModelDetailsText.Text = item.Description + "\nSource-declared license: " + (item.License ?? "Unknown")
                + "\n" + string.Join("\n", item.Sources.Select(source => source.Url.AbsoluteUri));
            GuidedModelStep.IsExpanded = true;
            GuidedEngineStep.IsExpanded = string.IsNullOrWhiteSpace(GuidedRuntimePathText.Text);
            GuidedStatusText.Text = "You selected this online result. Review its source, size, hardware fit, license and destination before approving anything.";
            GuidedSetupTab.IsSelected = true;
            RefreshGuidedControls();
            return;
        }
        DiscoveryDetailsText.Text = DescribeRecommendation(row);
        DiscoveryStatusText.Text = item.Kind == "Tool"
            ? "You selected online tool metadata. Its source and license are shown; an automated installation recipe has not been implemented. No software was installed."
            : "You selected this result. Review its details and supported setup actions. No software was installed.";
        DownloadsTab.IsSelected = true;
    }
}
