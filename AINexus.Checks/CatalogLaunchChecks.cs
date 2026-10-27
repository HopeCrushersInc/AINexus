using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Windows.Controls;
using AINexus.App;

namespace AINexus.Checks;

internal static class CatalogLaunchChecks
{
    internal static void RunNative(Action<bool, string> check)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var transferCalls = 0;
        using var http = new HttpClient(new Handler(() => transferCalls++));
        var window = new MainWindow(new ModelAcquisitionService(http)) { GuidedPersistPreferences = false };
        try
        {
            GuidedInstallChecks.Pump(async () =>
            {
                var bytes = "GGUF library selection fixture"u8.ToArray();
                var firstFolder = Path.Combine(root, "copy-one");
                var secondFolder = Path.Combine(root, "copy-two");
                Directory.CreateDirectory(firstFolder); Directory.CreateDirectory(secondFolder);
                var report = AcquisitionPlanChecks.FixtureReport(firstFolder, bytes);
                var item = report.Catalog.Items.Single();
                var firstPlan = ModelAcquisitionPlanner.Create(report, item.Id, firstFolder);
                var secondPlan = ModelAcquisitionPlanner.Create(report, item.Id, secondFolder);
                await File.WriteAllBytesAsync(firstPlan.DestinationPath, bytes);
                await File.WriteAllBytesAsync(secondPlan.DestinationPath, bytes);
                var firstReceipt = await ArtifactReceiptStore.SaveAsync(firstPlan,
                    new(await ModelArtifactVerifier.VerifyExistingAsync(firstPlan), true, DateTimeOffset.UtcNow));
                var secondReceipt = await ArtifactReceiptStore.SaveAsync(secondPlan,
                    new(await ModelArtifactVerifier.VerifyExistingAsync(secondPlan), true, DateTimeOffset.UtcNow));
                var originalReceipt = await File.ReadAllTextAsync(firstReceipt);
                var settingsField = typeof(MainWindow).GetField("_settings", BindingFlags.NonPublic | BindingFlags.Instance)!;
                var settings = (PortalSettings)settingsField.GetValue(window)!;
                settingsField.SetValue(window, settings with { ModelRoots = [firstFolder, secondFolder] });
                var store = new CatalogDatabase(Path.Combine(root, "catalog.db"));
                await store.UpsertDiscoveryAsync(report.Catalog);
                window.CompleteWorkloadScan(report.Machine);
                window.ApplyWorkloadSelection([DiscoveryInterest.LocalAssistants]);
                await window.ConfigureCatalogLibraryAsync(root);
                var hardwareCalls = 0;
                window.GuidedHardwareOverride = _ => { hardwareCalls++; return Task.FromResult(report.Machine); };
                var grid = (DataGrid)window.FindName("CatalogLibraryGrid");
                var copies = (ComboBox)window.FindName("CatalogInstallationSelector");
                var review = (Button)window.FindName("CatalogReviewLocalButton");
                var approval = (CheckBox)window.FindName("GuidedModelApprovalCheck");
                check(grid.SelectedIndex == -1 && copies.SelectedIndex == -1 && !review.IsEnabled,
                    "Installed Library starts without selecting a model, local copy or launch action");
                await window.ReviewCatalogLocalModelAsync();
                check(window.GuidedReceiptPath is null && !window.OwnedLlamaServerRunning,
                    "Library review without an explicit model and copy does nothing");
                grid.SelectedIndex = 0;
                check(copies.Items.Count == 2 && copies.SelectedIndex == -1 && !review.IsEnabled,
                    "A model with two installed copies requires the user to select an exact local path");
                copies.SelectedItem = copies.Items.Cast<CatalogInstallation>().Single(i => i.ModelPath == firstPlan.DestinationPath);
                check(review.IsEnabled && window.GuidedReceiptPath is null && approval.IsChecked != true,
                    "Choosing a local copy enables review without preparing a load or granting download approval");
                await window.ReviewCatalogLocalModelAsync();
                check(window.GuidedModelPath == firstPlan.DestinationPath && window.GuidedReceiptPath == firstReceipt
                    && approval.IsChecked != true && !((Button)window.FindName("GuidedDownloadButton")).IsEnabled
                    && !((Button)window.FindName("GuidedReviewButton")).IsEnabled,
                    "Installed-model review binds the exact selected copy and disables fresh-acquisition actions");
                check(transferCalls == 0 && hardwareCalls == 0 && !window.OwnedLlamaServerRunning,
                    "Library review performs no transfer, hardware scan or model launch");
                ((TextBox)window.FindName("GuidedRuntimePathText")).Text = Path.Combine(root, "missing-runtime.exe");
                var corrupt = bytes.ToArray(); corrupt[^1] ^= 1;
                await File.WriteAllBytesAsync(firstPlan.DestinationPath, corrupt);
                await window.StartGuidedChatAsync();
                check(!window.OwnedLlamaServerRunning && hardwareCalls == 0
                    && ((TextBlock)window.FindName("GuidedStatusText")).Text.Contains("receipt", StringComparison.OrdinalIgnoreCase),
                    "Explicit local load rejects same-size model corruption before hardware admission or engine startup");
                await File.WriteAllBytesAsync(firstPlan.DestinationPath, bytes);
                await File.WriteAllTextAsync(firstReceipt, await File.ReadAllTextAsync(secondReceipt));
                await window.StartGuidedChatAsync();
                check(!window.OwnedLlamaServerRunning && hardwareCalls == 0
                    && ((TextBlock)window.FindName("GuidedStatusText")).Text.Contains("receipt", StringComparison.OrdinalIgnoreCase),
                    "A receipt swapped to another valid copy after review cannot authorize the earlier selected model path");
                await window.ReviewCatalogLocalModelAsync();
                check(!((TabItem)window.FindName("GuidedSetupTab")).IsSelected
                    || window.GuidedReceiptPath is null,
                    "Changed receipt evidence cannot be accepted as the indexed local copy");
                await File.WriteAllTextAsync(firstReceipt, originalReceipt);
                grid.SelectedIndex = -1; grid.SelectedIndex = 0;
                check(copies.SelectedIndex == -1 && !review.IsEnabled,
                    "Changing the Library row clears the previous installed-copy choice");
                copies.SelectedItem = copies.Items.Cast<CatalogInstallation>().Single(i => i.ModelPath == firstPlan.DestinationPath);
                await window.ReviewCatalogLocalModelAsync();
                ((TextBox)window.FindName("GuidedRuntimePathText")).Text = Path.Combine(root, "missing-runtime.exe");
                var enteredScan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var completeScan = new TaskCompletionSource<MachineCapabilities>(TaskCreationOptions.RunContinuationsAsynchronously);
                window.GuidedHardwareOverride = async token =>
                {
                    hardwareCalls++;
                    enteredScan.TrySetResult();
                    return await completeScan.Task.WaitAsync(token);
                };
                var pendingLoad = window.StartGuidedChatAsync();
                await enteredScan.Task.WaitAsync(TimeSpan.FromSeconds(10));
                var mutationBlocked = false;
                try { await File.WriteAllBytesAsync(firstPlan.DestinationPath, corrupt); }
                catch (IOException) { mutationBlocked = true; }
                completeScan.SetResult(report.Machine with
                {
                    Resources = report.Machine.Resources with { RamUsedGb = 16, RamTotalGb = 16, Gpus = [] }
                });
                await pendingLoad;
                check(mutationBlocked && File.ReadAllBytes(firstPlan.DestinationPath).SequenceEqual(bytes),
                    "The selected model stays protected against writes while current hardware admission is pending");
                check(hardwareCalls == 1 && !window.OwnedLlamaServerRunning
                    && ((TextBlock)window.FindName("GuidedStatusText")).Text.Contains("hardware", StringComparison.OrdinalIgnoreCase),
                    "Insufficient current hardware blocks installed-model load before the engine is started");
                File.Delete(firstPlan.DestinationPath);
                await window.ReviewCatalogLocalModelAsync();
                check(window.GuidedReceiptPath is null && !window.OwnedLlamaServerRunning && transferCalls == 0,
                    "A model removed after Library indexing is rejected without automatic repair or download");
                await File.WriteAllBytesAsync(firstPlan.DestinationPath, bytes.Concat(new byte[] { 0 }).ToArray());
                await window.ReviewCatalogLocalModelAsync();
                check(window.GuidedReceiptPath is null && !window.OwnedLlamaServerRunning && transferCalls == 0,
                    "A local file whose byte count changed after indexing is rejected without overwriting it");
                await File.WriteAllBytesAsync(firstPlan.DestinationPath, bytes);
                await File.WriteAllTextAsync(firstReceipt, "invalid receipt");
                await window.ReviewCatalogLocalModelAsync();
                check(window.GuidedReceiptPath is null && !window.OwnedLlamaServerRunning && transferCalls == 0,
                    "Malformed receipt review preserves files and never starts an engine or transfer");
            }, TimeSpan.FromSeconds(45), () => window.Close());
        }
        finally
        {
            window.Close();
            var fixtureRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "fixtures")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(fixtureRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe fixture cleanup.");
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class Handler(Action called) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { called(); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)); }
    }
}
