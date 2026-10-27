using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AINexus.App;

namespace AINexus.Checks;

internal static class UserChoiceFlowChecks
{
    internal static void RunNative(Action<bool, string> check)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var window = new MainWindow { GuidedPersistPreferences = false };
        try
        {
            Pump(async () =>
            {
                var report = AcquisitionPlanChecks.FixtureReport(root, "GGUF user choice fixture"u8.ToArray());
                var item = report.Catalog.Items.Single() with { Interest = DiscoveryInterest.Coding };
                var other = item with { Id = "other-result", Name = "Another search result", Artifact = item.Artifact! with { FileName = "other.gguf" } };
                report = DiscoveryPipeline.Evaluate(report.Catalog with { Items = [item, other] }, report.Machine, report.TargetVolume);
                window.CompleteWorkloadScan(report.Machine);
                window.ApplyWorkloadSelection([DiscoveryInterest.Coding]);
                window.RenderDiscoveryReport(report);
                var grid = (DataGrid)window.FindName("DiscoveryResultsGrid");
                window.ReviewSelectedDiscoveryItem();
                check(grid.SelectedIndex == -1 && ((TabItem)window.FindName("DiscoveryTab")).IsSelected,
                    "No online result is chosen or opened for the user automatically");
                grid.SelectedIndex = 0; // Explicit simulated user selection.
                window.ReviewSelectedDiscoveryItem();
                check(((TabItem)window.FindName("GuidedSetupTab")).IsSelected
                    && ((TextBlock)window.FindName("GuidedModelTitleText")).Text == item.Name,
                    "An explicitly selected online model opens setup for that exact result without a preset-model requirement");
                var choices = (IEnumerable<DiscoveryWorkflow>)((ComboBox)window.FindName("DiscoveryInterestSelector")).ItemsSource;
                check(choices.Single().Interest == DiscoveryInterest.Coding,
                    "Reviewing an online coding result does not force the workspace into local assistants");
                ((TextBox)window.FindName("GuidedModelFolderText")).Text = root;
                var presetLookups = 0;
                window.GuidedLookupOverride = (_, _) => { presetLookups++; throw new InvalidOperationException("A user-selected result must not trigger a preset lookup."); };
                window.GuidedHardwareOverride = _ => Task.FromResult(report.Machine with { ScannedAt = DateTimeOffset.UtcNow });
                await window.ReviewGuidedModelAsync();
                check(presetLookups == 0 && ((TextBox)window.FindName("GuidedModelDetailsText")).Text.Contains(item.Artifact!.Sha256!)
                    && ((CheckBox)window.FindName("GuidedModelApprovalCheck")).IsChecked != true
                    && !((Button)window.FindName("GuidedDownloadButton")).IsEnabled && !window.OwnedLlamaServerRunning,
                    "User-selected online metadata reaches an exact review with no preset substitution, transfer approval or runtime launch");
            });
        }
        finally { window.Close(); Directory.Delete(root, recursive: true); }
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
            if (!task.IsCompleted) throw new TimeoutException("User-choice native checks exceeded their deadline.");
            task.GetAwaiter().GetResult();
        }
        finally { timer.Stop(); SynchronizationContext.SetSynchronizationContext(prior); }
    }
}
