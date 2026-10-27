using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace AINexus.App;

public partial class MainWindow
{
    private bool _catalogReviewBusy;

    internal static bool CatalogInstallationMatches(CatalogLibraryRow row, CatalogInstallation installation)
    {
        if (row.Record.Item.Kind != "Model" || row.Record.Item.Artifact is not { Format: "GGUF", Gated: false, SizeBytes: > 0, Sha256: not null } artifact
            || installation.Plan is not { } plan || !row.Installations.Contains(installation)
            || !installation.FilePresent || installation.ActualBytes != artifact.SizeBytes
            || plan.Artifact != artifact || row.Record.Id != installation.CatalogId
            || row.Record.Id != CatalogDatabase.CanonicalId(row.Record.Item)
            || plan.PlanId != ModelAcquisitionPlanner.ComputeFingerprint(plan)) return false;
        return Path.IsPathFullyQualified(installation.ModelPath)
            && string.Equals(Path.GetFullPath(installation.ModelPath), plan.DestinationPath, StringComparison.OrdinalIgnoreCase);
    }

    private void CatalogInstallationSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => RefreshCatalogLocalControls();

    private void RefreshCatalogLocalControls()
    {
        if (CatalogReviewLocalButton is null || CatalogInstallationSelector is null) return;
        CatalogReviewLocalButton.IsEnabled = !_catalogReviewBusy && !_guidedBusy && !_discoveryBusy
            && !_lifetime.IsCancellationRequested && _workspaceConfigured && _workloadMachine is not null
            && CatalogLibraryGrid.SelectedItem is CatalogLibraryRow row
            && CatalogInstallationSelector.SelectedItem is CatalogInstallation installation
            && CatalogInstallationMatches(row, installation);
    }

    private async void CatalogReviewLocalButton_Click(object sender, RoutedEventArgs e)
        => await ReviewCatalogLocalModelAsync();

    internal async Task ReviewCatalogLocalModelAsync()
    {
        if (_catalogReviewBusy || _guidedBusy || _discoveryBusy || _lifetime.IsCancellationRequested
            || !_workspaceConfigured || _workloadMachine is null) return;
        // A new explicit review attempt revokes the previous preparation, including on failure.
        _guidedModel = null;
        _guidedUserSelection = null;
        _guidedUserSnapshot = null;
        _guidedModelPlan = null;
        _guidedReceipt = null;
        _guidedInstalledOnly = false;
        GuidedModelApprovalCheck.IsChecked = false;
        InvalidateGuidedRuntimeReview();
        RefreshGuidedControls();
        if (CatalogLibraryGrid.SelectedItem is not CatalogLibraryRow row
            || CatalogInstallationSelector.SelectedItem is not CatalogInstallation installation
            || !CatalogInstallationMatches(row, installation))
        {
            CatalogLibraryStatusText.Text = "Choose a model and one matching installed copy. Missing files and conflicting receipts need review before loading.";
            return;
        }
        _catalogReviewBusy = true;
        GuidedStatusText.Text = "Reading the selected local acquisition receipt. No model or engine is being started.";
        RefreshCatalogLocalControls();
        try
        {
            await RunGuidedOperationAsync(async token =>
            {
                var receipt = await OfflineModelVerifier.ReadReceiptAsync(installation.ReceiptPath, token);
                token.ThrowIfCancellationRequested();
                if (!ReferenceEquals(CatalogLibraryGrid.SelectedItem, row)
                    || !ReferenceEquals(CatalogInstallationSelector.SelectedItem, installation)
                    || !_workspaceConfigured)
                {
                    GuidedStatusText.Text = "The Library selection changed during review. Choose the installed copy and review it again.";
                    return;
                }
                var plan = receipt.Plan;
                if (plan.PlanId != installation.Plan!.PlanId || plan.Artifact != row.Record.Item.Artifact
                    || !string.Equals(plan.DestinationPath, installation.ModelPath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The receipt changed since this Library listing. Refresh the Library and choose the installed copy again.");
                OfflineModelVerifier.CheckNoReparsePath(plan.DestinationPath);
                var modelFile = new FileInfo(plan.DestinationPath);
                if (!modelFile.Exists || modelFile.Length != plan.Artifact.SizeBytes)
                    throw new InvalidDataException("The selected installed file is missing or its size changed. No model was loaded.");

                // Text-change handlers revoke existing reviews. Bind the selected immutable plan last.
                _guidedModel = null;
                _guidedUserSelection = row.Record.Item;
                _guidedUserSnapshot = null;
                _guidedModelPlan = null;
                _guidedReceipt = null;
                _guidedInstalledOnly = false;
                GuidedModelApprovalCheck.IsChecked = false;
                InvalidateGuidedRuntimeReview();
                GuidedModelFolderText.Text = Path.GetDirectoryName(plan.DestinationPath)!;
                GuidedRuntimePathText.Text = _settings.LlamaCppExecutablePath;
                _guidedModelPlan = plan;
                _guidedReceipt = installation.ReceiptPath;
                _guidedInstalledOnly = true;
                GuidedModelTitleText.Text = plan.Name;
                GuidedModelDetailsText.Text = $"INSTALLED MODEL REVIEW\n{plan.Name}\nPath: {plan.DestinationPath}\n"
                    + $"Repository: {plan.Artifact.Repository}\nRevision: {plan.Artifact.Revision}\nFile: {plan.Artifact.FileName}\n"
                    + $"Bytes: {plan.Artifact.SizeBytes:N0}\nExpected SHA-256: {plan.Artifact.Sha256}\n"
                    + $"Recorded license: {plan.DeclaredLicense}\nPinned terms: {plan.LicenseReference}\n"
                    + $"Source evidence date: {plan.MetadataRetrievedAt.ToLocalTime():g}\n\n"
                    + "This is historical acquisition evidence, not a fresh source search or integrity check. "
                    + "No network request, download or runtime launch has occurred. Load model explicitly to check the file hash and current hardware, then test local generation.";
                GuidedModelStep.IsExpanded = true;
                GuidedEngineStep.IsExpanded = string.IsNullOrWhiteSpace(GuidedRuntimePathText.Text);
                GuidedProgress.Value = 0;
                GuidedStatusText.Text = "Installed copy selected. Review its saved source, choose an existing engine, then explicitly Load model and open chat. Nothing is loaded yet.";
                GuidedSetupTab.IsSelected = true;
                RefreshGuidedControls();
            });
            if (_guidedModelPlan is null && !_lifetime.IsCancellationRequested)
                CatalogLibraryStatusText.Text = "No local model was prepared. " + GuidedStatusText.Text;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            CatalogLibraryStatusText.Text = "Local model review could not complete. " + ex.Message;
        }
        finally { _catalogReviewBusy = false; RefreshCatalogLocalControls(); }
    }
}
