using System.Windows;
using AINexus.App;

namespace AINexus.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await ScanHardwareAsync();
    }

    private async void RescanButton_Click(object sender, RoutedEventArgs e) => await ScanHardwareAsync();

    private async Task ScanHardwareAsync()
    {
        ScanStatusText.Text = "SCANNING LOCAL HARDWARE…";
        NoticeText.Text = "Reading processor, memory, graphics adapters, and fixed storage.";
        try
        {
            var snapshot = await HardwareScanner.ScanAsync();
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
    }
}
