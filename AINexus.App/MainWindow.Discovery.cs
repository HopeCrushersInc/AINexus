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

    private async Task RunDiscoverySearchAsync(bool online)
    {
        if (DiscoveryInterestSelector.SelectedItem is not DiscoveryWorkflow workflow || _discoveryPipeline is null) return;
        var request = new OnlineDiscoveryRequest(workflow.Interest, DiscoveryQueryText.Text.Trim());
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
        InvalidateAcquisitionReview();
        if (DiscoveryResultsGrid.SelectedItem is DiscoveryRow row) DiscoveryDetailsText.Text = DescribeRecommendation(row);
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
            RecommendationStatus.ResourceCandidate => "Resource candidate",
            RecommendationStatus.InsufficientResources => "Insufficient resources",
            RecommendationStatus.Unsupported => "No supported recipe",
            _ => "More evidence needed"
        };
    }
}
