using System.IO;
using System.Reflection;
using System.Windows.Controls;
using AINexus.App;
using Microsoft.Data.Sqlite;

namespace AINexus.Checks;

internal static class CatalogDatabaseChecks
{
    internal static async Task RunLiveAsync(string directory, Action<bool, string> check)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var database = new CatalogDatabase(Path.Combine(directory, "catalog.db"));
        var machine = await HardwareCapabilityScanner.ScanAsync(deadline.Token);
        var target = machine.Volumes.Single(v => string.Equals(v.Path, Path.GetPathRoot(directory), StringComparison.OrdinalIgnoreCase));
        using var http = VerifiedOnlineDiscoveryProvider.CreateHttpClient();
        var pipeline = new DiscoveryPipeline(new VerifiedOnlineDiscoveryProvider(http),
            new DiscoveryCacheStore(Path.Combine(directory, "SourceCache")), database);
        var report = await pipeline.SearchWorkloadsOnlineAsync([DiscoveryInterest.Images, DiscoveryInterest.Video], machine, target.Path, token: deadline.Token);
        check(report.Catalog.Items.Any(i => i.Interest == DiscoveryInterest.Images) && report.Catalog.Items.Any(i => i.Interest == DiscoveryInterest.Video),
            "Live catalog refresh retrieves actual source evidence for both selected media categories");
        var reopened = await new CatalogDatabase(database.DatabasePath).ReadAsync(token: deadline.Token);
        var expected = report.Catalog.Items.Select(CatalogDatabase.CanonicalId).Distinct().ToArray();
        check(expected.Length > 0 && expected.All(id => reopened.Any(r => r.Id == id)),
            "Real online discoveries survive closing and reopening the SQLite catalog under canonical identities");
        check(reopened.Where(r => expected.Contains(r.Id)).All(r => r.LastCheckedAt != DateTimeOffset.MinValue && r.Item.Sources.Count > 0
            && (r.Item.Artifact is null || r.Interests.Contains(DiscoveryInterest.Images) && r.Item.Artifact.Format == "Safetensors")),
            "Live media listings retain source-backed tools and image artifacts without claiming installed weights or offline readiness");
        Console.WriteLine($"Live catalog: {reopened.Count} saved entries; {expected.Length} source identities from {report.Catalog.Items.Count} category results. Database: {database.DatabasePath}");
    }

    internal static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "catalog.db");
            var store = new CatalogDatabase(path);
            check((await store.ReadAsync()).Count == 0 && !File.Exists(path), "Reading an empty catalog does not create a database or contact a provider");
            var snapshot = DiscoveryPipelineChecks.FixtureCatalog();
            var model = snapshot.Items.Single();
            await store.UpsertDiscoveryAsync(snapshot with { Items = [model with { Id = "Coding:" + model.Id, Interest = DiscoveryInterest.Coding }] });
            await store.UpsertDiscoveryAsync(snapshot with { Items = [model with { Id = "Agents:" + model.Id, Interest = DiscoveryInterest.Agents }] });
            var saved = await new CatalogDatabase(path).ReadAsync();
            check(saved.Count == 1 && saved[0].Interests.Order().SequenceEqual(new[] { DiscoveryInterest.Coding, DiscoveryInterest.Agents }.Order())
                && saved[0].Id == CatalogDatabase.CanonicalId(model), "SQLite catalog survives reopening and merges categories without duplicating a pinned artifact");
            check(saved[0].Item.Artifact == model.Artifact && saved[0].LastCheckedAt == snapshot.RetrievedAt,
                "Persistent entries retain exact source identity and retrieval time without claiming a local installation");
            await store.UpsertDiscoveryAsync(snapshot with { RetrievedAt = snapshot.RetrievedAt.AddMinutes(-1), Items = [model with { Name = "stale title" }] });
            check((await store.ReadAsync()).Single().Item.Name == model.Name, "Older source metadata cannot overwrite newer catalog facts");
            var conflict = model with { Artifact = model.Artifact! with { Sha256 = new string('f', 64) } };
            var tool = new DiscoveredItem("ignored", "Atomic fixture tool", "Fixture", DiscoveryInterest.Images, "Tool", null, null,
                [new("Repository", new Uri("https://github.com/Fixture/Tool"), snapshot.RetrievedAt)], "Fixture source metadata only.");
            var rejected = false;
            try { await store.UpsertDiscoveryAsync(snapshot with { Items = [tool, conflict] }); }
            catch (InvalidDataException) { rejected = true; }
            check(rejected && (await store.ReadAsync()).Count == 1 && (await store.ReadAsync()).Single().Item.Artifact == model.Artifact,
                "Conflicting pinned hashes roll back the entire import, preserving both existing identity and catalog contents");
            check((await store.ReadAsync("' OR 1=1 --")).Count == 0 && (await store.ReadAsync(interest: DiscoveryInterest.Images)).Count == 0,
                "Catalog searches bind literal text and categories instead of executing source or user input");
            var cachedRejected = false;
            try { await store.UpsertDiscoveryAsync(snapshot with { IsCached = true }); } catch (InvalidDataException) { cachedRejected = true; }
            check(cachedRejected, "A saved search cannot become a new catalog freshness check");
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            var cancellationObserved = false;
            try { await store.UpsertDiscoveryAsync(snapshot, cancelled.Token); } catch (OperationCanceledException) { cancellationObserved = true; }
            check(cancellationObserved, "Cancelled catalog updates preserve existing committed data");
            var future = Path.Combine(root, "future.db");
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = future, Pooling = false }.ToString()))
            {
                connection.Open(); using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA user_version=99; CREATE TABLE retained(value TEXT); INSERT INTO retained VALUES('keep');";
                command.ExecuteNonQuery();
            }
            var original = await File.ReadAllBytesAsync(future);
            var futureRejected = false;
            try { await new CatalogDatabase(future).UpsertDiscoveryAsync(snapshot); } catch (InvalidDataException) { futureRejected = true; }
            check(futureRejected && original.SequenceEqual(await File.ReadAllBytesAsync(future)), "An unknown database schema is rejected without resetting or changing its bytes");
            var corrupt = Path.Combine(root, "corrupt.db");
            await File.WriteAllTextAsync(corrupt, "preserve this invalid database");
            var corruptBytes = await File.ReadAllBytesAsync(corrupt);
            try { await new CatalogDatabase(corrupt).UpsertDiscoveryAsync(snapshot); } catch (SqliteException) { }
            check(corruptBytes.SequenceEqual(await File.ReadAllBytesAsync(corrupt)), "Corrupt catalog files are preserved for recovery rather than replaced");
            var pipeline = new DiscoveryPipeline(new Provider(snapshot), new DiscoveryCacheStore(Path.Combine(root, "cache")), store);
            await pipeline.SearchOnlineAsync(new(DiscoveryInterest.Coding, "fixture"), DiscoveryPipelineChecks.FixtureMachine(), "C:\\");
            check((await store.ReadAsync()).Single().Id == CatalogDatabase.CanonicalId(model), "Successful source searches feed the persistent canonical catalog");
        }
        finally { Clean(root); }
    }

    internal static void RunNative(MainWindow window, Action<bool, string> check) =>
        WorkloadDiscoveryChecks.Pump(() => RunNativeAsync(window, check));

    private static async Task RunNativeAsync(MainWindow window, Action<bool, string> check)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var pipelineField = typeof(MainWindow).GetField("_discoveryPipeline", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var priorPipeline = pipelineField.GetValue(window);
        var settingsField = typeof(MainWindow).GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var priorSettings = (PortalSettings)settingsField.GetValue(window)!;
        try
        {
            var store = new CatalogDatabase(Path.Combine(root, "catalog.db"));
            await store.UpsertDiscoveryAsync(DiscoveryPipelineChecks.FixtureCatalog());
            var models = Path.Combine(root, "models");
            Directory.CreateDirectory(models);
            var bytes = "GGUF private receipt fixture"u8.ToArray();
            var report = AcquisitionPlanChecks.FixtureReport(models, bytes);
            var item = report.Catalog.Items.Single();
            item = item with { Artifact = item.Artifact! with { FileName = "local-only.gguf" } };
            report = DiscoveryPipeline.Evaluate(report.Catalog with { Items = [item] }, report.Machine, report.TargetVolume);
            var plan = ModelAcquisitionPlanner.Create(report, item.Id, models);
            await File.WriteAllBytesAsync(plan.DestinationPath, bytes);
            var integrity = await ModelArtifactVerifier.VerifyExistingAsync(plan);
            await ArtifactReceiptStore.SaveAsync(plan, new(integrity, true, DateTimeOffset.UtcNow));
            settingsField.SetValue(window, priorSettings with { ModelRoots = [models] });
            await window.ConfigureCatalogLibraryAsync(root);
            var grid = (DataGrid)window.FindName("CatalogLibraryGrid");
            check(grid.Items.Count == 2 && grid.SelectedIndex == -1, "The native Library combines saved catalog entries and historical local receipts without preselecting a model");
            check((await store.ReadAsync()).Count == 1, "Private receipt-derived Library entries are not exported into public-source catalog tables");
            ((CheckBox)window.FindName("CatalogInstalledOnlyCheck")).IsChecked = true;
            check(grid.Items.Count == 1 && ((MainWindow.CatalogLibraryRow)grid.Items[0]).LocalState.Contains("integrity not rechecked"), "The local-receipt filter excludes catalog-only listings and preserves honest integrity state");
            ((CheckBox)window.FindName("CatalogInstalledOnlyCheck")).IsChecked = false;
            ((TextBox)window.FindName("CatalogFilterText")).Text = "no such entry";
            check(grid.Items.Count == 0 && !window.OwnedLlamaServerRunning, "Local catalog filtering performs no launch or automatic model selection");
            ((TextBox)window.FindName("CatalogFilterText")).Clear();
            grid.SelectedIndex = 0;
            check(((TextBox)window.FindName("CatalogDetailsText")).Text.Contains("SHA-256")
                && ((CheckBox)window.FindName("AcquisitionApprovalCheck")).IsChecked != true,
                "Selecting saved catalog evidence shows exact source details without granting acquisition approval");
            var conflictingReport = AcquisitionPlanChecks.FixtureReport(models, bytes);
            var conflictingPlan = ModelAcquisitionPlanner.Create(conflictingReport, conflictingReport.Catalog.Items.Single().Id, models);
            await File.WriteAllBytesAsync(conflictingPlan.DestinationPath, bytes);
            var conflictingIntegrity = await ModelArtifactVerifier.VerifyExistingAsync(conflictingPlan);
            await ArtifactReceiptStore.SaveAsync(conflictingPlan, new(conflictingIntegrity, true, DateTimeOffset.UtcNow));
            await window.RefreshCatalogLibraryAsync();
            check(grid.Items.Cast<MainWindow.CatalogLibraryRow>().Any(row => row.LocalState.Contains("conflicts with saved source identity"))
                && (await store.ReadAsync()).Single().Item.Artifact!.SizeBytes == 1_073_741_824,
                "Conflicting private receipt/source evidence is labeled for review without replacing public artifact identity");
            var tab = (TabItem)window.FindName("CatalogLibraryTab");
            var panel = (System.Windows.FrameworkElement)tab.Content;
            tab.Content = null;
            var host = new System.Windows.Window { Width = 1250, Height = 800, Left = -10000, Top = -10000,
                ShowActivated = false, ShowInTaskbar = false, WindowStyle = System.Windows.WindowStyle.None,
                Resources = window.Resources, Content = panel, Background = window.Background,
                Foreground = window.Foreground, FontFamily = window.FontFamily, FontSize = window.FontSize };
            try
            {
                host.Show();
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                host.UpdateLayout();
                check(grid.Columns[0].ActualWidth > 80, "The visible native catalog gives model names a readable column width");
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(1250, 800, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(host);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                var reportRoot = Path.Combine(AppContext.BaseDirectory, "Reports", "Catalog");
                Directory.CreateDirectory(reportRoot);
                using var image = File.Create(Path.Combine(reportRoot, "library-fixture.png")); encoder.Save(image);
            }
            finally { host.Content = null; host.Close(); tab.Content = panel; }
        }
        finally
        {
            // Do not leave the shared native regression window attached to a deleted fixture.
            typeof(MainWindow).GetField("_catalogDatabase", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, null);
            pipelineField.SetValue(window, priorPipeline);
            settingsField.SetValue(window, priorSettings);
            Clean(root);
        }
    }

    private static void Clean(string root)
    {
        var path = Path.GetFullPath(root);
        if (!path.StartsWith(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "fixtures")) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Unsafe catalog fixture cleanup.");
        Directory.Delete(path, recursive: true);
    }
    private sealed class Provider(OnlineDiscoverySnapshot snapshot) : IOnlineDiscoveryProvider
    {
        public Task<OnlineDiscoverySnapshot> SearchAsync(OnlineDiscoveryRequest request, CancellationToken cancellationToken = default) => Task.FromResult(snapshot);
    }
}
