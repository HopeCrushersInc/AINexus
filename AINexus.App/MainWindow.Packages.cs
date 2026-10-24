using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace AINexus.App;

public partial class MainWindow
{
    private bool _packageBusy;
    private bool _packageInstalling;
    private ManagedPackage? _reviewedPackage;

    private void InitializePackages()
    {
        PackageSelector.ItemsSource = PackageManager.Catalog;
        PackageSelector.SelectedIndex = 0;
        Closing += PreventClosingDuringInstall;
    }

    private void PreventClosingDuringInstall(object? sender, CancelEventArgs e)
    {
        if (!_packageInstalling) return;
        e.Cancel = true;
        PackageTab.IsSelected = true;
        PackageStatusText.Text = "An installer is still running. Wait for its result before closing the portal.";
    }

    private void PackageSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _reviewedPackage = null;
        PackageAgreementCheck.IsChecked = false;
        if (PackageSelector.SelectedItem is PackageCatalogEntry entry)
        {
            PackagePlanText.Text = $"{entry.Name} | Package: {entry.PackageId} | Source: winget community | Scope: current user. " + entry.Restriction;
            PackageOutputText.Text = "Review the current package metadata, including license information, before installing or updating.";
        }
        UpdatePackageControls();
    }

    private void PackageAgreementChanged(object sender, RoutedEventArgs e) => UpdatePackageControls();

    private void UpdatePackageControls()
    {
        // During InitializeComponent not all named controls exist yet.
        if (PackageInstallButton is null || PackageUpdateButton is null) return;
        var entry = PackageSelector.SelectedItem as PackageCatalogEntry;
        var reviewed = entry is not null && _reviewedPackage == entry.Package;
        PackageSelector.IsEnabled = !_packageBusy;
        PackagePreviewButton.IsEnabled = !_packageBusy && entry is not null;
        PackageAgreementCheck.IsEnabled = !_packageBusy && reviewed && entry!.CanModify;
        var canExecute = !_packageBusy && reviewed && entry!.CanModify && PackageAgreementCheck.IsChecked == true;
        PackageInstallButton.IsEnabled = canExecute;
        PackageUpdateButton.IsEnabled = canExecute;
    }

    private async void PackagePreviewButton_Click(object sender, RoutedEventArgs e)
    {
        if (_packageBusy || PackageSelector.SelectedItem is not PackageCatalogEntry entry) return;
        _packageBusy = true;
        _reviewedPackage = null;
        PackageAgreementCheck.IsChecked = false;
        UpdatePackageControls();
        PackageStatusText.Text = "Reading current WinGet package metadata...";
        try
        {
            var result = await PackageManager.PreviewAsync(entry.Package, _lifetime.Token);
            if (_lifetime.IsCancellationRequested) return;
            DisplayPackageResult(result);
            if (result.Succeeded) _reviewedPackage = entry.Package;
            PackageStatusText.Text = result.Succeeded
                ? entry.CanModify ? "Review complete. Read the terms before accepting and choosing an operation." : entry.Restriction
                : "Package review failed. Details are shown above; installation remains disabled.";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { PackageStatusText.Text = $"Package review failed: {ex.Message}"; }
        finally
        {
            _packageBusy = false;
            UpdatePackageControls();
        }
    }

    private async void PackageInstallButton_Click(object sender, RoutedEventArgs e) => await ExecutePackageAsync(PackageAction.Install);
    private async void PackageUpdateButton_Click(object sender, RoutedEventArgs e) => await ExecutePackageAsync(PackageAction.Update);

    private async Task ExecutePackageAsync(PackageAction action)
    {
        if (_packageBusy || PackageSelector.SelectedItem is not PackageCatalogEntry entry || !entry.CanModify ||
            _reviewedPackage != entry.Package || PackageAgreementCheck.IsChecked != true) return;
        _packageBusy = _packageInstalling = true;
        UpdatePackageControls();
        PackageStatusText.Text = $"{action}: {entry.Name}. Waiting for WinGet to finish. Closing is disabled during this operation.";
        try
        {
            var result = await PackageManager.ExecuteAsync(entry.Package, action, acceptAgreements: true);
            DisplayPackageResult(result);
            PackageStatusText.Text = result.Succeeded
                ? $"WinGet reported completion for {entry.Name}. Refreshing discovery."
                : $"WinGet did not report success ({result.ExitCodeDisplay}). Review the captured output.";
            try
            {
                var log = await SavePackageResultAsync(entry, action, result);
                PackageStatusText.Text += $" Result log: {log}";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                PackageStatusText.Text += " Result could not be saved to the application log folder.";
            }
            await ScanSoftwareAsync();
        }
        catch (Exception ex) { PackageStatusText.Text = $"Operation failed: {ex.Message}. Check the actual installation state before retrying."; }
        finally
        {
            _packageBusy = _packageInstalling = false;
            _reviewedPackage = null;
            PackageAgreementCheck.IsChecked = false;
            UpdatePackageControls();
        }
    }

    private void DisplayPackageResult(PackageOperationResult result)
    {
        PackageOutputText.Text = $"Exit: {result.ExitCodeDisplay}{Environment.NewLine}{result.Output}" +
            (string.IsNullOrWhiteSpace(result.Error) ? "" : $"{Environment.NewLine}{result.Error}");
    }

    private static async Task<string> SavePackageResultAsync(PackageCatalogEntry entry, PackageAction action, PackageOperationResult result)
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AINexus", "Logs");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"package-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { Timestamp = DateTimeOffset.Now, entry.PackageId, Action = action.ToString(), Result = result },
            new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }
}
