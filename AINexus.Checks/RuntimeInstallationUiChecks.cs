using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AINexus.App;

namespace AINexus.Checks;

internal static class RuntimeInstallationUiChecks
{
    public static void Run(MainWindow window, Action<bool, string> check)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var priorExecutable = Find<TextBox>(window, "LlamaCppExecutableText").Text;
        var priorOfflineRuntime = Find<TextBox>(window, "OfflineRuntimeText").Text;
        try
        {
            Pump(async () =>
            {
                const string name = "llama-bin-win-cpu-x64.zip";
                var path = Path.Combine(root, name);
                using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
                {
                    using (var text = new StreamWriter(zip.CreateEntry("llama-server.exe").Open())) text.Write("Fixture only; never execute.");
                    using (var text = new StreamWriter(zip.CreateEntry("LICENSE").Open())) text.Write("Fixture bundled license text for native approval checks.");
                }
                var bytes = await File.ReadAllBytesAsync(path);
                var asset = new RuntimeReleaseAsset(name, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)),
                    new Uri("https://github.com/ggml-org/llama.cpp/releases/download/fixture/" + name));
                var report = RuntimeSetupChecks.Report(RuntimeSetupChecks.Machine(), asset);
                var destination = Path.Combine(root, "installed-runtime");
                var filePlan = RuntimeFilePlanner.Create(report, "fixture/runtime", destination, name);
                var inspected = await RuntimeArchiveInspector.InspectAsync(filePlan, root);
                var plan = await RuntimeInstallationPlanner.CreateAsync(inspected, root);
                Find<CheckBox>(window, "DiscoveryShowUnassessedCheck").IsChecked = true; // Explicit user access to runtime setup metadata.
                window.RenderDiscoveryReport(report);
                check(Find<DataGrid>(window, "DiscoveryResultsGrid").SelectedIndex == -1,
                    "Native installation review begins with no automatically selected runtime result");
                Find<DataGrid>(window, "DiscoveryResultsGrid").SelectedIndex = 0;
                Find<TextBox>(window, "RuntimeDestinationText").Text = destination;
                Find<ComboBox>(window, "RuntimeArchiveSelector").SelectedIndex = 0;
                window.RenderRuntimeInstallationPlan(plan);
                var install = Find<Button>(window, "RuntimeInstallButton");
                var bundled = Find<CheckBox>(window, "RuntimeInstallBundledCheck");
                check(!install.IsEnabled && !bundled.IsEnabled && Find<Expander>(window, "RuntimePackagesExpander").IsExpanded
                    && Find<TabItem>(window, "DownloadsTab").IsSelected,
                    "Native installation opens the exact review and requires license text before bundled approval");
                install.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                check(!Directory.Exists(destination), "Native installation cannot write before exact-plan approval");
                Find<Button>(window, "RuntimeInstallTermsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitUntilAsync(() => bundled.IsEnabled);
                check(Find<TextBox>(window, "DiscoveryDetailsText").Text.Contains("Fixture bundled license text"),
                    "Native terms viewer displays actual verified archive license text without extracting files");
                if (Environment.GetEnvironmentVariable("AINEXUS_RENDER_INSTALL_UI") == "1") SaveLayoutPreview(window);
                Find<CheckBox>(window, "RuntimeInstallLicenseCheck").IsChecked = true;
                bundled.IsChecked = true;
                check(!install.IsEnabled, "Accepting license terms alone does not approve destination writes");
                Find<CheckBox>(window, "RuntimeInstallWritesCheck").IsChecked = true;
                check(install.IsEnabled, "All exact-plan approvals enable the native installation action");
                install.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitUntilAsync(() => Find<Button>(window, "RuntimeUseInstalledButton").IsEnabled);
                check(File.ReadAllText(Path.Combine(destination, "llama-server.exe")) == "Fixture only; never execute."
                    && File.Exists(Path.Combine(destination, RuntimeInstallationPlanner.ReceiptName))
                    && !install.IsEnabled && !window.OwnedLlamaServerRunning,
                    "Native approved extraction installs fixture bytes and receipt, consumes approval, and runs no payload");
                Find<Button>(window, "RuntimeUseInstalledButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                check(Find<TextBox>(window, "OfflineRuntimeText").Text == Path.Combine(destination, "llama-server.exe")
                    && Find<TabItem>(window, "OfflineVerificationTab").IsSelected && !window.OwnedLlamaServerRunning,
                    "Selecting the installed runtime leads to explicit verification without starting it");
                Find<TextBox>(window, "RuntimeDestinationText").Text = destination + "-changed";
                check(!install.IsEnabled && !Find<Button>(window, "RuntimeUseInstalledButton").IsEnabled
                    && Find<StackPanel>(window, "RuntimeInstallApprovalPanel").Visibility == Visibility.Collapsed,
                    "Changing runtime inputs revokes installation approval and obsolete configuration actions");
            });
        }
        finally
        {
            Find<TextBox>(window, "LlamaCppExecutableText").Text = priorExecutable;
            Find<TextBox>(window, "OfflineRuntimeText").Text = priorOfflineRuntime;
            // The entire root is a newly generated, test-owned directory; no payload was executed.
            Directory.Delete(root, recursive: true);
        }
    }

    private static T Find<T>(MainWindow window, string name) where T : class => (T)window.FindName(name);
    internal static void SaveLayoutPreview(MainWindow window)
    {
        var surface = (FrameworkElement)window.Content;
        surface.Measure(new Size(1440, 1000));
        surface.Arrange(new Rect(0, 0, 1440, 1000));
        surface.UpdateLayout();
        var bitmap = new RenderTargetBitmap(1440, 1000, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(surface);
        var folder = Path.Combine(AppContext.BaseDirectory, "Reports", "RuntimeInstallation");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".png");
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(stream);
        Console.WriteLine("Runtime installation layout preview: " + path);
    }
    private static async Task WaitUntilAsync(Func<bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!ready()) await Task.Delay(20, timeout.Token);
    }
    private static void Pump(Func<Task> action)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var previous = SynchronizationContext.Current;
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        timer.Tick += (_, _) => frame.Continue = false;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var task = action();
            _ = task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
            timer.Start();
            if (!task.IsCompleted) Dispatcher.PushFrame(frame);
            if (!task.IsCompleted) throw new TimeoutException("Native runtime installation checks exceeded their deadline.");
            task.GetAwaiter().GetResult();
        }
        finally { timer.Stop(); SynchronizationContext.SetSynchronizationContext(previous); }
    }
}
