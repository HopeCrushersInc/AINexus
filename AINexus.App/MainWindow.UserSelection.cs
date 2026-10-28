using System.Windows;

namespace AINexus.App;

public partial class MainWindow
{
    internal Action<Uri>? DiscoverySourceOpenOverride { get; set; }
    private static Uri? DiscoverySourceUri(DiscoveredItem item) => item.Sources.Select(source => source.Url)
        .FirstOrDefault(url => url is { IsAbsoluteUri: true, Scheme: "https", Port: 443 }
            && (url.Host is "github.com" or "huggingface.co") && url.UserInfo.Length == 0
            && url.Query.Length == 0 && url.Fragment.Length == 0);

    private void DiscoveryOpenSourceButton_Click(object sender, RoutedEventArgs e) => OpenSelectedDiscoverySource();

    internal void OpenSelectedDiscoverySource()
    {
        if (_discoveryBusy || DiscoveryResultsGrid.SelectedItem is not DiscoveryRow row
            || DiscoverySourceUri(row.Recommendation.Item) is not { } source) return;
        try
        {
            if (DiscoverySourceOpenOverride is { } open) open(source);
            else System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(source.AbsoluteUri) { UseShellExecute = true });
            DiscoveryStatusText.Text = "Opened the selected public source page. No download, installation or model launch was approved by Nexus.";
        }
        catch (Exception ex) { DiscoveryStatusText.Text = "Could not open the source page: " + ex.Message; }
    }
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
        if (item is { Interest: DiscoveryInterest.Images, Kind: "Model", Artifact: { Format: "Safetensors" } })
        {
            if (!TrySelectImageArtifactSetup(item, row.Report.Catalog))
                DiscoveryStatusText.Text = "Image-file review could not open. Finish the active setup step and confirm an image workload with a hardware scan, then review this result again.";
            return;
        }
        if (item.Kind == "Model" && item.Artifact is { Format: "GGUF" }
            && (item.Interest is DiscoveryInterest.LocalAssistants or DiscoveryInterest.Coding or DiscoveryInterest.Agents))
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
            ? "Tool source only. Automatic installation is not available for this result. Open the source page for manual setup. No software was installed."
            : item.Interest == DiscoveryInterest.Video
                ? "Video pipeline source only. A complete model-file inventory, compatible runtime and installation recipe are not integrated. No download or hardware-fit claim was made."
                : "You selected this result. Review its details and supported setup actions. No software was installed.";
        if (item.Kind == "Tool" || item.Interest == DiscoveryInterest.Video)
        {
            DiscoveryDetailsExpander.IsExpanded = true;
            DiscoveryTab.IsSelected = true;
        }
        else DownloadsTab.IsSelected = true;
    }
}
