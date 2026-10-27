using System.IO;
using System.Reflection;
using System.Windows.Controls;
using AINexus.App;

namespace AINexus.Checks;

internal static class GpuPowerUiChecks
{
    internal static void RunNative(Action<bool, string> check)
    {
        var window = new MainWindow { GuidedPersistPreferences = false };
        var first = "GPU-11111111-1111-1111-1111-111111111111";
        var second = "GPU-22222222-2222-2222-2222-222222222222";
        var third = "GPU-33333333-3333-3333-3333-333333333333";
        var fourth = "GPU-44444444-4444-4444-4444-444444444444";
        var calls = new List<IReadOnlyList<string>>();
        var changed = false;
        var policy = new GpuPowerPolicy((arguments, _) =>
        {
            calls.Add(arguments);
            if (arguments[0].StartsWith("--id=", StringComparison.Ordinal)) { changed = true; return Task.FromResult("Applied"); }
            return Task.FromResult($"0, {first}, First GPU, {(changed ? 180 : 200)}, {(changed ? 180 : 200)}, 100, 220\n1, {second}, Second GPU, 450, 450, 100, 600\n2, {third}, Third GPU, 300, 300, 100, 400\n3, {fourth}, Fourth GPU, [N/A], [N/A], [N/A], [N/A]");
        });
        typeof(MainWindow).GetField("_gpuPowerPolicy", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, policy);
        try
        {
            WorkloadDiscoveryChecks.Pump(async () =>
            {
                var selector = (ComboBox)window.FindName("GpuPowerAdapterSelector");
                var watts = (TextBox)window.FindName("GpuPowerWattsText");
                var approval = (CheckBox)window.FindName("GpuPowerApprovalCheck");
                var apply = (Button)window.FindName("GpuPowerApplyButton");
                check(calls.Count == 0 && selector.SelectedIndex == -1 && watts.Text.Length == 0 && approval.IsChecked != true && !apply.IsEnabled,
                    "Power controls start with no driver operation, selected GPU, default wattage or remembered approval");
                await window.ApplySelectedGpuPowerAsync();
                check(calls.Count == 0, "The native power apply path is inert without explicit GPU, wattage and approval");
                await window.ReadGpuPowerAsync();
                check(calls.Count == 1 && selector.Items.Count == 4 && selector.SelectedIndex == -1,
                    "Four distinct GPU identities remain individually visible without automatic selection or power changes, including an unsupported controller");
                selector.SelectedIndex = 0;
                watts.Text = "180";
                approval.IsChecked = true;
                check(apply.IsEnabled, "A user-chosen supported limit below 250 W can be explicitly approved for its selected adapter");
                watts.Text = "190";
                check(approval.IsChecked != true && !apply.IsEnabled, "Changing chosen watts revokes the previous power-change approval");
                watts.Text = "180"; approval.IsChecked = true;
                await window.ApplySelectedGpuPowerAsync();
                check(calls.Count(a => a[0].StartsWith("--id=", StringComparison.Ordinal)) == 1
                    && calls.Any(a => a.SequenceEqual(new[] { "--id=" + first, "--power-limit=180" }))
                    && calls.All(a => !a.Contains("--id=" + second)),
                    "Native power apply changes only the explicitly approved card and leaves a higher-power second card alone");
                check(approval.IsChecked != true && selector.SelectedIndex == -1 && !apply.IsEnabled,
                    "Power apply consumes approval and discards stale readings before another request");
                var beforeLaunch = calls.Count;
                typeof(MainWindow).GetField("_guidedReceipt", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, Path.Combine(AppContext.BaseDirectory, Guid.NewGuid() + ".missing.json"));
                var report = AcquisitionPlanChecks.FixtureReport(Path.Combine(AppContext.BaseDirectory, "fixtures"));
                var plan = ModelAcquisitionPlanner.Create(report, report.Catalog.Items.Single().Id, Path.Combine(AppContext.BaseDirectory, "fixtures"));
                typeof(MainWindow).GetField("_guidedModelPlan", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, plan);
                await window.StartGuidedChatAsync();
                check(calls.Count == beforeLaunch && !window.OwnedLlamaServerRunning
                    && ((TextBlock)window.FindName("GuidedStatusText")).Text.Contains(".missing.json", StringComparison.OrdinalIgnoreCase),
                    "Model loading reaches receipt validation without querying or changing GPU power, even when no power policy was selected");
            });
        }
        finally { window.Close(); }
    }
}
