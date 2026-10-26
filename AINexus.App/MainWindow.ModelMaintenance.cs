using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace AINexus.App;

public partial class MainWindow
{
    private CancellationTokenSource? _maintenanceOperation;
    private const string MaintenanceNotice = "Select a GGUF file to check against an acquisition receipt. This reads local bytes only; it does not verify runtime readiness.";

    private void ModelFileGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _maintenanceOperation?.Cancel();
        ResetMaintenanceEvidence();
    }

    private void ResetMaintenanceEvidence()
    {
        if (LibraryMaintenanceStatusText is null) return;
        LibraryMaintenanceStatusText.Text = MaintenanceNotice;
        RefreshMaintenanceControls();
    }

    private void RefreshMaintenanceControls()
    {
        if (LibraryCheckButton is null || LibraryCancelCheckButton is null) return;
        LibraryCheckButton.IsEnabled = !_libraryBusy && ModelFileGrid.SelectedItem is ModelFile { Format: "GGUF" };
        LibraryCancelCheckButton.IsEnabled = _maintenanceOperation is not null;
    }

    private async void LibraryCheckButton_Click(object sender, RoutedEventArgs e)
    {
        if (_libraryBusy || ModelFileGrid.SelectedItem is not ModelFile { Format: "GGUF" } selected) return;
        ResetMaintenanceEvidence();
        var picker = new OpenFileDialog
        {
            Title = "Select this model's AI Nexus acquisition receipt",
            Filter = "Acquisition receipt (*.json)|*.json",
            CheckFileExists = true,
            Multiselect = false
        };
        if (picker.ShowDialog(this) != true) return;
        await CheckLibraryModelAsync(selected, picker.FileName);
    }

    private void LibraryCancelCheckButton_Click(object sender, RoutedEventArgs e)
    {
        _maintenanceOperation?.Cancel();
        LibraryMaintenanceStatusText.Text = "Cancelling integrity check...";
    }

    internal async Task CheckLibraryModelAsync(ModelFile selected, string receiptPath)
    {
        if (_libraryBusy || _lifetime.IsCancellationRequested || !ReferenceEquals(ModelFileGrid.SelectedItem, selected)) return;
        ResetMaintenanceEvidence();
        _libraryBusy = true;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _maintenanceOperation = operation;
        LibraryAddButton.IsEnabled = LibraryRemoveButton.IsEnabled = LibraryRescanButton.IsEnabled = ModelFileGrid.IsEnabled = false;
        RefreshMaintenanceControls();
        LibraryMaintenanceStatusText.Text = "Reading the selected acquisition receipt and model bytes. Large files can take time; no files are changed.";
        try
        {
            var result = await ModelMaintenance.CheckAsync(selected.FullPath, receiptPath, operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(ModelFileGrid.SelectedItem, selected)) return;
            LibraryMaintenanceStatusText.Text = $"{result.StatusDisplay} — checked {result.CheckedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}\n{result.Summary}";
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            if (!_lifetime.IsCancellationRequested) LibraryMaintenanceStatusText.Text = "Integrity check cancelled. No current result is available; files were preserved.";
        }
        catch (Exception ex)
        {
            if (!_lifetime.IsCancellationRequested) LibraryMaintenanceStatusText.Text = $"Integrity check unavailable: {ex.Message}\nNo successful result was recorded. Files were preserved.";
        }
        finally
        {
            _maintenanceOperation = null;
            _libraryBusy = false;
            if (!_lifetime.IsCancellationRequested)
            {
                LibraryAddButton.IsEnabled = LibraryRemoveButton.IsEnabled = LibraryRescanButton.IsEnabled = ModelFileGrid.IsEnabled = true;
                RefreshMaintenanceControls();
            }
        }
    }
}
