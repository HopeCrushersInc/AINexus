using System.IO;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace AINexus.App;

public partial class MainWindow
{
    private readonly HttpClient _discoveryHttp = VerifiedOnlineDiscoveryProvider.CreateHttpClient();
    private DiscoveryPipeline? _discoveryPipeline;
    private MachineCapabilities? _discoveryMachine;
    private CancellationTokenSource? _discoveryOperation;
    private bool _discoveryBusy;
    private DiscoveryReport? _lastDiscoveryReport;
    private ModelAcquisitionPlan? _reviewedAcquisitionPlan;
    private string? _discoveryTargetVolume;
    private RuntimeFilePlan? _reviewedRuntimeFiles;

    private void InitializeDiscovery()
    {
        InitializeAcquisition();
        _discoveryPipeline = new(new VerifiedOnlineDiscoveryProvider(_discoveryHttp), DiscoveryCacheStore.ForCurrentUser());
        DiscoveryInterestSelector.ItemsSource = VerifiedOnlineDiscoveryProvider.Workflows;
        DiscoveryInterestSelector.SelectedIndex = 0;
        Closed += (_, _) => _discoveryHttp.Dispose();
        RefreshDiscoveryControls();
    }

    private void DiscoveryInterestSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DiscoveryScopeText is null) return;
        if (DiscoveryInterestSelector.SelectedItem is DiscoveryWorkflow workflow) DiscoveryScopeText.Text = workflow.Scope;
        if (DiscoverySearchButton is not null) RefreshDiscoveryControls();
    }

    private void DiscoveryVolumeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_discoveryBusy || DiscoveryDetailsText is null) return;
        if (DiscoveryVolumeSelector.SelectedItem is StorageVolume volume) _discoveryTargetVolume = volume.Path;
        _lastDiscoveryReport = null;
        InvalidateAcquisitionReview();
        DiscoveryResultsGrid.ItemsSource = null;
        DiscoveryDetailsText.Text = "Storage selection changed. Search online or load saved evidence to evaluate this volume.";
        RefreshDiscoveryControls();
    }

    private void RefreshDiscoveryControls()
    {
        DiscoveryScanButton.IsEnabled = !_discoveryBusy;
        DiscoveryInterestSelector.IsEnabled = DiscoveryQueryText.IsEnabled = DiscoveryVolumeSelector.IsEnabled = !_discoveryBusy;
        var supported = DiscoveryInterestSelector.SelectedItem is DiscoveryWorkflow workflow && VerifiedOnlineDiscoveryProvider.Supports(workflow.Interest);
        DiscoverySearchButton.IsEnabled = !_discoveryBusy && supported;
        DiscoverySavedButton.IsEnabled = !_discoveryBusy && DiscoveryInterestSelector.SelectedItem is DiscoveryWorkflow;
        DiscoveryCancelButton.IsEnabled = _discoveryBusy;
        if (RuntimeSetupReviewButton is not null)
            RuntimeSetupReviewButton.IsEnabled = !_discoveryBusy && _lastDiscoveryReport is not null
                && DiscoveryResultsGrid.SelectedItem is DiscoveryRow { Recommendation.Item.RuntimeRelease: not null };
        if (RuntimeDestinationText is not null) RuntimeDestinationText.IsEnabled = !_discoveryBusy;
        if (RuntimeReleaseLookupButton is not null) RuntimeReleaseLookupButton.IsEnabled = !_discoveryBusy;
        if (RuntimeReleaseSavedButton is not null) RuntimeReleaseSavedButton.IsEnabled = !_discoveryBusy;
        if (RuntimeReleaseTagText is not null) RuntimeReleaseTagText.IsEnabled = !_discoveryBusy;
        if (RuntimeArchiveSelector is not null)
            RuntimeArchiveSelector.IsEnabled = !_discoveryBusy && DiscoveryResultsGrid.SelectedItem is DiscoveryRow { Recommendation.Item.RuntimeRelease: not null };
        if (RuntimeFileReviewButton is not null)
            RuntimeFileReviewButton.IsEnabled = !_discoveryBusy && RuntimeArchiveSelector?.SelectedItem is RuntimeReleaseAsset
                && DiscoveryResultsGrid.SelectedItem is DiscoveryRow { Recommendation.Item.RuntimeRelease: not null };
        if (RuntimeInspectButton is not null) RuntimeInspectButton.IsEnabled = !_discoveryBusy && _reviewedRuntimeFiles is not null;
        if (DiscoveryPlanButton is not null)
            DiscoveryPlanButton.IsEnabled = !_discoveryBusy && _lastDiscoveryReport is { Catalog.IsCached: false }
                && DiscoveryResultsGrid.SelectedItem is DiscoveryRow { Recommendation.Status: RecommendationStatus.ResourceCandidate };
        if (DiscoveryVerifyButton is not null) DiscoveryVerifyButton.IsEnabled = !_discoveryBusy && _reviewedAcquisitionPlan is not null;
        if (DiscoveryDestinationText is not null) DiscoveryDestinationText.IsEnabled = !_discoveryBusy;
        if (AcquisitionApprovalCheck is not null)
            AcquisitionApprovalCheck.IsEnabled = !_discoveryBusy && _reviewedAcquisitionPlan is not null;
        if (AcquisitionDownloadButton is not null)
            AcquisitionDownloadButton.IsEnabled = !_discoveryBusy && _reviewedAcquisitionPlan is { } plan
                && _approvedAcquisitionPlanId == plan.PlanId && AcquisitionApprovalCheck?.IsChecked == true;
        if (AcquisitionLibraryButton is not null)
            AcquisitionLibraryButton.IsEnabled = !_discoveryBusy && _lastAcquisitionResult?.Evidence.Matches == true;
    }

    private async void DiscoveryScanButton_Click(object sender, RoutedEventArgs e) =>
        await RunDiscoveryOperationAsync(async token =>
        {
            DiscoveryStatusText.Text = "Reading actual hardware, drivers, memory availability and fixed volumes...";
            await ScanDiscoveryHardwareAsync(token);
            DiscoveryResultsGrid.ItemsSource = null;
            DiscoveryDetailsText.Text = DescribeMachine(_discoveryMachine!);
            DiscoveryStatusText.Text = "Hardware evidence refreshed. Search public metadata or load a saved search. No inference workload was launched.";
        });

    private async Task ScanDiscoveryHardwareAsync(CancellationToken token)
    {
        await _preferencesReady.Task.WaitAsync(token);
        var selected = _discoveryTargetVolume ?? (DiscoveryVolumeSelector.SelectedItem as StorageVolume)?.Path;
        if (selected is null && !string.IsNullOrWhiteSpace(_settings.LlamaCppModelPath))
            selected = Path.GetPathRoot(_settings.LlamaCppModelPath);
        selected ??= Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        _discoveryTargetVolume = selected;
        var machine = await HardwareCapabilityScanner.ScanAsync(token);
        token.ThrowIfCancellationRequested();
        _discoveryMachine = machine;
        DiscoveryHardwareText.Text = $"{machine.Hardware.CpuName} | {machine.OsArchitecture} | {machine.Hardware.RamGb:N1} GiB RAM | " +
            $"{machine.Hardware.Gpus.Count} GPU(s) | scanned {machine.ScannedAt.ToLocalTime():HH:mm:ss}";
        DiscoveryVolumeSelector.ItemsSource = machine.Volumes;
        DiscoveryVolumeSelector.SelectedItem = machine.Volumes.FirstOrDefault(volume => string.Equals(volume.Path, selected, StringComparison.OrdinalIgnoreCase));
    }

    private async void DiscoverySearchButton_Click(object sender, RoutedEventArgs e) => await RunDiscoverySearchAsync(online: true);
    private async void DiscoverySavedButton_Click(object sender, RoutedEventArgs e) => await RunDiscoverySearchAsync(online: false);

    private async void RuntimeReleaseLookupButton_Click(object sender, RoutedEventArgs e) => await RunRuntimeReleaseLookupAsync(true);
    private async void RuntimeReleaseSavedButton_Click(object sender, RoutedEventArgs e) => await RunRuntimeReleaseLookupAsync(false);

    private async Task RunRuntimeReleaseLookupAsync(bool online)
    {
        var tag = RuntimeReleaseTagText.Text.Trim();
        if (tag.Length == 0)
        {
            DiscoveryStatusText.Text = "Enter an exact upstream runtime release tag. No release will be chosen automatically.";
            return;
        }
        await RunDiscoverySearchAsync(online, tag);
    }

    private async Task RunDiscoverySearchAsync(bool online, string? runtimeTag = null)
    {
        if (DiscoveryInterestSelector.SelectedItem is not DiscoveryWorkflow workflow || _discoveryPipeline is null) return;
        var request = runtimeTag is null ? new OnlineDiscoveryRequest(workflow.Interest, DiscoveryQueryText.Text.Trim())
            : new OnlineDiscoveryRequest(DiscoveryInterest.LocalAssistants, "", 1, runtimeTag);
        await RunDiscoveryOperationAsync(async token =>
        {
            DiscoveryResultsGrid.ItemsSource = null;
            _lastDiscoveryReport = null;
            InvalidateAcquisitionReview();
            DiscoveryDetailsText.Text = "Reading current hardware evidence before evaluating candidates...";
            DiscoveryStatusText.Text = "Refreshing local memory and selected-volume availability...";
            await ScanDiscoveryHardwareAsync(token);
            if (DiscoveryVolumeSelector.SelectedItem is not StorageVolume volume)
            {
                DiscoveryStatusText.Text = "No selected fixed volume is available. Resolve storage access and rescan before planning downloads.";
                return;
            }
            DiscoveryStatusText.Text = online ? "Searching public metadata; no model weights or installers are being downloaded..." : "Loading saved metadata without online requests...";
            var report = online
                ? await _discoveryPipeline.SearchOnlineAsync(request, _discoveryMachine!, volume.Path, token: token)
                : await _discoveryPipeline.LoadSavedAsync(request, _discoveryMachine!, volume.Path, token: token);
            token.ThrowIfCancellationRequested();
            RenderDiscoveryReport(report);
        });
    }

    private async Task RunDiscoveryOperationAsync(Func<CancellationToken, Task> action)
    {
        if (_discoveryBusy || _lifetime.IsCancellationRequested) return;
        _discoveryBusy = true;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _discoveryOperation = cancellation;
        RefreshDiscoveryControls();
        try { await action(cancellation.Token); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!_lifetime.IsCancellationRequested) DiscoveryStatusText.Text = "Operation cancelled. Existing files are preserved; no runtime or installer was started.";
        }
        catch (Exception ex)
        {
            if (!_lifetime.IsCancellationRequested) DiscoveryStatusText.Text = $"Discovery could not complete: {ex.Message}";
        }
        finally
        {
            _discoveryOperation = null;
            _discoveryBusy = false;
            if (!_lifetime.IsCancellationRequested) RefreshDiscoveryControls();
        }
    }

    private void DiscoveryCancelButton_Click(object sender, RoutedEventArgs e) => _discoveryOperation?.Cancel();

    private void RuntimeSetupReviewButton_Click(object sender, RoutedEventArgs e)
    {
        if (_discoveryBusy || _lastDiscoveryReport is null || DiscoveryResultsGrid.SelectedItem is not DiscoveryRow row
            || row.Recommendation.Item.RuntimeRelease is null) return;
        InvalidateAcquisitionReview();
        try
        {
            var review = RuntimeSetupPlanner.Create(_lastDiscoveryReport, row.Recommendation.Item.Id, RuntimeDestinationText.Text.Trim());
            DiscoveryDetailsText.Text = RuntimeSetupPlanner.Describe(review);
            DiscoveryStatusText.Text = "Runtime setup review prepared. No download, installation, compilation or approval has occurred.";
        }
        catch (Exception ex) { DiscoveryStatusText.Text = $"Runtime setup review requires attention: {ex.Message}"; }
        RefreshDiscoveryControls();
    }

    private void RuntimeArchiveSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        InvalidateRuntimeReviewDisplay();
        if (RuntimeFileReviewButton is not null) RefreshDiscoveryControls();
    }

    private void RuntimeDestinationText_TextChanged(object sender, TextChangedEventArgs e) => InvalidateRuntimeReviewDisplay();

    private void InvalidateRuntimeReviewDisplay()
    {
        _reviewedRuntimeFiles = null;
        if (RuntimeInspectButton is not null) RuntimeInspectButton.IsEnabled = false;
        if (DiscoveryDetailsText is not null && (DiscoveryDetailsText.Text.StartsWith("EXACT RUNTIME FILE REVIEW", StringComparison.Ordinal)
            || DiscoveryDetailsText.Text.StartsWith("RUNTIME SETUP REVIEW", StringComparison.Ordinal)))
        {
            DiscoveryDetailsText.Text = "Runtime selection or destination changed. Prepare a new review for the current inputs.";
            DiscoveryStatusText.Text = "Runtime review invalidated. No installation was approved.";
        }
    }

    private void RuntimeFileReviewButton_Click(object sender, RoutedEventArgs e)
    {
        if (_discoveryBusy || _lastDiscoveryReport is null || DiscoveryResultsGrid.SelectedItem is not DiscoveryRow row
            || RuntimeArchiveSelector.SelectedItem is not RuntimeReleaseAsset archive) return;
        InvalidateAcquisitionReview();
        try
        {
            var plan = RuntimeFilePlanner.Create(_lastDiscoveryReport, row.Recommendation.Item.Id, RuntimeDestinationText.Text.Trim(), archive.Name);
            _reviewedRuntimeFiles = plan;
            DiscoveryDetailsText.Text = RuntimeFilePlanner.Describe(plan);
            DiscoveryStatusText.Text = "Exact runtime files reviewed. No download or installation approved or performed.";
        }
        catch (Exception ex) { DiscoveryStatusText.Text = $"Runtime file review requires attention: {ex.Message}"; }
        RefreshDiscoveryControls();
    }

    private async void RuntimeInspectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_discoveryBusy || _reviewedRuntimeFiles is not { } plan) return;
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose the local folder containing the exact reviewed ZIP files", Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        await RunDiscoveryOperationAsync(async token =>
        {
            DiscoveryStatusText.Text = "Hashing the selected local archives and inspecting ZIP paths and declared sizes. Nothing will be extracted or run...";
            var result = await RuntimeArchiveInspector.InspectAsync(plan, dialog.FolderName, token);
            var text = new StringBuilder(RuntimeFilePlanner.Describe(plan));
            text.AppendLine($"\nLOCAL ARCHIVE INSPECTION\nEntries: {result.Entries.Count}\nDeclared expanded bytes: {result.DeclaredExpandedBytes:N0}\nRoot server filename present: {result.HasServer}");
            foreach (var entry in result.Entries.Take(100)) text.AppendLine($"{entry.Archive} | {entry.RelativePath} | {entry.DeclaredBytes:N0} bytes");
            if (result.Entries.Count > 100) text.AppendLine("Only the first 100 entries are displayed; all entries were checked.");
            foreach (var limitation in result.Limitations) text.AppendLine("• " + limitation);
            DiscoveryDetailsText.Text = text.ToString();
            DiscoveryStatusText.Text = "Archive identities and metadata inspected. No extraction, installation or execution was performed.";
        });
    }

    internal void RenderDiscoveryReport(DiscoveryReport report)
    {
        _lastDiscoveryReport = report;
        InvalidateAcquisitionReview();
        var rows = report.Recommendations.Select(recommendation => new DiscoveryRow(recommendation, report)).ToArray();
        DiscoveryResultsGrid.ItemsSource = rows;
        if (rows.Length > 0) DiscoveryResultsGrid.SelectedIndex = 0;
        else DiscoveryDetailsText.Text = string.Join(Environment.NewLine, report.Catalog.Notes);
        var retrieval = report.Catalog.RetrievedAt == DateTimeOffset.MinValue ? "no saved timestamp" : report.Catalog.RetrievedAt.ToLocalTime().ToString("g");
        DiscoveryStatusText.Text = $"{rows.Length} results | {(report.Catalog.IsCached ? "SAVED metadata; not refreshed online" : "Online metadata")} retrieved {retrieval} | " +
            $"Target {report.TargetVolume} | Resource estimates only; no candidate has been installed or verified by this search.";
        if (string.IsNullOrWhiteSpace(DiscoveryDestinationText.Text))
            DiscoveryDestinationText.Text = Path.Combine(report.TargetVolume, "AINexus", "Models");
        RefreshDiscoveryControls();
    }

    private void DiscoveryResultsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        InvalidateRuntimeReviewDisplay();
        InvalidateAcquisitionReview();
        if (RuntimeArchiveSelector is not null)
        {
            RuntimeArchiveSelector.ItemsSource = DiscoveryResultsGrid.SelectedItem is DiscoveryRow { Recommendation.Item.RuntimeRelease: { } runtime } selected
                ? runtime.Archives.Where(asset => RuntimeSetupPlanner.Assess(asset, runtime.Tag, selected.Report.Machine).Role == "Runtime archive").ToArray()
                : null;
            RuntimeArchiveSelector.SelectedIndex = -1;
        }
        if (DiscoveryResultsGrid.SelectedItem is DiscoveryRow row) DiscoveryDetailsText.Text = DescribeRecommendation(row);
        if (StoreSelectionTitleText is not null && StoreSelectionSummaryText is not null)
        {
            if (DiscoveryResultsGrid.SelectedItem is DiscoveryRow selectedRow)
            {
                StoreSelectionTitleText.Text = selectedRow.Name;
                StoreSelectionSummaryText.Text = selectedRow.StatusDisplay + ". " +
                    (selectedRow.Recommendation.EstimatedMemoryGiB is { } memory ? $"Estimated memory: {memory:N1} GiB. " : "") +
                    "Review the requirements before setup. Discovery does not mean installed or verified.";
            }
            else { StoreSelectionTitleText.Text = "Choose a result"; StoreSelectionSummaryText.Text = "Select an item to review its requirements."; }
        }
        if (DiscoveryPlanButton is not null) RefreshDiscoveryControls();
    }

    private static string DescribeMachine(MachineCapabilities machine)
    {
        var text = new StringBuilder();
        text.AppendLine($"Hardware evidence captured {machine.ScannedAt.ToLocalTime():g}");
        text.AppendLine($"OS: {machine.OsDescription} ({machine.OsArchitecture})");
        text.AppendLine($"CPU: {machine.Hardware.CpuName}; logical processors: {machine.LogicalProcessors}; usable AVX: {machine.AvxSupported}; AVX2: {machine.Avx2Supported}");
        text.AppendLine($"RAM: {machine.Hardware.RamGb:N1} GiB detected; currently available: {(machine.Resources.RamTotalGb - machine.Resources.RamUsedGb)?.ToString("N1") ?? "unknown"} GiB");
        foreach (var driver in machine.DriverEvidence)
            text.AppendLine($"Driver evidence: {driver.AdapterName} | version {driver.DriverVersion ?? "unknown"} | compute capability {driver.ComputeCapability ?? "unknown"} | {driver.Source} | {driver.Status}");
        foreach (var volume in machine.Volumes) text.AppendLine($"Volume {volume.Path}: {volume.AvailableBytes / 1073741824d:N1} GiB available");
        foreach (var note in machine.Notes) text.AppendLine(note);
        return text.ToString();
    }

    private static string DescribeRecommendation(DiscoveryRow row)
    {
        var result = row.Recommendation;
        var item = result.Item;
        var text = new StringBuilder();
        text.AppendLine($"{item.Name} — {row.StatusDisplay}");
        text.AppendLine($"Publisher: {item.Publisher}; declared license: {item.License ?? "unknown; review source terms"}");
        text.AppendLine(item.Description);
        if (item.RuntimeRelease is { } runtime)
        {
            text.AppendLine($"Release: {runtime.Tag} | Prerelease: {runtime.Prerelease} | Upstream immutable: {runtime.Immutable}");
            foreach (var archive in runtime.Archives)
                text.AppendLine($"Published archive: {archive.Name}\nBytes: {archive.SizeBytes:N0}\nSHA-256: {archive.Sha256 ?? "not supplied"}\n{archive.DownloadUrl}");
            foreach (var note in runtime.Notes) text.AppendLine(note);
        }
        text.AppendLine($"Execution path: {result.ExecutionPath}");
        text.AppendLine($"Estimated working memory: {result.EstimatedMemoryGiB?.ToString("N2") ?? "unknown"} GiB; model storage estimate: {result.RequiredStorageGiB?.ToString("N2") ?? "unknown"} GiB on {row.Report.TargetVolume}");
        if (item.Artifact is { } artifact)
        {
            text.AppendLine($"Repository: {artifact.Repository}\nRevision: {artifact.Revision}\nFile: {artifact.FileName}\nFormat: {artifact.Format}\nSHA-256: {artifact.Sha256 ?? "not reported"}\nAccess gated: {artifact.Gated}");
        }
        text.AppendLine("\nAssessment:");
        foreach (var reason in result.Reasons) text.AppendLine("• " + reason);
        text.AppendLine("\nAssumptions and unknowns:");
        foreach (var limitation in result.Limitations) text.AppendLine("• " + limitation);
        text.AppendLine("\nSource evidence:");
        foreach (var source in item.Sources) text.AppendLine($"{source.Name}\n{source.Url}\nRetrieved {source.RetrievedAt.ToLocalTime():g}");
        foreach (var note in row.Report.Catalog.Notes) text.AppendLine(note);
        text.AppendLine($"\nHardware evaluated at {row.Report.Machine.ScannedAt.ToLocalTime():g}. Saved metadata: {row.Report.Catalog.IsCached}.");
        text.AppendLine("No installation, compilation or runtime verification was performed. No performance benchmark is inferred.");
        return text.ToString();
    }

    private sealed record DiscoveryRow(CompatibilityRecommendation Recommendation, DiscoveryReport Report)
    {
        public string Name => Recommendation.Item.Name;
        public string Publisher => Recommendation.Item.Publisher;
        public string SizeDisplay => Recommendation.Item.Artifact?.SizeBytes is { } size ? (size / 1073741824d).ToString("N2") : "Unknown";
        public string LicenseDisplay => Recommendation.Item.License ?? "Unknown";
        public string ExecutionPath => Recommendation.ExecutionPath;
        public string StatusDisplay => Recommendation.Status switch
        {
            RecommendationStatus.ResourceCandidate => "Estimated to fit",
            RecommendationStatus.InsufficientResources => "Not enough resources",
            RecommendationStatus.Unsupported => "Not supported yet",
            _ => "Needs review"
        };
    }
}
