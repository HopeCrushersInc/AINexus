using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using AINexus.App;

namespace AINexus.Checks;

internal static class WorkloadSetupChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var machine = DiscoveryPipelineChecks.FixtureMachine();
        var scanRequired = false;
        try { WorkspaceProfile.Create(null, [DiscoveryInterest.Images]); } catch (InvalidOperationException) { scanRequired = true; }
        check(scanRequired, "Workload selection requires captured hardware evidence first");
        var selectionRequired = false;
        try { WorkspaceProfile.Create(machine, []); } catch (ArgumentException) { selectionRequired = true; }
        check(selectionRequired, "An empty workload choice cannot become a default text/image workspace");
        var invalidRejected = false;
        try { WorkspaceProfile.Create(machine, [(DiscoveryInterest)999]); } catch (ArgumentException) { invalidRejected = true; }
        check(invalidRejected, "Undefined workloads cannot create workspace functionality");
        var imageOnly = WorkspaceProfile.Create(machine, [DiscoveryInterest.Images, DiscoveryInterest.Images]);
        check(imageOnly.HasImages && !imageOnly.HasAssistant && imageOnly.Interests.Count == 1,
            "Image-only choice does not silently add text work or duplicate interests");
        check(!WorkspaceProfile.Create(machine, [DiscoveryInterest.Cad]).HasImages
            && !WorkspaceProfile.Create(machine, [DiscoveryInterest.MusicAudio]).HasAssistant,
            "Unsupported workload choices do not masquerade as existing image or text adapters");
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(root, "settings.json");
            var store = new PortalSettingsStore(path);
            var settings = PortalSettings.Default with { ComfyUIRoot = Path.Combine(root, "portable-engine") };
            await store.SaveAsync(settings);
            using (var saved = JsonDocument.Parse(await File.ReadAllTextAsync(path)))
                check(!saved.RootElement.TryGetProperty("SelectedWorkloads", out _),
                    "Workload choices are not written to preferences");
            var legacy = JsonSerializer.SerializeToNode(settings)!;
            legacy["SelectedWorkloads"] = new JsonArray((int)DiscoveryInterest.Images, (int)DiscoveryInterest.Coding, 999);
            await File.WriteAllTextAsync(path, legacy.ToJsonString());
            var restored = await store.LoadAsync();
            check(restored.Warning is null && restored.Settings.ComfyUIRoot == settings.ComfyUIRoot,
                "Legacy saved selections are ignored without discarding unrelated configuration");
            await store.SaveAsync(restored.Settings);
            using (var migrated = JsonDocument.Parse(await File.ReadAllTextAsync(path)))
                check(!migrated.RootElement.TryGetProperty("SelectedWorkloads", out _),
                    "Saving preferences cannot restore legacy workload selections");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    public static void RunNative(MainWindow window, Action<bool, string> check)
    {
        var setup = (TabItem)window.FindName("WorkloadSetupTab");
        var navigation = (StackPanel)window.FindName("StoreNavigation");
        var proceed = (Button)window.FindName("WorkloadContinueButton");
        check(setup.IsSelected && navigation.Visibility == Visibility.Collapsed && !proceed.IsEnabled,
            "Native startup shows hardware setup before exposing a configured workspace");
        ((TabItem)window.FindName("ImageStudioTab")).IsSelected = true;
        check(setup.IsSelected && !((Button)window.FindName("ImageStopButton")).IsEnabled && !window.OwnedLlamaServerRunning,
            "Direct navigation before setup cannot bypass hardware/choice gates or start model engines");
        var noScanRejected = false;
        try { window.ApplyWorkloadSelection([DiscoveryInterest.Images]); } catch (InvalidOperationException) { noScanRejected = true; }
        check(noScanRejected && setup.IsSelected, "Native workspace building cannot bypass the hardware scan");
        window.CompleteWorkloadScan(DiscoveryPipelineChecks.FixtureMachine());
        var choices = ((IEnumerable<WorkloadChoice>)((ItemsControl)window.FindName("WorkloadChoices")).ItemsSource).ToArray();
        check(window.FindName("WorkloadOptionsPanel") is StackPanel { Visibility: Visibility.Visible }
            && new[] { DiscoveryInterest.Coding, DiscoveryInterest.Images, DiscoveryInterest.MusicAudio }
                .All(interest => choices.Any(choice => choice.Interest == interest && !choice.Selected)),
            "Coding, art and music remain visible in the expanded workload chooser after the hardware scan");
        check(choices.Length == Enum.GetValues<DiscoveryInterest>().Length && choices.All(c => !c.Selected) && !proceed.IsEnabled,
            "The post-scan chooser lists all interests with no first-use workload selected automatically");
        choices.Single(c => c.Interest == DiscoveryInterest.Images).Selected = true;
        check(proceed.IsEnabled && setup.IsSelected && navigation.Visibility == Visibility.Collapsed,
            "Checking an interest enables confirmation but does not build a workspace or launch software");
        window.CompleteWorkloadScan(DiscoveryPipelineChecks.FixtureMachine());
        check(((IEnumerable<WorkloadChoice>)((ItemsControl)window.FindName("WorkloadChoices")).ItemsSource).All(choice => !choice.Selected)
            && !proceed.IsEnabled,
            "Rebuilding the chooser clears every previous choice and requires a fresh selection");
        window.ApplyWorkloadSelection([DiscoveryInterest.Images]);
        check(((TabItem)window.FindName("DiscoveryTab")).IsSelected && navigation.Visibility == Visibility.Visible
            && ((RadioButton)window.FindName("ImageWorkspaceNav")).Visibility == Visibility.Visible
            && ((RadioButton)window.FindName("AssistantWorkspaceNav")).Visibility == Visibility.Collapsed,
            "Confirmed image-only choice opens scoped online discovery without adding an assistant");
        var cards = (IEnumerable<WorkloadWorkspaceCard>)((ItemsControl)window.FindName("WorkspaceWorkloadCards")).ItemsSource;
        check(cards.Single().Interest == DiscoveryInterest.Images && !((Button)window.FindName("ImageGenerateButton")).IsEnabled,
            "A selected image workspace still requires actual engine/model configuration before generation");
        window.ApplyWorkloadSelection([DiscoveryInterest.Coding]);
        ((TabItem)window.FindName("ImageStudioTab")).IsSelected = true;
        check(((TabItem)window.FindName("StoreHomeTab")).IsSelected
            && ((RadioButton)window.FindName("ImageWorkspaceNav")).Visibility == Visibility.Collapsed,
            "Unselected image tools cannot be opened through direct tab navigation");
        var interests = ((IEnumerable<DiscoveryWorkflow>)((ComboBox)window.FindName("DiscoveryInterestSelector")).ItemsSource).ToArray();
        check(interests.Length == 1 && interests[0].Interest == DiscoveryInterest.Coding,
            "Compatible-options discovery is scoped to the user's confirmed interests");
        window.ApplyWorkloadSelection([DiscoveryInterest.Cad]);
        cards = (IEnumerable<WorkloadWorkspaceCard>)((ItemsControl)window.FindName("WorkspaceWorkloadCards")).ItemsSource;
        check(cards.Single().Available && cards.Single().TargetPage == "DiscoveryTab"
            && ((TabItem)window.FindName("DiscoveryTab")).IsSelected
            && ((RadioButton)window.FindName("ImageWorkspaceNav")).Visibility == Visibility.Collapsed
            && ((RadioButton)window.FindName("AssistantWorkspaceNav")).Visibility == Visibility.Collapsed,
            "A category without an installation adapter still opens online metadata discovery without fake launch actions");
        // Existing feature regression checks explicitly opt into these workspaces.
        window.ApplyWorkloadSelection([DiscoveryInterest.LocalAssistants, DiscoveryInterest.Images]);
        check(((ComboBox)window.FindName("DiscoveryInterestSelector")).SelectedIndex == -1
            && ((TabItem)window.FindName("DiscoveryTab")).IsSelected,
            "Multiple chosen workloads leave the discovery selector unselected until the user chooses a category");
    }
}
