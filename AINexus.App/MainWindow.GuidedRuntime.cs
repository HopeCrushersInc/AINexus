using System.Windows;
using Microsoft.Win32;

namespace AINexus.App;

public partial class MainWindow
{
    private RuntimeDownloadPlan? _guidedRuntimeDownloadPlan;
    private RuntimeInstallationPlan? _guidedRuntimeInstallationPlan;
    private string? _guidedRuntimeTermsPlanId;
    private string? _guidedRuntimeDownloadApprovalId;
    private string? _guidedRuntimeInstallApprovalId;

    internal void InvalidateGuidedRuntimeReview()
    {
        _guidedRuntimeDownloadPlan = null;
        _guidedRuntimeInstallationPlan = null;
        _guidedRuntimeTermsPlanId = null;
        _guidedRuntimeDownloadApprovalId = null;
        _guidedRuntimeInstallApprovalId = null;
        if (GuidedRuntimeDownloadApprovalCheck is not null) GuidedRuntimeDownloadApprovalCheck.IsChecked = false;
        if (GuidedRuntimeInstallApprovalCheck is not null) GuidedRuntimeInstallApprovalCheck.IsChecked = false;
        if (GuidedRuntimeDetailsText is not null) GuidedRuntimeDetailsText.Text = "";
        RefreshGuidedRuntimeControls();
    }

    private void RefreshGuidedRuntimeControls()
    {
        if (GuidedRuntimeReviewButton is null || GuidedRuntimeDownloadApprovalCheck is null
            || GuidedRuntimeInstallApprovalCheck is null || GuidedRuntimeDownloadButton is null || GuidedRuntimeInstallButton is null) return;
        GuidedRuntimeReviewButton.IsEnabled = !_guidedBusy;
        GuidedRuntimeDownloadApprovalCheck.IsEnabled = !_guidedBusy && _guidedRuntimeDownloadPlan is not null;
        GuidedRuntimeDownloadButton.IsEnabled = !_guidedBusy && _guidedRuntimeDownloadPlan is { } download
            && GuidedRuntimeDownloadApprovalCheck.IsChecked == true && _guidedRuntimeDownloadApprovalId == download.PlanId;
        GuidedRuntimeInstallApprovalCheck.IsEnabled = !_guidedBusy && _guidedRuntimeInstallationPlan is { } installation
            && _guidedRuntimeTermsPlanId == installation.PlanId;
        GuidedRuntimeInstallButton.IsEnabled = !_guidedBusy && _guidedRuntimeInstallationPlan is { } install
            && _guidedRuntimeTermsPlanId == install.PlanId && GuidedRuntimeInstallApprovalCheck.IsChecked == true
            && _guidedRuntimeInstallApprovalId == install.PlanId;
    }

    private void GuidedRuntimeApproval_Changed(object sender, RoutedEventArgs e)
    {
        _guidedRuntimeDownloadApprovalId = GuidedRuntimeDownloadApprovalCheck?.IsChecked == true ? _guidedRuntimeDownloadPlan?.PlanId : null;
        _guidedRuntimeInstallApprovalId = GuidedRuntimeInstallApprovalCheck?.IsChecked == true ? _guidedRuntimeInstallationPlan?.PlanId : null;
        RefreshGuidedRuntimeControls();
    }

    private void GuidedRuntimeBrowse_Click(object sender, RoutedEventArgs e)
    {
        if (_guidedBusy) return;
        var picker = new OpenFileDialog
        {
            Title = "Select an existing llama.cpp server",
            Filter = "llama.cpp server (llama-server.exe)|llama-server.exe",
            CheckFileExists = true,
            Multiselect = false
        };
        if (picker.ShowDialog(this) == true) GuidedRuntimePathText.Text = picker.FileName;
    }

    private async void GuidedRuntimeReview_Click(object sender, RoutedEventArgs e)
    {
        if (_guidedBusy) return;
        var folder = GuidedModelFolderText.Text.Trim();
        InvalidateGuidedRuntimeReview();
        await RunGuidedOperationAsync(async token =>
        {
            GuidedRuntimeStatusText.Text = "Scanning current hardware and reviewing the explicit CPU runtime release. Nothing is downloaded or installed...";
            var machine = await HardwareCapabilityScanner.ScanAsync(token);
            var plan = await GuidedRuntimeSetup.ReviewDownloadAsync(new(_discoveryHttp), machine, folder, token);
            token.ThrowIfCancellationRequested();
            _guidedRuntimeDownloadPlan = plan;
            GuidedRuntimeDetailsText.Text = RuntimeDownloadPlanner.Describe(plan);
            GuidedRuntimeStatusText.Text = "CPU runtime download reviewed. Read its source, license and destination, then explicitly approve the ZIP transfer. This is a CPU fallback, not GPU setup.";
        });
    }

    private async void GuidedRuntimeDownload_Click(object sender, RoutedEventArgs e)
    {
        if (_guidedBusy || _guidedRuntimeDownloadPlan is not { } plan
            || _guidedRuntimeDownloadApprovalId != plan.PlanId || GuidedRuntimeDownloadApprovalCheck.IsChecked != true) return;
        var service = _runtimeDownloadService;
        if (service is null) { GuidedRuntimeStatusText.Text = "The runtime download service is unavailable. Reopen the portal before retrying."; return; }
        _guidedRuntimeDownloadPlan = null;
        _guidedRuntimeDownloadApprovalId = null;
        GuidedRuntimeDownloadApprovalCheck.IsChecked = false;
        await RunGuidedOperationAsync(async token =>
        {
            GuidedRuntimeStatusText.Text = "Downloading the approved CPU ZIP; no extraction or executable launch...";
            var progress = new Progress<AcquisitionProgress>(update =>
            {
                if (!_lifetime.IsCancellationRequested)
                    GuidedRuntimeStatusText.Text = $"{update.Stage}: {update.CompletedBytes / 1048576d:N1} / {update.ExpectedBytes / 1048576d:N1} MiB";
            });
            var result = await service.AcquireAsync(plan, new(plan.PlanId, true, true, true), progress, token);
            var installation = await GuidedRuntimeSetup.ReviewDownloadedAsync(plan, result, token);
            var terms = await RuntimeInstallationPlanner.ReadLicenseTextsAsync(installation, token);
            token.ThrowIfCancellationRequested();
            _guidedRuntimeInstallationPlan = installation;
            _guidedRuntimeTermsPlanId = installation.PlanId;
            GuidedRuntimeDetailsText.Text = "BUNDLED LICENSE TERMS — READ BEFORE ACCEPTING\n\n" + terms
                + "\n\n" + RuntimeInstallationPlanner.Describe(installation);
            GuidedRuntimeStatusText.Text = "ZIP bytes and contents inspected. Bundled terms are displayed; accept those terms, the declared source license and exact new-folder writes before installation. Nothing has been extracted or run.";
        });
    }

    private async void GuidedRuntimeInstall_Click(object sender, RoutedEventArgs e)
    {
        if (_guidedBusy || _guidedRuntimeInstallationPlan is not { } plan
            || _guidedRuntimeTermsPlanId != plan.PlanId || _guidedRuntimeInstallApprovalId != plan.PlanId
            || GuidedRuntimeInstallApprovalCheck.IsChecked != true) return;
        _guidedRuntimeInstallationPlan = null;
        _guidedRuntimeTermsPlanId = null;
        _guidedRuntimeInstallApprovalId = null;
        GuidedRuntimeInstallApprovalCheck.IsChecked = false;
        await RunGuidedOperationAsync(async token =>
        {
            GuidedRuntimeStatusText.Text = "Extracting the explicitly approved CPU runtime into its new portable folder. No executable will run...";
            var result = await RuntimeInstallationService.InstallAsync(plan, new(plan.PlanId, true, true, true), token);
            token.ThrowIfCancellationRequested();
            GuidedRuntimePathText.Text = result.ExecutablePath;
            GuidedRuntimeDetailsText.Text = $"PORTABLE CPU FILES INSTALLED — EXECUTION NOT VERIFIED\n\nRuntime: {result.ExecutablePath}\nReceipt: {result.ReceiptPath}\n\nChoose Start and verify with an acquired model. Installation alone is not verified model inference.";
            GuidedRuntimeStatusText.Text = "CPU runtime installed and selected. No server was started. The next explicit Start action verifies and opens chat.";
        });
    }
}
