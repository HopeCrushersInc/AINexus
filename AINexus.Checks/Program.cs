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
            CheckResourceCalculations();
            CheckPackagePlans();
            CheckProcessExecution().GetAwaiter().GetResult();
            ModelChecks.RunAsync(Check).GetAwaiter().GetResult();
            AgentChecks.RunAsync(Check).GetAwaiter().GetResult();
            EmbeddedChecks.RunPolicy(Check);
            CheckWindow(args.Contains("--embedded-live"));
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

    private static void CheckWindow(bool embeddedLive)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var window = new MainWindow();
        Check(window.FindName("CpuText") is TextBlock, "Hardware XAML loads");
        Check(window.FindName("SoftwareList") is ItemsControl, "Software XAML loads");
        Check(window.FindName("SoftwareRescanButton") is Button, "Software rescan action is wired");
        Check(((Button)window.FindName("HardwareRescanButton")).IsEnabled, "Hardware rescan starts enabled");
        Check(!((Button)window.FindName("PackageInstallButton")).IsEnabled && !((Button)window.FindName("PackageUpdateButton")).IsEnabled,
            "Package modifications start disabled until review and acceptance");
        Check(window.FindName("GpuUsageList") is ItemsControl, "Monitoring XAML loads");
        Check(window.FindName("ModelFileGrid") is DataGrid && window.FindName("OllamaAvailableGrid") is DataGrid, "Model library and Ollama XAML load");
        window.RenderOllama(new(DateTimeOffset.Now, [], [], false, true, ["Fixture: available models unreadable"]));
        Check(((TextBlock)window.FindName("OllamaStatusText")).Text.Contains("Available: unavailable | Loaded: 0"),
            "Server failure is distinguished from a real empty model list");
        window.RenderResources(new(DateTimeOffset.Now, null, null, null, [], ["Fixture: telemetry unavailable"], "Fixture scope"));
        Check(((TextBlock)window.FindName("MemoryUsageText")).Text == "Unavailable", "Unknown memory renders honestly");
        Check(!((Button)window.FindName("AgentEnqueueButton")).IsEnabled, "Agent submission requires discovery and an explicitly selected model");
        if (embeddedLive) EmbeddedChecks.RunLive(window, Check);
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
        var monitor = new ResourceMonitor();
        var first = await monitor.SampleAsync(deadline.Token);
        Check(first.CpuUsagePercent is null, "First live CPU sample stays unknown");
        await Task.Delay(100, deadline.Token);
        var second = await monitor.SampleAsync(deadline.Token);
        Check(second.CpuUsagePercent is null or >= 0 and <= 100, "Live CPU utilization has valid bounds");
        Check(second.RamUsedGb is > 0 && second.RamUsedGb <= second.RamTotalGb, "Live RAM counters have valid bounds");
        Console.WriteLine($"Live resources: CPU {second.CpuUsagePercent:N1}%; RAM {second.RamUsedGb:N1}/{second.RamTotalGb:N1} GiB; {second.Gpus.Count} NVIDIA GPU samples.");
    }

    private static void CheckResourceCalculations()
    {
        Check(ResourceMonitor.CalculateCpuUsage(null, new(10, 20, 20)) is null, "First CPU reading stays unknown");
        Check(ResourceMonitor.CalculateCpuUsage(new(10, 20, 20), new(30, 60, 60)) == 75,
            "CPU delta subtracts idle from kernel plus user time");
        Check(ResourceMonitor.CalculateCpuUsage(new(30, 60, 60), new(10, 20, 20)) is null, "Counter reset discards invalid CPU interval");
        Check(ResourceMonitor.CalculateCpuUsage(new(10, 20, 20, 0), new(30, 60, 60, 1)) is null, "Processor group changes discard invalid CPU interval");
        Check(ResourceMonitor.CalculateCpuUsage(new(10, 20, 20), new(10, 20, 20)) is null, "Zero-duration CPU interval stays unknown");
        var samples = ResourceMonitor.ParseNvidiaResources("Fixture GPU, 25.5, 512, 2048\nUnsupported, N/A, N/A, N/A\nInvalid, 101, 3000, 2000");
        Check(samples.Count == 3 && samples[0].UtilizationPercent == 25.5 && samples[0].VramUsedGb == .5 && samples[0].VramTotalGb == 2,
            "GPU telemetry parses invariant values and converts MiB to GiB");
        Check(samples[1].UtilizationPercent is null && samples[1].VramTotalGb is null && samples[2].UtilizationPercent is null && samples[2].VramUsedGb is null,
            "Unsupported and inconsistent GPU telemetry stays unavailable");
    }

    private static void CheckPackagePlans()
    {
        var preview = PackageManager.BuildArguments(ManagedPackage.Ollama, null);
        Check(preview[0] == "show" && !preview.Contains("--accept-package-agreements") && !preview.Contains("--accept-source-agreements"),
            "Package preview does not install or silently accept agreements");
        var install = PackageManager.BuildArguments(ManagedPackage.Ollama, PackageAction.Install, true);
        Check(install.Contains("Ollama.Ollama") && install.Contains("--exact") && install.Contains("winget") && install.Contains("user"),
            "Install targets an exact catalog identity and current-user scope");
        Check(install.Contains("--silent") && install.Contains("--no-upgrade") && install.Contains("--skip-dependencies"),
            "Install avoids external installer UI, implicit upgrades and dependency modifications");
        var update = PackageManager.BuildArguments(ManagedPackage.Ollama, PackageAction.Update, true);
        Check(update[0] == "upgrade" && !update.Contains("--all") && !update.Contains("--force") && !update.Contains("--ignore-security-hash"),
            "Update is limited to one package with security checks intact");
        CheckThrows<InvalidOperationException>(() => PackageManager.BuildArguments(ManagedPackage.Ollama, PackageAction.Install), "Agreements require explicit acceptance");
        CheckThrows<InvalidOperationException>(() => PackageManager.BuildArguments(ManagedPackage.Gpt4All, PackageAction.Install, true), "Unsupported install scope cannot be bypassed");
        CheckThrows<ArgumentOutOfRangeException>(() => PackageManager.BuildArguments((ManagedPackage)999, PackageAction.Install, true), "Arbitrary package identities cannot be injected");
        Check(!new PackageOperationResult(true, 1, "", "error").Succeeded && !new PackageOperationResult(false, 0, "", "").Succeeded,
            "Failed or unstarted package operations never report success");
    }

    private static void CheckThrows<T>(Action action, string name) where T : Exception
    {
        try { action(); }
        catch (T) { Check(true, name); return; }
        throw new InvalidOperationException($"FAIL: {name}");
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException($"FAIL: {name}");
        _checks++;
        Console.WriteLine($"PASS: {name}");
    }
}
