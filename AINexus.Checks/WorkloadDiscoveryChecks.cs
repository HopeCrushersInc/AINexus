using System.IO;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Threading;
using AINexus.App;

namespace AINexus.Checks;

internal static class WorkloadDiscoveryChecks
{
    internal static async Task RunLiveAsync(Action<bool, string> check)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var machine = await HardwareCapabilityScanner.ScanAsync(deadline.Token);
        var target = machine.Volumes.Single(volume => string.Equals(volume.Path,
            Path.GetPathRoot(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase));
        var reportFolder = Path.Combine(AppContext.BaseDirectory, "Reports", "Discovery");
        var cacheFolder = Path.Combine(reportFolder, "selected-workloads-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(reportFolder);
        using var http = VerifiedOnlineDiscoveryProvider.CreateHttpClient();
        var pipeline = new DiscoveryPipeline(new VerifiedOnlineDiscoveryProvider(http), new DiscoveryCacheStore(cacheFolder));
        var report = await pipeline.SearchWorkloadsOnlineAsync([DiscoveryInterest.Images, DiscoveryInterest.Video],
            machine, target.Path, token: deadline.Token);
        var reportPath = Path.Combine(reportFolder, "last-selected-workloads-live-report.json");
        await File.WriteAllTextAsync(reportPath, System.Text.Json.JsonSerializer.Serialize(report,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), deadline.Token);
        Console.WriteLine("Saved selected-workload live evidence: " + reportPath);
        foreach (var interest in new[] { DiscoveryInterest.Images, DiscoveryInterest.Video })
        {
            var items = report.Catalog.Items.Where(item => item.Interest == interest).ToArray();
            Console.WriteLine($"Live {interest}: {items.Length} results; sources: " +
                string.Join(", ", items.SelectMany(item => item.Sources).Select(source => source.Url.Host).Distinct()));
            check(items.Length > 0 && items.All(item => item.Kind == "Tool" && item.Sources.Count > 0),
                $"Live {interest} search returns actual source-backed tool metadata for the confirmed category");
        }
        check(ReferenceEquals(report.Machine, machine) && report.Machine.ScannedAt == machine.ScannedAt
            && report.TargetVolume == target.Path && !report.Catalog.IsCached,
            "Live multi-workload discovery retains the actual startup hardware scan and selected volume without claiming cached evidence is new");
        check(report.Recommendations.Count > 0 && report.Recommendations.All(result => result.Status == RecommendationStatus.NeedsDetails)
            && report.Catalog.Items.All(item => item.Artifact is null),
            "Live image/video tool search honestly reports missing requirements recipes instead of fabricating offline-model hardware fit");
        check(Directory.GetFiles(cacheFolder, "*", SearchOption.AllDirectories).All(path => Path.GetExtension(path) == ".json"),
            "Live selected-workload discovery writes metadata evidence only, without model-weight or installer downloads");
        foreach (var note in report.Catalog.Notes) Console.WriteLine("Live source limitation: " + note);
    }

    internal static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
        try
        {
            var machine = DiscoveryPipelineChecks.FixtureMachine();
            var provider = new WorkloadProvider();
            var pipeline = new DiscoveryPipeline(provider, new DiscoveryCacheStore(root));
            var selected = new[] { DiscoveryInterest.Images, DiscoveryInterest.Coding, DiscoveryInterest.Video, DiscoveryInterest.Images };
            var report = await pipeline.SearchWorkloadsOnlineAsync(selected, machine, "C:\\", "user query");
            check(provider.Requests.Count == 3 && provider.Requests.Select(r => r.Interest).Order().SequenceEqual(selected.Distinct().Order())
                && provider.Requests.All(r => r.Query == "user query" && r.RuntimeReleaseTag is null),
                "Confirmed workloads each initiate one source search, preserving the user's query without adding a default workload");
            check(ReferenceEquals(report.Machine, machine) && report.Machine.ScannedAt == machine.ScannedAt && report.TargetVolume == "C:\\",
                "Combined workload discovery evaluates against the supplied startup hardware snapshot and selected drive");
            check(report.Recommendations.Any(r => r.Item.Interest == DiscoveryInterest.Coding && r.Status == RecommendationStatus.ResourceCandidate)
                && report.Recommendations.Where(r => r.Item.Interest is DiscoveryInterest.Images or DiscoveryInterest.Video)
                    .All(r => r.Status == RecommendationStatus.NeedsDetails),
                "Image and video repository metadata cannot borrow a text-model memory rule or masquerade as hardware-fit models");
            check(report.Catalog.Items.Count == 3 && !report.Catalog.IsCached && report.Catalog.Items.All(i => i.Sources.Count > 0),
                "The combined search retains category-specific source evidence without fabricating installation or verification records");

            var blocked = machine with { Resources = machine.Resources with { RamUsedGb = machine.Resources.RamTotalGb, Gpus = [] } };
            var busyReport = await pipeline.SearchWorkloadsOnlineAsync([DiscoveryInterest.Coding], blocked, "C:\\");
            check(busyReport.Recommendations.Single().Status == RecommendationStatus.InsufficientResources,
                "Selected workload results reflect actual available memory instead of total installed capacity alone");
            var missingDrive = await pipeline.SearchWorkloadsOnlineAsync([DiscoveryInterest.Coding], machine, "Z:\\");
            check(missingDrive.Recommendations.Single().Status == RecommendationStatus.NeedsDetails,
                "Automatic workload searches cannot substitute another drive's capacity for the selected volume");

            provider.FailingInterest = DiscoveryInterest.Video;
            var partial = await pipeline.SearchWorkloadsOnlineAsync([DiscoveryInterest.Video, DiscoveryInterest.Coding], machine, "C:\\");
            check(partial.Recommendations.Any(r => r.Item.Interest == DiscoveryInterest.Coding)
                && partial.Catalog.Notes.Any(note => note.Contains("Video", StringComparison.OrdinalIgnoreCase)
                    && note.Contains("fixture source unavailable", StringComparison.Ordinal)),
                "A failed category is reported explicitly while another selected workload keeps its source-backed results");

            var calls = provider.Requests.Count;
            var invalidRejected = false;
            try { await pipeline.SearchWorkloadsOnlineAsync([], machine, "C:\\"); }
            catch (ArgumentException) { invalidRejected = true; }
            check(invalidRejected && provider.Requests.Count == calls,
                "No workload selection produces no source request and never falls back to a promoted model");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var cancelled = false;
            try { await pipeline.SearchWorkloadsOnlineAsync([DiscoveryInterest.Coding], machine, "C:\\", token: cancellation.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            check(cancelled && provider.Requests.Count == calls,
                "Cancelling automatic workload discovery prevents subsequent category searches and propagates cancellation");

            var invalidQueryRejected = false;
            try { await pipeline.SearchWorkloadsOnlineAsync([DiscoveryInterest.Coding], machine, "C:\\", new string('x', 161)); }
            catch (ArgumentException) { invalidQueryRejected = true; }
            check(invalidQueryRejected && provider.Requests.Count == calls,
                "An invalid combined search query is rejected before any category contacts a source");

            provider.SharedItemIdentity = true;
            var reviewMachine = AcquisitionPlanChecks.FixtureReport(root).Machine;
            var combined = await pipeline.SearchWorkloadsOnlineAsync([DiscoveryInterest.Coding, DiscoveryInterest.LocalAssistants],
                reviewMachine, Path.GetPathRoot(root)!);
            check(combined.Catalog.Items.Count == 2 && combined.Catalog.Items.Select(item => item.Id).Distinct().Count() == 2
                && combined.Catalog.Items.Select(item => item.Artifact).Distinct().Count() == 1,
                "The same source artifact appearing in two selected workloads retains distinct selectable category identities");
            var chosen = combined.Catalog.Items.Single(item => item.Interest == DiscoveryInterest.Coding);
            var destination = Path.Combine(root, "not-created-by-review");
            var plan = ModelAcquisitionPlanner.Create(combined, chosen.Id, destination);
            check(plan.Artifact == chosen.Artifact && !Directory.Exists(destination),
                "A repeated cross-workload model can be reviewed by its explicit selected identity without ambiguous lookup or destination writes");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    internal static void RunNative(MainWindow window, Action<bool, string> check)
    {
        var catalog = DiscoveryPipelineChecks.FixtureCatalog();
        var model = catalog.Items.Single();
        var unknown = model with { Id = "fixture/images", Interest = DiscoveryInterest.Images, Kind = "Tool", Artifact = null };
        var tooLarge = model with { Id = "fixture/large", Artifact = model.Artifact! with { SizeBytes = 100L * 1_073_741_824 } };
        var report = DiscoveryPipeline.Evaluate(catalog with { Items = [model, unknown, tooLarge] },
            DiscoveryPipelineChecks.FixtureMachine(), "C:\\");
        var toggle = (CheckBox)window.FindName("DiscoveryShowUnassessedCheck");
        toggle.IsChecked = false;
        window.RenderDiscoveryReport(report);
        var grid = (DataGrid)window.FindName("DiscoveryResultsGrid");
        check(grid.Items.Count == 1 && grid.SelectedIndex == -1,
            "Discovery initially presents only estimated hardware-fit results and does not choose a model for the user");
        toggle.IsChecked = true;
        check(grid.Items.Count == 3 && grid.SelectedIndex == -1,
            "The user can inspect unknown and insufficient-resource alternatives without approving or selecting them");
        toggle.IsChecked = false;
        check(grid.Items.Count == 1 && !window.OwnedLlamaServerRunning,
            "Returning to hardware-fit results neither launches an engine nor changes the user's workload choices");

        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
        var selectionWindow = new MainWindow { GuidedPersistPreferences = false };
        try
        {
            Pump(async () =>
            {
                var machine = DiscoveryPipelineChecks.FixtureMachine();
                var provider = new WorkloadProvider();
                typeof(MainWindow).GetField("_discoveryPipeline", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(selectionWindow, new DiscoveryPipeline(provider, new DiscoveryCacheStore(root)));
                selectionWindow.CompleteWorkloadScan(machine);
                ((ComboBox)selectionWindow.FindName("DiscoveryVolumeSelector")).SelectedIndex = 0;
                var chosenInterests = new[] { DiscoveryInterest.Images, DiscoveryInterest.Video, DiscoveryInterest.Coding };
                var choices = (IEnumerable<WorkloadChoice>)((ItemsControl)selectionWindow.FindName("WorkloadChoices")).ItemsSource;
                foreach (var choice in choices) choice.Selected = chosenInterests.Contains(choice.Interest);
                ((TextBox)selectionWindow.FindName("DiscoveryQueryText")).Text = "explicit search";
                check(provider.Requests.Count == 0, "Preparing the chooser does not contact online sources before workload confirmation");
                await selectionWindow.ConfirmWorkloadSelectionAsync();
                var results = (DataGrid)selectionWindow.FindName("DiscoveryResultsGrid");
                var actualReport = (DiscoveryReport)results.Items[0].GetType().GetProperty("Report")!.GetValue(results.Items[0])!;
                check(provider.Requests.Count == 3 && provider.Requests.All(r => r.Query == "explicit search")
                    && ReferenceEquals(actualReport.Machine, machine) && results.SelectedIndex == -1,
                    "The native confirmed-workload path searches all selected categories using startup evidence without requiring a category dropdown selection");
                check(!selectionWindow.OwnedLlamaServerRunning && ((CheckBox)selectionWindow.FindName("AcquisitionApprovalCheck")).IsChecked != true
                    && ((CheckBox)selectionWindow.FindName("GuidedModelApprovalCheck")).IsChecked != true,
                    "Automatic selected-workload searches grant no acquisition approval and start no text server");
            });
        }
        finally { selectionWindow.Close(); if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private static void Pump(Func<Task> action)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var prior = SynchronizationContext.Current;
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        timer.Tick += (_, _) => frame.Continue = false;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var task = action();
            _ = task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
            timer.Start();
            if (!task.IsCompleted) Dispatcher.PushFrame(frame);
            if (!task.IsCompleted) throw new TimeoutException("Selected-workload native checks exceeded their deadline.");
            task.GetAwaiter().GetResult();
        }
        finally { timer.Stop(); SynchronizationContext.SetSynchronizationContext(prior); }
    }

    private sealed class WorkloadProvider : IOnlineDiscoveryProvider
    {
        internal List<OnlineDiscoveryRequest> Requests { get; } = [];
        internal DiscoveryInterest? FailingInterest { get; set; }
        internal bool SharedItemIdentity { get; set; }
        public Task<OnlineDiscoverySnapshot> SearchAsync(OnlineDiscoveryRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            if (request.Interest == FailingInterest) throw new IOException("fixture source unavailable");
            var catalog = DiscoveryPipelineChecks.FixtureCatalog();
            if (SharedItemIdentity)
            {
                var now = DateTimeOffset.UtcNow;
                catalog = catalog with { RetrievedAt = now, Items = catalog.Items.Select(item => item with
                    { Sources = item.Sources.Select(source => source with { RetrievedAt = now }).ToArray() }).ToArray() };
            }
            var model = catalog.Items.Single();
            var item = model with { Id = SharedItemIdentity ? model.Id : "fixture/" + request.Interest, Interest = request.Interest };
            if (request.Interest is DiscoveryInterest.Images or DiscoveryInterest.Video)
                item = item with { Kind = "Tool", Artifact = null };
            return Task.FromResult(catalog with { Items = [item] });
        }
    }
}
