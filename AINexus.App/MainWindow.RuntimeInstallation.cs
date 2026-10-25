using System.IO;
using System.Windows;

namespace AINexus.App;

public partial class MainWindow
{
    private RuntimeArchiveInspection? _runtimeInspection;
    private string? _runtimeArchiveDirectory;
    private RuntimeInstallationPlan? _runtimeInstallationPlan;
    private RuntimeInstallationResult? _runtimeInstallationResult;
    private string? _runtimeTermsReadPlanId;
    private bool _runtimeInstallationBusy;
    private bool _closeAfterRuntimeInstallation;

    private void InitializeRuntimeInstallation()
    {
        Closing += (_, args) =>
        {
            if (args.Cancel || !_runtimeInstallationBusy) return;
            args.Cancel = true;
            _closeAfterRuntimeInstallation = true;
            DiscoveryStatusText.Text = "Cancelling installation and finishing cleanup before closing...";
            _discoveryOperation?.Cancel();
        };
    }

    private void InvalidateRuntimeInstallation()
    {
        _runtimeInspection = null;
        _runtimeArchiveDirectory = null;
        _runtimeInstallationPlan = null;
        _runtimeInstallationResult = null;
        _runtimeTermsReadPlanId = null;
        if (RuntimeInstallApprovalPanel is null) return;
        RuntimeInstallApprovalPanel.Visibility = Visibility.Collapsed;
        RuntimeInstallLicenseCheck.IsChecked = RuntimeInstallBundledCheck.IsChecked = RuntimeInstallWritesCheck.IsChecked = false;
        RuntimeInstallButton.IsEnabled = RuntimeInstallReviewButton.IsEnabled = RuntimeUseInstalledButton.IsEnabled = false;
    }

    private void RefreshRuntimeInstallationControls()
    {
        if (RuntimeInstallReviewButton is null) return;
        RuntimeInstallReviewButton.IsEnabled = !_discoveryBusy && _runtimeInspection is not null && _runtimeArchiveDirectory is not null;
        RuntimeInstallLicenseCheck.IsEnabled = RuntimeInstallWritesCheck.IsEnabled = !_discoveryBusy && _runtimeInstallationPlan is not null;
        RuntimeInstallTermsButton.IsEnabled = !_discoveryBusy && _runtimeInstallationPlan is not null;
        RuntimeInstallBundledCheck.IsEnabled = !_discoveryBusy && _runtimeInstallationPlan is not null && _runtimeTermsReadPlanId == _runtimeInstallationPlan.PlanId;
        RuntimeInstallButton.IsEnabled = !_discoveryBusy && _runtimeInstallationPlan is not null
            && _runtimeTermsReadPlanId == _runtimeInstallationPlan.PlanId
            && RuntimeInstallLicenseCheck.IsChecked == true && RuntimeInstallBundledCheck.IsChecked == true && RuntimeInstallWritesCheck.IsChecked == true;
        RuntimeUseInstalledButton.IsEnabled = !_discoveryBusy && _runtimeInstallationResult is not null;
    }

    internal void RenderRuntimeInstallationPlan(RuntimeInstallationPlan plan)
    {
        _runtimeInstallationPlan = plan;
        _runtimeInstallationResult = null;
        _runtimeTermsReadPlanId = null;
        RuntimeInstallLicenseCheck.IsChecked = RuntimeInstallBundledCheck.IsChecked = RuntimeInstallWritesCheck.IsChecked = false;
        RuntimeInstallApprovalPanel.Visibility = Visibility.Visible;
        DownloadsTab.IsSelected = true;
        RuntimePackagesExpander.IsExpanded = true;
        ModelDownloadExpander.IsExpanded = false;
        DiscoveryDetailsExpander.IsExpanded = true;
        DiscoveryDetailsText.Text = RuntimeInstallationPlanner.Describe(plan);
        DiscoveryStatusText.Text = "Local installation reviewed. Accept the terms and exact folder writes before installing. Nothing was extracted or run.";
        RefreshRuntimeInstallationControls();
    }

    private async void RuntimeInstallReviewButton_Click(object sender, RoutedEventArgs e)
    {
        if (_discoveryBusy || _runtimeInspection is not { } inspection || _runtimeArchiveDirectory is not { } directory) return;
        _runtimeInstallationPlan = null;
        RuntimeInstallApprovalPanel.Visibility = Visibility.Collapsed;
        await RunDiscoveryOperationAsync(async token =>
        {
            DiscoveryStatusText.Text = "Rechecking the inspected local archive and new destination. Nothing is being installed...";
            var plan = await RuntimeInstallationPlanner.CreateAsync(inspection, directory, token);
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_runtimeInspection, inspection) || _runtimeArchiveDirectory != directory)
                throw new InvalidOperationException("The selected runtime changed. Inspect and review it again.");
            RenderRuntimeInstallationPlan(plan);
        });
    }

    private void RuntimeInstallationApprovalChanged(object sender, RoutedEventArgs e) => RefreshRuntimeInstallationControls();

    private async void RuntimeInstallTermsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_discoveryBusy || _runtimeInstallationPlan is not { } plan) return;
        _runtimeTermsReadPlanId = null;
        RuntimeInstallBundledCheck.IsChecked = false;
        await RunDiscoveryOperationAsync(async token =>
        {
            DiscoveryStatusText.Text = "Verifying archive identity and reading bounded bundled license text. No files are being installed...";
            var terms = await RuntimeInstallationPlanner.ReadLicenseTextsAsync(plan, token);
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_runtimeInstallationPlan, plan)) throw new InvalidOperationException("Installation inputs changed. Review the new plan and terms.");
            _runtimeTermsReadPlanId = plan.PlanId;
            DiscoveryDetailsText.Text = "BUNDLED LICENSE TERMS - REVIEW ONLY\n\n" + terms + "\n\n" + RuntimeInstallationPlanner.Describe(plan);
            DiscoveryDetailsExpander.IsExpanded = true;
            DiscoveryStatusText.Text = "Bundled terms are displayed for your review. Reading them grants no installation approval.";
        });
    }

    private async void RuntimeInstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (_discoveryBusy || _runtimeInstallationPlan is not { } plan
            || _runtimeTermsReadPlanId != plan.PlanId
            || RuntimeInstallLicenseCheck.IsChecked != true || RuntimeInstallBundledCheck.IsChecked != true || RuntimeInstallWritesCheck.IsChecked != true) return;
        var approval = new RuntimeInstallationApproval(plan.PlanId, true, true, true);
        _runtimeInstallationPlan = null;
        RuntimeInstallLicenseCheck.IsChecked = RuntimeInstallBundledCheck.IsChecked = RuntimeInstallWritesCheck.IsChecked = false;
        _runtimeInstallationBusy = true;
        try
        {
            await RunDiscoveryOperationAsync(async token =>
            {
                DiscoveryStatusText.Text = "Installing approved local files into a new portable folder. No executable will run...";
                RuntimeInstallationResult result;
                try { result = await RuntimeInstallationService.InstallAsync(plan, approval, token); }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    DiscoveryDetailsText.Text = "INSTALLATION DID NOT COMPLETE\n\n" + ex.Message
                        + (ex.InnerException is null ? "" : "\n" + ex.InnerException.Message)
                        + "\n\nThe reviewed destination was not reported as successfully installed. Existing runtimes were preserved.";
                    throw;
                }
                _runtimeInstallationResult = result;
                _runtimeInspection = null;
                _runtimeArchiveDirectory = null;
                DiscoveryDetailsText.Text = $"PORTABLE FILES INSTALLED — RUNTIME NOT VERIFIED\n\nFolder: {result.Destination}\nServer: {result.ExecutablePath}\nReceipt: {result.ReceiptPath}\n\nNo runtime was started or dependency installer run. CPU instruction support, dependencies, publisher authenticity and actual generation remain unverified.\n\nChoose Use installed runtime to select this executable, then explicitly run the offline smoke test with your chosen model.";
                DiscoveryStatusText.Text = "Portable files installed. Approval consumed; runtime execution remains unverified.";
            });
        }
        finally
        {
            _runtimeInstallationBusy = false;
            if (!_lifetime.IsCancellationRequested) RefreshRuntimeInstallationControls();
            if (_closeAfterRuntimeInstallation && !_lifetime.IsCancellationRequested)
            {
                _closeAfterRuntimeInstallation = false;
                Close();
            }
        }
    }

    private void RuntimeUseInstalledButton_Click(object sender, RoutedEventArgs e)
    {
        if (_discoveryBusy || _runtimeInstallationResult is not { } result) return;
        try
        {
            OfflineModelVerifier.CheckNoReparsePath(result.ExecutablePath);
            if (!File.Exists(result.ExecutablePath)) throw new FileNotFoundException("The installed executable is missing. Review the installation again.");
            LlamaCppExecutableText.Text = result.ExecutablePath;
            OfflineRuntimeText.Text = result.ExecutablePath;
            OfflineVerificationTab.IsSelected = true;
            OfflineStatusText.Text = "Installed runtime selected. Choose an acquisition receipt and explicitly run the offline smoke test; installation alone is not verification.";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            _runtimeInstallationResult = null;
            DiscoveryStatusText.Text = $"Installed runtime could not be selected: {ex.Message}";
            RefreshRuntimeInstallationControls();
        }
    }
}
