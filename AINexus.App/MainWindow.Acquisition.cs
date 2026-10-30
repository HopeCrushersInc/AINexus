using System.Text;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace AINexus.App;

public partial class MainWindow
{
    private readonly HttpClient _acquisitionHttp = ModelAcquisitionService.CreateHttpClient();
    private ModelAcquisitionService? _acquisitionService;
    private string? _approvedAcquisitionPlanId;
    private ModelAcquisitionResult? _lastAcquisitionResult;
    private bool _acquisitionInProgress;
    private bool _closeAfterAcquisition;

    private void InitializeAcquisition()
    {
        _acquisitionService = new(_acquisitionHttp);
        Closed += (_, _) => _acquisitionHttp.Dispose();
        Closing += (_, args) =>
        {
            if (args.Cancel || !_acquisitionInProgress) return;
            args.Cancel = true;
            _closeAfterAcquisition = true;
            DiscoveryStatusText.Text = "Cancelling acquisition and cleaning up its temporary file before closing...";
            _discoveryOperation?.Cancel();
        };
    }

    private void InvalidateAcquisitionReview()
    {
        _reviewedAcquisitionPlan = null;
        _approvedAcquisitionPlanId = null;
        _lastAcquisitionResult = null;
        if (AcquisitionApprovalCheck is not null) AcquisitionApprovalCheck.IsChecked = false;
        if (AcquisitionProgressBar is not null) AcquisitionProgressBar.Value = 0;
    }

    private void AcquisitionApprovalChanged(object sender, RoutedEventArgs e)
    {
        _approvedAcquisitionPlanId = AcquisitionApprovalCheck.IsChecked == true ? _reviewedAcquisitionPlan?.PlanId : null;
        if (AcquisitionDownloadButton is not null) RefreshDiscoveryControls();
    }
    private void DiscoveryDestinationText_TextChanged(object sender, TextChangedEventArgs e)
    {
        InvalidateAcquisitionReview();
        if (DiscoveryVerifyButton is not null) RefreshDiscoveryControls();
    }

    private void DiscoveryChooseFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_discoveryBusy) return;
        var dialog = new OpenFolderDialog { Title = "Choose a model folder on the assessed storage volume", Multiselect = false };
        if (dialog.ShowDialog(this) == true) DiscoveryDestinationText.Text = dialog.FolderName;
    }

    private void DiscoveryPlanButton_Click(object sender, RoutedEventArgs e)
    {
        if (_discoveryBusy || _lastDiscoveryReport is null || DiscoveryResultsGrid.SelectedItem is not DiscoveryRow row) return;
        try
        {
            InvalidateAcquisitionReview();
            var plan = ModelAcquisitionPlanner.Create(_lastDiscoveryReport, row.Recommendation.Item.Id, DiscoveryDestinationText.Text.Trim());
            _reviewedAcquisitionPlan = plan;
            DiscoveryDetailsText.Text = DescribeAcquisitionPlan(plan);
            DiscoveryStatusText.Text = "Plan prepared. Review its destination and license before approving. No download or Windows change has occurred.";
        }
        catch (Exception ex)
        {
            InvalidateAcquisitionReview();
            DiscoveryStatusText.Text = $"Download plan requires attention: {ex.Message}";
        }
        RefreshDiscoveryControls();
    }

    private async void AcquisitionDownloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_discoveryBusy || _acquisitionService is null || _reviewedAcquisitionPlan is not { } plan
            || _approvedAcquisitionPlanId != plan.PlanId || AcquisitionApprovalCheck.IsChecked != true) return;
        _acquisitionInProgress = true;
        _lastAcquisitionResult = null;
        try
        {
            await RunDiscoveryOperationAsync(async token =>
            {
                var progress = new Progress<AcquisitionProgress>(update =>
                {
                    if (_lifetime.IsCancellationRequested || !_acquisitionInProgress) return;
                    AcquisitionProgressBar.Value = update.ExpectedBytes > 0 ? Math.Clamp(update.CompletedBytes * 100d / update.ExpectedBytes, 0, 100) : 0;
                    DiscoveryStatusText.Text = $"{update.Stage}: {update.CompletedBytes / 1048576d:N1} / {update.ExpectedBytes / 1048576d:N1} MiB. Cancel stops this acquisition.";
                });
                _lastAcquisitionResult = await _acquisitionService.AcquireAsync(plan,
                    new(plan.PlanId, AcceptDeclaredLicense: true, ApproveDestinationWrites: true), progress, token);
                AcquisitionProgressBar.Value = 100;
                string receiptDescription;
                try
                {
                    var receipt = await ArtifactReceiptStore.SaveAsync(plan, _lastAcquisitionResult);
                    receiptDescription = $"\nLocal acquisition receipt: {receipt}";
                    if (!_offlineBusy) OfflineReceiptText.Text = receipt;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    receiptDescription = "\nArtifact integrity was verified, but the acquisition receipt could not be saved.";
                }
                DiscoveryDetailsText.Text = DescribeAcquisitionPlan(plan) +
                    $"\n\nACQUISITION EVIDENCE\nPath: {_lastAcquisitionResult.Evidence.Path}\nReused existing file: {_lastAcquisitionResult.ReusedExisting}\n" +
                    $"Verified inventory: {ModelFileSet.TotalBytes(plan.Artifact):N0} bytes across {1 + (plan.Artifact.AdditionalFiles?.Count ?? 0)} file(s)\nFirst-file SHA-256: {_lastAcquisitionResult.Evidence.ActualSha256}\n" +
                    _lastAcquisitionResult.Evidence.Description + receiptDescription;
                DiscoveryStatusText.Text = "Model artifact verified. It has not been loaded or tested for offline inference. Add it to the library when ready.";
            });
        }
        finally
        {
            _acquisitionInProgress = false;
            _approvedAcquisitionPlanId = null;
            AcquisitionApprovalCheck.IsChecked = false;
            RefreshDiscoveryControls();
            if (_closeAfterAcquisition && !_lifetime.IsCancellationRequested)
            {
                _closeAfterAcquisition = false;
                Close();
            }
        }
    }

    private async void AcquisitionLibraryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_discoveryBusy || _lastAcquisitionResult?.Evidence is not { Matches: true } evidence) return;
        var folder = Path.GetDirectoryName(evidence.Path)!;
        if (!_settings.ModelRoots.Contains(folder, StringComparer.OrdinalIgnoreCase) && _settings.ModelRoots.Count >= 64)
        {
            DiscoveryStatusText.Text = "The library already tracks 64 folders. Stop tracking a folder before adding another.";
            return;
        }
        _settings = _settings with { ModelRoots = _settings.ModelRoots.Append(folder).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() };
        RefreshLibraryRoots();
        await SaveSettingsAsync();
        await ScanLibraryAsync();
        DiscoveryStatusText.Text = "The acquired artifact's folder is tracked in Model Files. No runtime was launched.";
    }

    private async void DiscoveryVerifyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_reviewedAcquisitionPlan is not { } plan) return;
        await RunDiscoveryOperationAsync(async token =>
        {
            DiscoveryStatusText.Text = "Reading the existing artifact and computing SHA-256. No inference or download is running...";
            var evidence = await ModelArtifactVerifier.VerifyExistingAsync(plan, token);
            token.ThrowIfCancellationRequested();
            DiscoveryDetailsText.Text = DescribeAcquisitionPlan(plan) +
                $"\n\nEXISTING FILE EVIDENCE\nChecked: {evidence.CheckedAt.ToLocalTime():g}\nExists: {evidence.Exists}\n" +
                $"Size/hash match: {evidence.Matches}\nActual bytes: {evidence.ActualBytes?.ToString() ?? "not available"}\n" +
                $"Actual SHA-256: {evidence.ActualSha256 ?? "not computed"}\n{evidence.Description}";
            DiscoveryStatusText.Text = evidence.Description;
        });
    }

    private static string DescribeAcquisitionPlan(ModelAcquisitionPlan plan)
    {
        var text = new StringBuilder();
        text.AppendLine("MODEL DATA ACQUISITION PLAN");
        text.AppendLine($"{plan.Name}\nPlan ID: {plan.PlanId}\nSource: {plan.DownloadUrl}\nDestination: {plan.DestinationPath}");
        text.AppendLine($"Expected bytes: {plan.Artifact.SizeBytes:N0}\nExpected SHA-256: {plan.Artifact.Sha256}\nPinned revision: {plan.Artifact.Revision}");
        if (plan.Artifact.AdditionalFiles is not null)
        {
            text.AppendLine($"COMPLETE MODEL FILE SET: {1 + plan.Artifact.AdditionalFiles.Count} files, {ModelFileSet.TotalBytes(plan.Artifact):N0} total bytes. The identity above is the first file, not an aggregate hash.");
            foreach (var file in ModelFileSet.FilePlans(plan))
                text.AppendLine($"File: {file.Artifact.FileName}\nBytes: {file.Artifact.SizeBytes:N0}\nSHA-256: {file.Artifact.Sha256}\nSource: {file.DownloadUrl}\nDestination: {file.DestinationPath}");
            text.AppendLine("Completed verified files are retained if a later file fails or is cancelled; the model is not ready until every file verifies.");
        }
        text.AppendLine($"Declared license: {plan.DeclaredLicense}\nTerms reference: {plan.LicenseReference}\nOnly model data and an acquisition receipt are written. No runtime installer or Windows change is included.");
        text.AppendLine($"Source metadata: {plan.MetadataRetrievedAt.ToLocalTime():g}\nHardware evidence: {plan.HardwareScannedAt.ToLocalTime():g}");
        text.AppendLine("\nProposed actions:");
        foreach (var action in plan.Actions) text.AppendLine("• " + action);
        text.AppendLine("\nScope and limitations:");
        foreach (var limitation in plan.Limitations) text.AppendLine("• " + limitation);
        return text.ToString();
    }
}
