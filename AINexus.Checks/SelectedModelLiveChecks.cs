using System.IO;
using System.Text.Json;
using System.Windows.Controls;
using AINexus.App;

namespace AINexus.Checks;

internal static class SelectedModelLiveChecks
{
    internal static void Run(string repository, string fileName, string modelFolder, string runtime, Action<bool, string> check)
    {
        var existing = Path.GetFullPath(Path.Combine(modelFolder, fileName));
        if (!File.Exists(existing) || !File.Exists(runtime)) throw new ArgumentException("This live reuse check requires an already installed exact model and an existing approved runtime.");
        var window = new MainWindow { GuidedPersistPreferences = false };
        try
        {
            GuidedInstallChecks.Pump(async () =>
            {
                var machine = await HardwareCapabilityScanner.ScanAsync();
                window.CompleteWorkloadScan(machine);
                window.ApplyWorkloadSelection([DiscoveryInterest.Coding]);
                using var http = VerifiedOnlineDiscoveryProvider.CreateHttpClient();
                var catalog = await new VerifiedOnlineDiscoveryProvider(http).SearchAsync(new(DiscoveryInterest.Coding, repository, 8));
                foreach (var candidate in catalog.Items) Console.WriteLine("Live source result: " + (candidate.Artifact?.Repository + "/" + candidate.Artifact?.FileName ?? candidate.Name));
                foreach (var note in catalog.Notes) Console.WriteLine("Live source note: " + note);
                var item = catalog.Items.Single(i => i.Artifact is { } a && a.Repository == repository && a.FileName == fileName);
                var report = DiscoveryPipeline.Evaluate(catalog, machine, Path.GetPathRoot(existing)!);
                check(report.Recommendations.Count > 1 && report.Recommendations.Single(r => r.Item.Id == item.Id).Status == RecommendationStatus.ResourceCandidate,
                    "Live generic discovery returns multiple choices and evaluates the exact chosen existing artifact against actual hardware");
                window.RenderDiscoveryReport(report);
                var grid = (DataGrid)window.FindName("DiscoveryResultsGrid");
                check(grid.SelectedIndex == -1, "Live generic model results require an explicit selection");
                grid.SelectedItem = grid.Items.Cast<object>().Single(row => row.GetType().GetProperty("Recommendation")!.GetValue(row) is CompatibilityRecommendation r && r.Item.Id == item.Id);
                window.ReviewSelectedDiscoveryItem();
                ((TextBox)window.FindName("GuidedModelFolderText")).Text = Path.GetFullPath(modelFolder);
                ((TextBox)window.FindName("GuidedRuntimePathText")).Text = Path.GetFullPath(runtime);
                await window.ReviewGuidedModelAsync();
                if (!((TextBox)window.FindName("GuidedModelDetailsText")).Text.Contains("MODEL DATA ACQUISITION PLAN"))
                    throw new InvalidOperationException("Selected model review failed: " + ((TextBlock)window.FindName("GuidedStatusText")).Text);
                check(window.GuidedModelPath == existing && ((CheckBox)window.FindName("GuidedModelApprovalCheck")).IsChecked != true,
                    "Live review targets only the user-selected result and grants no automatic acquisition approval");
                ((CheckBox)window.FindName("GuidedModelApprovalCheck")).IsChecked = true;
                await window.DownloadGuidedModelAsync();
                if (!File.Exists(window.GuidedReceiptPath)) throw new InvalidOperationException("Selected artifact verification did not produce an acquisition receipt.");
                var receipt = await OfflineModelVerifier.ReadReceiptAsync(window.GuidedReceiptPath!);
                check(receipt.Plan.Artifact == item.Artifact && receipt.Acquisition.ReusedExisting,
                    "Explicitly approved generic setup rehashes the exact installed model and records reuse without substituting or downloading another model");
                await window.StartGuidedChatAsync();
                if (!window.OwnedLlamaServerRunning || !((Button)window.FindName("ChatSendButton")).IsEnabled)
                    throw new InvalidOperationException("Selected model local launch failed: " + ((TextBlock)window.FindName("GuidedStatusText")).Text);
                check(true, "Generic selected-model configuration launches the approved local runtime and verifies actual generation without a fixed GPU wattage gate");
                var prompt = (TextBox)window.FindName("ChatPromptText");
                var transcript = (TextBox)window.FindName("ChatTranscriptText");
                prompt.Text = "Reply with one short greeting.";
                await window.SendChatAsync();
                prompt.Text = "Now reply with a short goodbye.";
                await window.SendChatAsync();
                check(transcript.Text.Split("Assistant:", StringSplitOptions.None).Length >= 3 && window.ChatHistoryCount >= 4,
                    "The chosen local model completes two actual chat turns through the generic user-selected path");
                var root = Path.Combine(AppContext.BaseDirectory, "Reports", "SelectedModelLive");
                Directory.CreateDirectory(root);
                await File.WriteAllTextAsync(Path.Combine(root, "last-run.json"), JsonSerializer.Serialize(new
                {
                    Repository = repository, FileName = fileName, ModelPath = existing, Runtime = runtime,
                    CompletedAt = DateTimeOffset.UtcNow, Receipt = window.GuidedReceiptPath,
                    ActualSourceSearchResults = report.Recommendations.Count, ReusedExisting = true, ChatTurns = 2,
                    Limitations = "One actual PC/model/runtime combination. Existing weights reused; no multi-GPU or model-quality claim."
                }, new JsonSerializerOptions { WriteIndented = true }));
            }, TimeSpan.FromMinutes(8), () => window.Close());
        }
        finally { window.Close(); }
        check(!window.OwnedLlamaServerRunning, "Closing the generic live test stops only its own model server");
    }
}
