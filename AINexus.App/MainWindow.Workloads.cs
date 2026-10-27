using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace AINexus.App;

public partial class MainWindow
{
    private MachineCapabilities? _workloadMachine;
    private WorkspaceProfile? _workspaceProfile;
    private bool _workloadScanBusy;
    private bool _workspaceConfigured;

    private void InitializeWorkloadSetup()
    {
        StoreNavigation.Visibility = Visibility.Collapsed;
        StoreHomeTab.IsSelected = false;
        WorkloadSetupTab.IsSelected = true;
        Loaded += async (_, _) => await RunWorkloadScanAsync();
    }

    private async Task RunWorkloadScanAsync()
    {
        if (_workloadScanBusy || _lifetime.IsCancellationRequested) return;
        _workloadScanBusy = true;
        _workloadMachine = null;
        RefreshGuidedControls();
        WorkloadOptionsPanel.Visibility = Visibility.Collapsed;
        WorkloadContinueButton.IsEnabled = false;
        WorkloadRescanButton.IsEnabled = false;
        WorkloadScanProgress.Visibility = Visibility.Visible;
        WorkloadScanStatusText.Text = "Reading your processor, memory, graphics, drivers and available storage...";
        try
        {
            var machine = await HardwareCapabilityScanner.ScanAsync(_lifetime.Token);
            await _preferencesReady.Task.WaitAsync(_lifetime.Token);
            if (!_lifetime.IsCancellationRequested) CompleteWorkloadScan(machine);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { WorkloadScanStatusText.Text = "The scan could not finish. Retry before choosing workloads. " + ex.Message; }
        finally
        {
            _workloadScanBusy = false;
            WorkloadScanProgress.Visibility = Visibility.Collapsed;
            WorkloadRescanButton.IsEnabled = true;
        }
    }

    internal void CompleteWorkloadScan(MachineCapabilities machine)
    {
        _workloadMachine = _discoveryMachine = machine;
        RenderHardwareSnapshot(machine.Hardware);
        var selectedRoot = _discoveryTargetVolume ?? Path.GetPathRoot(AppContext.BaseDirectory);
        _discoveryTargetVolume = selectedRoot;
        DiscoveryVolumeSelector.ItemsSource = machine.Volumes;
        DiscoveryVolumeSelector.SelectedItem = machine.Volumes.FirstOrDefault(v => string.Equals(v.Path, selectedRoot, StringComparison.OrdinalIgnoreCase));
        DiscoveryHardwareText.Text = $"{machine.Hardware.CpuName} | {machine.OsArchitecture} | {machine.Hardware.RamGb:N1} GiB RAM | scanned {machine.ScannedAt.ToLocalTime():HH:mm:ss}";
        var ram = machine.Hardware.RamGb > 0 ? $"{machine.Hardware.RamGb:N1} GiB RAM" : "RAM unavailable";
        WorkloadHardwareText.Text = $"{machine.Hardware.CpuName}\n{ram} · {machine.OsArchitecture}\n" +
            string.Join("\n", machine.Hardware.Gpus.Select(gpu => gpu.Name + (gpu.VramGb is { } vram ? $" · {vram:N1} GiB VRAM" : " · VRAM unavailable"))) +
            "\n" + string.Join(" · ", machine.Volumes.Select(volume => $"{volume.Path} {volume.AvailableBytes / 1073741824d:N1} GiB free"));
        WorkloadScanStatusText.Text = "Hardware scan finished. Choose what you want to do next. Missing hardware details remain unknown; this scan does not establish model compatibility.";
        var choices = VerifiedOnlineDiscoveryProvider.Workflows.Select(workflow => new WorkloadChoice(workflow)).ToArray();
        foreach (var choice in choices) choice.PropertyChanged += (_, _) => RefreshWorkloadContinue();
        WorkloadChoices.ItemsSource = choices;
        WorkloadOptionsPanel.Visibility = Visibility.Visible;
        RefreshWorkloadContinue();
        RefreshGuidedControls();
    }

    private void RefreshWorkloadContinue() => WorkloadContinueButton.IsEnabled = _workloadMachine is not null
        && WorkloadChoices.ItemsSource is IEnumerable<WorkloadChoice> choices && choices.Any(choice => choice.Selected);

    internal void ApplyWorkloadSelection(IEnumerable<DiscoveryInterest> interests)
    {
        var profile = WorkspaceProfile.Create(_workloadMachine, interests);
        _workspaceProfile = profile;
        _workspaceConfigured = true;
        ImageWorkspaceNav.Visibility = profile.HasImages ? Visibility.Visible : Visibility.Collapsed;
        AssistantWorkspaceNav.Visibility = profile.HasAssistant ? Visibility.Visible : Visibility.Collapsed;
        CreateWorkspaceHeading.Visibility = profile.HasImages || profile.HasAssistant ? Visibility.Visible : Visibility.Collapsed;
        var workflows = VerifiedOnlineDiscoveryProvider.Workflows.Where(w => profile.Interests.Contains(w.Interest)).ToArray();
        DiscoveryInterestSelector.ItemsSource = workflows;
        DiscoveryInterestSelector.SelectedIndex = 0;
        WorkspaceWorkloadCards.ItemsSource = workflows.Select(workflow => new WorkloadWorkspaceCard(workflow.Interest, workflow.Name,
            new WorkloadChoice(workflow).Description,
            workflow.Interest == DiscoveryInterest.Images ? "Configure an existing image engine" : VerifiedOnlineDiscoveryProvider.Supports(workflow.Interest) ? "Find compatible options" : "Adapter not available yet",
            workflow.Interest == DiscoveryInterest.Images ? "ImageStudioTab" : "DiscoveryTab",
            workflow.Interest == DiscoveryInterest.Images || VerifiedOnlineDiscoveryProvider.Supports(workflow.Interest))).ToArray();
        StoreNavigation.Visibility = Visibility.Visible;
        StoreSetupNoticeText.Text = "Workspace built from your choices. No software was downloaded, installed or started. Choose compatible tools and review each download before proceeding.";
        StoreHomeTab.IsSelected = true;
    }

    private async void WorkloadContinueButton_Click(object sender, RoutedEventArgs e)
    {
        if (_workloadMachine is null || WorkloadChoices.ItemsSource is not IEnumerable<WorkloadChoice> choices) return;
        try
        {
            var selected = WorkspaceProfile.Create(_workloadMachine, choices.Where(c => c.Selected).Select(c => c.Interest));
            await _preferencesReady.Task.WaitAsync(_lifetime.Token);
            ApplyWorkloadSelection(selected.Interests);
            await ScanSoftwareAsync();
            if (_settings.ModelRoots.Count > 0) await ScanLibraryAsync();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { WorkloadScanStatusText.Text = "Workspace selection could not be applied: " + ex.Message; }
    }

    private async void WorkloadRescanButton_Click(object sender, RoutedEventArgs e) => await RunWorkloadScanAsync();
    private async void ChangeWorkloadsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_imageBusy || _imageServer.IsRunning || _llamaServer.IsRunning || _llamaLifecycleBusy || _discoveryBusy || _packageBusy || _offlineBusy
            || _agentRows.Any(row => row.Status is "Queued" or "Checking resources" or "Running"))
        {
            StoreSetupNoticeText.Text = "Finish active tasks/downloads and stop owned engines before changing workloads. Your current workspace and running work have been preserved.";
            return;
        }
        _workspaceConfigured = false;
        StoreNavigation.Visibility = Visibility.Collapsed;
        WorkloadSetupTab.IsSelected = true;
        await RunWorkloadScanAsync();
    }

    private void WorkloadCardAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: WorkloadWorkspaceCard { Available: true } card })
        {
            DiscoveryInterestSelector.SelectedItem = (DiscoveryInterestSelector.ItemsSource as IEnumerable<DiscoveryWorkflow>)?.FirstOrDefault(w => w.Interest == card.Interest);
            if (FindName(card.TargetPage) is TabItem page) page.IsSelected = true;
        }
    }
}
