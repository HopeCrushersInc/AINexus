using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace AINexus.App;

public partial class MainWindow
{
    private ComfyPortableRelease? _comfyPortableRelease;
    private ComfyPortableDownloadPlan? _comfyPortablePlan;
    private bool _comfyPortableBusy;
    private bool _closeAfterComfyPortable;
    private long _comfyPortableGeneration;
    private CancellationTokenSource? _comfyPortableOperation;
    internal Func<HttpClient>? ComfyPortableHttpOverride { get; set; }
    internal Action<Uri>? ComfyPortableSourceOpenOverride { get; set; }
    internal string? ComfyPortableReviewedPlanId => _comfyPortablePlan?.PlanId;

    private void InitializeComfyPortableSetup()
    {
        Closing += (_, args) =>
        {
            if (!_comfyPortableBusy) return;
            args.Cancel = true;
            _closeAfterComfyPortable = true;
            _comfyPortableOperation?.Cancel();
            ComfyPortableStatusText.Text = "Cancelling and cleaning up this download before closing...";
        };
        RefreshComfyPortableControls();
    }

    private void ComfyPortableOpen_Click(object sender, RoutedEventArgs e)
    {
        if (_comfyPortableBusy || _reviewedTool is null || !IsComfyPortableTool(_reviewedTool)) return;
        _comfyPortableRelease = null;
        ComfyPortableAssetSelector.ItemsSource = null;
        ComfyPortableTagText.Text = ComfyPortableDestinationText.Text = "";
        InvalidateComfyPortablePlan();
        ComfyPortableDetailsText.Text = "No release, archive or destination selected.";
        ComfyPortableStatusText.Text = "Check releases when ready. No network lookup runs until you ask.";
        ComfyPortableSetupTab.IsSelected = true;
        RefreshComfyPortableControls();
    }

    private void InvalidateComfyPortablePlan()
    {
        _comfyPortableGeneration++;
        _comfyPortablePlan = null;
        if (ComfyPortableProgress is not null) ComfyPortableProgress.Value = 0;
        _comfyPortableOperation?.Cancel();
        if (ComfyPortableApprovalCheck is not null) ComfyPortableApprovalCheck.IsChecked = false;
        if (ComfyPortableDetailsText is not null) ComfyPortableDetailsText.Text = "Review the chosen archive and destination before approving.";
    }

    private void ComfyPortableInputChanged(object sender, TextChangedEventArgs e)
    {
        if (ComfyPortableDownloadButton is null) return;
        if (ReferenceEquals(sender, ComfyPortableTagText))
        {
            _comfyPortableRelease = null;
            ComfyPortableAssetSelector.ItemsSource = null;
        }
        InvalidateComfyPortablePlan();
        RefreshComfyPortableControls();
    }
    private void ComfyPortableAssetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ComfyPortableDownloadButton is null) return;
        InvalidateComfyPortablePlan();
        RefreshComfyPortableControls();
    }
    private void ComfyPortableApprovalChanged(object sender, RoutedEventArgs e) => RefreshComfyPortableControls();

    private void RefreshComfyPortableControls()
    {
        if (ComfyPortableDownloadButton is null) return;
        var idle = !_comfyPortableBusy && !_lifetime.IsCancellationRequested;
        ComfyPortableLookupButton.IsEnabled = ComfyPortableTagText.IsEnabled = idle;
        ComfyPortableDestinationText.IsEnabled = ComfyPortableBrowseButton.IsEnabled = idle;
        ComfyPortableAssetSelector.IsEnabled = idle && _comfyPortableRelease is not null;
        ComfyPortableReviewButton.IsEnabled = idle && _comfyPortableRelease is not null
            && ComfyPortableAssetSelector.SelectedItem is ComfyPortableAsset
            && !string.IsNullOrWhiteSpace(ComfyPortableDestinationText.Text);
        ComfyPortableApprovalCheck.IsEnabled = idle && _comfyPortablePlan is not null;
        ComfyPortableDownloadButton.IsEnabled = idle && _comfyPortablePlan is not null && ComfyPortableApprovalCheck.IsChecked == true;
        ComfyPortableCancelButton.IsEnabled = _comfyPortableBusy;
        ComfyPortableTermsButton.IsEnabled = idle && _comfyPortableRelease is not null;
    }

    private async Task RunComfyPortableOperationAsync(Func<CancellationToken, Task> operation)
    {
        if (_comfyPortableBusy || _lifetime.IsCancellationRequested) return;
        _comfyPortableBusy = true;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _comfyPortableOperation = cancel;
        RefreshComfyPortableControls();
        try { await operation(cancel.Token); }
        catch (OperationCanceledException) { ComfyPortableStatusText.Text = "Cancelled. No engine was installed or started."; }
        catch (Exception ex) { ComfyPortableStatusText.Text = "Could not complete: " + ex.Message; }
        finally
        {
            _comfyPortableOperation = null;
            _comfyPortableBusy = false;
            RefreshComfyPortableControls();
            if (_closeAfterComfyPortable) { _closeAfterComfyPortable = false; Close(); }
        }
    }

    private async void ComfyPortableLookup_Click(object sender, RoutedEventArgs e) => await LookupComfyPortableReleaseAsync();
    internal async Task LookupComfyPortableReleaseAsync()
    {
        if (_comfyPortableBusy || _reviewedTool is null || !IsComfyPortableTool(_reviewedTool)) return;
        _comfyPortableRelease = null;
        ComfyPortableAssetSelector.ItemsSource = null;
        InvalidateComfyPortablePlan();
        var generation = _comfyPortableGeneration;
        var tag = ComfyPortableTagText.Text.Trim();
        await RunComfyPortableOperationAsync(async token =>
        {
            ComfyPortableStatusText.Text = "Reading official release metadata only...";
            using var http = ComfyPortableHttpOverride?.Invoke() ?? ComfyPortableReleaseClient.CreateHttpClient();
            var release = await new ComfyPortableReleaseClient(http).ReadAsync(tag.Length == 0 ? "latest" : tag, token);
            if (generation != _comfyPortableGeneration || token.IsCancellationRequested) return;
            _comfyPortableRelease = release;
            ComfyPortableAssetSelector.ItemsSource = release.Assets;
            ComfyPortableAssetSelector.SelectedIndex = -1;
            ComfyPortableDetailsText.Text = $"Release: {release.Tag}\n{release.ReleaseUrl}\nSource terms: {release.SourceTermsUrl}";
            ComfyPortableStatusText.Text = release.Assets.Count == 0
                ? "No supported archives with published checksums were found. Nothing was selected or downloaded."
                : "Choose an archive. Nothing was selected or downloaded automatically; hardware compatibility remains unverified.";
        });
    }

    private void ComfyPortableBrowse_Click(object sender, RoutedEventArgs e)
    {
        if (_comfyPortableBusy) return;
        var dialog = new OpenFolderDialog { Title = "Choose an existing folder for the ComfyUI archive" };
        if (dialog.ShowDialog(this) == true) ComfyPortableDestinationText.Text = dialog.FolderName;
    }
    private void ComfyPortableTerms_Click(object sender, RoutedEventArgs e) => OpenComfyPortableTerms();
    internal void OpenComfyPortableTerms()
    {
        if (_comfyPortableBusy || _comfyPortableRelease is not { } release) return;
        try
        {
            ComfyPortableReleaseClient.ValidateRelease(release);
            if (ComfyPortableSourceOpenOverride is { } open) open(release.SourceTermsUrl);
            else System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(release.SourceTermsUrl.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) { ComfyPortableStatusText.Text = "Could not open the source terms: " + ex.Message; }
    }
    private void ComfyPortableReview_Click(object sender, RoutedEventArgs e) => ReviewComfyPortableDownload();
    internal void ReviewComfyPortableDownload()
    {
        if (_comfyPortableBusy) return;
        InvalidateComfyPortablePlan();
        try
        {
            if (_comfyPortableRelease is null || ComfyPortableAssetSelector.SelectedItem is not ComfyPortableAsset asset)
                throw new InvalidOperationException("Check a release and choose an archive first.");
            _comfyPortablePlan = ComfyPortableDownloadPlanner.Create(_comfyPortableRelease, asset, ComfyPortableDestinationText.Text.Trim());
            ComfyPortableDetailsText.Text = ComfyPortableDownloadPlanner.Describe(_comfyPortablePlan);
            ComfyPortableStatusText.Text = "Review the exact download and source terms, then approve. No archive has been downloaded.";
        }
        catch (Exception ex) { ComfyPortableStatusText.Text = "Could not review: " + ex.Message; }
        RefreshComfyPortableControls();
    }
    private async void ComfyPortableDownload_Click(object sender, RoutedEventArgs e) => await DownloadComfyPortableArchiveAsync();
    internal async Task DownloadComfyPortableArchiveAsync()
    {
        if (_comfyPortableBusy || _comfyPortablePlan is not { } plan || ComfyPortableApprovalCheck.IsChecked != true) return;
        _comfyPortablePlan = null;
        ComfyPortableApprovalCheck.IsChecked = false;
        var generation = _comfyPortableGeneration;
        await RunComfyPortableOperationAsync(async token =>
        {
            ComfyPortableStatusText.Text = "Downloading and verifying the approved archive...";
            using var http = ComfyPortableHttpOverride?.Invoke() ?? ComfyPortableDownloadService.CreateHttpClient();
            var progress = new Progress<AcquisitionProgress>(update =>
            {
                if (!_comfyPortableBusy || token.IsCancellationRequested || generation != _comfyPortableGeneration) return;
                ComfyPortableProgress.Value = update.ExpectedBytes > 0 ? Math.Clamp(update.CompletedBytes * 100d / update.ExpectedBytes, 0, 100) : 0;
                ComfyPortableStatusText.Text = $"{update.Stage}: {update.CompletedBytes:N0} / {update.ExpectedBytes:N0} bytes";
            });
            var result = await new ComfyPortableDownloadService(http).AcquireAsync(plan,
                new ComfyPortableDownloadApproval(plan.PlanId, true, true, true), progress, token);
            ComfyPortableProgress.Value = 100;
            ComfyPortableDetailsText.Text = $"Archive: {result.ArchivePath}\nReceipt: {result.ReceiptPath}";
            ComfyPortableStatusText.Text = "Archive verified. Engine NOT installed: extraction and engine verification are not integrated yet. No engine was started.";
        });
    }
    private void ComfyPortableCancel_Click(object sender, RoutedEventArgs e) => _comfyPortableOperation?.Cancel();
}
