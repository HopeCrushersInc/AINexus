using System.Windows;

namespace AINexus.App;

public partial class MainWindow
{
    private readonly ResourceMonitor _resourceMonitor = new();

    private void InitializeMonitoring()
    {
        Loaded += async (_, _) => await MonitorResourcesAsync();
    }

    private async Task MonitorResourcesAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                if (MonitoringEnabled.IsChecked == true && !IsVisible)
                {
                    ResourceStatusText.Text = "Monitoring paused while window is hidden";
                }
                else if (MonitoringEnabled.IsChecked == true && WindowState != WindowState.Minimized)
                {
                    try
                    {
                        var sample = await _resourceMonitor.SampleAsync(_lifetime.Token);
                        if (_lifetime.IsCancellationRequested) return;
                        RenderResources(sample);
                    }
                    catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        CpuUsageText.Text = "Unavailable";
                        MemoryUsageText.Text = "Unavailable";
                        MemoryAvailableText.Text = "";
                        GpuUsageList.ItemsSource = null;
                        ResourceStatusText.Text = "Resource sample failed";
                        ResourceNoticeText.Text = ex.Message;
                    }
                }
                else
                {
                    ResourceStatusText.Text = "Monitoring paused - displayed values are the last sample";
                }
                await Task.Delay(TimeSpan.FromSeconds(2), _lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    internal void RenderResources(ResourceSnapshot sample)
    {
        CpuUsageText.Text = sample.CpuUsagePercent.HasValue ? $"{sample.CpuUsagePercent:N1}%" : "Waiting for interval sample";
        MemoryUsageText.Text = sample.RamUsedGb.HasValue && sample.RamTotalGb.HasValue
            ? $"{sample.RamUsedGb:N1} / {sample.RamTotalGb:N1} GiB" : "Unavailable";
        MemoryAvailableText.Text = sample.RamTotalGb.HasValue && sample.RamUsedGb.HasValue
            ? $"{sample.RamTotalGb - sample.RamUsedGb:N1} GiB available" : "Physical memory data unavailable";
        GpuUsageList.ItemsSource = sample.Gpus.Select(gpu => new
        {
            gpu.Name,
            UsageDisplay = gpu.UtilizationPercent.HasValue ? $"GPU utilization: {gpu.UtilizationPercent:N1}%" : "GPU utilization unavailable",
            MemoryDisplay = gpu.VramUsedGb.HasValue && gpu.VramTotalGb.HasValue
                ? $"VRAM: {gpu.VramUsedGb:N1} / {gpu.VramTotalGb:N1} GiB" : "VRAM usage unavailable"
        }).ToList();
        ResourceStatusText.Text = $"Sampled {sample.ScannedAt.ToLocalTime():HH:mm:ss} - {sample.CpuScope}";
        ResourceNoticeText.Text = "Sampling interval: 2 seconds. Monitoring does not impose resource limits. " +
            (sample.Notes.Count > 0 ? string.Join(" ", sample.Notes) : "GPU metrics are provided by the NVIDIA driver utility.");
    }
}
