using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace AINexus.App;

public partial class MainWindow
{
    private ComfyCheckpointFolderPlan? _comfyFolderPlan;
    private ComfyCheckpointFolderApplyResult? _comfyFolderResult;
    private string? _comfyFolderApprovedPlanId;
    private bool _comfyFolderBusy;
    private bool _closeAfterComfyFolders;
    private long _comfyFolderGeneration;
    private CancellationTokenSource? _comfyFolderOperation;

    internal Func<string, IReadOnlyList<string>, CancellationToken, Task<ComfyCheckpointFolderPlan>>? ComfyCheckpointFolderReviewOverride { get; set; }
    internal Func<ComfyCheckpointFolderPlan, ComfyCheckpointFolderApproval, CancellationToken, Task<ComfyCheckpointFolderApplyResult>>? ComfyCheckpointFolderApplyOverride { get; set; }
    internal string? ComfyCheckpointFolderReviewedPlanId => _comfyFolderPlan?.PlanId;
    internal string? ComfyCheckpointFolderAppliedConfigPath => _comfyFolderResult?.ConfigPath;

    private void InitializeComfyCheckpointFolders()
    {
        Closing += (_, args) =>
        {
            if (!_comfyFolderBusy) return;
            args.Cancel = true;
            _closeAfterComfyFolders = true;
            _comfyFolderOperation?.Cancel();
            ComfyFolderStatusText.Text = "Cancelling the folder-connection step and waiting for its configuration-file cleanup before closing...";
        };
        RefreshComfyCheckpointFolderControls();
    }

    internal bool OpenComfyCheckpointFolderSetup(string? explicitCheckpointFolder = null)
    {
        if (_selectedImageTool is null || !_workspaceConfigured || _workloadMachine is null || _guidedBusy || _discoveryBusy || _imageBusy
            || _comfyFolderBusy || _lifetime.IsCancellationRequested) return false;
        InvalidateComfyCheckpointFolderReview();
        ComfyFolderEngineText.Text = "";
        ComfyFolderPathsText.Text = explicitCheckpointFolder ?? "";
        ComfyFolderDetailsText.Text = "Choose an existing ComfyUI engine folder and one or more existing checkpoint folders. "
            + "Opening this page reads no configuration and writes nothing. Review will show the complete proposed app-owned configuration and any backup path before approval. "
            + "The connection takes effect only on a separate, explicit next engine start. It does not start or stop an engine, choose a checkpoint, or establish hardware fit or complete checkpoint contents.";
        ComfyFolderStatusText.Text = explicitCheckpointFolder is null
            ? "No engine or checkpoint folder was selected automatically. Choose the folders, then review."
            : "The folder you explicitly chose for the verified file was copied here. Choose an engine folder and review before approving any configuration write.";
        ComfyCheckpointFoldersTab.IsSelected = true;
        RefreshComfyCheckpointFolderControls();
        return true;
    }

    private void ComfyFolderOpenButton_Click(object sender, RoutedEventArgs e) => OpenComfyCheckpointFolderSetup();

    private void ImageArtifactConnectFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (ImageArtifactResultPath is not { } path || ImageArtifactReceiptPath is null) return;
        if (_selectedImageTool is null)
        {
            ImageFindTools_Click(sender, e);
            return;
        }
        OpenComfyCheckpointFolderSetup(Path.GetDirectoryName(path));
    }

    private void InvalidateComfyCheckpointFolderReview()
    {
        _comfyFolderGeneration++;
        _comfyFolderPlan = null;
        _comfyFolderResult = null;
        _comfyFolderApprovedPlanId = null;
        _comfyFolderOperation?.Cancel();
        if (ComfyFolderApprovalCheck is not null) ComfyFolderApprovalCheck.IsChecked = false;
    }

    private void RefreshComfyCheckpointFolderControls()
    {
        if (ComfyFolderCancelButton is null || ComfyFolderOpenImagesButton is null) return;
        var idle = !_comfyFolderBusy && !_guidedBusy && !_discoveryBusy && !_imageBusy && !_lifetime.IsCancellationRequested;
        ComfyFolderEngineText.IsEnabled = ComfyFolderPathsText.IsEnabled = ComfyFolderEngineBrowseButton.IsEnabled = ComfyFolderAddButton.IsEnabled = idle;
        ComfyFolderReviewButton.IsEnabled = idle && !string.IsNullOrWhiteSpace(ComfyFolderEngineText.Text)
            && !string.IsNullOrWhiteSpace(ComfyFolderPathsText.Text);
        ComfyFolderApprovalCheck.IsEnabled = idle && _comfyFolderPlan is not null;
        ComfyFolderApplyButton.IsEnabled = idle && _comfyFolderPlan is { } plan
            && ComfyFolderApprovalCheck.IsChecked == true && _comfyFolderApprovedPlanId == plan.PlanId;
        ComfyFolderCancelButton.IsEnabled = _comfyFolderBusy;
        ComfyFolderOpenImagesButton.IsEnabled = idle && _comfyFolderResult is not null && _workspaceProfile?.HasImages == true;
        if (ImageConnectFolderButton is not null) ImageConnectFolderButton.IsEnabled = idle && _selectedImageTool is not null;
    }

    private void ComfyFolderInputsChanged(object sender, TextChangedEventArgs e)
    {
        if (ComfyFolderReviewButton is null) return;
        InvalidateComfyCheckpointFolderReview();
        ComfyFolderStatusText.Text = "Folder selection changed. Review the current engine, folders and configuration again; approval was revoked.";
        RefreshComfyCheckpointFolderControls();
    }

    private void ComfyFolderApprovalChanged(object sender, RoutedEventArgs e)
    {
        if (ComfyFolderApprovalCheck is null) return;
        _comfyFolderApprovedPlanId = ComfyFolderApprovalCheck.IsChecked == true ? _comfyFolderPlan?.PlanId : null;
        RefreshComfyCheckpointFolderControls();
    }

    private void ComfyFolderEngineBrowseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_comfyFolderBusy || _guidedBusy || _discoveryBusy || _imageBusy) return;
        var dialog = new OpenFolderDialog { Title = "Choose an existing ComfyUI engine folder", Multiselect = false };
        if (dialog.ShowDialog(this) == true) ComfyFolderEngineText.Text = dialog.FolderName;
    }

    private void ComfyFolderAddButton_Click(object sender, RoutedEventArgs e)
    {
        if (_comfyFolderBusy || _guidedBusy || _discoveryBusy || _imageBusy) return;
        var dialog = new OpenFolderDialog { Title = "Choose an existing checkpoint folder to connect", Multiselect = false };
        if (dialog.ShowDialog(this) == true)
            ComfyFolderPathsText.Text = string.IsNullOrWhiteSpace(ComfyFolderPathsText.Text)
                ? dialog.FolderName : ComfyFolderPathsText.Text.TrimEnd() + Environment.NewLine + dialog.FolderName;
    }

    private async void ComfyFolderReviewButton_Click(object sender, RoutedEventArgs e) => await ReviewComfyCheckpointFoldersAsync();

    internal async Task ReviewComfyCheckpointFoldersAsync()
    {
        if (_comfyFolderBusy || _guidedBusy || _discoveryBusy || _imageBusy || _lifetime.IsCancellationRequested) return;
        InvalidateComfyCheckpointFolderReview();
        var engine = ComfyFolderEngineText.Text.Trim();
        var folders = ComfyFolderPathsText.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var generation = _comfyFolderGeneration;
        await RunComfyCheckpointFolderOperationAsync(async token =>
        {
            ComfyFolderStatusText.Text = "Reading the chosen engine, checkpoint folders and app-owned configuration. No files will be written or engine started...";
            var plan = ComfyCheckpointFolderReviewOverride is { } review
                ? await review(engine, folders, token)
                : await Task.Run(() => ComfyCheckpointFolderPlanner.Create(engine, folders), token).WaitAsync(token);
            token.ThrowIfCancellationRequested();
            if (generation != _comfyFolderGeneration) throw new OperationCanceledException("Folder selection changed during review.", token);
            _comfyFolderPlan = plan;
            ComfyFolderDetailsText.Text = ComfyCheckpointFolderPlanner.Describe(plan);
            ComfyFolderStatusText.Text = "Folder connection reviewed. Read the exact configuration and backup paths, then approve explicitly. Only a separate next engine start can read this configuration; no model or engine was started.";
        });
    }

    private async void ComfyFolderApplyButton_Click(object sender, RoutedEventArgs e) => await ApplyComfyCheckpointFoldersAsync();

    internal async Task ApplyComfyCheckpointFoldersAsync()
    {
        if (_comfyFolderBusy || _guidedBusy || _discoveryBusy || _imageBusy || _lifetime.IsCancellationRequested
            || _comfyFolderPlan is not { } plan || ComfyFolderApprovalCheck.IsChecked != true
            || _comfyFolderApprovedPlanId != plan.PlanId) return;
        var approval = new ComfyCheckpointFolderApproval(plan.PlanId, true);
        var generation = _comfyFolderGeneration;
        _comfyFolderPlan = null;
        _comfyFolderApprovedPlanId = null;
        ComfyFolderApprovalCheck.IsChecked = false;
        await RunComfyCheckpointFolderOperationAsync(async token =>
        {
            ComfyFolderStatusText.Text = "Applying only the approved app-owned configuration and its reviewed backup. Existing YAML and running engines are unchanged...";
            var result = ComfyCheckpointFolderApplyOverride is { } apply
                ? await apply(plan, approval, token)
                : await new ComfyCheckpointFolderService().ApplyAsync(plan, approval, token);
            // Atomic application can commit before cancellation arrives. A successful result
            // remains completed evidence even if closing or a changed input cancelled the wait.
            var inputsChanged = generation != _comfyFolderGeneration;
            _comfyFolderResult = result;
            ComfyFolderDetailsText.Text = ComfyCheckpointFolderPlanner.Describe(plan)
                + $"\n\nAPPROVED FOLDER CONFIGURATION\nEngine: {result.EngineRoot}\nConfiguration: {result.ConfigPath}\nBackup: {result.BackupPath ?? "None required"}\nChanged: {result.Changed}\n"
                + "No engine was started or stopped. No checkpoint was selected, scanned for completeness, or verified for hardware fit. "
                + "The approved paths are read only on a separate next explicit start of this engine. A currently running engine was not reconfigured.";
            ComfyFolderStatusText.Text = inputsChanged
                ? $"The previously approved operation completed: {result.ConfigPath}. Backup: {result.BackupPath ?? "None required"}. Current inputs changed and have no approval; review them before another write. No engine was started."
                : "Approved folder configuration saved. Approval consumed. Start the chosen engine separately when ready, then inspect its actual checkpoint list and choose a checkpoint yourself.";
        });
    }

    private void ComfyFolderCancelButton_Click(object sender, RoutedEventArgs e) => _comfyFolderOperation?.Cancel();

    private void ComfyFolderOpenImagesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_comfyFolderResult is not { } result || _workspaceProfile?.HasImages != true || _comfyFolderBusy
            || _guidedBusy || _discoveryBusy || _imageBusy) return;
        if (!string.Equals(ImageEnginePathText.Text, result.EngineRoot, StringComparison.OrdinalIgnoreCase))
        {
            ImageEnginePathText.Text = result.EngineRoot;
            ImageInputsChanged(this, new RoutedEventArgs());
        }
        ImageStudioTab.IsSelected = true;
        ImageStatusText.Text = $"Approved checkpoint folders were saved for {result.EngineRoot}. Its engine folder is shown; start or check its connection explicitly. No engine was started by opening Images.";
    }

    private async Task RunComfyCheckpointFolderOperationAsync(Func<CancellationToken, Task> action)
    {
        if (_comfyFolderBusy || _guidedBusy || _discoveryBusy || _imageBusy || _lifetime.IsCancellationRequested) return;
        _comfyFolderBusy = _guidedBusy = _discoveryBusy = _imageBusy = true;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        operation.CancelAfter(TimeSpan.FromMinutes(2));
        _comfyFolderOperation = _guidedOperation = _discoveryOperation = _imageOperation = operation;
        RefreshComfyCheckpointFolderControls();
        RefreshGuidedControls(); RefreshDiscoveryControls(); RefreshImageControls(); RefreshImageArtifactSetupControls();
        try { await action(operation.Token); }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            _comfyFolderPlan = null;
            ComfyFolderApprovalCheck.IsChecked = false;
            if (!_lifetime.IsCancellationRequested) ComfyFolderStatusText.Text = "Folder-connection operation cancelled or timed out. Completed configuration data was preserved; cleanup was limited to this operation's owned temporary files where paths remained valid. Review again before approval.";
        }
        catch (Exception ex)
        {
            _comfyFolderPlan = null;
            ComfyFolderApprovalCheck.IsChecked = false;
            if (!_lifetime.IsCancellationRequested) ComfyFolderStatusText.Text = "Folder connection needs attention: " + ex.Message + " Inspect the displayed configuration and backup paths, then review again before approval. No engine or model was started.";
        }
        finally
        {
            _comfyFolderBusy = _guidedBusy = _discoveryBusy = _imageBusy = false;
            _comfyFolderApprovedPlanId = null;
            _comfyFolderOperation = _guidedOperation = _discoveryOperation = _imageOperation = null;
            RefreshComfyCheckpointFolderControls();
            RefreshGuidedControls(); RefreshDiscoveryControls(); RefreshImageControls(); RefreshImageArtifactSetupControls();
            if ((_closeAfterComfyFolders || _guidedClose || _closeAfterImages) && !_lifetime.IsCancellationRequested)
            {
                _closeAfterComfyFolders = _guidedClose = _closeAfterImages = false;
                Close();
            }
        }
    }
}
