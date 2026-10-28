using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Controls;
using AINexus.App;

namespace AINexus.Checks;

internal static class ComfyCheckpointFolderLiveChecks
{
    internal static void Run(string engineRoot, string checkpointFolder, Action<bool, string> check)
    {
        var resolvedEngine = ComfyCheckpointFolderPlanner.ResolveEngineRoot(engineRoot);
        var resolvedCheckpointFolder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(checkpointFolder));
        var yamlPath = Path.Combine(resolvedEngine, "ainexus-models.yaml");
        var originalYamlHash = File.Exists(yamlPath) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(yamlPath))) : null;
        var configPath = Path.Combine(resolvedEngine, ComfyCheckpointFolderPlanner.ConfigurationFileName);
        var before = ComfyCheckpointFolderPlanner.ReadSnapshot(configPath);
        var window = new MainWindow { GuidedPersistPreferences = false };
        try
        {
            GuidedInstallChecks.Pump(async () =>
            {
                window.CompleteWorkloadScan(await HardwareCapabilityScanner.ScanAsync());
                window.ApplyWorkloadSelection([DiscoveryInterest.Images]);
                check(window.OpenComfyCheckpointFolderSetup()
                    && string.IsNullOrEmpty(((TextBox)window.FindName("ComfyFolderEngineText")).Text)
                    && string.IsNullOrEmpty(((TextBox)window.FindName("ComfyFolderPathsText")).Text)
                    && window.ComfyCheckpointFolderReviewedPlanId is null,
                    "Live folder setup starts with no chosen engine, folder or write approval");
                ((TextBox)window.FindName("ComfyFolderEngineText")).Text = resolvedEngine;
                ((TextBox)window.FindName("ComfyFolderPathsText")).Text = resolvedCheckpointFolder;
                await window.ReviewComfyCheckpointFoldersAsync();
                if (window.ComfyCheckpointFolderReviewedPlanId is null)
                    throw new InvalidOperationException("Live folder review failed: " + ((TextBlock)window.FindName("ComfyFolderStatusText")).Text);
                var approval = (CheckBox)window.FindName("ComfyFolderApprovalCheck");
                check(approval.IsChecked != true && ComfyCheckpointFolderPlanner.ReadSnapshot(configPath)?.Hash == before?.Hash,
                    "Live folder review leaves actual engine configuration unchanged and requires exact approval");
                approval.IsChecked = true;
                await window.ApplyComfyCheckpointFoldersAsync();
                if (window.ComfyCheckpointFolderAppliedConfigPath != configPath)
                    throw new InvalidOperationException("Live folder apply failed: " + ((TextBlock)window.FindName("ComfyFolderStatusText")).Text);
                var applied = ComfyCheckpointFolderPlanner.ReadSnapshot(configPath)!.Value;
                var folders = ComfyCheckpointFolderPlanner.ParseFolders(applied.Contents);
                check(folders.Contains(resolvedCheckpointFolder, StringComparer.OrdinalIgnoreCase) && approval.IsChecked != true,
                    "Explicit live folder approval saves only the selected connection and consumes approval");
                var yamlHash = File.Exists(yamlPath) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(yamlPath))) : null;
                check(yamlHash == originalYamlHash, "Live folder connection preserves the existing engine YAML byte-for-byte");
                var imageServer = (ComfyServerController)typeof(MainWindow).GetField("_imageServer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                check(!imageServer.IsRunning && !window.OwnedLlamaServerRunning
                    && ((ComboBox)window.FindName("ImageModelSelector")).SelectedIndex == -1,
                    "Live configuration does not start either engine or choose an image checkpoint");
                check(!ComfyCheckpointFolderPlanner.Create(resolvedEngine, [resolvedCheckpointFolder]).HasChanges,
                    "A repeated real folder review detects the already approved connection without another write");
                var evidenceRoot = Path.Combine(AppContext.BaseDirectory, "Reports", "ComfyFolderLive");
                Directory.CreateDirectory(evidenceRoot);
                await File.WriteAllTextAsync(Path.Combine(evidenceRoot, "last-run.json"), JsonSerializer.Serialize(new
                {
                    CompletedAt = DateTimeOffset.UtcNow, EngineRoot = resolvedEngine, CheckpointFolder = resolvedCheckpointFolder,
                    ConfigPath = configPath, PreviousConfigSha256 = before?.Hash, AppliedConfigSha256 = applied.Hash,
                    ExistingYamlSha256 = originalYamlHash, EngineStarted = false, CheckpointSelected = false,
                    Limitation = "Actual approved configuration write only; no model completeness, hardware fit or generation claim."
                }, new JsonSerializerOptions { WriteIndented = true }));
            }, TimeSpan.FromMinutes(2), () => window.Close());
        }
        finally { window.Close(); }
    }
}
