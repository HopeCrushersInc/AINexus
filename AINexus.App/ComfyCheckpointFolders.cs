using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AINexus.App;

public sealed record ComfyCheckpointFolderPlan(string PlanId, string EngineRoot, string ConfigPath,
    IReadOnlyList<string> RequestedFolders, IReadOnlyList<string> ApprovedFolders, string? ExistingContents,
    string? ExistingConfigSha256, string ProposedContents, string? BackupPath, bool HasChanges, DateTimeOffset CreatedAt);
public sealed record ComfyCheckpointFolderApproval(string PlanId, bool ApproveConfigurationWrites);
public sealed record ComfyCheckpointFolderApplyResult(string EngineRoot, string ConfigPath, string? BackupPath,
    IReadOnlyList<string> ApprovedFolders, bool Changed, DateTimeOffset AppliedAt);

/// <summary>Reviews only explicit local checkpoint-folder connections for a separately started ComfyUI engine.
/// It neither selects models nor reads weights, downloads, changes other configs or starts/stops processes.</summary>
public static class ComfyCheckpointFolderPlanner
{
    public const string ConfigurationFileName = "ainexus-approved-checkpoints.json";
    internal const int MaximumConfigBytes = 65_536;
    internal const int MaximumFolders = 16;
    internal const string GroupName = "ainexus_approved_checkpoints";
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static ComfyCheckpointFolderPlan Create(string engineDirectory, IEnumerable<string> requestedFolders,
        DateTimeOffset? currentTime = null)
    {
        ArgumentNullException.ThrowIfNull(requestedFolders);
        var root = ResolveEngineRoot(engineDirectory);
        var requested = NormalizeFolders(requestedFolders, rejectDuplicates: false);
        if (requested.Count == 0) throw new ArgumentException("Choose at least one existing local checkpoint folder.", nameof(requestedFolders));
        var config = Path.Combine(root, ConfigurationFileName);
        var snapshot = ReadSnapshot(config);
        var existing = snapshot is null ? [] : ParseFolders(snapshot.Value.Contents);
        var approved = existing.Concat(requested).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (approved.Length > MaximumFolders) throw new InvalidDataException("A connection review supports at most16 explicitly approved checkpoint folders.");
        var changed = snapshot is null || approved.Length != existing.Count;
        var contents = changed ? Serialize(approved) : snapshot!.Value.Contents;
        var backup = changed && snapshot is not null ? Path.Combine(root, "ainexus-approved-checkpoints.backup-" + Guid.NewGuid().ToString("N") + ".json") : null;
        var plan = new ComfyCheckpointFolderPlan("", root, config, Array.AsReadOnly(requested.ToArray()), Array.AsReadOnly(approved),
            snapshot?.Contents, snapshot?.Hash, contents, backup, changed, currentTime ?? DateTimeOffset.UtcNow);
        return plan with { PlanId = Fingerprint(plan) };
    }

    internal static void ValidateForApply(ComfyCheckpointFolderPlan plan, ComfyCheckpointFolderApproval approval)
    {
        ArgumentNullException.ThrowIfNull(plan); ArgumentNullException.ThrowIfNull(approval);
        if (!approval.ApproveConfigurationWrites || string.IsNullOrWhiteSpace(approval.PlanId))
            throw new InvalidOperationException("Approve this exact folder/configuration review before writing anything.");
        if (plan.ProposedContents is null || Utf8.GetByteCount(plan.ProposedContents) > MaximumConfigBytes
            || plan.ExistingContents is not null && Utf8.GetByteCount(plan.ExistingContents) > MaximumConfigBytes
            || plan.RequestedFolders is null || plan.RequestedFolders.Count is < 1 or > MaximumFolders
            || plan.ApprovedFolders is null || plan.ApprovedFolders.Count is < 1 or > MaximumFolders)
            throw new InvalidDataException("The folder review exceeds its bounded configuration limits.");
        if (plan.PlanId != approval.PlanId || plan.PlanId != Fingerprint(plan))
            throw new InvalidOperationException("Configuration approval does not match this exact engine, folders and contents.");
        var now = DateTimeOffset.UtcNow;
        if (plan.CreatedAt == DateTimeOffset.MinValue || plan.CreatedAt > now.AddMinutes(1) || now - plan.CreatedAt > TimeSpan.FromMinutes(30))
            throw new InvalidOperationException("This connection review expired. Review the folders/configuration again.");
        var expected = Create(plan.EngineRoot, plan.RequestedFolders, plan.CreatedAt) with { BackupPath = plan.BackupPath };
        if (Fingerprint(expected) != plan.PlanId)
            throw new InvalidOperationException("The engine, folder or existing configuration changed since review. Nothing was applied; review again.");
        if (plan.BackupPath is { } backup)
        {
            var name = Path.GetFileName(backup);
            const string prefix = "ainexus-approved-checkpoints.backup-";
            if (!string.Equals(Path.GetDirectoryName(backup), plan.EngineRoot, StringComparison.OrdinalIgnoreCase)
                || !name.StartsWith(prefix, StringComparison.Ordinal) || !name.EndsWith(".json", StringComparison.Ordinal)
                || !Guid.TryParseExact(name[prefix.Length..^5], "N", out _))
                throw new InvalidDataException("The reviewed backup must be a uniquely named sibling of this owned configuration.");
            OfflineModelVerifier.CheckNoReparsePath(backup);
            if (File.Exists(backup) || Directory.Exists(backup)) throw new IOException("The reviewed backup location is occupied. Nothing was overwritten.");
        }
        if ((plan.HasChanges && plan.ExistingConfigSha256 is not null) != (plan.BackupPath is not null))
            throw new InvalidDataException("Replacing an existing owned configuration requires its exact reviewed backup path.");
    }

    internal static string ResolveEngineRoot(string directory)
    {
        var root = RuntimeDownloadPlanner.ResolveDirectory(directory);
        var python = Path.Combine(root, "python_embeded", "python.exe");
        OfflineModelVerifier.CheckNoReparsePath(python);
        if (!File.Exists(python)) root = RuntimeDownloadPlanner.ResolveDirectory(Path.Combine(root, "ComfyUI_windows_portable"));
        foreach (var file in new[] { Path.Combine(root, "python_embeded", "python.exe"), Path.Combine(root, "ComfyUI", "main.py") })
        {
            OfflineModelVerifier.CheckNoReparsePath(file);
            if (!File.Exists(file)) throw new FileNotFoundException("Choose an existing ComfyUI portable engine folder; no runtime is installed by this operation.");
        }
        return root;
    }

    // Read when the caller explicitly starts the engine, never when browsing or applying a review.
    internal static void AppendStartupConfiguration(ProcessStartInfo info, string engineRoot)
    {
        ArgumentNullException.ThrowIfNull(info);
        OfflineModelVerifier.RequireLocalDrivePath(engineRoot);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(engineRoot));
        var path = Path.Combine(root, ConfigurationFileName);
        if (ReadSnapshot(path) is not { } snapshot) return;
        ResolveEngineRoot(root);
        ParseFolders(snapshot.Contents);
        info.ArgumentList.Add("--extra-model-paths-config");
        info.ArgumentList.Add(path);
    }

    internal static (string Contents, string Hash)? ReadSnapshot(string path)
    {
        try
        {
            var payload = ReadBoundedBytes(path);
            var contents = Utf8.GetString(payload);
            ParseFolders(contents);
            return (contents, Convert.ToHexString(SHA256.HashData(payload)));
        }
        catch (FileNotFoundException) { return null; }
        catch (Exception ex) when (ex is JsonException or DecoderFallbackException)
        { throw new InvalidDataException("The dedicated checkpoint configuration is malformed or foreign; it was preserved. Review it manually before connecting folders."); }
    }

    // Publication backups may contain an external editor's arbitrary bytes. Compare
    // the actual bounded bytes without interpreting them as an app-owned JSON config.
    internal static string ReadBoundedFileHash(string path) => Convert.ToHexString(SHA256.HashData(ReadBoundedBytes(path)));

    private static byte[] ReadBoundedBytes(string path)
    {
        OfflineModelVerifier.CheckNoReparsePath(path);
        if (Directory.Exists(path)) throw new InvalidDataException("A folder occupies the checkpoint configuration file path; it was preserved.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumConfigBytes) throw new InvalidDataException("Checkpoint configuration bytes exceeded64KiB.");
        using var bytes = new MemoryStream();
        var buffer = new byte[8192]; int read;
        while ((read = stream.Read(buffer)) > 0)
        {
            if (bytes.Length + read > MaximumConfigBytes) throw new InvalidDataException("Checkpoint configuration bytes exceeded64KiB.");
            bytes.Write(buffer, 0, read);
        }
        return bytes.ToArray();
    }

    internal static IReadOnlyList<string> ParseFolders(string contents)
    {
        if (Utf8.GetByteCount(contents) > MaximumConfigBytes) throw new InvalidDataException("Checkpoint configuration exceeds64KiB.");
        using var json = JsonDocument.Parse(contents, new JsonDocumentOptions { MaxDepth = 4 });
        if (json.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("The dedicated checkpoint config is not an owned object.");
        var groups = json.RootElement.EnumerateObject().ToArray();
        if (groups.Length != 1 || groups[0].Name != GroupName || groups[0].Value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Unknown checkpoint configuration groups were preserved rather than overwritten.");
        var fields = groups[0].Value.EnumerateObject().ToArray();
        if (fields.Length != 1 || fields[0].Name != "checkpoints" || fields[0].Value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("The owned configuration supports explicit checkpoint folders only; defaults and other settings are not accepted.");
        var folders = NormalizeFolders(fields[0].Value.GetString()!.Split('\n'), rejectDuplicates: true);
        if (folders.Count == 0) throw new InvalidDataException("The owned configuration contains no approved checkpoint folders.");
        return folders;
    }

    private static IReadOnlyList<string> NormalizeFolders(IEnumerable<string> folders, bool rejectDuplicates)
    {
        var bounded = folders.Take(MaximumFolders + 1).ToArray();
        if (bounded.Length > MaximumFolders) throw new InvalidDataException("At most16 explicit checkpoint folders can be reviewed.");
        var paths = new List<string>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in bounded)
        {
            var full = RuntimeDownloadPlanner.ResolveDirectory(folder);
            if (!seen.Add(full))
            {
                if (rejectDuplicates) throw new InvalidDataException("Duplicate folders are not valid in this dedicated configuration.");
                continue;
            }
            paths.Add(full);
        }
        return paths;
    }
    private static string Serialize(IEnumerable<string> folders)
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, object>
        { [GroupName] = new Dictionary<string, string> { ["checkpoints"] = string.Join('\n', folders.Select(path => path.Replace('\\', '/'))) } },
            new JsonSerializerOptions { WriteIndented = true });
        if (Utf8.GetByteCount(json) > MaximumConfigBytes) throw new InvalidDataException("The approved checkpoint configuration exceeds64KiB.");
        return json;
    }
    internal static string Fingerprint(ComfyCheckpointFolderPlan plan) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(plan with { PlanId = "" })));

    public static string Describe(ComfyCheckpointFolderPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.PlanId != Fingerprint(plan)) throw new InvalidDataException("The folder connection review changed.");
        return $"LOCAL CHECKPOINT FOLDER CONNECTION — EXPLICIT CONFIGURATION APPROVAL REQUIRED\nPlan: {plan.PlanId}\n"
            + $"Engine: {plan.EngineRoot}\nOwned config: {plan.ConfigPath}\nExisting config SHA-256: {plan.ExistingConfigSha256 ?? "Not present"}\nBackup: {plan.BackupPath ?? "No existing config will be replaced"}\n"
            + "Explicit folders:\n" + string.Join('\n', plan.ApprovedFolders) + "\n\nExisting config:\n" + (plan.ExistingContents ?? "Not present")
            + "\n\nProposed exact config:\n" + plan.ProposedContents
            + "\n\nOnly this dedicated config and its displayed backup may be written. Existing ainexus-models.yaml and other settings remain byte-for-byte unchanged. "
            + "No engine starts, stops or reloads; the folders become available only on a subsequent explicit engine start and checkpoint refresh. "
            + "No default folder or model is selected. Folder presence proves neither complete checkpoints, hardware fit nor image generation. "
            + "Other configuration files may already reference overlapping folders; they are not rewritten or inferred by this review.";
    }
}

/// <summary>Publishes only an explicitly approved app-owned folder config. Atomic replacement preserves
/// the actual prior file as a unique backup. Cooperating app instances serialize; arbitrary external
/// editors are not claimed to participate in an OS compare-and-swap transaction.</summary>
public sealed class ComfyCheckpointFolderService
{
    public Task<ComfyCheckpointFolderApplyResult> ApplyAsync(ComfyCheckpointFolderPlan plan, ComfyCheckpointFolderApproval approval,
        CancellationToken cancellationToken = default) => Task.Run(() => ApplyCore(plan, approval, cancellationToken), cancellationToken);

    private static ComfyCheckpointFolderApplyResult ApplyCore(ComfyCheckpointFolderPlan plan, ComfyCheckpointFolderApproval approval, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(plan); ArgumentNullException.ThrowIfNull(approval);
        token.ThrowIfCancellationRequested();
        // The mutex is thread-affine. All bounded config I/O and publication stay on this worker thread.
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(plan.EngineRoot).ToUpperInvariant())));
        using var mutex = new Mutex(false, "Local\\AINexus-Comfy-CheckpointFolders-" + key);
        var held = false;
        string? temporary = null; var created = false;
        try
        {
            var deadline = Stopwatch.StartNew();
            while (!held)
            {
                token.ThrowIfCancellationRequested();
                try { held = mutex.WaitOne(100); } catch (AbandonedMutexException) { held = true; }
                if (!held && deadline.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Another folder configuration operation is busy; nothing was applied.");
            }
            ComfyCheckpointFolderPlanner.ValidateForApply(plan, approval);
            token.ThrowIfCancellationRequested();
            if (!plan.HasChanges) return new(plan.EngineRoot, plan.ConfigPath, null, plan.ApprovedFolders, false, DateTimeOffset.UtcNow);
            temporary = Path.Combine(plan.EngineRoot, ".ainexus-checkpoint-config-" + Guid.NewGuid().ToString("N") + ".partial");
            OfflineModelVerifier.CheckNoReparsePath(temporary);
            var bytes = new UTF8Encoding(false, true).GetBytes(plan.ProposedContents);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.WriteThrough))
            { created = true; stream.Write(bytes); stream.Flush(flushToDisk: true); }
            token.ThrowIfCancellationRequested();
            ComfyCheckpointFolderPlanner.ValidateForApply(plan, approval);
            OfflineModelVerifier.CheckNoReparsePath(temporary);
            if (plan.ExistingConfigSha256 is null) File.Move(temporary, plan.ConfigPath, overwrite: false);
            else File.Replace(temporary, plan.ConfigPath, plan.BackupPath!, ignoreMetadataErrors: false);
            created = false;
            VerifyPublishedConfiguration(plan, bytes);
            return new(plan.EngineRoot, plan.ConfigPath, plan.BackupPath, plan.ApprovedFolders, true, DateTimeOffset.UtcNow);
        }
        finally
        {
            if (created && temporary is not null) DeleteOwnTemporary(temporary, plan.EngineRoot);
            if (held) mutex.ReleaseMutex();
        }
    }

    internal static void VerifyPublishedConfiguration(ComfyCheckpointFolderPlan plan, byte[] expectedBytes)
    {
        try
        {
            if (plan.BackupPath is { } backup && ComfyCheckpointFolderPlanner.ReadBoundedFileHash(backup) != plan.ExistingConfigSha256)
                throw new InvalidDataException("The actual replaced backup differs from the reviewed configuration snapshot.");
            if (ComfyCheckpointFolderPlanner.ReadBoundedFileHash(plan.ConfigPath) != Convert.ToHexString(SHA256.HashData(expectedBytes)))
                throw new InvalidDataException("The configuration bytes changed after publication.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new InvalidOperationException("Configuration publication needs manual review. Changes may already have occurred at: " + plan.ConfigPath
                + ". The reviewed prior-file backup path is: " + (plan.BackupPath ?? "no prior configuration existed")
                + ". No successful connection is claimed and no rollback was attempted. Verification left any files present untouched; preserve and inspect them before reviewing the folders again.", ex);
        }
    }

    private static void DeleteOwnTemporary(string temporary, string root)
    {
        try
        {
            const string prefix = ".ainexus-checkpoint-config-";
            var path = Path.GetFullPath(temporary); var name = Path.GetFileName(path);
            if (!string.Equals(Path.GetDirectoryName(path), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase)
                || !name.StartsWith(prefix, StringComparison.Ordinal) || !name.EndsWith(".partial", StringComparison.Ordinal)
                || !Guid.TryParseExact(name[prefix.Length..^8], "N", out _)) return;
            OfflineModelVerifier.CheckNoReparsePath(path);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        { /* Do not mask the real failure or follow changed links to clean a temporary. */ }
    }
}
