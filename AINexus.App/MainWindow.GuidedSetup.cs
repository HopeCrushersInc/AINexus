using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using Microsoft.Win32;

namespace AINexus.App;

public partial class MainWindow
{
    private FeaturedModel? _guidedModel;
    private DiscoveredItem? _guidedUserSelection;
    private OnlineDiscoverySnapshot? _guidedUserSnapshot;
    private ModelAcquisitionPlan? _guidedModelPlan;
    private bool _guidedInstalledOnly;
    private string? _guidedReceipt;
    private bool _guidedBusy;
    private bool _guidedClose;
    private bool _guidedLoadingModel;
    private CancellationTokenSource? _guidedOperation;
    private CancellationTokenSource? _chatOperation;
    private LocalChatSession? _chatSession;
    internal Func<FeaturedModel, CancellationToken, Task<OnlineDiscoverySnapshot>>? GuidedLookupOverride { get; set; }
    internal Func<CancellationToken, Task<MachineCapabilities>>? GuidedHardwareOverride { get; set; }
    internal bool GuidedPersistPreferences { get; set; } = true;
    internal string? GuidedReceiptPath => _guidedReceipt;
    internal string? GuidedModelPath => _guidedModelPlan?.DestinationPath;
    internal int ChatHistoryCount => _chatSession?.History.Count ?? 0;

    private void InitializeGuidedSetup()
    {
        _chatSession = new(new LocalChatClient(_modelHttp));
        RefreshFeaturedModels();
        Loaded += async (_, _) =>
        {
            await _preferencesReady.Task;
            if (!_lifetime.IsCancellationRequested) GuidedRuntimePathText.Text = _settings.LlamaCppExecutablePath;
        };
        Closing += (_, e) =>
        {
            _chatOperation?.Cancel();
            if (!_guidedBusy) return;
            e.Cancel = true;
            _guidedClose = true;
            _guidedOperation?.Cancel();
            _llamaServer.CancelStartup();
            GuidedStatusText.Text = "Cancelling the current step and preserving completed files before closing...";
        };
        RefreshGuidedControls();
    }

    private void FeaturedNsfw_Changed(object sender, RoutedEventArgs e) => RefreshFeaturedModels();
    private void RefreshFeaturedModels()
    {
        if (FeaturedCards is not null) FeaturedCards.ItemsSource = FeaturedModelCatalog.Visible(FeaturedNsfwCheck?.IsChecked == true);
    }

    private async void FeaturedModelInstall_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: FeaturedModel model })
        {
            SelectGuidedModel(model);
            await TryUseInstalledGuidedModelAsync(model);
        }
    }

    private async Task TryUseInstalledGuidedModelAsync(FeaturedModel model)
    {
        if (_guidedBusy || _guidedModel != model) return;
        var folder = GuidedModelFolderText.Text.Trim();
        if (!Directory.Exists(folder)) return;
        var receipts = Path.Combine(folder, ".ainexus", "receipts");
        try
        {
            OfflineModelVerifier.CheckNoReparsePath(receipts);
            if (!Directory.Exists(receipts)) return;
            foreach (var file in Directory.EnumerateFiles(receipts, "*.json").Take(50))
            {
                try
                {
                    var receipt = await OfflineModelVerifier.ReadReceiptAsync(file, _lifetime.Token);
                    if (_lifetime.IsCancellationRequested || _guidedBusy || _guidedModel != model || GuidedModelFolderText.Text.Trim() != folder) return;
                    if (receipt.Plan.Artifact.Repository != model.Repository || receipt.Plan.Artifact.FileName != model.PreferredFile
                        || !string.Equals(receipt.Plan.DestinationPath, Path.Combine(folder, model.PreferredFile), StringComparison.OrdinalIgnoreCase)) continue;
                    _guidedModelPlan = receipt.Plan;
                    _guidedReceipt = file;
                    _guidedInstalledOnly = true;
                    GuidedModelStep.IsExpanded = false;
                    GuidedEngineStep.IsExpanded = string.IsNullOrWhiteSpace(GuidedRuntimePathText.Text);
                    GuidedModelDetailsText.Text = "Previously acquired model found: " + receipt.Plan.DestinationPath
                        + "\nIts bytes will be checked against the saved receipt before loading. No online download is needed to launch it.";
                    GuidedStatusText.Text = "Installed model found. Choose Load model and open chat below.";
                    RefreshGuidedControls();
                    return;
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or System.Text.Json.JsonException or InvalidOperationException) { }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException) { }
    }

    internal void SelectGuidedModel(FeaturedModel model)
    {
        if (_guidedBusy || _workloadMachine is null || !_workspaceConfigured) return;
        if (model.Nsfw && FeaturedNsfwCheck.IsChecked != true) return;
        _guidedModel = model;
        _guidedUserSelection = null;
        _guidedUserSnapshot = null;
        _guidedModelPlan = null;
        _guidedInstalledOnly = false;
        _guidedReceipt = null;
        GuidedModelApprovalCheck.IsChecked = false;
        InvalidateGuidedRuntimeReview();
        GuidedModelTitleText.Text = model.Name;
        GuidedModelDetailsText.Text = model.Description + "\n" + model.LicenseNote + "\nSource: " + model.SourceUrl;
        var matchingRoot = _settings.ModelRoots.FirstOrDefault(root => Directory.Exists(root) && File.Exists(Path.Combine(root, model.PreferredFile)));
        GuidedModelFolderText.Text = matchingRoot ?? "";
        GuidedRuntimePathText.Text = _settings.LlamaCppExecutablePath;
        GuidedProgress.Value = 0;
        GuidedModelStep.IsExpanded = true;
        GuidedEngineStep.IsExpanded = string.IsNullOrWhiteSpace(GuidedRuntimePathText.Text);
        GuidedSetupTab.IsSelected = true;
        GuidedStatusText.Text = "Choose where to keep this model, then check its exact download size and your hardware. No download has started.";
        RefreshGuidedControls();
    }

    private void GuidedChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_guidedBusy || _guidedInstalledOnly) return;
        var picker = new OpenFolderDialog { Title = "Choose an existing folder for model and portable engine files", Multiselect = false };
        if (picker.ShowDialog(this) == true) GuidedModelFolderText.Text = picker.FolderName;
    }

    private void GuidedInputsChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (GuidedModelApprovalCheck is null || _guidedBusy) return;
        _guidedModelPlan = null;
        _guidedInstalledOnly = false;
        _guidedReceipt = null;
        GuidedModelApprovalCheck.IsChecked = false;
        InvalidateGuidedRuntimeReview();
        RefreshGuidedControls();
    }

    private void GuidedApproval_Changed(object sender, RoutedEventArgs e) => RefreshGuidedControls();
    private void GuidedRuntimePath_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (GuidedStartChatButton is not null) RefreshGuidedControls();
    }
    private void GuidedLicense_Click(object sender, RoutedEventArgs e)
    {
        if (_guidedModelPlan is not { } plan) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(plan.LicenseReference.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { GuidedStatusText.Text = "Could not open the browser. Copy the terms URL from Source details. " + ex.Message; }
    }
    private async void GuidedReview_Click(object sender, RoutedEventArgs e) => await ReviewGuidedModelAsync();

    internal async Task ReviewGuidedModelAsync()
    {
        var model = _guidedModel;
        var chosen = _guidedUserSelection;
        if (model is null && chosen is null) return;
        var folder = GuidedModelFolderText.Text.Trim();
        await RunGuidedOperationAsync(async token =>
        {
            _guidedModelPlan = null;
            _guidedInstalledOnly = false;
            _guidedReceipt = null;
            GuidedModelApprovalCheck.IsChecked = false;
            GuidedStatusText.Text = "Reviewing the selected source evidence and reading current hardware...";
            OfflineModelVerifier.CheckNoReparsePath(folder);
            if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("Choose an existing model folder first.");
            var machine = GuidedHardwareOverride is { } scan ? await scan(token) : await HardwareCapabilityScanner.ScanAsync(token);
            var snapshot = chosen is not null ? _guidedUserSnapshot ?? throw new InvalidOperationException("Search online and select a result first.")
                : GuidedLookupOverride is { } lookup ? await lookup(model!, token)
                : await new VerifiedOnlineDiscoveryProvider(_discoveryHttp).LookupFeaturedModelAsync(model!, token);
            var report = DiscoveryPipeline.Evaluate(snapshot, machine, Path.GetPathRoot(folder)!);
            var item = chosen ?? snapshot.Items.Single();
            var plan = ModelAcquisitionPlanner.Create(report, item.Id, folder);
            token.ThrowIfCancellationRequested();
            _guidedModelPlan = plan;
            var fit = report.Recommendations.Single(result => result.Item.Id == item.Id);
            GuidedModelDetailsText.Text = $"Download: {ByteSizeDisplay.Format(plan.Artifact.SizeBytes!.Value)}\nHardware estimate: {fit.ExecutionPath}\n"
                + string.Join(" ", fit.Reasons) + "\n\n" + DescribeAcquisitionPlan(plan);
            GuidedStatusText.Text = $"Download: {ByteSizeDisplay.Format(plan.Artifact.SizeBytes.Value)} · License: {plan.DeclaredLicense}. Read the source terms, approve below, then download. Hardware fit remains an estimate until an actual run.";
        });
    }

    private async void GuidedDownload_Click(object sender, RoutedEventArgs e) => await DownloadGuidedModelAsync();
    internal async Task DownloadGuidedModelAsync()
    {
        if (_guidedInstalledOnly || _guidedModelPlan is not { } plan || GuidedModelApprovalCheck.IsChecked != true || _acquisitionService is null) return;
        await RunGuidedOperationAsync(async token =>
        {
            _guidedReceipt = null;
            GuidedModelApprovalCheck.IsChecked = false;
            var progress = new Progress<AcquisitionProgress>(p =>
            {
                if (_lifetime.IsCancellationRequested || !_guidedBusy) return;
                GuidedProgress.Value = p.ExpectedBytes > 0 ? Math.Clamp(p.CompletedBytes * 100d / p.ExpectedBytes, 0, 100) : 0;
                GuidedStatusText.Text = $"{p.Stage}: {ByteSizeDisplay.Format(p.CompletedBytes)} / {ByteSizeDisplay.Format(p.ExpectedBytes)}";
            });
            var result = await _acquisitionService.AcquireAsync(plan, new(plan.PlanId, true, true), progress, token);
            _guidedReceipt = await ArtifactReceiptStore.SaveAsync(plan, result);
            if (!_settings.ModelRoots.Contains(Path.GetDirectoryName(plan.DestinationPath)!, StringComparer.OrdinalIgnoreCase))
                _settings = _settings with { ModelRoots = _settings.ModelRoots.Append(Path.GetDirectoryName(plan.DestinationPath)!).Take(64).ToArray() };
            if (GuidedPersistPreferences) await SaveSettingsAsync();
            RefreshLibraryRoots();
            GuidedProgress.Value = 100;
            GuidedModelStep.IsExpanded = false;
            GuidedStatusText.Text = "Model download verified and receipt saved. Choose an engine below, then load the model and open chat.";
        });
    }

    private async void GuidedStartChat_Click(object sender, RoutedEventArgs e) => await StartGuidedChatAsync();
    internal async Task StartGuidedChatAsync()
    {
        if (_guidedReceipt is not { } receipt || _guidedModelPlan is not { } plan) return;
        await RunGuidedOperationAsync(async token =>
        {
            if (_llamaLifecycleBusy || _llamaCppBusy || _llamaServer.IsRunning)
                throw new InvalidOperationException("Finish the current engine operation and stop the current model before loading another.");
            _llamaLifecycleBusy = true;
            RefreshLlamaRuntimeControls();
            try
            {
            GuidedStatusText.Text = "Verifying installed model bytes, then loading the model. Large models can take several minutes; Cancel stops this load.";
            var validatedReceipt = await OfflineModelVerifier.ReadReceiptAsync(receipt, token);
            if (validatedReceipt.Plan.PlanId != plan.PlanId || plan.PlanId != ModelAcquisitionPlanner.ComputeFingerprint(plan)
                || validatedReceipt.Plan.Artifact != plan.Artifact
                || !string.Equals(validatedReceipt.Plan.DestinationPath, plan.DestinationPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The receipt no longer matches the selected model review. Choose and review the installed copy again.");
            OfflineModelVerifier.CheckNoReparsePath(plan.DestinationPath);
            await using var modelLease = new FileStream(plan.DestinationPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var integrity = await ModelArtifactVerifier.VerifyExistingAsync(plan, token);
            if (!integrity.Matches) throw new InvalidDataException("The acquired model no longer matches its receipt. Run the download review again.");
            var machine = GuidedHardwareOverride is { } scan ? await scan(token) : await HardwareCapabilityScanner.ScanAsync(token);
            var capacity = OfflineModelVerifier.EvaluateCapacity(validatedReceipt, machine);
            if (capacity.Status != RecommendationStatus.ResourceCandidate)
                throw new InvalidOperationException("Current hardware cannot establish a fit for this installed model. " + string.Join(" ", capacity.Reasons));
            if (_llamaServer.IsRunning) throw new InvalidOperationException("Stop the current portal-owned model in Chat before loading another model.");
            LlamaCppExecutableText.Text = GuidedRuntimePathText.Text.Trim();
            LlamaCppModelPathText.Text = plan.DestinationPath;
            LlamaCppContextText.Text = "2048";
            var listener = new TcpListener(IPAddress.Loopback, 0);
            int port;
            try { listener.Start(); port = ((IPEndPoint)listener.LocalEndpoint).Port; } finally { listener.Stop(); }
            LlamaCppPortText.Text = port.ToString();
            _guidedLoadingModel = true;
            await _llamaServer.StartAsync(LlamaCppExecutableText.Text.Trim(), plan.DestinationPath, port, 2048, token, offlineMode: true);
            _ownedLlamaEndpoint = _llamaServer.Endpoint;
            if (_ownedLlamaEndpoint is not null) LlamaCppEndpointText.Text = _ownedLlamaEndpoint.GetLeftPart(UriPartial.Authority);
            token.ThrowIfCancellationRequested();
            if (!_llamaServer.IsRunning || _ownedLlamaEndpoint is not { } endpoint)
                throw new InvalidOperationException(LlamaCppRuntimeStatusText.Text);
            var ids = await new LlamaCppClient(_modelHttp).GetModelsAsync(endpoint, token);
            if (ids.Count != 1) throw new InvalidOperationException("The selected engine did not return exactly one loaded model ID.");
            GuidedStatusText.Text = "Model loaded. Testing a short local response before opening chat...";
            await new LocalChatClient(_modelHttp).SendAsync(endpoint, ids[0], [new("user", "Reply with a short greeting.")], token);
            token.ThrowIfCancellationRequested();
            _chatSession!.Configure(endpoint, ids[0]);
            RenderLlamaCppModels(ids, endpoint);
            if (GuidedPersistPreferences) await PersistLlamaCppPreferencesAsync();
            token.ThrowIfCancellationRequested();
            _guidedLoadingModel = false;
            ChatModelText.Text = _guidedUserSelection?.Name ?? _guidedModel?.Name ?? ids[0];
            ChatTranscriptText.Text = "";
            ChatStatusText.Text = "Local generation verified. Your conversation stays in session memory; no online chat provider is used.";
            ChatTab.IsSelected = true;
            GuidedStatusText.Text = "Model loaded and local generation verified. Chat is ready.";
            }
            finally { _llamaLifecycleBusy = false; RefreshLlamaRuntimeControls(); }
        });
    }

    private void GuidedCancel_Click(object sender, RoutedEventArgs e)
    {
        _guidedOperation?.Cancel();
        _llamaServer.CancelStartup();
    }

    private async Task RunGuidedOperationAsync(Func<CancellationToken, Task> action)
    {
        if (_guidedBusy || _discoveryBusy || _lifetime.IsCancellationRequested) return;
        _guidedBusy = _discoveryBusy = true;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _guidedOperation = _discoveryOperation = operation;
        RefreshGuidedControls();
        RefreshDiscoveryControls();
        try { await action(operation.Token); }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            var cleanup = await FinishGuidedLoadCleanupAsync();
            if (!_lifetime.IsCancellationRequested) GuidedStatusText.Text = "Step cancelled. Completed files are preserved; an unfinished model transfer removes only its own partial file." + cleanup;
        }
        catch (Exception ex)
        {
            var cleanup = await FinishGuidedLoadCleanupAsync();
            if (!_lifetime.IsCancellationRequested) GuidedStatusText.Text = "This step needs attention: " + ex.Message + cleanup;
        }
        finally
        {
            _guidedBusy = _discoveryBusy = false;
            _guidedLoadingModel = false;
            _guidedOperation = _discoveryOperation = null;
            RefreshGuidedControls();
            RefreshDiscoveryControls();
            if (_guidedClose && !_lifetime.IsCancellationRequested) { _guidedClose = false; Close(); }
        }
    }

    private void RefreshGuidedControls()
    {
        if (GuidedReviewButton is null) return;
        GuidedReviewButton.IsEnabled = !_guidedBusy && !_guidedInstalledOnly && (_guidedModel is not null || _guidedUserSelection is not null);
        GuidedDownloadButton.IsEnabled = !_guidedBusy && !_guidedInstalledOnly && _guidedModelPlan is not null && GuidedModelApprovalCheck.IsChecked == true;
        GuidedStartChatButton.IsEnabled = !_guidedBusy && _guidedReceipt is not null && !string.IsNullOrWhiteSpace(GuidedRuntimePathText.Text);
        GuidedCancelButton.IsEnabled = _guidedBusy;
        GuidedRuntimePathText.IsEnabled = !_guidedBusy;
        GuidedModelFolderText.IsEnabled = GuidedChooseFolderButton.IsEnabled = GuidedModelApprovalCheck.IsEnabled = !_guidedBusy && !_guidedInstalledOnly;
        GuidedModelStep.Header = _guidedInstalledOnly ? "1 · Installed model review" : "1 · Model download";
        FeaturedCards.IsEnabled = !_guidedBusy && _workloadMachine is not null;
        ChatSendButton.IsEnabled = _chatOperation is null && _chatSession?.Model is not null && _llamaServer.IsRunning;
        ChatCancelButton.IsEnabled = _chatOperation is not null;
        ChatStopButton.IsEnabled = !_guidedBusy && !_llamaLifecycleBusy && _llamaServer.IsRunning;
        RefreshGuidedRuntimeControls();
        RefreshCatalogLocalControls();
        RefreshImageArtifactSetupControls();
    }

    private async Task<string> FinishGuidedLoadCleanupAsync()
    {
        if (!_guidedLoadingModel) return "";
        try { await _llamaServer.StopAsync(); return ""; }
        catch (Exception ex) { return " Engine cleanup is incomplete; the owned engine may still be running. " + ex.Message; }
    }

    private async void ChatSend_Click(object sender, RoutedEventArgs e) => await SendChatAsync();
    internal async Task SendChatAsync()
    {
        if (_chatOperation is not null || _chatSession?.Model is null || !_llamaServer.IsRunning) return;
        var prompt = ChatPromptText.Text.Trim();
        if (prompt.Length == 0) return;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _chatOperation = operation;
        RefreshGuidedControls();
        ChatStatusText.Text = "Generating locally...";
        try
        {
            var session = _chatSession;
            var reply = await session.SendAsync(prompt, operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_chatSession, session)) return;
            ChatTranscriptText.Text = string.Join("\n\n", session.History.Select(m => (m.Role == "user" ? "You" : "Assistant") + ": " + m.Content));
            ChatTranscriptText.ScrollToEnd();
            ChatPromptText.Text = "";
            ChatStatusText.Text = reply.Truncated ? "Reply reached the output limit. Ask it to continue." : "Ready · local conversation";
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { ChatStatusText.Text = "Reply cancelled. Your message is preserved for retry."; }
        catch (Exception ex) { ChatStatusText.Text = "Reply failed: " + ex.Message; }
        finally { _chatOperation = null; RefreshGuidedControls(); }
    }

    private void ChatCancel_Click(object sender, RoutedEventArgs e) => _chatOperation?.Cancel();
    private void ChatClear_Click(object sender, RoutedEventArgs e)
    {
        if (_chatOperation is not null) return;
        _chatSession?.Clear();
        ChatTranscriptText.Text = "";
        ChatStatusText.Text = "New conversation · local history cleared";
    }
    private async void ChatStop_Click(object sender, RoutedEventArgs e)
    {
        _chatOperation?.Cancel();
        await StopOwnedLlamaServerAsync();
        _chatSession = new(new LocalChatClient(_modelHttp));
        ChatTranscriptText.Text = "";
        ChatModelText.Text = "No model loaded";
        ChatStatusText.Text = "Local model stopped. Load a model from Install & chat.";
        RefreshGuidedControls();
    }

    private void ResetChatAfterRuntimeStop()
    {
        if (_chatSession?.Model is not null)
        {
            _chatOperation?.Cancel();
            _chatSession = new(new LocalChatClient(_modelHttp));
            ChatTranscriptText.Text = "";
            ChatModelText.Text = "No model loaded";
            ChatStatusText.Text = "The local model stopped. Load a model to start a new conversation.";
        }
        RefreshGuidedControls();
    }
}
