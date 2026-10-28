using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using AINexus.App;

namespace AINexus.Checks;

internal static class ComfyCheckpointFolderUiChecks
{
    internal static void RunNative(Action<bool, string> check)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            ReviewApprovalAndWrites(root, check);
            StaleReview(root, check);
            CloseBeforeApply(root, check);
            CommittedApplyAndCancellation(root, check);
            CommittedApplyWithChangedEngine(root, check);
        }
        finally
        {
            var prefix = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "fixtures")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe checkpoint-folder UI fixture cleanup.");
            Directory.Delete(root, recursive: true);
        }
    }

    private static void ReviewApprovalAndWrites(string root, Action<bool, string> check)
    {
        var engine = MakeEngine(Path.Combine(root, "review engine"));
        var folderA = NewFolder(Path.Combine(root, "models A"));
        var folderB = NewFolder(Path.Combine(root, "models B"));
        var legacy = Path.Combine(engine, "ainexus-models.yaml");
        var legacyBytes = "# User YAML must remain exact\r\nexisting:\r\n  checkpoints: D:/old\r\n"u8.ToArray();
        File.WriteAllBytes(legacy, legacyBytes);
        var config = Path.Combine(engine, ComfyCheckpointFolderPlanner.ConfigurationFileName);
        var window = NewWindow();
        try
        {
            GuidedInstallChecks.Pump(async () =>
            {
                var reviews = 0;
                ComfyCheckpointFolderPlan? reviewed = null;
                window.ComfyCheckpointFolderReviewOverride = (selectedEngine, selectedFolders, _) =>
                {
                    reviews++;
                    var plan = ComfyCheckpointFolderPlanner.Create(selectedEngine, selectedFolders);
                    reviewed = plan;
                    return Task.FromResult(plan);
                };
                var choices = ((IEnumerable<WorkloadChoice>)Find<ItemsControl>(window, "WorkloadChoices").ItemsSource).Select(choice => choice.Selected).ToArray();
                var imageModels = Find<ComboBox>(window, "ImageModelSelector");
                var oldEngineSelection = "previous explicit engine selection";
                Find<TextBox>(window, "ImageEnginePathText").Text = oldEngineSelection;
                Find<Button>(window, "ImageConnectFolderButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                check(Find<TabItem>(window, "ComfyCheckpointFoldersTab").IsSelected
                    && Find<TextBox>(window, "ComfyFolderEngineText").Text == "" && Find<TextBox>(window, "ComfyFolderPathsText").Text == ""
                    && reviews == 0 && !File.Exists(config) && !Find<Button>(window, "ComfyFolderApplyButton").IsEnabled,
                    "Opening checkpoint-folder setup selects no engine or folder and performs no review or configuration write");
                await window.ApplyComfyCheckpointFoldersAsync();
                check(!File.Exists(config) && !Directory.GetFiles(engine, "*.backup-*.json").Any(),
                    "Folder configuration cannot be applied before any exact review or approval");
                Find<TextBox>(window, "ComfyFolderEngineText").Text = engine;
                Find<TextBox>(window, "ComfyFolderPathsText").Text = folderA;
                await window.ReviewComfyCheckpointFoldersAsync();
                var approval = Find<CheckBox>(window, "ComfyFolderApprovalCheck");
                var details = Find<TextBox>(window, "ComfyFolderDetailsText");
                check(reviews == 1 && reviewed is not null && window.ComfyCheckpointFolderReviewedPlanId == reviewed.PlanId
                    && details.Text.Contains(reviewed.ProposedContents, StringComparison.Ordinal)
                    && details.Text.Contains(config, StringComparison.Ordinal) && approval.IsChecked != true && !File.Exists(config),
                    "Folder review displays the complete proposed configuration and exact paths without writing or checking approval");
                await window.ApplyComfyCheckpointFoldersAsync();
                check(!File.Exists(config), "A reviewed folder connection still cannot write without its own explicit approval");
                approval.IsChecked = true;
                Find<TextBox>(window, "ComfyFolderPathsText").Text = folderB;
                check(window.ComfyCheckpointFolderReviewedPlanId is null && approval.IsChecked != true
                    && !Find<Button>(window, "ComfyFolderApplyButton").IsEnabled,
                    "Changing a checkpoint folder revokes the previous configuration plan and approval");
                await window.ReviewComfyCheckpointFoldersAsync();
                approval.IsChecked = true;
                await window.ApplyComfyCheckpointFoldersAsync();
                check(window.ComfyCheckpointFolderAppliedConfigPath == config && File.Exists(config)
                    && ComfyCheckpointFolderPlanner.ParseFolders(await File.ReadAllTextAsync(config)).SequenceEqual([folderB])
                    && File.ReadAllBytes(legacy).SequenceEqual(legacyBytes) && approval.IsChecked != true
                    && window.ComfyCheckpointFolderReviewedPlanId is null && imageModels.SelectedIndex == -1 && !OwnedImageEngine(window).IsRunning,
                    "Approved native folder connection writes only the exact app-owned JSON, consumes approval and preserves YAML without choosing or starting a model");
                check(Find<TextBox>(window, "ImageEnginePathText").Text == oldEngineSelection,
                    "Applying a folder connection does not automatically replace the current Image Studio engine selection");
                var original = await File.ReadAllBytesAsync(config);
                Find<TextBox>(window, "ComfyFolderPathsText").Text = folderA;
                await window.ReviewComfyCheckpointFoldersAsync();
                check(reviewed!.ExistingConfigSha256 is { } hash && details.Text.Contains(hash, StringComparison.Ordinal)
                    && reviewed.BackupPath is { } backup && details.Text.Contains(backup, StringComparison.Ordinal)
                    && details.Text.Contains(reviewed.ExistingContents!, StringComparison.Ordinal),
                    "An update review exposes the existing configuration identity, exact prior contents and planned backup path");
                await File.WriteAllBytesAsync(config, original.Concat(new byte[] { 10 }).ToArray());
                var changed = await File.ReadAllBytesAsync(config);
                approval.IsChecked = true;
                await window.ApplyComfyCheckpointFoldersAsync();
                check(File.ReadAllBytes(config).SequenceEqual(changed) && window.ComfyCheckpointFolderAppliedConfigPath is null
                    && window.ComfyCheckpointFolderReviewedPlanId is null && approval.IsChecked != true
                    && (reviewed.BackupPath is null || !File.Exists(reviewed.BackupPath)),
                    "A raw configuration-byte change after review blocks stale approval and preserves the changed file without an unreviewed backup");
                await File.WriteAllBytesAsync(config, original);
                await window.ReviewComfyCheckpointFoldersAsync();
                approval.IsChecked = true;
                await window.ApplyComfyCheckpointFoldersAsync();
                check(reviewed!.BackupPath is { } savedBackup && File.ReadAllBytes(savedBackup).SequenceEqual(original)
                    && ComfyCheckpointFolderPlanner.ParseFolders(await File.ReadAllTextAsync(config)).SequenceEqual([folderB, folderA])
                    && File.ReadAllBytes(legacy).SequenceEqual(legacyBytes),
                    "A newly approved folder addition preserves the exact prior JSON as a backup while retaining existing YAML");
                var backupCount = Directory.GetFiles(engine, "ainexus-approved-checkpoints.backup-*.json").Length;
                await window.ApplyComfyCheckpointFoldersAsync();
                check(Directory.GetFiles(engine, "ainexus-approved-checkpoints.backup-*.json").Length == backupCount
                    && !OwnedImageEngine(window).IsRunning && !window.OwnedLlamaServerRunning,
                    "Consumed folder-write approval cannot repeat an application or start an image or text engine");
                Find<Button>(window, "ComfyFolderOpenImagesButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                check(Find<TabItem>(window, "ImageStudioTab").IsSelected && Find<TextBox>(window, "ImageEnginePathText").Text == engine
                    && imageModels.SelectedIndex == -1 && !OwnedImageEngine(window).IsRunning
                    && ((IEnumerable<WorkloadChoice>)Find<ItemsControl>(window, "WorkloadChoices").ItemsSource).Select(choice => choice.Selected).SequenceEqual(choices),
                    "Explicitly opening Images carries the approved engine folder while preserving workload choices, engine state and no checkpoint selection");
                window.OpenComfyCheckpointFolderSetup();
                check(Find<TextBox>(window, "ComfyFolderEngineText").Text == "" && Find<TextBox>(window, "ComfyFolderPathsText").Text == ""
                    && approval.IsChecked != true && File.Exists(config),
                    "Reopening folder setup clears remembered fields and approval while preserving the intentionally applied configuration on disk");
                var beforeOpenReviews = reviews;
                window.OpenComfyCheckpointFolderSetup(folderA);
                check(Find<TextBox>(window, "ComfyFolderEngineText").Text == "" && Find<TextBox>(window, "ComfyFolderPathsText").Text == folderA
                    && reviews == beforeOpenReviews && approval.IsChecked != true,
                    "An explicit folder entry copies only that exact path and neither chooses an engine nor reviews or applies it");
            }, TimeSpan.FromSeconds(20), () => window.Close());
        }
        finally { window.Close(); }
    }

    private static void StaleReview(string root, Action<bool, string> check)
    {
        var engine = MakeEngine(Path.Combine(root, "stale engine"));
        var first = NewFolder(Path.Combine(root, "stale first"));
        var second = NewFolder(Path.Combine(root, "stale second"));
        var window = NewWindow();
        try
        {
            GuidedInstallChecks.Pump(async () =>
            {
                window.OpenComfyCheckpointFolderSetup();
                Find<TextBox>(window, "ComfyFolderEngineText").Text = engine;
                Find<TextBox>(window, "ComfyFolderPathsText").Text = first;
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var released = new TaskCompletionSource<ComfyCheckpointFolderPlan>(TaskCreationOptions.RunContinuationsAsynchronously);
                var oldPlan = ComfyCheckpointFolderPlanner.Create(engine, [first]);
                window.ComfyCheckpointFolderReviewOverride = (_, _, _) => { entered.SetResult(); return released.Task; };
                var pending = window.ReviewComfyCheckpointFoldersAsync();
                await entered.Task;
                check(!Find<TextBox>(window, "ComfyFolderEngineText").IsEnabled && !Find<Button>(window, "ImageStartButton").IsEnabled,
                    "An active folder review visibly serializes its inputs and existing image-engine actions");
                Find<TextBox>(window, "ComfyFolderPathsText").Text = second;
                released.SetResult(oldPlan);
                await pending;
                check(window.ComfyCheckpointFolderReviewedPlanId is null && Find<CheckBox>(window, "ComfyFolderApprovalCheck").IsChecked != true
                    && !File.Exists(oldPlan.ConfigPath) && Find<TextBox>(window, "ComfyFolderEngineText").IsEnabled,
                    "A changed input during an awaited review cancels stale results and restores controls without writing or approving the old plan");
            }, TimeSpan.FromSeconds(15), () => window.Close());
        }
        finally { window.Close(); }
    }

    private static void CloseBeforeApply(string root, Action<bool, string> check)
    {
        var engine = MakeEngine(Path.Combine(root, "cancel engine"));
        var folder = NewFolder(Path.Combine(root, "cancel models"));
        var window = NewWindow();
        var closed = false;
        window.Closed += (_, _) => closed = true;
        try
        {
            GuidedInstallChecks.Pump(async () =>
            {
                window.OpenComfyCheckpointFolderSetup(folder);
                Find<TextBox>(window, "ComfyFolderEngineText").Text = engine;
                await window.ReviewComfyCheckpointFoldersAsync();
                Find<CheckBox>(window, "ComfyFolderApprovalCheck").IsChecked = true;
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                window.ComfyCheckpointFolderApplyOverride = async (plan, approval, token) =>
                {
                    entered.SetResult();
                    await Task.Delay(Timeout.Infinite, token);
                    return await new ComfyCheckpointFolderService().ApplyAsync(plan, approval, token);
                };
                var pending = window.ApplyComfyCheckpointFoldersAsync();
                await entered.Task;
                window.Close();
                check(!closed, "Closing an awaited folder application waits for cancellation instead of abandoning its operation");
                await pending;
                check(closed && !File.Exists(Path.Combine(engine, ComfyCheckpointFolderPlanner.ConfigurationFileName))
                    && !Directory.GetFiles(engine, "*.partial").Any() && window.ComfyCheckpointFolderReviewedPlanId is null
                    && !OwnedImageEngine(window).IsRunning,
                    "Cancellation before configuration commit closes cleanly with consumed approval and no engine startup or pending configuration file");
            }, TimeSpan.FromSeconds(15), () => window.Close());
        }
        finally { window.Close(); }
    }

    private static void CommittedApplyAndCancellation(string root, Action<bool, string> check)
    {
        var engine = MakeEngine(Path.Combine(root, "committed engine"));
        var first = NewFolder(Path.Combine(root, "committed first"));
        var second = NewFolder(Path.Combine(root, "committed second"));
        var window = NewWindow();
        var closed = false;
        window.Closed += (_, _) => closed = true;
        try
        {
            GuidedInstallChecks.Pump(async () =>
            {
                window.OpenComfyCheckpointFolderSetup(first);
                Find<TextBox>(window, "ComfyFolderEngineText").Text = engine;
                await window.ReviewComfyCheckpointFoldersAsync();
                Find<CheckBox>(window, "ComfyFolderApprovalCheck").IsChecked = true;
                window.ComfyCheckpointFolderApplyOverride = async (plan, approval, token) =>
                {
                    var result = await new ComfyCheckpointFolderService().ApplyAsync(plan, approval, token);
                    Find<TextBox>(window, "ComfyFolderPathsText").Text = second;
                    window.Close();
                    return result;
                };
                await window.ApplyComfyCheckpointFoldersAsync();
                var config = Path.Combine(engine, ComfyCheckpointFolderPlanner.ConfigurationFileName);
                check(closed && File.Exists(config) && window.ComfyCheckpointFolderAppliedConfigPath == config
                    && ComfyCheckpointFolderPlanner.ParseFolders(await File.ReadAllTextAsync(config)).SequenceEqual([first])
                    && Find<TextBlock>(window, "ComfyFolderStatusText").Text.Contains("previously approved operation completed", StringComparison.Ordinal)
                    && window.ComfyCheckpointFolderReviewedPlanId is null,
                    "A configuration committed before cancellation remains reported with its actual path even when inputs change and closing follows");
                check(!ReadFlag(window, "_guidedClose") && !ReadFlag(window, "_closeAfterImages") && !ReadFlag(window, "_closeAfterComfyFolders"),
                    "Folder-operation close cleanup clears shared guided/image close latches after preserving committed evidence");
            }, TimeSpan.FromSeconds(15), () => window.Close());
        }
        finally { window.Close(); }
    }

    private static void CommittedApplyWithChangedEngine(string root, Action<bool, string> check)
    {
        var approvedEngine = MakeEngine(Path.Combine(root, "approved completed engine"));
        var unapprovedEngine = MakeEngine(Path.Combine(root, "new unapproved engine"));
        var folder = NewFolder(Path.Combine(root, "completed engine models"));
        var window = NewWindow();
        try
        {
            GuidedInstallChecks.Pump(async () =>
            {
                window.OpenComfyCheckpointFolderSetup(folder);
                Find<TextBox>(window, "ComfyFolderEngineText").Text = approvedEngine;
                await window.ReviewComfyCheckpointFoldersAsync();
                Find<CheckBox>(window, "ComfyFolderApprovalCheck").IsChecked = true;
                window.ComfyCheckpointFolderApplyOverride = async (plan, approval, token) =>
                {
                    var result = await new ComfyCheckpointFolderService().ApplyAsync(plan, approval, token);
                    Find<TextBox>(window, "ComfyFolderEngineText").Text = unapprovedEngine;
                    return result;
                };
                await window.ApplyComfyCheckpointFoldersAsync();
                Find<TextBox>(window, "ImageEnginePathText").Text = unapprovedEngine;
                check(window.ComfyCheckpointFolderAppliedConfigPath == Path.Combine(approvedEngine, ComfyCheckpointFolderPlanner.ConfigurationFileName)
                    && window.ComfyCheckpointFolderReviewedPlanId is null
                    && !File.Exists(Path.Combine(unapprovedEngine, ComfyCheckpointFolderPlanner.ConfigurationFileName)),
                    "An input change after committed configuration preserves the completed engine identity without applying the newly typed engine");
                Find<Button>(window, "ComfyFolderOpenImagesButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                check(Find<TextBox>(window, "ImageEnginePathText").Text == approvedEngine
                    && Find<ComboBox>(window, "ImageModelSelector").SelectedIndex == -1 && !OwnedImageEngine(window).IsRunning,
                    "The explicit Open Images action uses the completed approved engine rather than later unapproved input, without starting or selecting a model");
            }, TimeSpan.FromSeconds(15), () => window.Close());
        }
        finally { window.Close(); }
    }

    private static MainWindow NewWindow()
    {
        var window = new MainWindow { GuidedPersistPreferences = false };
        window.CompleteWorkloadScan(DiscoveryPipelineChecks.FixtureMachine());
        window.ApplyWorkloadSelection([DiscoveryInterest.Images, DiscoveryInterest.Coding]);
        return window;
    }
    private static string MakeEngine(string path)
    {
        Directory.CreateDirectory(Path.Combine(path, "python_embeded")); Directory.CreateDirectory(Path.Combine(path, "ComfyUI"));
        File.WriteAllText(Path.Combine(path, "python_embeded", "python.exe"), "fixture marker; never executed");
        File.WriteAllText(Path.Combine(path, "ComfyUI", "main.py"), "fixture marker; never executed");
        return path;
    }
    private static string NewFolder(string path) { Directory.CreateDirectory(path); return path; }
    private static T Find<T>(MainWindow window, string name) => (T)window.FindName(name);
    private static ComfyServerController OwnedImageEngine(MainWindow window) => (ComfyServerController)typeof(MainWindow).GetField("_imageServer", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
    private static bool ReadFlag(MainWindow window, string name) => (bool)typeof(MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
}
