using System.Windows.Controls;
using AINexus.App;

namespace AINexus.Checks;

internal static class MediaDiscoveryActionChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var window = new MainWindow { GuidedPersistPreferences = false };
        try
        {
            var original = DiscoveryPipelineChecks.FixtureCatalog();
            var template = original.Items.Single();
            var now = DateTimeOffset.UtcNow;
            var tool = template with
            {
                Id = "fixture/video-engine", Name = "Synthetic video engine", Interest = DiscoveryInterest.Video,
                Kind = "Tool", Artifact = null, PipelineTag = null,
                Description = "Synthetic video-engine repository metadata; no installer recipe.",
                Sources = [new("Fixture tool source", new Uri("https://github.com/Fixture/VideoEngine"), now)]
            };
            var video = template with
            {
                Id = "fixture/video-pipeline", Name = "Synthetic video model repository", Interest = DiscoveryInterest.Video,
                Kind = "Model", Artifact = null, PipelineTag = "text-to-video",
                Description = "Synthetic video pipeline; no complete artifact or runtime recipe verified.",
                Sources = [new("Fixture model source", new Uri("https://huggingface.co/Fixture/VideoPipeline"), now)]
            };
            var image = template with
            {
                Id = "fixture/image-file", Name = "Synthetic image file", Interest = DiscoveryInterest.Images,
                Kind = "Model", Artifact = template.Artifact! with { FileName = "image.safetensors", Format = "Safetensors" },
                PipelineTag = "text-to-image", Description = "Synthetic image data candidate."
            };
            var machine = DiscoveryPipelineChecks.FixtureMachine();
            window.CompleteWorkloadScan(machine);
            window.ApplyWorkloadSelection([DiscoveryInterest.Video, DiscoveryInterest.Images]);
            var report = DiscoveryPipeline.Evaluate(original with { Items = [tool, video, image], RetrievedAt = now }, machine, "C:\\");
            var grid = Find<DataGrid>(window, "DiscoveryResultsGrid");
            var fitOnly = Find<CheckBox>(window, "DiscoveryShowUnassessedCheck");
            var sourceButton = Find<Button>(window, "DiscoveryOpenSourceButton");
            var openedSources = new List<Uri>();
            window.DiscoverySourceOpenOverride = uri => openedSources.Add(uri);
            window.RenderDiscoveryReport(report);

            check(grid.Items.Count == 3 && grid.SelectedIndex == -1 && fitOnly.IsChecked != true,
                "Video tools and model repositories remain visible alongside image files by default without selecting a result");
            window.OpenSelectedDiscoverySource();
            check(!sourceButton.IsEnabled && openedSources.Count == 0,
                "Opening a source requires an explicit selected result and does not open a browser automatically");
            check(report.Recommendations.Where(result => result.Item.Interest == DiscoveryInterest.Video)
                    .All(result => result.Status == RecommendationStatus.NeedsDetails && result.EstimatedMemoryGiB is null),
                "Visible video choices remain unassessed and do not inherit text-model memory or hardware-fit claims");

            var toolRow = Row(grid, tool.Id);
            var videoRow = Row(grid, video.Id);
            var imageRow = Row(grid, image.Id);
            check(Value<string>(toolRow, "ActionDisplay") == "View source / manual setup"
                && Value<string>(videoRow, "ActionDisplay") == "Inspect video pipeline / manual setup"
                && Value<string>(imageRow, "ActionDisplay") == "Review image file download",
                "Discovery actions distinguish manual tool setup, video-pipeline inspection and supported image-file acquisition");

            grid.SelectedItem = toolRow;
            window.ReviewSelectedDiscoveryItem();
            var details = Find<TextBox>(window, "DiscoveryDetailsText");
            var status = Find<TextBlock>(window, "DiscoveryStatusText").Text;
            check(Find<TabItem>(window, "DiscoveryTab").IsSelected
                && !Find<TabItem>(window, "DownloadsTab").IsSelected
                && Find<Expander>(window, "DiscoveryDetailsExpander").IsExpanded
                && details.Text.Contains(tool.Sources.Single().Url.AbsoluteUri)
                && details.Text.Contains(tool.Description)
                && (status.Contains("not implemented", StringComparison.OrdinalIgnoreCase)
                    || status.Contains("not available", StringComparison.OrdinalIgnoreCase)
                    || status.Contains("manual", StringComparison.OrdinalIgnoreCase)),
                "Reviewing a tool keeps its source and manual-install limitation visible instead of navigating to an unrelated downloads page");
            check(sourceButton.IsEnabled && openedSources.Count == 0,
                "Reviewing tool details enables its source action without opening that source automatically");
            window.OpenSelectedDiscoverySource();
            check(openedSources.SequenceEqual(new[] { tool.Sources.Single().Url })
                && !window.OwnedLlamaServerRunning && Find<CheckBox>(window, "AcquisitionApprovalCheck").IsChecked != true,
                "An explicit tool-source action opens only its recorded public source without approving a download or starting an engine");

            grid.SelectedItem = videoRow;
            window.ReviewSelectedDiscoveryItem();
            check(Find<TabItem>(window, "DiscoveryTab").IsSelected
                && Find<Expander>(window, "DiscoveryDetailsExpander").IsExpanded
                && details.Text.Contains(video.Sources.Single().Url.AbsoluteUri)
                && !Find<TabItem>(window, "GuidedSetupTab").IsSelected
                && !Find<Button>(window, "DiscoveryPlanButton").IsEnabled
                && window.GuidedModelPath is null && !window.OwnedLlamaServerRunning,
                "Video repository inspection exposes exact source details without opening text-model setup, granting a download plan or launching an engine");

            fitOnly.IsChecked = true;
            check(grid.Items.Count == 0 && grid.SelectedIndex == -1 && !sourceButton.IsEnabled,
                "The explicitly requested estimated-fit filter excludes unassessed media without converting it into a recommendation");
            fitOnly.IsChecked = false;
            check(grid.Items.Count == 3 && grid.SelectedIndex == -1,
                "Clearing the fit-only filter restores all media alternatives with no automatic model choice");

            grid.SelectedItem = Row(grid, image.Id);
            window.ReviewSelectedDiscoveryItem();
            check(Find<TabItem>(window, "ImageArtifactSetupTab").IsSelected
                && Find<TextBlock>(window, "ImageArtifactSetupTitleText").Text == image.Name
                && Find<TextBox>(window, "ImageArtifactDestinationText").Text == ""
                && Find<CheckBox>(window, "ImageArtifactApprovalCheck").IsChecked != true
                && !Find<Button>(window, "ImageArtifactDownloadButton").IsEnabled
                && !window.OwnedLlamaServerRunning,
                "Explicit image-file review still opens its supported acquisition path with no destination, approval, transfer or engine launch");

            var invalidSources = new[]
            {
                new Uri("http://github.com/Fixture/VideoEngine"),
                new Uri("https://user:password@github.com/Fixture/VideoEngine"),
                new Uri("https://github.com/Fixture/VideoEngine?token=fixture"),
                new Uri("https://github.com/Fixture/VideoEngine#fragment"),
                new Uri("https://localhost/Fixture/VideoEngine")
            };
            foreach (var uri in invalidSources)
            {
                var invalidTool = tool with { Sources = [new("Unsafe synthetic source", uri, now)] };
                window.RenderDiscoveryReport(DiscoveryPipeline.Evaluate(original with { Items = [invalidTool] }, machine, "C:\\"));
                grid.SelectedIndex = 0;
                window.OpenSelectedDiscoverySource();
                check(!sourceButton.IsEnabled && openedSources.Count == 1,
                    "Source opening rejects insecure, credential-bearing, query-bearing or unsupported origins without following them");
            }
        }
        finally { window.Close(); }
    }

    private static object Row(DataGrid grid, string id) => grid.Items.Cast<object>().Single(row =>
        Value<CompatibilityRecommendation>(row, "Recommendation").Item.Id == id);
    private static T Value<T>(object row, string name) => (T)row.GetType().GetProperty(name)!.GetValue(row)!;
    private static T Find<T>(MainWindow window, string name) where T : class => (T)window.FindName(name);
}
