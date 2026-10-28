using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AINexus.App;

namespace AINexus.Checks;

internal static class ComfyCheckpointFolderChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var engine = MakeEngine(Path.Combine(root, "engine"));
            var folderA = MakeFolder(Path.Combine(root, "models A"));
            var folderB = MakeFolder(Path.Combine(root, "models B"));
            var folderC = MakeFolder(Path.Combine(root, "models C"));
            var legacy = Path.Combine(engine, "ainexus-models.yaml");
            var legacyBytes = "# Existing user configuration must survive exactly\r\nlegacy:\r\n  checkpoints: D:/elsewhere\r\n"u8.ToArray();
            await File.WriteAllBytesAsync(legacy, legacyBytes);
            var service = new ComfyCheckpointFolderService();
            var plan = ComfyCheckpointFolderPlanner.Create(engine, [folderA, folderA.ToUpperInvariant()]);
            check(plan.ApprovedFolders.SequenceEqual([folderA]) && plan.RequestedFolders.Count == 1
                && plan.ExistingConfigSha256 is null && !File.Exists(plan.ConfigPath)
                && !Directory.GetFiles(engine, "*.partial").Any(),
                "Checkpoint-folder review keeps only explicitly selected existing folders, deduplicates paths and writes no configuration");
            var description = ComfyCheckpointFolderPlanner.Describe(plan);
            check(description.Contains(plan.ConfigPath, StringComparison.Ordinal) && description.Contains(plan.ProposedContents, StringComparison.Ordinal)
                && description.Contains("No engine starts", StringComparison.Ordinal) && description.Contains("No default folder or model", StringComparison.Ordinal),
                "Folder connection review displays exact config contents and separates apply from engine start and model selection");
            check(await RejectsAsync(() => service.ApplyAsync(plan, new(plan.PlanId, false)))
                && await RejectsAsync(() => service.ApplyAsync(plan, new("another-review", true)))
                && !File.Exists(plan.ConfigPath),
                "Declined or mismatched folder-configuration approval causes no config/backup writes");
            var tampered = plan with { ConfigPath = Path.Combine(engine, "foreign.json") };
            tampered = tampered with { PlanId = ComfyCheckpointFolderPlanner.Fingerprint(tampered) };
            check(await RejectsAsync(() => service.ApplyAsync(tampered, new(tampered.PlanId, true)))
                && !File.Exists(tampered.ConfigPath), "Even a recomputed review cannot redirect writes to another engine configuration file");
            var first = await service.ApplyAsync(plan, new(plan.PlanId, true));
            check(first.Changed && first.BackupPath is null && ComfyCheckpointFolderPlanner.ParseFolders(await File.ReadAllTextAsync(plan.ConfigPath)).SequenceEqual([folderA])
                && File.ReadAllBytes(legacy).SequenceEqual(legacyBytes),
                "An explicitly approved new folder config publishes only the dedicated JSON and preserves the original YAML byte-for-byte");
            using (var json = JsonDocument.Parse(await File.ReadAllTextAsync(plan.ConfigPath)))
            {
                check(json.RootElement.EnumerateObject().Count() == 1
                    && json.RootElement.GetProperty("ainexus_approved_checkpoints").EnumerateObject().Single().Name == "checkpoints"
                    && json.RootElement.GetProperty("ainexus_approved_checkpoints").GetProperty("checkpoints").GetString() == folderA.Replace('\\', '/'),
                    "The YAML-compatible JSON supplies only explicit checkpoint paths, with no default flag or arbitrary engine settings");
            }
            var prior = await File.ReadAllBytesAsync(plan.ConfigPath);
            var merge = ComfyCheckpointFolderPlanner.Create(engine, [folderB]);
            check(merge.ApprovedFolders.SequenceEqual([folderA, folderB]) && merge.BackupPath is not null
                && ComfyCheckpointFolderPlanner.Describe(merge).Contains(merge.BackupPath, StringComparison.Ordinal)
                && ComfyCheckpointFolderPlanner.Describe(merge).Contains(merge.ExistingConfigSha256!, StringComparison.Ordinal),
                "A subsequent connection review merges only existing and explicitly requested folders and exposes its unique backup path");
            var merged = await service.ApplyAsync(merge, new(merge.PlanId, true));
            check(merged.Changed && File.ReadAllBytes(merged.BackupPath!).SequenceEqual(prior)
                && ComfyCheckpointFolderPlanner.ParseFolders(await File.ReadAllTextAsync(plan.ConfigPath)).SequenceEqual([folderA, folderB])
                && !Directory.GetFiles(engine, "*.partial").Any() && File.ReadAllBytes(legacy).SequenceEqual(legacyBytes),
                "Atomic folder-config replacement preserves exact previous bytes as a recoverable backup without altering legacy YAML or leaving partials");
            var backupCount = Directory.GetFiles(engine, "ainexus-approved-checkpoints.backup-*.json").Length;
            var noOp = ComfyCheckpointFolderPlanner.Create(engine, [folderA]);
            var noOpBytes = await File.ReadAllBytesAsync(noOp.ConfigPath);
            var unchanged = await service.ApplyAsync(noOp, new(noOp.PlanId, true));
            check(!noOp.HasChanges && !unchanged.Changed && unchanged.BackupPath is null
                && File.ReadAllBytes(noOp.ConfigPath).SequenceEqual(noOpBytes)
                && Directory.GetFiles(engine, "ainexus-approved-checkpoints.backup-*.json").Length == backupCount,
                "Connecting an already approved folder is a byte-preserving no-op, without duplicate entries or another backup");
            var stale = ComfyCheckpointFolderPlanner.Create(engine, [folderC]);
            var external = Config([folderA, folderC]);
            await File.WriteAllTextAsync(stale.ConfigPath, external);
            check(await RejectsAsync(() => service.ApplyAsync(stale, new(stale.PlanId, true)))
                && File.ReadAllText(stale.ConfigPath) == external && !File.Exists(stale.BackupPath),
                "A config changed after review is preserved instead of clobbered by stale approval");
            await File.WriteAllBytesAsync(stale.ConfigPath, noOpBytes);
            var occupied = ComfyCheckpointFolderPlanner.Create(engine, [folderC]);
            await File.WriteAllTextAsync(occupied.BackupPath!, "preserve occupied backup");
            check(await RejectsAsync(() => service.ApplyAsync(occupied, new(occupied.PlanId, true)))
                && File.ReadAllText(occupied.BackupPath!) == "preserve occupied backup" && File.ReadAllBytes(occupied.ConfigPath).SequenceEqual(noOpBytes),
                "An occupied recovery-backup path aborts configuration changes without overwriting either existing file");
            var info = new ProcessStartInfo("fixture-never-started.exe");
            info.ArgumentList.Add("--extra-model-paths-config"); info.ArgumentList.Add(legacy);
            ComfyCheckpointFolderPlanner.AppendStartupConfiguration(info, engine);
            check(info.ArgumentList.SequenceEqual(["--extra-model-paths-config", legacy, "--extra-model-paths-config", plan.ConfigPath])
                && File.ReadAllBytes(legacy).SequenceEqual(legacyBytes),
                "The explicit-start hook adds validated owned JSON alongside the original YAML without starting a process or changing either file");
            await InvalidConfigurationsAsync(root, folderA, check);
            await PostPublicationAsync(root, check);
            await CancellationAsync(engine, folderC, service, check);
            await ReparseAsync(root, folderA, check);
        }
        finally
        {
            var full = Path.GetFullPath(root);
            if (!full.StartsWith(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "fixtures")) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe folder-connection fixture cleanup.");
            Directory.Delete(full, recursive: true);
        }
    }

    private static async Task PostPublicationAsync(string root, Action<bool, string> check)
    {
        var engine = MakeEngine(Path.Combine(root, "publication verification"));
        var first = MakeFolder(Path.Combine(root, "publication folder A"));
        var second = MakeFolder(Path.Combine(root, "publication folder B"));
        var config = Path.Combine(engine, ComfyCheckpointFolderPlanner.ConfigurationFileName);
        var original = Encoding.UTF8.GetBytes(Config([first]));
        await File.WriteAllBytesAsync(config, original);
        var plan = ComfyCheckpointFolderPlanner.Create(engine, [second]);
        var proposed = Encoding.UTF8.GetBytes(plan.ProposedContents);
        var external = new byte[] { 0xff, 0x00, 0x1b, 0x01 };
        // Stage the actual post-File.Replace state deterministically, without a timing race:
        // a proposed config was published and an external editor's arbitrary bytes backed up.
        await File.WriteAllBytesAsync(config, proposed);
        await File.WriteAllBytesAsync(plan.BackupPath!, external);
        var conflict = VerificationFailure();
        check(conflict is not null && conflict.Message.Contains("Changes may already have occurred", StringComparison.Ordinal)
            && conflict.Message.Contains(config, StringComparison.Ordinal) && conflict.Message.Contains(plan.BackupPath!, StringComparison.Ordinal)
            && conflict.Message.Contains("No successful connection", StringComparison.Ordinal)
            && File.ReadAllBytes(config).SequenceEqual(proposed) && File.ReadAllBytes(plan.BackupPath!).SequenceEqual(external),
            "Post-publication verification reports actual config/backup paths and possible changes when arbitrary foreign backup bytes differ, without clobbering either file");
        await File.WriteAllBytesAsync(plan.BackupPath!, original);
        await File.WriteAllTextAsync(config, "external edit after publication");
        conflict = VerificationFailure();
        check(conflict is not null && conflict.Message.Contains(config, StringComparison.Ordinal)
            && conflict.Message.Contains(plan.BackupPath!, StringComparison.Ordinal)
            && File.ReadAllText(config) == "external edit after publication" && File.ReadAllBytes(plan.BackupPath!).SequenceEqual(original),
            "Published-file hash mismatch is reported as a post-publication conflict without parsing or overwriting the external edit");
        File.Delete(config); Directory.CreateDirectory(config);
        conflict = VerificationFailure();
        check(conflict is not null && conflict.Message.Contains("Changes may already have occurred", StringComparison.Ordinal)
            && conflict.Message.Contains(config, StringComparison.Ordinal) && Directory.Exists(config),
            "Post-publication file-read failures retain the same explicit partial-change report instead of an ambiguous parse or filesystem error");
        Directory.Delete(config);
        await File.WriteAllBytesAsync(config, proposed);
        Directory.Delete(first);
        ComfyCheckpointFolderService.VerifyPublishedConfiguration(plan, proposed);
        check(Rejects(() => ComfyCheckpointFolderPlanner.AppendStartupConfiguration(new("fixture-never-started.exe"), engine))
            && File.ReadAllBytes(config).SequenceEqual(proposed) && File.ReadAllBytes(plan.BackupPath!).SequenceEqual(original),
            "Publication byte verification remains separate from a later startup's required current folder validation");

        InvalidOperationException? VerificationFailure()
        {
            try { ComfyCheckpointFolderService.VerifyPublishedConfiguration(plan, proposed); return null; }
            catch (InvalidOperationException ex) { return ex; }
        }
    }

    private static async Task InvalidConfigurationsAsync(string root, string folder, Action<bool, string> check)
    {
        var engine = MakeEngine(Path.Combine(root, "invalid engine"));
        var path = Path.Combine(engine, ComfyCheckpointFolderPlanner.ConfigurationFileName);
        var info = new ProcessStartInfo("fixture-never-started.exe");
        ComfyCheckpointFolderPlanner.AppendStartupConfiguration(info, engine);
        check(info.ArgumentList.Count == 0 && !File.Exists(path), "An absent owned checkpoint config adds no implicit folder or startup argument");
        var expired = ComfyCheckpointFolderPlanner.Create(engine, [folder], DateTimeOffset.UtcNow.AddHours(-1));
        check(await RejectsAsync(() => new ComfyCheckpointFolderService().ApplyAsync(expired, new(expired.PlanId, true))) && !File.Exists(path),
            "Expired folder-connection approval cannot write or start an engine");
        foreach (var contents in new[]
        {
            "invalid JSON", "{\"foreign\":{\"checkpoints\":\"F:/elsewhere\"}}",
            "{\"ainexus_approved_checkpoints\":{\"checkpoints\":\"F:/elsewhere\",\"is_default\":true}}",
            "{\"ainexus_approved_checkpoints\":{\"checkpoints\":\"\",\"checkpoints\":\"\"}}",
            "{\"ainexus_approved_checkpoints\":{\"checkpoints\":[]}}",
            Config([folder, folder]), Config(["\\\\unapproved-host\\models"]), new string(' ', 65_537)
        })
        {
            await File.WriteAllTextAsync(path, contents);
            check(Rejects(() => ComfyCheckpointFolderPlanner.Create(engine, [folder]))
                && Rejects(() => ComfyCheckpointFolderPlanner.AppendStartupConfiguration(new("fixture-never-started.exe"), engine))
                && File.ReadAllText(path) == contents && !Directory.GetFiles(engine, "*.partial").Any(),
                "Malformed, foreign, oversized, duplicate or network checkpoint configurations are preserved and rejected for both review and startup");
        }
        File.Delete(path);
        check(Rejects(() => ComfyCheckpointFolderPlanner.Create(engine, []))
            && Rejects(() => ComfyCheckpointFolderPlanner.Create(engine, [Path.GetPathRoot(folder)!]))
            && Rejects(() => ComfyCheckpointFolderPlanner.Create(engine, [Path.Combine(folder, "not-created")]))
            && Rejects(() => ComfyCheckpointFolderPlanner.Create(engine, [Environment.GetFolderPath(Environment.SpecialFolder.Windows)]))
            && !Directory.Exists(Path.Combine(folder, "not-created")),
            "Connection reviews require explicit existing private folders, rejecting empty choices, whole drives, Windows paths and automatic folder creation");
        var many = Enumerable.Range(0, 17).Select(index => MakeFolder(Path.Combine(root, "many-" + index))).ToArray();
        check(Rejects(() => ComfyCheckpointFolderPlanner.Create(engine, many)) && !File.Exists(path),
            "The checkpoint configuration is bounded to sixteen explicitly approved local folders");
    }

    private static async Task CancellationAsync(string engine, string folder, ComfyCheckpointFolderService service, Action<bool, string> check)
    {
        var plan = ComfyCheckpointFolderPlanner.Create(engine, [folder]);
        var original = await File.ReadAllBytesAsync(plan.ConfigPath);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(engine).ToUpperInvariant())));
        var owner = Task.Run(() =>
        {
            using var mutex = new Mutex(false, "Local\\AINexus-Comfy-CheckpointFolders-" + key);
            mutex.WaitOne();
            try { entered.TrySetResult(); release.Wait(TimeSpan.FromSeconds(5)); }
            finally { mutex.ReleaseMutex(); }
        });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            using var cancellation = new CancellationTokenSource();
            var waiting = service.ApplyAsync(plan, new(plan.PlanId, true), cancellation.Token);
            await Task.Delay(200); cancellation.Cancel();
            check(await RejectsAsync(() => waiting.WaitAsync(TimeSpan.FromSeconds(3))) && waiting.IsCanceled
                && File.ReadAllBytes(plan.ConfigPath).SequenceEqual(original) && !File.Exists(plan.BackupPath)
                && !Directory.GetFiles(engine, "*.partial").Any(),
                "Cancellation while another configuration writer owns the mutex is bounded and leaves config, backup and temporary paths untouched");
        }
        finally { release.Set(); await owner; }
    }

    private static async Task ReparseAsync(string root, string target, Action<bool, string> check)
    {
        var engine = MakeEngine(Path.Combine(root, "reparse engine"));
        var link = Path.Combine(root, "linked checkpoints");
        try
        {
            try { Directory.CreateSymbolicLink(link, target); }
            catch (IOException ex) when (OperatingSystem.IsWindows() && (ex.HResult & 0xffff) == 1314)
            {
                if (new[] { link, target }.Any(path => path.IndexOfAny("%\"&|<>^()".ToCharArray()) >= 0 || path.Any(char.IsControl)))
                    throw new InvalidOperationException("Unsafe junction fixture paths.");
                await HardwareScanner.RunProcessAsync(Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                    ["/d", "/v:off", "/c", "mklink", "/J", link, target], CancellationToken.None, TimeSpan.FromSeconds(5));
            }
            check(File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint)
                && Rejects(() => ComfyCheckpointFolderPlanner.Create(engine, [link]))
                && !File.Exists(Path.Combine(engine, ComfyCheckpointFolderPlanner.ConfigurationFileName)),
                "An actual directory reparse point cannot connect checkpoints outside the explicitly reviewed local path");
        }
        finally
        {
            if (Directory.Exists(link) && File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint)) Directory.Delete(link);
        }
        check(Directory.Exists(target), "Removing the owned checkpoint-link fixture preserves its real target folder");
    }

    private static string MakeFolder(string path) { Directory.CreateDirectory(path); return path; }
    private static string MakeEngine(string path)
    {
        Directory.CreateDirectory(Path.Combine(path, "python_embeded")); Directory.CreateDirectory(Path.Combine(path, "ComfyUI"));
        File.WriteAllText(Path.Combine(path, "python_embeded", "python.exe"), "fixture marker; never executed");
        File.WriteAllText(Path.Combine(path, "ComfyUI", "main.py"), "fixture marker; never executed"); return path;
    }
    private static string Config(IEnumerable<string> folders) => JsonSerializer.Serialize(new Dictionary<string, object>
        { ["ainexus_approved_checkpoints"] = new Dictionary<string, string> { ["checkpoints"] = string.Join('\n', folders) } });
    private static bool Rejects(Action action) { try { action(); return false; } catch (Exception ex) when (Expected(ex)) { return true; } }
    private static async Task<bool> RejectsAsync(Func<Task> action) { try { await action(); return false; } catch (Exception ex) when (Expected(ex)) { return true; } }
    private static bool Expected(Exception ex) => ex is InvalidDataException or InvalidOperationException or ArgumentException or IOException or OperationCanceledException or JsonException;
}
