using System.Windows;
using AINexus.App;

namespace AINexus.App;

public partial class MainWindow : Window
{
    private readonly CancellationTokenSource _lifetime = new();
    private bool _scanningHardware;
    private bool _scanningSoftware;

    public MainWindow()
    {
        InitializeComponent();
        InitializeMonitoring();
        InitializePackages();
        Loaded += async (_, _) => await Task.WhenAll(ScanHardwareAsync(), ScanSoftwareAsync());
        Closed += (_, _) => _lifetime.Cancel();
    }

    private async void RescanButton_Click(object sender, RoutedEventArgs e) => await ScanHardwareAsync();

    private async Task ScanHardwareAsync()
    {
        if (_scanningHardware || _lifetime.IsCancellationRequested) return;
        _scanningHardware = true;
        HardwareRescanButton.IsEnabled = false;
        ScanStatusText.Text = "SCANNING LOCAL HARDWARE…";
        NoticeText.Text = "Reading processor, memory, graphics adapters, and fixed storage.";
        try
        {
            var snapshot = await HardwareScanner.ScanAsync(_lifetime.Token);
            if (_lifetime.IsCancellationRequested) return;
            CpuText.Text = snapshot.CpuName;
            RamText.Text = snapshot.RamGb > 0 ? $"{snapshot.RamGb:N1} GB" : "Unavailable";

            GpuList.ItemsSource = snapshot.Gpus.Select(gpu => new
            {
                gpu.Name,
                VramDisplay = gpu.VramGb.HasValue
                    ? $"{gpu.VramGb.Value:N1} GB VRAM · {gpu.VramSource}"
                    : gpu.VramSource
            }).ToList();
            GpuCountText.Text = snapshot.Gpus.Count == 1 ? "1 adapter" : $"{snapshot.Gpus.Count} adapters";

            StorageList.ItemsSource = snapshot.Storage.Select(drive => new
            {
                drive.Name,
                FreeDisplay = $"{drive.FreeGb:N1} GB free",
                CapacityDisplay = $"{drive.FreeGb:N1} GB free of {drive.TotalGb:N1} GB",
                FreePercent = drive.TotalGb > 0 ? drive.FreeGb / drive.TotalGb * 100 : 0
            }).ToList();

            ScanStatusText.Text = $"SCAN COMPLETE · {DateTime.Now:HH:mm:ss}";
            NoticeText.Text = snapshot.ScanNote ??
                "Hardware scan complete. NVIDIA VRAM is read from nvidia-smi when the driver utility is available.";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            ScanStatusText.Text = "SCAN INCOMPLETE";
            NoticeText.Text = $"Some hardware details could not be read: {ex.Message}";
            CpuText.Text = "Unavailable";
            RamText.Text = "Unavailable";
            GpuCountText.Text = "Unavailable";
            GpuList.ItemsSource = null;
            StorageList.ItemsSource = null;
        }
        finally
        {
            _scanningHardware = false;
            HardwareRescanButton.IsEnabled = true;
        }
    }

    private async void SoftwareRescanButton_Click(object sender, RoutedEventArgs e) => await ScanSoftwareAsync();

    private async Task ScanSoftwareAsync()
    {
        if (_scanningSoftware || _lifetime.IsCancellationRequested) return;
        _scanningSoftware = true;
        SoftwareRescanButton.IsEnabled = false;
        SoftwareStatusText.Text = "Scanning local installations...";
        try
        {
            var snapshot = await SoftwareDiscovery.ScanAsync(_lifetime.Token);
            if (_lifetime.IsCancellationRequested) return;
            SoftwareList.ItemsSource = snapshot.Applications;
            SoftwareStatusText.Text = $"{snapshot.Applications.Count(app => app.IsDetected)} of {snapshot.Applications.Count} supported tools detected - {snapshot.ScannedAt.ToLocalTime():HH:mm:ss}";
            SoftwareScopeText.Text = snapshot.Scope;
            SoftwareNoticeText.Text = snapshot.Warnings.Count > 0
                ? string.Join(Environment.NewLine, snapshot.Warnings)
                : "Discovery is read-only. Tools are not launched or installed. Not detected means no evidence was found in the locations checked.";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            SoftwareStatusText.Text = "Software scan incomplete";
            SoftwareNoticeText.Text = $"Discovery failed: {ex.Message}";
        }
        finally
        {
            _scanningSoftware = false;
            SoftwareRescanButton.IsEnabled = true;
        }
    }
}
