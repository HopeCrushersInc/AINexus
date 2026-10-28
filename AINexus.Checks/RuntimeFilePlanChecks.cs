using System.IO;
using System.Windows;
using System.Windows.Controls;
using AINexus.App;

namespace AINexus.Checks;

internal static class RuntimeFilePlanChecks
{
    private static RuntimeReleaseAsset Asset(string name, long bytes = 1024) => new(name, bytes, new string('a', 64),
        new Uri("https://github.com/ggml-org/llama.cpp/releases/download/fixture/" + Uri.EscapeDataString(name)));

    public static void Run(Action<bool, string> check)
    {
        var cpu = Asset("llama-bin-win-cpu-x64.zip");
        var cuda = Asset("llama-bin-win-cuda-12.4-x64.zip", 2048);
        var support = Asset("cudart-llama-bin-win-cuda-12.4-x64.zip", 4096);
        var machine = RuntimeSetupChecks.Machine() with { DriverEvidence =
            [new("Fixture adapter", "Fixture driver", null, "NVIDIA driver (nvidia-smi)", "Fixture report")] };
        var report = RuntimeSetupChecks.Report(machine, cpu, cuda, support,
            Asset("cudart-llama-bin-win-cuda-13.0-x64.zip"), Asset("llama-bin-win-cpu-arm64.zip"));
        var destination = Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
        var selected = RuntimeFilePlanner.Create(report, "fixture/runtime", destination, cuda.Name);
        check(selected.Files.Select(f => f.Name).SequenceEqual(new[] { cuda.Name, support.Name }) && selected.CompressedBytes == 6144,
            "Exact runtime file review includes only the chosen runtime and its exact same-release/backend/architecture CUDA companion");
        check(selected.PlanId.Length == 64 && selected.PlanId == RuntimeFilePlanner.Fingerprint(selected)
            && selected.Blockers.Any(b => b.Contains("NVIDIA license")) && !Directory.Exists(destination),
            "Runtime file review has a reproducible identity, distinguishes dependency licenses and leaves the destination untouched");
        var cpuPlan = RuntimeFilePlanner.Create(report, "fixture/runtime", destination, cpu.Name);
        check(cpuPlan.Files.Count == 1 && cpuPlan.CompressedBytes == 1024, "CPU file selection does not include unrelated GPU packages");
        check(RuntimeFilePlanner.Fingerprint(selected with { Destination = destination + "-other" }) != selected.PlanId
            && RuntimeFilePlanner.Fingerprint(selected with { Files = [cuda] }) != selected.PlanId
            && RuntimeFilePlanner.Fingerprint(selected with { Machine = machine with { OsArchitecture = "Arm64" } }) != selected.PlanId,
            "Destination, exact file set and hardware evidence changes cannot retain the prior runtime review identity");
        var missing = RuntimeFilePlanner.Create(RuntimeSetupChecks.Report(machine, cuda), "fixture/runtime", destination, cuda.Name);
        check(missing.Files.Count == 1 && missing.Blockers.Any(b => b.Contains("No exact same-release")),
            "Missing CUDA companions remain unresolved instead of substituting another runtime release");
        var ambiguousReport = RuntimeSetupChecks.Report(machine, cuda, support, Asset("cudart-llama-fixture-bin-win-cuda-12.4-x64.zip"));
        check(RuntimeFilePlanner.Create(ambiguousReport, "fixture/runtime", destination, cuda.Name).Blockers.Any(b => b.Contains("ambiguous")),
            "Ambiguous CUDA support packages cannot be silently chosen");
        var noHash = cpu with { Sha256 = null };
        check(RuntimeFilePlanner.Create(RuntimeSetupChecks.Report(machine, noHash), "fixture/runtime", destination, noHash.Name)
            .Blockers.Any(b => b.Contains("No usable upstream SHA-256")), "Selected files without upstream hashes cannot become verified acquisition plans");
        bool Reject(DiscoveryReport candidate, string name)
        {
            try { RuntimeFilePlanner.Create(candidate, "fixture/runtime", destination, name); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or InvalidDataException) { return true; }
            return false;
        }
        check(Reject(report, support.Name) && Reject(report, "llama-bin-win-cpu-arm64.zip") && Reject(report, "not-published.zip"),
            "Exact-file review rejects dependency-only, wrong-platform and unpublished selections");
        var hostile = cpu with { DownloadUrl = new Uri("https://example.com/runtime.zip") };
        check(Reject(RuntimeSetupChecks.Report(machine, hostile), hostile.Name), "Exact-file review revalidates download origin rather than trusting a package label");
        check(!Directory.Exists(destination), "All exact runtime file reviews preserve the filesystem");
    }

    public static void RunNative(MainWindow window, Action<bool, string> check)
    {
        var cpu = Asset("llama-bin-win-cpu-x64.zip");
        var destination = Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
        ((CheckBox)window.FindName("DiscoveryShowUnassessedCheck")).IsChecked = false; // Runtime metadata is not an assessed model fit.
        window.RenderDiscoveryReport(RuntimeSetupChecks.Report(RuntimeSetupChecks.Machine(), cpu));
        check(((DataGrid)window.FindName("DiscoveryResultsGrid")).SelectedIndex == -1,
            "Exact runtime file review cannot inherit an automatically selected search result");
        ((DataGrid)window.FindName("DiscoveryResultsGrid")).SelectedIndex = 0;
        var selector = (ComboBox)window.FindName("RuntimeArchiveSelector");
        var button = (Button)window.FindName("RuntimeFileReviewButton");
        ((TextBox)window.FindName("RuntimeDestinationText")).Text = destination;
        check(selector.SelectedIndex == -1 && !button.IsEnabled, "Native runtime file review requires explicit archive selection");
        selector.SelectedIndex = 0;
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var details = (TextBox)window.FindName("DiscoveryDetailsText");
        check(details.Text.Contains("EXACT RUNTIME FILE REVIEW") && details.Text.Contains(cpu.Name) && !Directory.Exists(destination)
            && ((Button)window.FindName("RuntimeInspectButton")).IsEnabled,
            "Native selected-file review displays exact publisher identity without starting acquisition");
        ((TextBox)window.FindName("RuntimeDestinationText")).Text = destination + "-changed";
        check(details.Text.Contains("Prepare a new review") && !((Button)window.FindName("RuntimeInspectButton")).IsEnabled,
            "Changing the runtime destination invalidates the displayed review and disables archive inspection");
        window.RenderDiscoveryReport(RuntimeSetupChecks.Report(RuntimeSetupChecks.Machine()));
        check(selector.Items.Count == 0 && !button.IsEnabled, "Changing runtime results clears an obsolete selected archive");
        ((TextBox)window.FindName("RuntimeReleaseTagText")).Text = "";
        ((Button)window.FindName("RuntimeReleaseLookupButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check(((TextBlock)window.FindName("DiscoveryStatusText")).Text.Contains("Enter an exact upstream"),
            "Native explicit runtime lookup cannot silently choose a release when the tag is empty");
    }
}
