using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace AINexus.App;

public partial class MainWindow
{
    private GpuPowerPolicy _gpuPowerPolicy = new();
    private bool _gpuPowerBusy;

    private async void GpuPowerReadButton_Click(object sender, RoutedEventArgs e) => await ReadGpuPowerAsync();

    internal async Task ReadGpuPowerAsync()
    {
        if (_gpuPowerBusy || _lifetime.IsCancellationRequested) return;
        _gpuPowerBusy = true;
        GpuPowerApprovalCheck.IsChecked = false;
        GpuPowerWattsText.Clear();
        GpuPowerAdapterSelector.ItemsSource = null;
        RefreshGpuPowerControls();
        try
        {
            var snapshot = await _gpuPowerPolicy.InspectAsync(_lifetime.Token);
            _lifetime.Token.ThrowIfCancellationRequested();
            GpuPowerAdapterSelector.ItemsSource = snapshot.Adapters.Select(adapter => new GpuPowerChoice(adapter)).ToArray();
            GpuPowerAdapterSelector.SelectedIndex = -1;
            GpuPowerStatusText.Text = snapshot.Message + " Reading limits changes no power setting.";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { GpuPowerStatusText.Text = "Power inspection unavailable. Existing settings are unchanged. " + ex.Message; }
        finally { _gpuPowerBusy = false; RefreshGpuPowerControls(); }
    }

    private void GpuPowerInput_Changed(object sender, RoutedEventArgs e)
    {
        if (GpuPowerApprovalCheck is null) return;
        GpuPowerApprovalCheck.IsChecked = false;
        if (sender == GpuPowerAdapterSelector && GpuPowerWattsText is not null) GpuPowerWattsText.Clear();
        if (GpuPowerAdapterSelector.SelectedItem is GpuPowerChoice choice)
        {
            var a = choice.Adapter;
            GpuPowerRangeText.Text = $"{a.Name}\n{a.Uuid}\nConfigured: {Watts(a.ConfiguredWatts)} · Enforced: {Watts(a.EnforcedWatts)} · Supported range: {Watts(a.MinimumWatts)} to {Watts(a.MaximumWatts)}";
        }
        else GpuPowerRangeText.Text = "Choose a graphics card. No card or wattage is selected automatically.";
        RefreshGpuPowerControls();
    }

    private void GpuPowerApproval_Changed(object sender, RoutedEventArgs e)
    {
        if (GpuPowerApplyButton is not null) RefreshGpuPowerControls();
    }

    private void RefreshGpuPowerControls()
    {
        if (GpuPowerApplyButton is null) return;
        GpuPowerReadButton.IsEnabled = !_gpuPowerBusy;
        GpuPowerAdapterSelector.IsEnabled = GpuPowerWattsText.IsEnabled = GpuPowerApprovalCheck.IsEnabled = !_gpuPowerBusy;
        GpuPowerApplyButton.IsEnabled = !_gpuPowerBusy && GpuPowerApprovalCheck.IsChecked == true && TryGetGpuPowerRequest(out _);
    }

    private bool TryGetGpuPowerRequest(out GpuPowerRequest? request)
    {
        request = null;
        if (GpuPowerAdapterSelector.SelectedItem is not GpuPowerChoice { Adapter: var adapter }
            || !int.TryParse(GpuPowerWattsText.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var watts)
            || watts <= 0 || adapter.ConfiguredWatts is not > 0 || adapter.EnforcedWatts is not > 0
            || adapter.MinimumWatts is not { } min || min < 0 || adapter.MaximumWatts is not { } max
            || watts < min || watts > max) return false;
        request = new(adapter.Uuid, watts);
        return true;
    }

    private async void GpuPowerApplyButton_Click(object sender, RoutedEventArgs e) => await ApplySelectedGpuPowerAsync();

    internal async Task ApplySelectedGpuPowerAsync()
    {
        if (_gpuPowerBusy || _lifetime.IsCancellationRequested || GpuPowerApprovalCheck.IsChecked != true
            || !TryGetGpuPowerRequest(out var request)) return;
        _gpuPowerBusy = true;
        GpuPowerApprovalCheck.IsChecked = false;
        RefreshGpuPowerControls();
        try
        {
            var evidence = await _gpuPowerPolicy.ApplyAsync([request!], _lifetime.Token);
            if (!_lifetime.IsCancellationRequested) GpuPowerStatusText.Text = $"Requested {request!.Watts} W for {request.Uuid}. " + evidence.Message;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { /* A driver request may have completed; never undo it automatically. */ }
        catch (Exception ex) { GpuPowerStatusText.Text = "The requested change could not be verified. Read current limits before retrying; some settings may have changed. " + ex.Message; }
        finally
        {
            _gpuPowerBusy = false;
            GpuPowerAdapterSelector.ItemsSource = null;
            GpuPowerWattsText.Clear();
            RefreshGpuPowerControls();
            if (!_lifetime.IsCancellationRequested) GpuPowerStatusText.Text += " Read current limits again before another change.";
        }
    }

    private static string Watts(decimal? value) => value is { } watts ? watts.ToString("0.##", CultureInfo.InvariantCulture) + " W" : "Unavailable";
    internal sealed record GpuPowerChoice(GpuPowerAdapter Adapter)
    {
        public string Display => Adapter.Name + " · " + Adapter.Uuid;
    }
}
