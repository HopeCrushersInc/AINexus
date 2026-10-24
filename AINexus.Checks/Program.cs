using System.IO;
using System.Windows;
using System.Windows.Controls;
using AINexus.App;

namespace AINexus.Checks;

internal static class Program
{
    private static int _checks;

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            CheckDiscovery();
            CheckGpuInventory();
            CheckProcessExecution().GetAwaiter().GetResult();
            CheckWindow();
            if (args.Contains("--live")) CheckLiveDiscovery().GetAwaiter().GetResult();
            Console.WriteLine($"PASS: {_checks} checks.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void CheckDiscovery()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var empty = SoftwareDiscovery.ScanLocations([], [root]);
            Check(empty.Applications.Count == 8, "Catalog includes supported runtimes and dependencies");
            Check(empty.Applications.All(app => !app.IsDetected), "Empty locations do not fabricate installations");

            // Fixtures deliberately are not valid programs. Discovery must inspect files without executing them.
            File.WriteAllText(Path.Combine(root, "ollama.exe"), "Discovery fixture - never execute");
            var scan = SoftwareDiscovery.ScanLocations([root, root.ToUpperInvariant()], [root]);
            var ollama = scan.Applications.Single(app => app.Id == "ollama");
            Check(ollama.IsInstalled, "PATH executable is detected");
            Check(ollama.Installations.Count == 1, "Duplicate paths collapse case-insensitively");
            Check(ollama.Version == "Version unavailable", "Missing version metadata stays unknown");

            var common = Path.Combine(root, "Programs", "LM Studio");
            Directory.CreateDirectory(common);
            File.WriteAllText(Path.Combine(common, "LM Studio.exe"), "Fixture");
            scan = SoftwareDiscovery.ScanLocations([], [root]);
            Check(scan.Applications.Single(app => app.Id == "lm-studio").IsInstalled, "Common per-user installation is detected");

            var aliases = Path.Combine(root, "Microsoft", "WindowsApps");
            Directory.CreateDirectory(aliases);
            File.WriteAllText(Path.Combine(aliases, "python.exe"), "Alias fixture");
            Check(!SoftwareDiscovery.ScanLocations([aliases], []).Applications.Single(app => app.Id == "python").IsDetected,
                "Windows Store Python aliases are excluded");

            var registered = new SoftwareApplication("fixture", "Fixture", "Test", [new(null, "1.0", "Registry")]);
            Check(registered.IsDetected && !registered.IsInstalled, "Registry evidence does not imply verified executable");
            Check(registered.Status.Contains("unverified"), "Registry-only status explains uncertainty");

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var cancelled = false;
            try { SoftwareDiscovery.ScanLocations([root], [], cancellation.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "Cancellation propagates");
        }
        finally
        {
            // Only this run's newly created, absolute fixture directory is removed.
            Directory.Delete(root, recursive: true);
        }
    }

    private static void CheckWindow()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var window = new MainWindow();
        Check(window.FindName("CpuText") is TextBlock, "Hardware XAML loads");
        Check(window.FindName("SoftwareList") is ItemsControl, "Software XAML loads");
        Check(window.FindName("SoftwareRescanButton") is Button, "Software rescan action is wired");
        Check(((Button)window.FindName("HardwareRescanButton")).IsEnabled, "Hardware rescan starts enabled");
        window.Close();
        app.Shutdown();
    }

    private static void CheckGpuInventory()
    {
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new("de-DE");
            var parsed = HardwareScanner.ParseNvidiaOutput("NVIDIA Fixture GPU, 1536.5\nMissing, N/A\nInvalid, NaN\nNegative, -1\nMalformed");
            Check(parsed.Count == 4 && parsed[0].VramGb == 1.5, "NVIDIA values use invariant culture");
            Check(parsed.Skip(1).All(gpu => gpu.VramGb is null), "Invalid VRAM readings remain unavailable");
            var merged = HardwareScanner.MergeGpuInfo(
                [new("Fixture Integrated GPU", null, "VRAM unavailable"), new("Fixture GPU", null, "VRAM unavailable"), new("Fixture GPU", null, "VRAM unavailable")],
                [parsed[0], parsed[0]]);
            Check(merged.Count == 3, "GPU merge preserves mixed vendors and identical physical cards");
            Check(merged[0].VramGb is null && merged.Skip(1).All(gpu => gpu.VramGb == 1.5), "VRAM attaches only to matching adapters");
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = culture; }
    }

    private static async Task CheckProcessExecution()
    {
        var output = await HardwareScanner.RunProcessAsync("powershell.exe",
            ["-NoProfile", "-NonInteractive", "-Command", "[Console]::Error.Write(('e'*131072)); [Console]::Out.Write('ok')"], CancellationToken.None);
        Check(output == "ok", "Child process stdout and stderr drain without deadlock");
        var timedOut = false;
        try
        {
            await HardwareScanner.RunProcessAsync("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30"],
                CancellationToken.None, TimeSpan.FromMilliseconds(300));
        }
        catch (TimeoutException) { timedOut = true; }
        Check(timedOut, "Hung hardware queries have a deadline");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var cancelled = false;
        try
        {
            await HardwareScanner.RunProcessAsync("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30"], cancellation.Token);
        }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled, "Child query cancellation propagates");
    }

    private static async Task CheckLiveDiscovery()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var software = await SoftwareDiscovery.ScanAsync(deadline.Token);
        Check(software.Applications.All(app => app.Installations.All(item => item.ExecutablePath is null || File.Exists(item.ExecutablePath))),
            "Every detected live executable exists");
        Console.WriteLine($"Live software: {software.Applications.Count(app => app.IsDetected)} detected; {software.Warnings.Count} warnings.");
        var hardware = await HardwareScanner.ScanAsync(deadline.Token);
        Check(!string.IsNullOrWhiteSpace(hardware.CpuName), "Hardware scan returns an explicit CPU result");
        Check(hardware.Storage.All(drive => drive.TotalGb >= 0 && drive.FreeGb >= 0 && drive.FreeGb <= drive.TotalGb),
            "Live storage readings have valid bounds");
        Console.WriteLine($"Live hardware: {hardware.Gpus.Count} GPUs; {hardware.Storage.Count} fixed drives; note: {hardware.ScanNote ?? "none"}.");
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException($"FAIL: {name}");
        _checks++;
        Console.WriteLine($"PASS: {name}");
    }
}
