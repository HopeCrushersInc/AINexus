using System.IO;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Data;
using AINexus.App;

namespace AINexus.Checks;

internal static class WorkloadSelectionRelevanceChecks
{
    internal static void RunNative(Action<bool, string> check) => WorkloadDiscoveryChecks.Pump(async () =>
    {
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", "grouped-tasks-" + Guid.NewGuid().ToString("N"));
        var window = new MainWindow { GuidedPersistPreferences = false };
        try
        {
            var connections = (ComboBox)window.FindName("AgentRuntimeSelector");
            var models = (ComboBox)window.FindName("AgentModelSelector");
            check(connections.Items.Count == 0 && connections.SelectedIndex == -1 && models.Items.Count == 0
                && !((Button)window.FindName("AgentRefreshModelsButton")).IsEnabled && !((Button)window.FindName("AgentEnqueueButton")).IsEnabled,
                "Model tasks start with no advertised assistant engines, default connection or default model");
            window.SetAgentModelCatalog(LocalGenerationRuntime.LlamaCpp, new Uri("http://127.0.0.1:8080"), ["source-reported-model"]);
            check(connections.Items.Count == 1 && connections.SelectedIndex == -1 && models.Items.Count == 0,
                "A queried local engine is offered as a connection only and never selected as an assistant");
            connections.SelectedIndex = 0;
            check(models.Items.Cast<string>().SequenceEqual(new[] { "source-reported-model" }) && models.SelectedIndex == -1,
                "Choosing a queried connection exposes only its actual reported model IDs without selecting one");
            models.SelectedIndex = 0;
            check(((Button)window.FindName("AgentEnqueueButton")).IsEnabled, "Explicit connection and reported-model choices are both required to enable a task");
            connections.SelectedIndex = -1;
            check(models.Items.Count == 0 && !((Button)window.FindName("AgentEnqueueButton")).IsEnabled,
                "Clearing a connection cannot fall back to llama.cpp or another backend");

            var machine = DiscoveryPipelineChecks.FixtureMachine();
            var provider = new Provider();
            typeof(MainWindow).GetField("_discoveryPipeline", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(window, new DiscoveryPipeline(provider, new DiscoveryCacheStore(root)));
            window.DiscoveryHardwareOverride = _ => Task.FromResult(machine);
            window.CompleteWorkloadScan(machine);
            ((ComboBox)window.FindName("DiscoveryVolumeSelector")).SelectedItem = machine.Volumes.Single(volume => volume.Path == "C:\\");
            var choices = ((IEnumerable<WorkloadChoice>)((ItemsControl)window.FindName("WorkloadChoices")).ItemsSource).ToArray();
            var cad = choices.Single(choice => choice.Interest == DiscoveryInterest.Cad);
            var printing = choices.Single(choice => choice.Interest == DiscoveryInterest.ThreeDPrinting);
            var groups = CollectionViewSource.GetDefaultView(((ItemsControl)window.FindName("WorkloadChoices")).ItemsSource).Groups!.Cast<CollectionViewGroup>().ToArray();
            check(groups.Length == 4 && cad.Group == "CAD & 3D printing" && printing.Group == cad.Group
                && cad.Interests.SequenceEqual(new[] { DiscoveryInterest.Cad })
                && printing.Interests.SequenceEqual(new[] { DiscoveryInterest.ThreeDPrinting }) && choices.All(choice => !choice.Selected),
                "Four parent headings organize independent unchecked tasks; CAD and printing share a parent without selecting each other");
            cad.Selected = true;
            await window.ConfirmWorkloadSelectionAsync();
            check(provider.Requests.Select(request => request.Interest).SequenceEqual(new[] { DiscoveryInterest.Cad }),
                "Choosing CAD alone searches CAD without silently adding printing, image or model sources");
            window.ApplyWorkloadSelection([DiscoveryInterest.Cad, DiscoveryInterest.ThreeDPrinting]);
            provider.Requests.Clear();
            await window.FindSelectedWorkloadsAsync();
            check(provider.Requests.Select(request => request.Interest).SequenceEqual(new[] { DiscoveryInterest.Cad, DiscoveryInterest.ThreeDPrinting }),
                "Choosing both CAD and printing searches both distinct task roles and nothing unrelated");
            var selector = (ComboBox)window.FindName("DiscoveryInterestSelector");
            check(selector.Items.Count == 2 && ((ItemsControl)window.FindName("WorkspaceWorkloadCards")).Items.Count == 2,
                "CAD and printing keep independent search filters and task cards inside their shared parent group");
            selector.SelectedItem = selector.Items.Cast<DiscoveryWorkflow>().Single(workflow => workflow.Interest == DiscoveryInterest.ThreeDPrinting);
            provider.Requests.Clear();
            var scope = typeof(MainWindow).GetMethod("RunDiscoverySearchAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await (Task)scope.Invoke(window, new object?[] { true, null })!;
            check(provider.Requests.Select(request => request.Interest).SequenceEqual(new[] { DiscoveryInterest.ThreeDPrinting }),
                "Selecting the printing subcategory never polls CAD design sources");
            provider.Requests.Clear();
            await (Task)scope.Invoke(window, new object?[] { false, null })!;
            check(provider.Requests.Count == 0 && ((DataGrid)window.FindName("DiscoveryResultsGrid")).Items.Count == 1,
                "A saved printing search stays offline and does not mix CAD or other task roles");
        }
        finally { window.Close(); if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    });

    private sealed class Provider : IOnlineDiscoveryProvider
    {
        internal List<OnlineDiscoveryRequest> Requests { get; } = [];
        public Task<OnlineDiscoverySnapshot> SearchAsync(OnlineDiscoveryRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var name = "Fixture/" + request.Interest;
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new OnlineDiscoverySnapshot([new("github-tool:" + name, name, "Fixture", request.Interest,
                "Tool", "MIT", null, [new("Fixture source", new Uri("https://github.com/" + name), now)], "Fixture tool metadata, not an installed application.")], [], now));
        }
    }
}
