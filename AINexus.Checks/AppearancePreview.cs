using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AINexus.App;

namespace AINexus.Checks;

internal static class AppearancePreview
{
    internal static void Capture(string directory) => WorkloadDiscoveryChecks.Pump(async () =>
    {
        Directory.CreateDirectory(directory);
        var subject = new MainWindow { GuidedPersistPreferences = false };
        subject.CompleteWorkloadScan(DiscoveryPipelineChecks.FixtureMachine());
        subject.ApplyWorkloadSelection([DiscoveryInterest.Coding, DiscoveryInterest.Images]);
        ((TabItem)subject.FindName("StoreHomeTab")).IsSelected = true;
        var content = (FrameworkElement)subject.Content;
        subject.Content = null;
        var host = new Window { Width = 1440, Height = 900, Left = -10000, Top = -10000,
            ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None,
            Resources = subject.Resources, Content = content };
        try
        {
            host.Show();
            foreach (var mode in Enum.GetValues<NexusAppearanceMode>())
            {
                subject.ApplyAppearanceSelection(mode, false);
                host.Background = subject.Background; host.Foreground = subject.Foreground;
                host.FontFamily = subject.FontFamily; host.FontSize = subject.FontSize;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                host.UpdateLayout();
                Save(host, Path.Combine(directory, mode.ToString().ToLowerInvariant() + ".png"));
            }
            subject.ApplyAppearanceSelection(NexusAppearanceMode.Retro, false);
            host.Background = subject.Background; host.Foreground = subject.Foreground;
            host.FontFamily = subject.FontFamily;
            subject.HandleUiScaleWheel(true, 600);
            host.FontSize = subject.FontSize;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); host.UpdateLayout();
            Save(host, Path.Combine(directory, "retro-150-percent.png"));
            subject.HandleUiScaleWheel(true, 360);
            host.FontSize = subject.FontSize;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); host.UpdateLayout();
            Save(host, Path.Combine(directory, "retro-175-percent.png"));
            subject.ResetUiScale();
            subject.ApplyAppearanceSelection(NexusAppearanceMode.Standard, false);
            typeof(MainWindow).GetField("_workspaceConfigured", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(subject, false);
            subject.CompleteWorkloadScan(DiscoveryPipelineChecks.FixtureMachine());
            ((TabItem)subject.FindName("WorkloadSetupTab")).IsSelected = true;
            ((FrameworkElement)subject.FindName("StoreNavigation")).Visibility = Visibility.Collapsed;
            ((FrameworkElement)subject.FindName("WorkloadScanProgress")).Visibility = Visibility.Collapsed;
            host.Background = subject.Background; host.Foreground = subject.Foreground;
            host.FontFamily = subject.FontFamily; host.FontSize = subject.FontSize;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); host.UpdateLayout();
            Save(host, Path.Combine(directory, "startup.png"));
        }
        finally { host.Content = null; host.Close(); subject.Content = content; subject.Close(); }
    });

    private static void Save(FrameworkElement element, string path)
    {
        var bitmap = new RenderTargetBitmap(1440, 900, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write); encoder.Save(file);
    }
}
