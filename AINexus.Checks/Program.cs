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
            WorkflowToolDiscoveryChecks.RunAsync(Check).GetAwaiter().GetResult();
            ModelMaintenanceChecks.RunAsync(Check).GetAwaiter().GetResult();
            ModelRecoveryChecks.RunAsync(Check).GetAwaiter().GetResult();
            FeaturedModelCatalogChecks.RunAsync(Check).GetAwaiter().GetResult();
            LocalChatChecks.RunAsync(Check).GetAwaiter().GetResult();
            LargeModelStartupChecks.RunAsync(Check).GetAwaiter().GetResult();
            GuidedRuntimeSetupChecks.RunAsync(Check).GetAwaiter().GetResult();
            GpuPowerPolicyChecks.RunAsync(Check).GetAwaiter().GetResult();
            AppearanceChecks.Run(Check);
            AgentChecks.RunAsync(Check).GetAwaiter().GetResult();
            LlamaCppChecks.RunAsync(Check).GetAwaiter().GetResult();
            LlamaServerChecks.RunAsync(Check).GetAwaiter().GetResult();
            HardwareCapabilityChecks.Run(Check);
            CompatibilityChecks.Run(Check);
            RuntimeReleaseChecks.Run(Check);
            RuntimeSetupChecks.Run(Check);
            RuntimeFilePlanChecks.Run(Check);
            RuntimeReleaseLookupChecks.RunAsync(Check).GetAwaiter().GetResult();
            RuntimeArchiveChecks.RunAsync(Check).GetAwaiter().GetResult();
            SevenZipInspectionChecks.RunAsync(Check).GetAwaiter().GetResult();
            SevenZipInspectionChecks.RunWorkerAsync(Check).GetAwaiter().GetResult();
            RuntimeInstallationChecks.RunAsync(Check).GetAwaiter().GetResult();
            RuntimeDownloadChecks.RunAsync(Check).GetAwaiter().GetResult();
            ComfyPortableChecks.RunAsync(Check).GetAwaiter().GetResult();
            if (args.Contains("--comfy-portable-live")) ComfyPortableChecks.RunLiveAsync(Check).GetAwaiter().GetResult();
            ImageWorkflowChecks.RunAsync(Check).GetAwaiter().GetResult();
            WorkloadSetupChecks.RunAsync(Check).GetAwaiter().GetResult();
            OnlineDiscoveryChecks.RunAsync(Check).GetAwaiter().GetResult();
            MultiSourceTextDiscoveryChecks.RunAsync(Check).GetAwaiter().GetResult();
            TaskTextDiscoveryChecks.RunAsync(Check).GetAwaiter().GetResult();
            if (args.Contains("--task-search-live"))
            {
                TaskTextDiscoveryChecks.RunLiveAsync(Check).GetAwaiter().GetResult();
                WorkflowToolDiscoveryChecks.RunLiveAsync(Check).GetAwaiter().GetResult();
            }
            ModelEvaluationEvidenceChecks.Run(Check);
            ModelEvaluationUiChecks.RunAsync(Check).GetAwaiter().GetResult();
            ModelEvaluationCacheChecks.RunAsync(Check).GetAwaiter().GetResult();
            TextSearchUiChecks.RunAsync(Check).GetAwaiter().GetResult();
            ImageArtifactDiscoveryChecks.RunAsync(Check).GetAwaiter().GetResult();
            VideoModelDiscoveryChecks.RunAsync(Check).GetAwaiter().GetResult();
            VideoCatalogChecks.RunAsync(Check).GetAwaiter().GetResult();
            ImageArtifactAcquisitionChecks.RunAsync(Check).GetAwaiter().GetResult();
            ComfyCheckpointFolderChecks.RunAsync(Check).GetAwaiter().GetResult();
            DiscoveryPipelineChecks.RunAsync(Check).GetAwaiter().GetResult();
            WorkloadDiscoveryChecks.RunAsync(Check).GetAwaiter().GetResult();
            CatalogDatabaseChecks.RunAsync(Check).GetAwaiter().GetResult();
            CatalogLibraryIndexChecks.RunAsync(Check).GetAwaiter().GetResult();
            AcquisitionPlanChecks.RunAsync(Check).GetAwaiter().GetResult();
            ModelAcquisitionServiceChecks.RunAsync(Check).GetAwaiter().GetResult();
            OfflineVerificationChecks.RunAsync(Check).GetAwaiter().GetResult();
            EmbeddedChecks.RunPolicy(Check);
            CheckWindow(args);
            if (args.Contains("--live")) CheckLiveDiscovery().GetAwaiter().GetResult();
            if (args.Contains("--llama-live")) CheckLiveLlamaCpp().GetAwaiter().GetResult();
            if (args.Contains("--discovery-live")) OnlineDiscoveryChecks.RunLiveAsync(Check).GetAwaiter().GetResult();
            if (args.Contains("--video-discovery-live")) VideoCatalogChecks.RunLiveAsync(Check).GetAwaiter().GetResult();
            var textSearchLive = Array.IndexOf(args, "--text-model-search-live");
            var modelEvidenceLive = Array.IndexOf(args, "--model-evidence-live");
            var savedEvidenceLive = Array.IndexOf(args, "--evaluation-cache-live");
            if (savedEvidenceLive >= 0)
            {
                if (savedEvidenceLive + 2 >= args.Length) throw new ArgumentException("--evaluation-cache-live requires an exact public repository and local catalog cache directory.");
                ModelEvaluationCacheChecks.RunLiveAsync(args[savedEvidenceLive + 1], args[savedEvidenceLive + 2], Check).GetAwaiter().GetResult();
            }
            if (modelEvidenceLive >= 0)
            {
                if (modelEvidenceLive + 1 >= args.Length) throw new ArgumentException("--model-evidence-live requires an exact public model repository.");
                ModelEvaluationLiveChecks.RunAsync(args[modelEvidenceLive + 1], Check).GetAwaiter().GetResult();
            }
            if (textSearchLive >= 0)
            {
                if (textSearchLive + 1 >= args.Length) throw new ArgumentException("--text-model-search-live requires an explicit public model query or repository.");
                TextModelSearchLiveChecks.RunAsync(args[textSearchLive + 1], Check).GetAwaiter().GetResult();
            }
            var imageMetadataLive = Array.IndexOf(args, "--image-artifacts-live");
            if (imageMetadataLive >= 0)
            {
                if (imageMetadataLive + 1 >= args.Length) throw new ArgumentException("--image-artifacts-live requires an explicit image repository or query.");
                ImageArtifactDiscoveryChecks.RunLiveAsync(args[imageMetadataLive + 1], Check).GetAwaiter().GetResult();
            }
            if (args.Contains("--discovery-live")) DiscoveryPipelineChecks.RunLiveAsync(Check).GetAwaiter().GetResult();
            if (args.Contains("--selected-workloads-live")) WorkloadDiscoveryChecks.RunLiveAsync(Check).GetAwaiter().GetResult();
            var catalogLiveIndex = Array.IndexOf(args, "--catalog-live");
            if (catalogLiveIndex >= 0)
            {
                if (catalogLiveIndex + 1 >= args.Length) throw new ArgumentException("--catalog-live requires an explicit catalog folder.");
                CatalogDatabaseChecks.RunLiveAsync(args[catalogLiveIndex + 1], Check).GetAwaiter().GetResult();
            }
            if (args.Contains("--live")) HardwareCapabilityChecks.RunLiveAsync(Check).GetAwaiter().GetResult();
            var acquisitionIndex = Array.IndexOf(args, "--acquisition-live");
            var downloadIndex = Array.IndexOf(args, "--acquisition-download-live");
            if (acquisitionIndex >= 0 || downloadIndex >= 0)
            {
                var index = downloadIndex >= 0 ? downloadIndex : acquisitionIndex;
                if (index + 1 >= args.Length) throw new ArgumentException("Live acquisition checks require the path of the previously approved existing model.");
                AcquisitionLiveChecks.RunAsync(args[index + 1], downloadIndex >= 0, Check).GetAwaiter().GetResult();
            }
            var offlineIndex = Array.IndexOf(args, "--offline-verification-live");
            var imageIndex = Array.IndexOf(args, "--image-live");
            if (imageIndex >= 0)
            {
                if (imageIndex + 3 >= args.Length) throw new ArgumentException("--image-live requires the approved portable root, checkpoint name and output folder.");
                var imageSize = Array.IndexOf(args, "--image-size");
                var imageWidth = 512; var imageHeight = 512;
                if (imageSize >= 0 && (imageSize + 2 >= args.Length || !int.TryParse(args[imageSize + 1], out imageWidth)
                    || !int.TryParse(args[imageSize + 2], out imageHeight)))
                    throw new ArgumentException("--image-size requires integer width and height.");
                ImageWorkflowChecks.RunLiveAsync(args[imageIndex + 1], args[imageIndex + 2], args[imageIndex + 3], Check,
                    imageWidth, imageHeight).GetAwaiter().GetResult();
            }
            var archiveIndex = Array.IndexOf(args, "--runtime-archive-live");
            if (archiveIndex >= 0)
            {
                if (archiveIndex + 3 >= args.Length) throw new ArgumentException("--runtime-archive-live requires a saved release report, local archive folder and exact archive name.");
                RuntimeArchiveChecks.RunLiveAsync(args[archiveIndex + 1], args[archiveIndex + 2], args[archiveIndex + 3], Check).GetAwaiter().GetResult();
            }
            var runtimeIndex = Array.IndexOf(args, "--runtime-release-live");
            if (runtimeIndex >= 0)
            {
                if (runtimeIndex + 1 >= args.Length) throw new ArgumentException("--runtime-release-live requires an explicit upstream release tag.");
                RuntimeReleaseLookupChecks.RunLiveAsync(args[runtimeIndex + 1], Check).GetAwaiter().GetResult();
            }
            if (offlineIndex >= 0)
            {
                if (offlineIndex + 2 >= args.Length) throw new ArgumentException("--offline-verification-live requires an acquisition receipt and local runtime executable path.");
                OfflineVerificationChecks.RunLiveAsync(args[offlineIndex + 1], args[offlineIndex + 2], Check).GetAwaiter().GetResult();
            }
            var offlineLaunchIndex = Array.IndexOf(args, "--offline-launch-live");
            if (offlineLaunchIndex >= 0)
            {
                if (offlineLaunchIndex + 2 >= args.Length) throw new ArgumentException("--offline-launch-live requires an existing runtime executable and GGUF model.");
                OfflineLaunchLiveChecks.RunAsync(args[offlineLaunchIndex + 1], args[offlineLaunchIndex + 2], Check).GetAwaiter().GetResult();
            }
            var runtimeDownloadIndex = Array.IndexOf(args, "--runtime-download-live");
            if (runtimeDownloadIndex >= 0)
            {
                if (runtimeDownloadIndex + 2 >= args.Length) throw new ArgumentException("--runtime-download-live requires an explicit release tag and a local archive folder.");
                RuntimeDownloadLiveChecks.RunAsync(args[runtimeDownloadIndex + 1], args[runtimeDownloadIndex + 2], Check).GetAwaiter().GetResult();
            }
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
            Check(empty.Applications.Count == 9, "Catalog includes supported runtimes and dependencies");
            Check(empty.Applications.All(app => !app.IsDetected), "Empty locations do not fabricate installations");

            // Fixtures deliberately are not valid programs. Discovery must inspect files without executing them.
            File.WriteAllText(Path.Combine(root, "ollama.exe"), "Discovery fixture - never execute");
            File.WriteAllText(Path.Combine(root, "llama-server.exe"), "Portable runtime discovery fixture - never execute");
            var scan = SoftwareDiscovery.ScanLocations([root, root.ToUpperInvariant()], [root]);
            var ollama = scan.Applications.Single(app => app.Id == "ollama");
            Check(ollama.IsInstalled, "PATH executable is detected");
            Check(ollama.Installations.Count == 1, "Duplicate paths collapse case-insensitively");
            Check(ollama.Version == "Version unavailable", "Missing version metadata stays unknown");
            Check(scan.Applications.Single(app => app.Id == "llama-cpp").IsInstalled, "Portable llama.cpp executables are discovered without launching them");
            var configured = SoftwareDiscovery.ScanAsync(additionalSearchDirectories: [root]).GetAwaiter().GetResult();
            Check(configured.Applications.Single(app => app.Id == "llama-cpp").Installations.Any(installation =>
                installation.ExecutablePath == Path.Combine(root, "llama-server.exe") && installation.Source.Contains("Configured portable")),
                "Selected portable runtime folders supply explicit installation evidence without changing PATH");

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

    private static void CheckWindow(string[] args)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var window = new MainWindow();
        StartupSelectionChecks.RunNative(window, Check);
        Check(window.FindName("CpuText") is TextBlock, "Hardware XAML loads");
        WorkloadSetupChecks.RunNative(window, Check);
        WorkloadDiscoveryChecks.RunNative(window, Check);
        TextSearchUiChecks.Run(Check);
        ModelEvaluationUiChecks.RunNative(Check);
        ModelEvaluationCacheChecks.RunNative(Check);
        GpuPowerUiChecks.RunNative(Check);
        AppearanceChecks.RunNative(window, Check);
        ComfyPortableUiChecks.RunNative(Check);
        ToolSelectionChecks.RunNative(Check);
        WorkloadSelectionRelevanceChecks.RunNative(Check);
        var appearancePreview = Array.IndexOf(args, "--appearance-preview");
        if (appearancePreview >= 0)
        {
            if (appearancePreview + 1 >= args.Length) throw new ArgumentException("--appearance-preview requires an output folder.");
            AppearancePreview.Capture(args[appearancePreview + 1]);
        }
        CatalogDatabaseChecks.RunNative(window, Check);
        CatalogLaunchChecks.RunNative(Check);
        var libraryLive = Array.IndexOf(args, "--library-model-live");
        if (libraryLive >= 0)
        {
            if (libraryLive + 2 >= args.Length) throw new ArgumentException("--library-model-live requires an exact existing acquisition receipt and approved runtime path.");
            CatalogLaunchLiveChecks.Run(args[libraryLive + 1], args[libraryLive + 2], Check);
        }
        ModelMaintenanceUiChecks.RunNative(window, Check);
        ModelRecoveryUiChecks.RunNative(window, Check);
        GuidedInstallChecks.RunNative(window, Check);
        UserChoiceFlowChecks.RunNative(Check);
        ImageDiscoveryUiChecks.RunNative(Check);
        MediaDiscoveryActionChecks.Run(Check);
        ImageArtifactSetupUiChecks.RunNative(Check);
        ComfyCheckpointFolderUiChecks.RunNative(Check);
        var comfyFoldersLive = Array.IndexOf(args, "--comfy-folders-live");
        if (comfyFoldersLive >= 0)
        {
            if (comfyFoldersLive + 2 >= args.Length) throw new ArgumentException("--comfy-folders-live requires the explicit existing engine root and checkpoint folder; it applies an approved app-owned configuration.");
            ComfyCheckpointFolderLiveChecks.Run(args[comfyFoldersLive + 1], args[comfyFoldersLive + 2], Check);
        }
        var imageSetupLive = Array.IndexOf(args, "--image-artifact-setup-live");
        if (imageSetupLive >= 0)
        {
            if (imageSetupLive + 3 >= args.Length) throw new ArgumentException("--image-artifact-setup-live requires an exact repository, filename and existing destination folder; it downloads that explicit file.");
            ImageArtifactSetupLiveChecks.Run(args[imageSetupLive + 1], args[imageSetupLive + 2], args[imageSetupLive + 3], Check);
        }
        var selectedLive = Array.IndexOf(args, "--selected-model-live");
        if (selectedLive >= 0)
        {
            if (selectedLive + 4 >= args.Length) throw new ArgumentException("--selected-model-live requires repository, exact filename, existing model folder and runtime path.");
            SelectedModelLiveChecks.Run(args[selectedLive + 1], args[selectedLive + 2], args[selectedLive + 3], args[selectedLive + 4], Check);
        }
        var featuredLive = Array.IndexOf(args, "--featured-install-live");
        if (featuredLive >= 0)
        {
            if (featuredLive + 3 >= args.Length) throw new ArgumentException("--featured-install-live requires catalog ID, existing download folder and existing runtime path.");
            GuidedInstallChecks.RunLive(args[featuredLive + 1], args[featuredLive + 2], args[featuredLive + 3], Check);
        }
        ImageWorkflowChecks.RunNative(window, Check);
        ImageDimensionsChecks.RunNative(Check);
        RuntimeSetupChecks.RunNative(window, Check);
        RuntimeFilePlanChecks.RunNative(window, Check);
        RuntimeInstallationUiChecks.Run(window, Check);
        OfflineVerificationUiChecks.RunNative(window, Check);
        Check(window.FindName("OfflineVerificationTab") is TabItem, "Offline verification XAML loads");
        ((TextBox)window.FindName("OfflineReceiptText")).Text = "";
        Check(!((Button)window.FindName("OfflineAuditButton")).IsEnabled && !((Button)window.FindName("OfflineRunButton")).IsEnabled,
            "Offline verification requires an explicit existing receipt before audit or inference");
        ((TextBox)window.FindName("OfflineReceiptText")).Text = @"\\unapproved-server\models\receipt.json";
        Check(!((Button)window.FindName("OfflineAuditButton")).IsEnabled && !((Button)window.FindName("OfflineRunButton")).IsEnabled,
            "Offline verification controls reject network paths without contacting a server");
        ((TextBox)window.FindName("OfflineReceiptText")).Text = "";
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
        Check(window.FindName("LlamaCppModelList") is ItemsControl, "llama.cpp model inventory XAML loads");
        Check(window.FindName("DiscoveryResultsGrid") is DataGrid, "Hardware-aware discovery XAML loads");
        var fixtureReport = DiscoveryPipeline.Evaluate(DiscoveryPipelineChecks.FixtureCatalog() with { IsCached = true }, DiscoveryPipelineChecks.FixtureMachine(), "C:\\");
        window.RenderDiscoveryReport(fixtureReport);
        Check(((DataGrid)window.FindName("DiscoveryResultsGrid")).SelectedIndex == -1,
            "Discovery results remain unselected until an explicit user choice");
        ((DataGrid)window.FindName("DiscoveryResultsGrid")).SelectedIndex = 0;
        Check(((TextBlock)window.FindName("DiscoveryStatusText")).Text.Contains("SAVED metadata; not refreshed online"),
            "Discovery UI identifies saved metadata without claiming a new online verification");
        Check(((TextBox)window.FindName("DiscoveryDetailsText")).Text.Contains("No installation, compilation or runtime verification was performed"),
            "Discovery UI distinguishes a resource estimate from installed and verified software");
        var acquisitionDestination = Path.Combine(AppContext.BaseDirectory, "fixture-plan-ui", Guid.NewGuid().ToString("N"));
        var acquisitionReport = AcquisitionPlanChecks.FixtureReport(acquisitionDestination);
        window.RenderDiscoveryReport(acquisitionReport);
        Check(((DataGrid)window.FindName("DiscoveryResultsGrid")).SelectedIndex == -1,
            "Fresh acquisition search cannot silently choose its first model result");
        ((DataGrid)window.FindName("DiscoveryResultsGrid")).SelectedIndex = 0;
        var destinationControl = (TextBox)window.FindName("DiscoveryDestinationText");
        destinationControl.Text = acquisitionDestination;
        ((Button)window.FindName("DiscoveryPlanButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var approvalControl = (CheckBox)window.FindName("AcquisitionApprovalCheck");
        var downloadControl = (Button)window.FindName("AcquisitionDownloadButton");
        Check(approvalControl.IsEnabled && !downloadControl.IsEnabled && !Directory.Exists(acquisitionDestination),
            "Preparing a native acquisition review does not approve, download or create its destination");
        approvalControl.IsChecked = true;
        Check(downloadControl.IsEnabled, "An explicitly approved native review enables acquisition for that plan");
        destinationControl.Text = acquisitionDestination + "-changed";
        Check(!downloadControl.IsEnabled && approvalControl.IsChecked == false,
            "Changing the native destination revokes the previous acquisition approval");
        window.RenderLlamaCppModels(["fixture-llama-model"], new("http://127.0.0.1:8080"));
        var models = (ComboBox)window.FindName("AgentModelSelector");
        Check(((ComboBox)window.FindName("AgentRuntimeSelector")).SelectedIndex == -1 && models.Items.Count == 0,
            "Querying a local engine does not select it as an assistant or choose a model");
        ((ComboBox)window.FindName("AgentRuntimeSelector")).SelectedIndex = 0;
        Check(models.Items.Count == 1 && models.SelectedIndex == -1, "llama.cpp catalog requires explicit model selection");
        models.SelectedIndex = 0;
        Check(((Button)window.FindName("AgentEnqueueButton")).IsEnabled, "Actual catalog model selection enables llama.cpp task submission");
        ((ComboBox)window.FindName("AgentRuntimeSelector")).SelectedIndex = -1;
        Check(models.SelectedIndex == -1 && !((Button)window.FindName("AgentEnqueueButton")).IsEnabled,
            "Clearing the queried connection clears model selection instead of falling back to a default runtime");
        var uiIndex = Array.IndexOf(args, "--llama-ui-live");
        if (uiIndex >= 0)
        {
            if (uiIndex + 2 >= args.Length) throw new ArgumentException("--llama-ui-live requires an executable path and a model path.");
            LlamaCppUiChecks.Run(window, args[uiIndex + 1], args[uiIndex + 2], Check);
        }
        if (args.Contains("--embedded-live")) EmbeddedChecks.RunLive(window, Check);
        if (uiIndex >= 0) LlamaCppUiChecks.RunCloseCleanup(window, Check);
        window.Close();
        NativeAcquisitionChecks.Run(Check);
        RuntimeDownloadUiChecks.Run(Check);
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

    private static async Task CheckLiveLlamaCpp()
    {
        // Explicit live flag only. Derive the model ID from the server rather than embedding one in tests.
        var endpoint = new Uri("http://127.0.0.1:8080");
        using var http = OllamaClient.CreateLocalHttpClient();
        var models = await new LlamaCppClient(http).GetModelsAsync(endpoint);
        if (models.Count != 1) throw new InvalidOperationException("The live smoke test requires one explicitly loaded model.");
        await LlamaCppChecks.RunLiveAsync(endpoint, models[0], Check);
    }
}
