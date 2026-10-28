using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace AINexus.App;

public partial class MainWindow
{
    private DiscoveredItem? _imageArtifactSelection;
    private OnlineDiscoverySnapshot? _imageArtifactSnapshot;
    private ImageArtifactAcquisitionPlan? _imageArtifactPlan;
    private string? _imageArtifactApprovedPlanId;
    private string? _imageArtifactActivePlanId;
    private bool _imageArtifactSetupBusy;
    private bool _closeAfterImageArtifactSetup;
    private long _imageArtifactSelectionGeneration;
    private CancellationTokenSource? _imageArtifactSetupOperation;

    internal Func<CancellationToken, Task<MachineCapabilities>>? ImageArtifactHardwareOverride { get; set; }
    internal Func<HttpClient>? ImageArtifactHttpFactoryOverride { get; set; }
    internal string? ImageArtifactReviewedPlanId => _imageArtifactPlan?.PlanId;
    internal string? ImageArtifactResultPath { get; private set; }
    internal string? ImageArtifactReceiptPath { get; private set; }

    private void InitializeImageArtifactSetup()
    {
        DiscoveryResultsGrid.SelectionChanged += (_, _) =>
        {
            if (_imageArtifactSelection is null) return;
            _imageArtifactSelection = null;
            _imageArtifactSnapshot = null;
            InvalidateImageArtifactReview();
            ImageArtifactSetupTitleText.Text = "No image file selected";
            ImageArtifactSetupDetailsText.Text = "Discovery selection changed. Select an image file and review it again before approving a download.";
            ImageArtifactSetupStatusText.Text = "Selection changed; the previous image-file download approval was revoked.";
            RefreshImageArtifactSetupControls();
        };
        Closing += (_, args) =>
        {
            if (!_imageArtifactSetupBusy) return;
            args.Cancel = true;
            _closeAfterImageArtifactSetup = true;
            _imageArtifactSetupOperation?.Cancel();
            ImageArtifactSetupStatusText.Text = "Cancelling the image-file operation and waiting for its partial-file cleanup before closing...";
        };
        RefreshImageArtifactSetupControls();
    }

    internal bool TrySelectImageArtifactSetup(DiscoveredItem item, OnlineDiscoverySnapshot snapshot)
    {
        if (_guidedBusy || _discoveryBusy || _imageArtifactSetupBusy || _lifetime.IsCancellationRequested
            || !_workspaceConfigured || _workloadMachine is null
            || item is not { Kind: "Model", Interest: DiscoveryInterest.Images, Artifact.Format: "Safetensors" }
            || !snapshot.Items.Contains(item)) return false;
        _imageArtifactSelection = item;
        _imageArtifactSnapshot = snapshot;
        InvalidateImageArtifactReview();
        ImageArtifactDestinationText.Text = "";
        ImageArtifactSetupTitleText.Text = item.Name;
        ImageArtifactSetupDetailsText.Text = $"IMAGE FILE CANDIDATE\nSource-declared license: {item.License ?? "Unknown"}\n"
            + $"Repository: {item.Artifact!.Repository}\nRevision: {item.Artifact.Revision}\nFile: {item.Artifact.FileName}\n"
            + $"Bytes: {item.Artifact.SizeBytes?.ToString("N0") ?? "Unknown"}\nSHA-256: {item.Artifact.Sha256 ?? "Unknown"}\n"
            + string.Join("\n", item.Sources.Select(source => source.Url.AbsoluteUri))
            + "\n\nChoose an existing destination folder, then review the exact download and current storage. "
            + "Hardware fit, complete checkpoint contents, companion assets and runtime compatibility remain unverified. "
            + "Selecting this file performs no search, download, hash scan, installation or model launch.";
        ImageArtifactSetupStatusText.Text = snapshot.IsCached
            ? "Saved source metadata cannot authorize a new download. Search online and explicitly select the source file again."
            : "No file or destination was selected automatically. Choose a folder and review the exact file before approving anything.";
        ImageArtifactSetupTab.IsSelected = true;
        RefreshImageArtifactSetupControls();
        return true;
    }

    private void InvalidateImageArtifactReview()
    {
        _imageArtifactSelectionGeneration++;
        _imageArtifactPlan = null;
        _imageArtifactApprovedPlanId = null;
        ImageArtifactResultPath = ImageArtifactReceiptPath = null;
        _imageArtifactSetupOperation?.Cancel();
        if (ImageArtifactApprovalCheck is not null) ImageArtifactApprovalCheck.IsChecked = false;
        if (ImageArtifactSetupProgress is not null) ImageArtifactSetupProgress.Value = 0;
    }

    private void RefreshImageArtifactSetupControls()
    {
        if (ImageArtifactReviewButton is null || ImageArtifactCancelButton is null) return;
        var idle = !_imageArtifactSetupBusy && !_guidedBusy && !_discoveryBusy && !_lifetime.IsCancellationRequested;
        ImageArtifactDestinationText.IsEnabled = ImageArtifactBrowseButton.IsEnabled = idle;
        ImageArtifactReviewButton.IsEnabled = idle && _imageArtifactSelection is not null
            && _imageArtifactSnapshot is { IsCached: false } && !string.IsNullOrWhiteSpace(ImageArtifactDestinationText.Text);
        ImageArtifactApprovalCheck.IsEnabled = idle && _imageArtifactPlan is not null;
        ImageArtifactDownloadButton.IsEnabled = idle && _imageArtifactPlan is { } plan
            && ImageArtifactApprovalCheck.IsChecked == true && _imageArtifactApprovedPlanId == plan.PlanId;
        ImageArtifactCancelButton.IsEnabled = _imageArtifactSetupBusy;
    }

    private void ImageArtifactDestinationChanged(object sender, TextChangedEventArgs e)
    {
        if (ImageArtifactReviewButton is null) return;
        InvalidateImageArtifactReview();
        ImageArtifactSetupStatusText.Text = "Destination changed. Review the exact source, destination and storage again; approval was revoked.";
        RefreshImageArtifactSetupControls();
    }

    private void ImageArtifactApprovalChanged(object sender, RoutedEventArgs e)
    {
        if (ImageArtifactApprovalCheck is null) return;
        _imageArtifactApprovedPlanId = ImageArtifactApprovalCheck.IsChecked == true ? _imageArtifactPlan?.PlanId : null;
        RefreshImageArtifactSetupControls();
    }

    private void ImageArtifactBrowseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_guidedBusy || _discoveryBusy || _imageArtifactSetupBusy) return;
        var dialog = new OpenFolderDialog { Title = "Choose an existing folder for this exact image file", Multiselect = false };
        if (dialog.ShowDialog(this) == true) ImageArtifactDestinationText.Text = dialog.FolderName;
    }

    private async void ImageArtifactReviewButton_Click(object sender, RoutedEventArgs e) => await ReviewImageArtifactDownloadAsync();

    internal async Task ReviewImageArtifactDownloadAsync()
    {
        if (_guidedBusy || _discoveryBusy || _imageArtifactSetupBusy || _lifetime.IsCancellationRequested
            || _imageArtifactSelection is not { } item || _imageArtifactSnapshot is not { } snapshot) return;
        InvalidateImageArtifactReview();
        var generation = _imageArtifactSelectionGeneration;
        var folder = ImageArtifactDestinationText.Text.Trim();
        await RunImageArtifactOperationAsync(async token =>
        {
            if (snapshot.IsCached) throw new InvalidOperationException("Search online and select the exact source again before reviewing a download.");
            if (!Path.IsPathFullyQualified(folder) || !Directory.Exists(folder))
                throw new DirectoryNotFoundException("Choose an existing absolute destination folder before reviewing the image-file download.");
            ImageArtifactSetupStatusText.Text = "Reading current hardware and available storage for the selected file. This does not establish model fit or start a download...";
            var machine = ImageArtifactHardwareOverride is { } scan ? await scan(token) : await HardwareCapabilityScanner.ScanAsync(token);
            token.ThrowIfCancellationRequested();
            if (generation != _imageArtifactSelectionGeneration || !ReferenceEquals(item, _imageArtifactSelection))
                throw new OperationCanceledException("Image-file selection changed during review.", token);
            var report = DiscoveryPipeline.Evaluate(snapshot, machine, Path.GetPathRoot(Path.GetFullPath(folder))!);
            var plan = ImageArtifactAcquisitionPlanner.Create(report, item.Id, folder);
            token.ThrowIfCancellationRequested();
            _imageArtifactPlan = plan;
            ImageArtifactSetupDetailsText.Text = ImageArtifactAcquisitionPlanner.Describe(plan);
            ImageArtifactSetupStatusText.Text = "Exact image-file data download reviewed. Read its source, license, bytes and destination, then approve this plan explicitly. Complete checkpoint contents, hardware fit and local execution remain unverified.";
        }, TimeSpan.FromMinutes(2));
    }

    private async void ImageArtifactDownloadButton_Click(object sender, RoutedEventArgs e) => await DownloadImageArtifactAsync();

    internal async Task DownloadImageArtifactAsync()
    {
        if (_guidedBusy || _discoveryBusy || _imageArtifactSetupBusy || _lifetime.IsCancellationRequested
            || _imageArtifactPlan is not { } plan || ImageArtifactApprovalCheck.IsChecked != true
            || _imageArtifactApprovedPlanId != plan.PlanId) return;
        var approval = new AcquisitionApproval(plan.PlanId, true, true);
        var generation = _imageArtifactSelectionGeneration;
        _imageArtifactPlan = null;
        _imageArtifactApprovedPlanId = null;
        ImageArtifactApprovalCheck.IsChecked = false;
        _imageArtifactActivePlanId = plan.PlanId;
        try
        {
            await RunImageArtifactOperationAsync(async token =>
            {
                var progress = new Progress<AcquisitionProgress>(value =>
                {
                    if (!_imageArtifactSetupBusy || _imageArtifactActivePlanId != plan.PlanId
                        || generation != _imageArtifactSelectionGeneration || _lifetime.IsCancellationRequested) return;
                    ImageArtifactSetupProgress.Value = value.ExpectedBytes > 0
                        ? Math.Clamp(value.CompletedBytes * 100d / value.ExpectedBytes, 0, 100) : 0;
                    ImageArtifactSetupStatusText.Text = value.Stage + ": " + ByteSizeDisplay.Format(value.CompletedBytes) + " / " + ByteSizeDisplay.Format(value.ExpectedBytes);
                });
                using var http = ImageArtifactHttpFactoryOverride is { } createHttp ? createHttp() : ImageArtifactAcquisitionService.CreateHttpClient();
                var result = await new ImageArtifactAcquisitionService(http).AcquireAsync(plan, approval, progress, token);
                token.ThrowIfCancellationRequested();
                if (generation != _imageArtifactSelectionGeneration) throw new OperationCanceledException("Image-file selection changed during acquisition.", token);
                ImageArtifactResultPath = result.Path;
                ImageArtifactReceiptPath = result.ReceiptPath;
                ImageArtifactSetupProgress.Value = 100;
                ImageArtifactSetupDetailsText.Text = ImageArtifactAcquisitionPlanner.Describe(plan)
                    + $"\n\nIMAGE FILE BYTES VERIFIED\nFile: {result.Path}\nReceipt: {result.ReceiptPath}\nActual bytes: {result.ActualBytes:N0}\nActual SHA-256: {result.ActualSha256}\nReused matching existing file: {result.ReusedExisting}\n\n"
                    + "Only this file's identity and bytes were verified. This is not proof of a complete checkpoint, compatible hardware, installed engine or successful generation. No engine configuration or model launch occurred.";
                ImageArtifactSetupStatusText.Text = "Image file verified and private receipt saved. Approval consumed. Engine setup and actual local generation remain separate, unimplemented steps for this artifact.";
            }, TimeSpan.FromHours(2));
        }
        finally { _imageArtifactActivePlanId = null; }
    }

    private void ImageArtifactCancelButton_Click(object sender, RoutedEventArgs e) => _imageArtifactSetupOperation?.Cancel();

    private async Task RunImageArtifactOperationAsync(Func<CancellationToken, Task> action, TimeSpan timeout)
    {
        if (_guidedBusy || _discoveryBusy || _imageArtifactSetupBusy || _lifetime.IsCancellationRequested) return;
        _imageArtifactSetupBusy = _guidedBusy = _discoveryBusy = true;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        operation.CancelAfter(timeout);
        _imageArtifactSetupOperation = _guidedOperation = _discoveryOperation = operation;
        RefreshImageArtifactSetupControls();
        RefreshGuidedControls();
        RefreshDiscoveryControls();
        try { await action(operation.Token); }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            _imageArtifactPlan = null;
            ImageArtifactApprovalCheck.IsChecked = false;
            if (!_lifetime.IsCancellationRequested) ImageArtifactSetupStatusText.Text = "Image-file operation cancelled or timed out. Completed files were preserved; cleanup was attempted only for this operation's own partial file. Review again before approving another action.";
        }
        catch (Exception ex)
        {
            _imageArtifactPlan = null;
            ImageArtifactApprovalCheck.IsChecked = false;
            if (!_lifetime.IsCancellationRequested) ImageArtifactSetupStatusText.Text = "Image-file operation needs attention: " + ex.Message + " No installation or model launch was performed. Review again before approval.";
        }
        finally
        {
            _imageArtifactSetupBusy = _guidedBusy = _discoveryBusy = false;
            _imageArtifactApprovedPlanId = null;
            _imageArtifactSetupOperation = _guidedOperation = _discoveryOperation = null;
            RefreshImageArtifactSetupControls();
            RefreshGuidedControls();
            RefreshDiscoveryControls();
            if ((_closeAfterImageArtifactSetup || _guidedClose) && !_lifetime.IsCancellationRequested)
            {
                _closeAfterImageArtifactSetup = _guidedClose = false;
                Close();
            }
        }
    }
}
