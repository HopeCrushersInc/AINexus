using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace AINexus.App;

public partial class MainWindow
{
    private readonly HttpClient _runtimeDownloadHttp = RuntimeDownloadService.CreateHttpClient();
    private RuntimeDownloadService? _runtimeDownloadService;
    private RuntimeDownloadPlan? _runtimeDownloadPlan;
    private RuntimeDownloadResult? _runtimeDownloadResult;
    private RuntimeFilePlan? _downloadedRuntimeFiles;
    private string? _runtimeDownloadApprovedPlanId;
    private string? _runtimeDownloadActivePlanId;
    private bool _runtimeDownloadBusy;
    private bool _closeAfterRuntimeDownload;

    private void InitializeRuntimeDownload()
    {
        _runtimeDownloadService = new(_runtimeDownloadHttp);
        Closed += (_, _) => _runtimeDownloadHttp.Dispose();
        Closing += (_, args) =>
        {
            if (args.Cancel || !_runtimeDownloadBusy) return;
            args.Cancel = true;
            _closeAfterRuntimeDownload = true;
            DiscoveryStatusText.Text = "Cancelling runtime download and cleaning its temporary file before closing...";
            _discoveryOperation?.Cancel();
        };
    }

    private void InvalidateRuntimeDownload()
    {
        _runtimeDownloadPlan = null;
        _runtimeDownloadResult = null;
        _downloadedRuntimeFiles = null;
        _runtimeDownloadApprovedPlanId = null;
        if (RuntimeDownloadApprovalCheck is null) return;
        RuntimeDownloadApprovalCheck.IsChecked = false;
        RuntimeDownloadApprovalCheck.IsEnabled = RuntimeDownloadButton.IsEnabled = RuntimeDownloadedInspectButton.IsEnabled = false;
        RuntimeDownloadProgressBar.Value = 0;
    }

    private void RefreshRuntimeDownloadControls()
    {
        if (RuntimeDownloadReviewButton is null) return;
        RuntimeDownloadDirectoryText.IsEnabled = RuntimeDownloadBrowseButton.IsEnabled = !_discoveryBusy;
        RuntimeDownloadReviewButton.IsEnabled = !_discoveryBusy && _lastDiscoveryReport is { Catalog.IsCached: false } report
            && _reviewedRuntimeFiles is { Files.Count: 1 } files && !string.IsNullOrWhiteSpace(RuntimeDownloadDirectoryText.Text)
            && RuntimeSetupPlanner.Assess(files.Files[0], files.Release, report.Machine) is { Backend: "cpu", Role: "Runtime archive", Status: RuntimeArchiveStatus.PlatformMatch };
        RuntimeDownloadApprovalCheck.IsEnabled = !_discoveryBusy && _runtimeDownloadPlan is not null;
        RuntimeDownloadButton.IsEnabled = !_discoveryBusy && _runtimeDownloadPlan is not null
            && RuntimeDownloadApprovalCheck.IsChecked == true && _runtimeDownloadApprovedPlanId == _runtimeDownloadPlan.PlanId;
        RuntimeDownloadCancelButton.IsEnabled = _runtimeDownloadBusy;
        RuntimeDownloadedInspectButton.IsEnabled = !_discoveryBusy && _runtimeDownloadResult is not null && _downloadedRuntimeFiles is not null;
    }

    private void RuntimeDownloadDirectoryChanged(object sender, TextChangedEventArgs e)
    {
        var hadPlan = _runtimeDownloadPlan is not null || _runtimeDownloadResult is not null;
        InvalidateRuntimeDownload();
        InvalidateRuntimeInstallation();
        if (hadPlan && DiscoveryDetailsText is not null)
        {
            DiscoveryDetailsText.Text = "Runtime download folder changed. Review the current files and folder again. Previously downloaded archives are preserved.";
            DiscoveryStatusText.Text = "Runtime download approval invalidated.";
        }
        RefreshRuntimeDownloadControls();
    }

    private void RuntimeDownloadBrowseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_discoveryBusy) return;
        var dialog = new OpenFolderDialog { Title = "Choose a local folder for the reviewed runtime ZIP", Multiselect = false };
        if (dialog.ShowDialog(this) == true) RuntimeDownloadDirectoryText.Text = dialog.FolderName;
    }

    private void RuntimeDownloadReviewButton_Click(object sender, RoutedEventArgs e)
    {
        if (_discoveryBusy || _lastDiscoveryReport is not { } report || _reviewedRuntimeFiles is not { } files) return;
        InvalidateRuntimeDownload();
        InvalidateRuntimeInstallation();
        try { RenderRuntimeDownloadPlan(RuntimeDownloadPlanner.Create(report, files, RuntimeDownloadDirectoryText.Text.Trim())); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { DiscoveryStatusText.Text = "Runtime download review could not complete: " + ex.Message; }
    }

    internal void RenderRuntimeDownloadPlan(RuntimeDownloadPlan plan)
    {
        _runtimeDownloadPlan = plan;
        _runtimeDownloadApprovedPlanId = null;
        _runtimeDownloadResult = null;
        _downloadedRuntimeFiles = null;
        RuntimeDownloadApprovalCheck.IsChecked = false;
        DownloadsTab.IsSelected = true;
        RuntimePackagesExpander.IsExpanded = true;
        RuntimeDownloadExpander.IsExpanded = true;
        ModelDownloadExpander.IsExpanded = false;
        DiscoveryDetailsText.Text = RuntimeDownloadPlanner.Describe(plan);
        DiscoveryStatusText.Text = "Runtime download reviewed. No network transfer, extraction or execution has been approved or performed.";
        RefreshRuntimeDownloadControls();
    }

    private void RuntimeDownloadApprovalChanged(object sender, RoutedEventArgs e)
    {
        _runtimeDownloadApprovedPlanId = RuntimeDownloadApprovalCheck.IsChecked == true ? _runtimeDownloadPlan?.PlanId : null;
        RefreshRuntimeDownloadControls();
    }

    private async void RuntimeDownloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_discoveryBusy || _runtimeDownloadService is null || _runtimeDownloadPlan is not { } plan
            || RuntimeDownloadApprovalCheck.IsChecked != true || _runtimeDownloadApprovedPlanId != plan.PlanId) return;
        var approval = new RuntimeDownloadApproval(plan.PlanId, true, true, true);
        _runtimeDownloadPlan = null;
        _runtimeDownloadApprovedPlanId = null;
        RuntimeDownloadApprovalCheck.IsChecked = false;
        _runtimeDownloadActivePlanId = plan.PlanId;
        _runtimeDownloadBusy = true;
        try
        {
            await RunDiscoveryOperationAsync(async token =>
            {
                var progress = new Progress<AcquisitionProgress>(value =>
                {
                    if (!_runtimeDownloadBusy || _runtimeDownloadActivePlanId != plan.PlanId || _lifetime.IsCancellationRequested) return;
                    RuntimeDownloadProgressBar.Value = value.ExpectedBytes > 0 ? Math.Clamp(100d * value.CompletedBytes / value.ExpectedBytes, 0, 100) : 0;
                    DiscoveryStatusText.Text = value.Stage + ": " + ByteSizeDisplay.Format(value.CompletedBytes) + " / " + ByteSizeDisplay.Format(value.ExpectedBytes);
                });
                var result = await _runtimeDownloadService.AcquireAsync(plan, approval, progress, token);
                _runtimeDownloadResult = result;
                _downloadedRuntimeFiles = plan.Files;
                RuntimeDownloadProgressBar.Value = 100;
                DiscoveryDetailsText.Text = RuntimeDownloadPlanner.Describe(plan) + $"\n\nARCHIVE BYTES VERIFIED\nFile: {result.ArchivePath}\nReceipt: {result.ReceiptPath}\nReused existing matching file: {result.ReusedExisting}\n\nNo archive was extracted or executable started. Inspect the archive before reviewing bundled terms and a separate installation.";
                DiscoveryStatusText.Text = "Runtime archive verified and receipt saved. Approval consumed. Inspection and installation remain separate actions.";
            });
        }
        finally
        {
            _runtimeDownloadBusy = false;
            _runtimeDownloadActivePlanId = null;
            if (!_lifetime.IsCancellationRequested) RefreshRuntimeDownloadControls();
            if (_closeAfterRuntimeDownload && !_lifetime.IsCancellationRequested)
            {
                _closeAfterRuntimeDownload = false;
                Close();
            }
        }
    }

    private void RuntimeDownloadCancelButton_Click(object sender, RoutedEventArgs e) => _discoveryOperation?.Cancel();

    private async void RuntimeDownloadedInspectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_discoveryBusy || _runtimeDownloadResult is not { } result || _downloadedRuntimeFiles is not { } files) return;
        InvalidateRuntimeInstallation();
        await RunDiscoveryOperationAsync(async token =>
        {
            DiscoveryStatusText.Text = "Inspecting the verified downloaded ZIP without extracting or running it...";
            var inspection = await RuntimeArchiveInspector.InspectAsync(files, result.ArchiveDirectory, token);
            RenderRuntimeArchiveInspection(inspection, result.ArchiveDirectory);
        });
    }

    private void RenderRuntimeArchiveInspection(RuntimeArchiveInspection inspection, string directory)
    {
        _runtimeInspection = inspection;
        _runtimeArchiveDirectory = directory;
        var text = new StringBuilder(RuntimeFilePlanner.Describe(inspection.Plan));
        text.AppendLine($"\nLOCAL ARCHIVE INSPECTION\nEntries: {inspection.Entries.Count}\nDeclared expanded bytes: {inspection.DeclaredExpandedBytes:N0}\nRoot server filename present: {inspection.HasServer}");
        foreach (var entry in inspection.Entries.Take(100)) text.AppendLine($"{entry.Archive} | {entry.RelativePath} | {entry.DeclaredBytes:N0} bytes");
        if (inspection.Entries.Count > 100) text.AppendLine("Only the first 100 entries are displayed; all entries were checked.");
        foreach (var limitation in inspection.Limitations) text.AppendLine("* " + limitation);
        DiscoveryDetailsText.Text = text.ToString();
        DiscoveryStatusText.Text = "Archive inspected. No extraction or execution occurred. Review the supported local installation separately.";
    }
}
