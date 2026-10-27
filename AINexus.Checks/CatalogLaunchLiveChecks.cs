using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Controls;
using AINexus.App;

namespace AINexus.Checks;

internal static class CatalogLaunchLiveChecks
{
    internal static void Run(string receiptPath, string runtimePath, Action<bool, string> check)
    {
        var receipt = OfflineModelVerifier.ReadReceiptAsync(receiptPath).GetAwaiter().GetResult();
        if (!File.Exists(receipt.Plan.DestinationPath) || !File.Exists(runtimePath))
            throw new ArgumentException("Library live checks require an existing exact receipt, model and approved runtime.");
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var window = new MainWindow { GuidedPersistPreferences = false };
        try
        {
            GuidedInstallChecks.Pump(async () =>
            {
                var settingsField = typeof(MainWindow).GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var settings = (PortalSettings)settingsField.GetValue(window)!;
                settingsField.SetValue(window, settings with { ModelRoots = [Path.GetDirectoryName(receipt.Plan.DestinationPath)!] });
                window.CompleteWorkloadScan(await HardwareCapabilityScanner.ScanAsync());
                window.ApplyWorkloadSelection([DiscoveryInterest.LocalAssistants]);
                await window.ConfigureCatalogLibraryAsync(root);
                var grid = (DataGrid)window.FindName("CatalogLibraryGrid");
                check(grid.SelectedIndex == -1, "Live Library opens without choosing an installed model");
                grid.SelectedItem = grid.Items.Cast<MainWindow.CatalogLibraryRow>().Single(row =>
                    row.Installations.Any(copy => copy.ReceiptPath.Equals(Path.GetFullPath(receiptPath), StringComparison.OrdinalIgnoreCase)));
                var copies = (ComboBox)window.FindName("CatalogInstallationSelector");
                check(copies.SelectedIndex == -1, "Live Library requires explicit installed-copy selection");
                copies.SelectedItem = copies.Items.Cast<CatalogInstallation>().Single(copy =>
                    copy.ReceiptPath.Equals(Path.GetFullPath(receiptPath), StringComparison.OrdinalIgnoreCase));
                await window.ReviewCatalogLocalModelAsync();
                check(window.GuidedModelPath == receipt.Plan.DestinationPath && window.GuidedReceiptPath == Path.GetFullPath(receiptPath)
                    && !window.OwnedLlamaServerRunning && !((Button)window.FindName("GuidedDownloadButton")).IsEnabled,
                    "Live Library review prepares only the exact installed artifact without transfer or launch");
                ((TextBox)window.FindName("GuidedRuntimePathText")).Text = Path.GetFullPath(runtimePath);
                await window.StartGuidedChatAsync();
                if (!window.OwnedLlamaServerRunning || !((Button)window.FindName("ChatSendButton")).IsEnabled)
                    throw new InvalidOperationException("Library model load failed: " + ((TextBlock)window.FindName("GuidedStatusText")).Text);
                check(true, "Explicit Library load verifies current model bytes, current hardware and actual local generation");
                var prompt = (TextBox)window.FindName("ChatPromptText");
                prompt.Text = "Reply with a short greeting."; await window.SendChatAsync();
                prompt.Text = "Reply with a short goodbye."; await window.SendChatAsync();
                var transcript = ((TextBox)window.FindName("ChatTranscriptText")).Text;
                check(window.ChatHistoryCount >= 4 && transcript.Split("Assistant:", StringSplitOptions.None).Length >= 3,
                    "The explicitly chosen Library model completes two real local chat turns");
                var evidenceRoot = Path.Combine(AppContext.BaseDirectory, "Reports", "LibraryModelLive");
                Directory.CreateDirectory(evidenceRoot);
                await File.WriteAllTextAsync(Path.Combine(evidenceRoot, "last-run.json"), JsonSerializer.Serialize(new
                {
                    CompletedAt = DateTimeOffset.UtcNow, receipt.Plan.Artifact, ModelPath = receipt.Plan.DestinationPath,
                    ReceiptPath = Path.GetFullPath(receiptPath), RuntimePath = Path.GetFullPath(runtimePath), ChatTurns = 2,
                    FreshSourceLookup = false, WeightsDownloaded = false,
                    Limitations = "One installed-model/runtime/hardware combination. Historical source receipt; current hash, capacity and actual generation checked. No model-quality or multi-GPU claim."
                }, new JsonSerializerOptions { WriteIndented = true }));
            }, TimeSpan.FromMinutes(8), () => window.Close());
        }
        finally
        {
            window.Close();
            var fixturePrefix = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "fixtures")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(fixturePrefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe fixture cleanup.");
            Directory.Delete(root, recursive: true);
        }
        check(!window.OwnedLlamaServerRunning, "Closing the Library live test stops only its owned server");
    }
}
