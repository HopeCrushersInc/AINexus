using System.Windows;
using System.Windows.Controls;
using AINexus.App;

namespace AINexus.Checks;

internal static class ImageDiscoveryUiChecks
{
    internal static void RunNative(Action<bool, string> check)
    {
        var window = new MainWindow { GuidedPersistPreferences = false };
        try
        {
            var original = DiscoveryPipelineChecks.FixtureCatalog();
            var text = original.Items.Single();
            var image = text with
            {
                Id = "image-fixture", Name = "Image file fixture", Interest = DiscoveryInterest.Images,
                Artifact = text.Artifact! with { FileName = "image.safetensors", Format = "Safetensors" },
                PipelineTag = "text-to-image"
            };
            var unknownImage = image with
            {
                Id = "unknown-image", Name = "Unknown image bytes",
                Artifact = image.Artifact! with { FileName = "unknown.safetensors", SizeBytes = null, Sha256 = null }
            };
            var tool = image with { Id = "image-tool", Name = "Image tool fixture", Kind = "Tool", Artifact = null };
            var machine = DiscoveryPipelineChecks.FixtureMachine();
            window.CompleteWorkloadScan(machine);
            window.ApplyWorkloadSelection([DiscoveryInterest.Images, DiscoveryInterest.LocalAssistants]);
            var report = DiscoveryPipeline.Evaluate(original with { Items = [text, image, unknownImage, tool] }, machine, "C:\\");
            var grid = (DataGrid)window.FindName("DiscoveryResultsGrid");
            var showOthers = (CheckBox)window.FindName("DiscoveryShowUnassessedCheck");
            var notice = (TextBlock)window.FindName("DiscoveryImageCandidatesNoticeText");
            window.RenderDiscoveryReport(report);
            check(grid.Items.Count == 3 && grid.SelectedIndex == -1 && showOthers.IsChecked != true,
                "Image artifact facts are visible alongside estimated-fit text results without selecting a model or checking other-result options");
            check(report.Recommendations.Where(r => r.Item.Interest == DiscoveryInterest.Images)
                    .All(r => r.Status == RecommendationStatus.NeedsDetails && r.EstimatedMemoryGiB is null)
                && notice.Visibility == Visibility.Visible && notice.Text.Contains("unverified"),
                "Visible image metadata remains explicitly unverified and gains no text-model memory estimate");
            check(((TextBlock)window.FindName("DiscoveryFitSummaryText")).Text.Contains("1 estimated to fit · 2 image file candidates (fit unverified)"),
                "Discovery counts image candidates separately from hardware-fit results");
            grid.SelectedItem = grid.Items.Cast<object>().Single(row =>
                (string?)row.GetType().GetProperty("Name")!.GetValue(row) == image.Name);
            window.ReviewSelectedDiscoveryItem();
            var details = ((TextBox)window.FindName("DiscoveryDetailsText")).Text;
            check(((TabItem)window.FindName("DiscoveryTab")).IsSelected
                    && ((Expander)window.FindName("DiscoveryDetailsExpander")).IsExpanded
                    && details.Contains(image.Artifact!.Repository) && details.Contains(image.Artifact.Revision)
                    && details.Contains(image.Artifact.FileName) && details.Contains(image.Artifact.Sha256!)
                    && details.Contains("unverified"),
                "Explicit image review stays in Discover and exposes the exact pinned file and unverified execution requirements");
            check(window.GuidedModelPath is null && !window.OwnedLlamaServerRunning
                && ((CheckBox)window.FindName("GuidedModelApprovalCheck")).IsChecked != true
                && !((Button)window.FindName("DiscoveryPlanButton")).IsEnabled,
                "Image metadata review cannot prepare a GGUF acquisition, grant approval or start a model");
            var volumeSelector = (ComboBox)window.FindName("DiscoveryVolumeSelector");
            volumeSelector.SelectedIndex = -1;
            volumeSelector.SelectedItem = machine.Volumes.Single();
            check(grid.Items.Count == 0 && notice.Visibility == Visibility.Collapsed,
                "Changing storage invalidates the old image report and removes its unverified-candidate notice");
            window.RenderDiscoveryReport(report);
            showOthers.IsChecked = true;
            check(grid.Items.Count == 4 && grid.SelectedIndex == -1,
                "Explicitly revealing other results includes the unassessed tool while clearing the old selection");
            showOthers.IsChecked = false;
            window.RenderDiscoveryReport(DiscoveryPipeline.Evaluate(original, machine, "C:\\"));
            check(grid.Items.Count == 1 && grid.SelectedIndex == -1 && notice.Visibility == Visibility.Collapsed,
                "A later text-only report removes the image notice and preserves the ordinary fit filter");
            var unsupportedText = image with { Interest = DiscoveryInterest.LocalAssistants };
            window.RenderDiscoveryReport(DiscoveryPipeline.Evaluate(original with { Items = [unsupportedText, tool] }, machine, "C:\\"));
            check(grid.Items.Count == 0 && notice.Visibility == Visibility.Collapsed,
                "Default image visibility does not expose unrelated unassessed text formats or tools as fit models");
        }
        finally { window.Close(); }
    }
}
