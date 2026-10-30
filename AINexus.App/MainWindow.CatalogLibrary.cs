using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace AINexus.App;

public partial class MainWindow
{
    private CatalogDatabase? _catalogDatabase;
    private readonly SemaphoreSlim _catalogRefreshGate = new(1, 1);
    private IReadOnlyList<CatalogRecord> _catalogRecords = [];
    private CatalogInstallationSnapshot _catalogInstallations = new([], []);

    private void InitializeCatalogLibrary()
    {
        Loaded += async (_, _) =>
        {
            try
            {
                await _preferencesReady.Task.WaitAsync(_lifetime.Token);
                var directory = _settings.CatalogDirectory;
                if (string.IsNullOrWhiteSpace(directory))
                {
                    var marker = Path.Combine(AppContext.BaseDirectory, "catalog-root.txt");
                    if (File.Exists(marker))
                    {
                        OfflineModelVerifier.CheckNoReparsePath(marker);
                        if (new FileInfo(marker).Length > 2048) throw new InvalidDataException("Portable catalog location file is oversized.");
                        directory = (await File.ReadAllTextAsync(marker, _lifetime.Token)).Trim();
                    }
                    else directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AINexus", "Catalog");
                }
                await ConfigureCatalogLibraryAsync(directory);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception ex) { CatalogLibraryStatusText.Text = "Catalog unavailable; existing data was preserved. " + ex.Message; }
        };
    }

    internal async Task ConfigureCatalogLibraryAsync(string directory)
    {
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("Choose an absolute catalog folder.");
        var store = new CatalogDatabase(Path.Combine(directory, "catalog.db"));
        // Read first: an unknown or damaged database never replaces the current selection.
        await store.ReadAsync(token: _lifetime.Token);
        _catalogDatabase = store;
        _discoveryPipeline = new(new VerifiedOnlineDiscoveryProvider(_discoveryHttp), DiscoveryCacheStore.ForCurrentUser(), store);
        CatalogLocationText.Text = store.DatabasePath;
        await RefreshCatalogLibraryAsync();
    }

    internal async Task RefreshCatalogLibraryAsync()
    {
        if (_catalogDatabase is not { } store || _lifetime.IsCancellationRequested) return;
        try { await _catalogRefreshGate.WaitAsync(_lifetime.Token); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
        CatalogRefreshButton.IsEnabled = CatalogFolderButton.IsEnabled = false;
        try
        {
            _catalogRecords = [];
            _catalogInstallations = new([], []);
            CatalogLibraryGrid.ItemsSource = null;
            CatalogLibraryStatusText.Text = "Reading saved source metadata and private acquisition receipts. No online requests or model-byte scans.";
            var records = await store.ReadAsync(token: _lifetime.Token);
            var installations = await CatalogLibraryIndex.ScanAsync(_settings.ModelRoots, _lifetime.Token);
            _lifetime.Token.ThrowIfCancellationRequested();
            _catalogRecords = records;
            _catalogInstallations = installations;
            RenderCatalogLibraryRows();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { CatalogLibraryStatusText.Text = "Catalog could not be read; existing data was preserved. " + ex.Message; }
        finally
        {
            _catalogRefreshGate.Release();
            if (!_lifetime.IsCancellationRequested) CatalogRefreshButton.IsEnabled = CatalogFolderButton.IsEnabled = true;
        }
    }

    private async void CatalogRefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshCatalogLibraryAsync();
    private async void CatalogFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_discoveryBusy) { CatalogLibraryStatusText.Text = "Finish or cancel the current source search before opening another catalog."; return; }
        var dialog = new OpenFolderDialog { Title = "Open a local catalog folder (existing data is never moved or deleted)" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            await ConfigureCatalogLibraryAsync(dialog.FolderName);
            _settings = _settings with { CatalogDirectory = dialog.FolderName };
            await SaveSettingsAsync();
        }
        catch (Exception ex) { CatalogLibraryStatusText.Text = "Catalog folder could not be opened. " + ex.Message; }
    }

    private void CatalogFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (CatalogLibraryGrid is not null) RenderCatalogLibraryRows();
    }

    private void RenderCatalogLibraryRows()
    {
        if (CatalogLibraryGrid is null || _catalogDatabase is null) return;
        var records = _catalogRecords.ToDictionary(r => r.Id, StringComparer.Ordinal);
        // Receipts are private historical evidence, not freshly fetched public metadata.
        // Show local acquisitions even when their source has not been searched this session.
        foreach (var installation in _catalogInstallations.Installations)
        {
            if (records.ContainsKey(installation.CatalogId) || installation.Plan is not { } plan) continue;
            var item = new DiscoveredItem(installation.CatalogId, plan.Name, plan.Artifact.Repository.Split('/')[0],
                DiscoveryInterest.LocalAssistants, "Model", plan.DeclaredLicense, plan.Artifact,
                [new("Historical acquisition source", plan.LicenseReference, plan.MetadataRetrievedAt)],
                "From a private acquisition receipt. Source freshness, current integrity and runtime success have not been established by this listing.", "text-generation");
            records.Add(installation.CatalogId, new(installation.CatalogId, item, [], plan.MetadataRetrievedAt));
        }
        var query = CatalogFilterText.Text.Trim();
        var rows = records.Values.Select(record =>
        {
            var local = _catalogInstallations.Installations.Where(i => i.CatalogId == record.Id).Select(i =>
                record.Item.Artifact is { } source && i.Plan?.Artifact is { } receipt
                && (source.Sha256 is { } hash && receipt.Sha256 is { } received && !hash.Equals(received, StringComparison.OrdinalIgnoreCase)
                    || source.SizeBytes is { } size && receipt.SizeBytes is { } receivedSize && size != receivedSize
                    || (source.AdditionalFiles is not null || receipt.AdditionalFiles is not null) && !ModelFileSet.SameIdentity(source, receipt))
                    ? i with { State = "Receipt conflicts with saved source identity; review required" } : i).ToArray();
            var machine = _discoveryMachine ?? _workloadMachine;
            var estimate = machine is not null && _discoveryTargetVolume is { } volume
                ? HardwareCompatibilityEngine.Evaluate(record.Item, machine, volume).Status switch
                {
                    RecommendationStatus.ResourceCandidate => "Estimated to fit (saved source)",
                    RecommendationStatus.InsufficientResources => "Not enough available resources",
                    RecommendationStatus.Unsupported => "Platform recipe needed",
                    _ => "Requirements need review"
                } : "Hardware scan needed";
            return new CatalogLibraryRow(record, local, estimate);
        }).Where(row => (!CatalogInstalledOnlyCheck.IsChecked.GetValueOrDefault() || row.Installations.Count > 0)
            && (query.Length == 0 || row.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || row.Record.Item.Publisher.Contains(query, StringComparison.OrdinalIgnoreCase)
                || row.Categories.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        CatalogLibraryGrid.ItemsSource = rows;
        CatalogLibraryGrid.SelectedIndex = -1;
        CatalogLibraryStatusText.Text = $"{rows.Length} shown · {_catalogRecords.Count} saved source entries · {_catalogInstallations.Installations.Count} private acquisition receipts. "
            + "Browsing stays offline. Saved listings do not establish current compatibility or offline readiness."
            + (_catalogInstallations.Warnings.Count > 0 ? "\n" + string.Join("\n", _catalogInstallations.Warnings.Take(8)) : "");
    }

    private void CatalogLibraryGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CatalogDetailsText is null) return;
        CatalogInstallationSelector.ItemsSource = (CatalogLibraryGrid.SelectedItem as CatalogLibraryRow)?.Installations;
        CatalogInstallationSelector.SelectedIndex = -1;
        RefreshCatalogLocalControls();
        CatalogSearchSourceButton.IsEnabled = CatalogLibraryGrid.SelectedItem is CatalogLibraryRow row
            && row.Record.Interests.Any(i => _workspaceProfile?.Interests.Contains(i) == true) && !_discoveryBusy;
        if (CatalogLibraryGrid.SelectedItem is not CatalogLibraryRow selected)
        {
            CatalogDetailsText.Text = "Choose an entry to see its sources, exact artifact and private installation receipts. Nothing is selected automatically.";
            return;
        }
        var item = selected.Record.Item;
        var text = new StringBuilder($"{item.Name}\n{item.Description}\n\nSource license: {item.License ?? "Unknown"}\nLast source evidence: {selected.Record.LastCheckedAt.ToLocalTime():g}\n{selected.Fit}\n");
        if (item.Artifact is { } artifact)
        {
            text.AppendLine($"Repository: {artifact.Repository}\nRevision: {artifact.Revision}\nFile: {artifact.FileName}\nBytes: {artifact.SizeBytes?.ToString() ?? "Unknown"}\nSHA-256: {artifact.Sha256 ?? "Unknown"}");
            if (artifact.AdditionalFiles is { } parts)
            {
                text.AppendLine($"Complete inventory: {parts.Count + 1} files; {ModelFileSet.TotalBytes(artifact):N0} total bytes. The hash above identifies the first file, not the whole set.");
                foreach (var part in parts) text.AppendLine($"Part: {part.FileName}\nBytes: {part.SizeBytes:N0}\nSHA-256: {part.Sha256}");
            }
        }
        foreach (var source in item.Sources) text.AppendLine($"\n{source.Name}\n{source.Url}\nRetrieved: {source.RetrievedAt.ToLocalTime():g}");
        foreach (var installation in selected.Installations)
            text.AppendLine($"\nPRIVATE LOCAL RECEIPT\n{installation.State}\n{installation.ModelPath}\n{installation.ReceiptPath}\nAcquired: {installation.ReceiptCompletedAt.ToLocalTime():g}");
        text.AppendLine("\nListing, resource estimates, acquisition integrity and successful offline testing are separate evidence. This page grants no install or launch approval.");
        CatalogDetailsText.Text = text.ToString();
    }

    private async void CatalogSearchSourceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_discoveryBusy || CatalogLibraryGrid.SelectedItem is not CatalogLibraryRow row) return;
        var workflow = (DiscoveryInterestSelector.ItemsSource as IEnumerable<DiscoveryWorkflow>)?
            .FirstOrDefault(w => row.Record.Interests.Contains(w.Interest));
        if (workflow is null) { CatalogLibraryStatusText.Text = "Choose this entry's workload in setup before searching its source."; return; }
        DiscoveryInterestSelector.SelectedItem = workflow;
        DiscoveryQueryText.Text = row.Record.Item.Artifact?.Repository ?? row.Record.Item.Publisher;
        DiscoveryTab.IsSelected = true;
        await RunDiscoverySearchAsync(online: true);
    }

    internal sealed record CatalogLibraryRow(CatalogRecord Record, IReadOnlyList<CatalogInstallation> Installations, string Fit)
    {
        public string Name => Record.Item.Name;
        public string Kind => Record.Item.Kind;
        public string Categories => Record.Interests.Count == 0 ? "Local receipt; task unclassified"
            : string.Join(", ", VerifiedOnlineDiscoveryProvider.Workflows.Where(w => Record.Interests.Contains(w.Interest)).Select(w => w.Name));
        public string Size => ByteSizeDisplay.ModelSize(Record.Item.Artifact is { AdditionalFiles: not null } artifact
            ? ModelFileSet.TotalBytes(artifact) : Record.Item.Artifact?.SizeBytes);
        public string LocalState => Installations.Count == 0 ? "Listed; no tracked acquisition receipt" : string.Join(" · ", Installations.Select(i => i.State).Distinct());
        public string Checked => Record.LastCheckedAt.ToLocalTime().ToString("g");
    }
}
