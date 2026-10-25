using System.IO;
using System.Text.Json;
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
            var settings = PortalSettings.Default with { SelectedWorkloads = [DiscoveryInterest.Images, DiscoveryInterest.Coding] };
            await store.SaveAsync(settings);
            check((await store.LoadAsync()).Settings.SelectedWorkloads.SequenceEqual(settings.SelectedWorkloads),
                "Saved workload choices survive settings reload without installing or starting anything");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(settings with { SelectedWorkloads = [(DiscoveryInterest)999] }));
            var invalid = await store.LoadAsync();
            check(invalid.Warning is not null && invalid.Settings.SelectedWorkloads.Count == 0,
                "Invalid saved interests are rejected rather than inventing configured workflows");
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
        check(choices.Length == Enum.GetValues<DiscoveryInterest>().Length && choices.All(c => !c.Selected) && !proceed.IsEnabled,
            "The post-scan chooser lists all interests with no first-use workload selected automatically");
        choices.Single(c => c.Interest == DiscoveryInterest.Images).Selected = true;
        check(proceed.IsEnabled && setup.IsSelected && navigation.Visibility == Visibility.Collapsed,
            "Checking an interest enables confirmation but does not build a workspace or launch software");
        window.ApplyWorkloadSelection([DiscoveryInterest.Images]);
        check(((TabItem)window.FindName("StoreHomeTab")).IsSelected && navigation.Visibility == Visibility.Visible
            && ((RadioButton)window.FindName("ImageWorkspaceNav")).Visibility == Visibility.Visible
            && ((RadioButton)window.FindName("AssistantWorkspaceNav")).Visibility == Visibility.Collapsed,
            "Confirmed image-only choice builds image navigation without adding an assistant");
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
        check(!cards.Single().Available && ((RadioButton)window.FindName("ImageWorkspaceNav")).Visibility == Visibility.Collapsed
            && ((RadioButton)window.FindName("AssistantWorkspaceNav")).Visibility == Visibility.Collapsed,
            "Unsupported interests show an honest unavailable adapter without fake launch actions");
        // Existing feature regression checks explicitly opt into these workspaces.
        window.ApplyWorkloadSelection([DiscoveryInterest.LocalAssistants, DiscoveryInterest.Images]);
    }
}
