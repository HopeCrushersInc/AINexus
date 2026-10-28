using System.IO;
using System.Windows;
using System.Windows.Controls;
using AINexus.App;

namespace AINexus.Checks;

internal static class TextSearchUiChecks
{
    internal static void Run(Action<bool, string> check)
    {
        const long GiB = 1_073_741_824;
        var destination = Path.Combine(AppContext.BaseDirectory, "fixtures", "text-search-ui-" + Guid.NewGuid().ToString("N"));
        var window = new MainWindow { GuidedPersistPreferences = false };
        try
        {
            var order = Find<ComboBox>(window, "DiscoveryOrderSelector");
            check(order.SelectedIndex == -1
                && order.Items.Cast<MainWindow.TextSearchChoice>().Select(choice => choice.Order)
                    .SequenceEqual(Enum.GetValues<TextSearchOrder>()),
                "Text search offers broad, popular, trending and recent source orders without a remembered or preselected choice");

            var original = AcquisitionPlanChecks.FixtureReport(destination);
            var model = original.Catalog.Items.Single();
            var updated = DateTimeOffset.Parse("2026-10-20T23:15:00Z");
            var known = model with
            {
                Id = "larger-known-signals", Name = "Synthetic larger source model",
                Artifact = model.Artifact! with { SizeBytes = 2 * GiB },
                Signals = new(123456, 321, updated, 32762123264)
            };
            var unknown = model with
            {
                Id = "smaller-unknown-signals", Name = "Synthetic smaller source model",
                Artifact = model.Artifact! with { SizeBytes = GiB }, Signals = new(null, null, null)
            };
            var large = model with
            {
                Id = "large-unmet-memory", Name = "Synthetic large source model",
                Artifact = model.Artifact! with { SizeBytes = 101 * GiB }, Signals = null
            };
            var report = DiscoveryPipeline.Evaluate(original.Catalog with { Items = [known, unknown, large] },
                original.Machine, original.TargetVolume);
            var grid = Find<DataGrid>(window, "DiscoveryResultsGrid");
            window.RenderDiscoveryReport(report);
            check(grid.Items.Count == 3 && grid.SelectedIndex == -1
                && RowValue<string>(grid.Items[0], "Name") == known.Name
                && RowValue<string>(grid.Items[1], "Name") == unknown.Name,
                "Text discovery preserves source order within equal assessments and keeps larger or unmet model alternatives visible without selecting them");
            var knownRow = grid.Items[0];
            check(RowValue<string>(knownRow, "DownloadsDisplay") == 123456L.ToString("N0")
                && RowValue<string>(knownRow, "UpdatedDisplay") == "2026-10-20"
                && RowValue<string>(knownRow, "ParametersDisplay") == $"{32.762123264:N2}B (source)",
                "Text result columns show source downloads, UTC repository-update date and genuine source-reported parameters separately");
            var unknownRow = grid.Items[1];
            check(RowValue<string>(unknownRow, "DownloadsDisplay") == "Unknown"
                && RowValue<string>(unknownRow, "UpdatedDisplay") == "Unknown"
                && RowValue<string>(unknownRow, "ParametersDisplay") == "Unknown"
                && RowValue<string>(grid.Items[2], "ParametersDisplay") == "Unknown",
                "Missing source signals remain unknown rather than becoming zero popularity, invented release dates or filename-based parameter counts");

            grid.SelectedItem = knownRow;
            Find<TextBox>(window, "DiscoveryDestinationText").Text = destination;
            Find<Button>(window, "DiscoveryPlanButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var approval = Find<CheckBox>(window, "AcquisitionApprovalCheck");
            approval.IsChecked = true;
            check(Find<Button>(window, "AcquisitionDownloadButton").IsEnabled && !Directory.Exists(destination),
                "Native text-search fixture prepares an explicitly approved exact review without downloading or creating the destination");
            order.SelectedItem = order.Items.Cast<MainWindow.TextSearchChoice>().Single(choice => choice.Order == TextSearchOrder.RecentlyUpdated);
            check(grid.Items.Count == 0 && grid.SelectedIndex == -1 && approval.IsChecked != true
                && !Find<Button>(window, "AcquisitionDownloadButton").IsEnabled
                && !Find<Button>(window, "DiscoveryBrowseVariantsButton").IsEnabled
                && Find<TextBlock>(window, "DiscoveryFitSummaryText").Text.Contains("Search order changed"),
                "Changing source order invalidates prior results, model choice and acquisition approval until a fresh explicit search and review");
            check(!window.OwnedLlamaServerRunning && window.GuidedModelPath is null && !Directory.Exists(destination),
                "Changing text-search ordering starts no engine, chooses no runtime model and creates no model destination");
        }
        finally { window.Close(); }
    }

    internal static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", "text-search-cache-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new DiscoveryCacheStore(root);
            var popular = new OnlineDiscoveryRequest(DiscoveryInterest.LocalAssistants, "fixture", SearchOrder: TextSearchOrder.Popular);
            var trending = popular with { SearchOrder = TextSearchOrder.Trending };
            var original = DiscoveryPipelineChecks.FixtureCatalog();
            var source = original.Items.Single();
            var known = source with { Id = "popular-source", Signals = new(123, 4, DateTimeOffset.Parse("2026-10-20T00:00:00Z"), 32762123264) };
            var unknown = source with { Id = "trending-source", Signals = new(null, null, null) };
            await cache.SaveAsync(popular, original with { Items = [known] });
            check(await cache.LoadAsync(trending) is null,
                "A saved popular search cannot silently substitute for an uncached trending search with otherwise identical inputs");
            await cache.SaveAsync(trending, original with { Items = [unknown] });
            var popularSaved = await cache.LoadAsync(popular);
            var trendingSaved = await cache.LoadAsync(trending);
            check(Directory.GetFiles(root, "*.json").Length == 2
                && popularSaved is { IsCached: true } && trendingSaved is { IsCached: true }
                && popularSaved.Items.Single().Id == known.Id && popularSaved.Items.Single().Signals == known.Signals
                && trendingSaved.Items.Single().Id == unknown.Id,
                "Popular and trending search caches retain separate source identities and exact reported signal values");
            check(trendingSaved!.Items.Single().Signals is { Downloads: null, Likes: null, UpdatedAt: null, ParameterCount: null }
                && trendingSaved.RetrievedAt == original.RetrievedAt,
                "Cached unknown model signals stay unknown and preserve original retrieval time without inventing popularity, freshness or parameter facts");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private static T Find<T>(MainWindow window, string name) where T : class => (T)window.FindName(name);
    private static T RowValue<T>(object row, string name) => (T)row.GetType().GetProperty(name)!.GetValue(row)!;
}
