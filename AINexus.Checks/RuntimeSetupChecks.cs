using System.IO;
using System.Windows;
using System.Windows.Controls;
using AINexus.App;

namespace AINexus.Checks;

internal static class RuntimeSetupChecks
{
    private static RuntimeReleaseAsset Asset(string name, bool hash = true) => new(name, 1024,
        hash ? new string('a', 64) : null,
        new Uri("https://github.com/ggml-org/llama.cpp/releases/download/fixture/" + Uri.EscapeDataString(name)));

    internal static MachineCapabilities Machine() => DiscoveryPipelineChecks.FixtureMachine() with
    {
        ScannedAt = DateTimeOffset.UtcNow,
        Volumes = [new(Path.GetPathRoot(AppContext.BaseDirectory)!, 20_000_000, 30_000_000)]
    };

    internal static DiscoveryReport Report(MachineCapabilities machine, params RuntimeReleaseAsset[] assets)
    {
        var item = DiscoveryPipelineChecks.FixtureCatalog().Items[0] with
        {
            Id = "fixture/runtime", Name = "Fixture runtime", Kind = "Runtime", Artifact = null, License = "MIT",
            RuntimeRelease = new("fixture", false, false, assets, [])
        };
        return DiscoveryPipeline.Evaluate(new([item], [], DateTimeOffset.UtcNow), machine, machine.Volumes[0].Path);
    }

    public static void Run(Action<bool, string> check)
    {
        var machine = Machine();
        var cpu = Asset("llama-bin-win-cpu-x64.zip");
        var cuda = Asset("llama-bin-win-cuda-12.4-x64.zip");
        var cpuResult = RuntimeSetupPlanner.Assess(cpu, "fixture", machine);
        check(cpuResult.Status == RuntimeArchiveStatus.PlatformMatch && cpuResult.Backend == "cpu"
            && cpuResult.Reasons.Any(r => r.Contains("not execution verification")),
            "Runtime CPU review matches actual supplied OS/architecture without claiming executable compatibility");
        check(RuntimeSetupPlanner.Assess(cpu, "fixture", machine with { OsArchitecture = "Arm64" }).Status == RuntimeArchiveStatus.PlatformMismatch,
            "Runtime review never assumes x64 emulation on an ARM machine");
        check(RuntimeSetupPlanner.Assess(cpu, "fixture", machine with { OsDescription = "Linux fixture" }).Status == RuntimeArchiveStatus.PlatformMismatch,
            "Windows runtime archives cannot match a Linux host");
        check(RuntimeSetupPlanner.Assess(cuda, "fixture", machine).Status == RuntimeArchiveStatus.HardwareEvidenceMissing,
            "CUDA runtime review requires actual NVIDIA query evidence rather than a filename or GPU assumption");
        var nvidia = machine with { DriverEvidence = [new("Fixture NVIDIA adapter", "Fixture driver", "Fixture compute",
            "NVIDIA driver (nvidia-smi)", "Fixture report")] };
        var accelerated = RuntimeSetupPlanner.Assess(cuda, "fixture", nvidia);
        check(accelerated.Status == RuntimeArchiveStatus.PlatformMatch && accelerated.Reasons.Any(r => r.Contains("remain unresolved")),
            "NVIDIA metadata does not invent driver minima or compiled CUDA architecture support");
        check(RuntimeSetupPlanner.Assess(Asset("cudart-llama-bin-win-cuda-12.4-x64.zip"), "fixture", nvidia).Role == "CUDA support archive",
            "CUDA dependency archives are distinguished from standalone server archives");
        check(RuntimeSetupPlanner.Assess(Asset("llama-fixture-bin-win-cpu-x64.zip"), "fixture", machine).Status == RuntimeArchiveStatus.PlatformMatch,
            "Known release-prefixed and unprefixed runtime labels share the same conservative assessment");
        check(RuntimeSetupPlanner.Assess(Asset("llama-bin-win-vulkan-x64.zip"), "fixture", machine).Status == RuntimeArchiveStatus.HardwareEvidenceMissing,
            "Vulkan compatibility stays unresolved until backend-specific probes exist");
        check(RuntimeSetupPlanner.Assess(Asset("llama-linux-cpu-x64.zip"), "fixture", machine).Status == RuntimeArchiveStatus.Unrecognized,
            "Unrecognized package labels do not receive invented runtime requirements");
        var destination = Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
        var report = Report(machine, cpu);
        check(report.Recommendations[0].Status == RecommendationStatus.NeedsDetails
            && report.Recommendations[0].ExecutionPath == "Review runtime setup",
            "Runtime archive assessment cannot masquerade as a model resource candidate or verified installation");
        var review = RuntimeSetupPlanner.Create(report, "fixture/runtime", destination);
        check(!Directory.Exists(destination) && review.Actions.Count > 0 && review.Blockers.Any(b => b.Contains("cannot authorize")),
            "Runtime setup review produces concrete ordered steps without creating folders or authorizing installation");
        check(RuntimeSetupPlanner.Create(Report(machine), "fixture/runtime", destination).Blockers.Any(b => b.Contains("no supported published archives")),
            "Marker-only runtime releases do not silently become nightly downloads or source builds");
        check(RuntimeSetupPlanner.Create(Report(machine, Asset(cpu.Name, false)), "fixture/runtime", destination)
            .Blockers.Any(b => b.Contains("lacks an upstream SHA-256")), "Missing archive digests block checksum-verified runtime acquisition");
        var cached = report with { Catalog = report.Catalog with { IsCached = true }, Machine = machine with { ScannedAt = DateTimeOffset.UtcNow.AddDays(-1) } };
        var savedReview = RuntimeSetupPlanner.Create(cached, "fixture/runtime", destination);
        check(savedReview.CachedMetadata && savedReview.Blockers.Any(b => b.Contains("saved source"))
            && savedReview.Blockers.Any(b => b.Contains("stale")), "Saved catalogs and stale hardware cannot be presented as fresh setup approval evidence");
        bool Rejected(string path)
        {
            try { RuntimeSetupPlanner.Create(report, "fixture/runtime", path); }
            catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException) { return true; }
            return false;
        }
        check(Rejected(AppContext.BaseDirectory) && Rejected(machine.Volumes[0].Path), "Runtime setup reviews reject existing folders and whole-drive destinations");
        check(Rejected(@"\\unapproved-server\share\runtime") && Rejected(Path.Combine(destination, "CON")),
            "Runtime setup reviews reject network locations and reserved Windows folder names");
        check(!Directory.Exists(destination), "Rejected runtime plans leave the filesystem unchanged");
    }

    public static void RunNative(MainWindow window, Action<bool, string> check)
    {
        var destination = Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
        ((CheckBox)window.FindName("DiscoveryShowUnassessedCheck")).IsChecked = true; // Explicitly inspect a runtime requiring further review.
        window.RenderDiscoveryReport(Report(Machine(), Asset("llama-bin-win-cpu-x64.zip")));
        check(((DataGrid)window.FindName("DiscoveryResultsGrid")).SelectedIndex == -1,
            "Runtime search results require an explicit user selection before setup review");
        ((DataGrid)window.FindName("DiscoveryResultsGrid")).SelectedIndex = 0;
        ((TextBox)window.FindName("RuntimeDestinationText")).Text = destination;
        var button = (Button)window.FindName("RuntimeSetupReviewButton");
        check(button.IsEnabled && !((Button)window.FindName("AcquisitionDownloadButton")).IsEnabled,
            "Native runtime review is available without enabling model acquisition or installation");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check(((TextBox)window.FindName("DiscoveryDetailsText")).Text.Contains("NO INSTALLATION APPROVED")
            && !Directory.Exists(destination), "Native runtime setup review renders its limitations without touching the destination");
    }
}
