using System.IO;

using System.IO.Compression;

using System.Runtime.InteropServices;

using System.Security.Cryptography;

using System.Text;

using System.Text.Json;



namespace AINexus.App;



public sealed record RuntimeInstallationPlan(string PlanId, RuntimeArchiveInspection Inspection,

    string LocalArchiveDirectory, DateTimeOffset CreatedAt)

{

    public string Destination => Inspection.Plan.Destination;

    public long DeclaredExpandedBytes => Inspection.DeclaredExpandedBytes;

}

public sealed record RuntimeInstallationApproval(string PlanId, bool AcceptDeclaredLicense,

    bool ConfirmBundledLicenseReview, bool ApproveDestinationWrites);

public sealed record RuntimeInstallationProgress(string Stage, long CompletedBytes, long ExpectedBytes);

public sealed record RuntimeInstallationResult(string Destination, string ExecutablePath, string ReceiptPath, DateTimeOffset CompletedAt);



/// <summary>Review of an existing CPU ZIP only; grants no approval and never executes its payload.</summary>

public static class RuntimeInstallationPlanner

{

    internal const string ReceiptName = ".ainexus-installation.json";

    internal const long ReceiptHeadroom = 16L * 1024 * 1024;



    public static async Task<RuntimeInstallationPlan> CreateAsync(RuntimeArchiveInspection inspection,

        string localArchiveDirectory, CancellationToken token = default)

    {

        token.ThrowIfCancellationRequested();

        ArgumentNullException.ThrowIfNull(inspection);

        ValidateRecipe(inspection);

        var directory = NormalizeLocalDirectory(localArchiveDirectory);

        var current = await RuntimeArchiveInspector.InspectAsync(inspection.Plan, directory, token).ConfigureAwait(false);

        RequireSameInspection(inspection, current);

        var plan = new RuntimeInstallationPlan("", inspection, directory, DateTimeOffset.UtcNow);

        return plan with { PlanId = Fingerprint(plan) };

    }



    internal static string Fingerprint(RuntimeInstallationPlan plan) => Convert.ToHexString(SHA256.HashData(

        JsonSerializer.SerializeToUtf8Bytes(plan with { PlanId = "" })));



    internal static void ValidateRecipe(RuntimeArchiveInspection inspection)

    {

        var plan = inspection.Plan;

        if (plan is null || inspection.PlanId != plan.PlanId || plan.PlanId != RuntimeFilePlanner.Fingerprint(plan)

            || plan.Files.Count != 1) throw new InvalidDataException("Installation requires one unchanged exact runtime file review.");

        var native = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();

        var assessment = RuntimeSetupPlanner.Assess(plan.Files[0], plan.Release, plan.Machine);

        if (!OperatingSystem.IsWindows() || native is not ("x64" or "arm64")

            || !plan.Machine.OsDescription.Contains("Windows", StringComparison.OrdinalIgnoreCase)

            || !plan.Machine.OsArchitecture.Equals(native, StringComparison.OrdinalIgnoreCase)

            || assessment.Role != "Runtime archive" || assessment.Backend != "cpu"

            || assessment.Status != RuntimeArchiveStatus.PlatformMatch

            || !assessment.Architecture.Equals(native, StringComparison.OrdinalIgnoreCase))

            throw new NotSupportedException("Local extraction currently supports only one native Windows CPU x64/arm64 runtime ZIP. GPU, support, alternate-architecture and multi-archive recipes are unavailable.");

        RuntimeFilePlanner.ValidateIdentity(plan.Files[0], plan.Release);

        if (plan.Files[0].Sha256 is null || plan.CompressedBytes != plan.Files[0].SizeBytes)

            throw new InvalidDataException("A matching recorded archive size and SHA-256 are required.");

        if (string.IsNullOrWhiteSpace(plan.DeclaredLicense) || new[] { "unknown", "unavailable", "not specified" }.Contains(plan.DeclaredLicense.Trim(), StringComparer.OrdinalIgnoreCase))

            throw new InvalidDataException("The runtime's declared license is unknown; extraction cannot be approved.");

        if (!inspection.HasServer || !inspection.Entries.Any(e => !e.Directory && e.RelativePath.Equals("llama-server.exe", StringComparison.OrdinalIgnoreCase) && e.DeclaredBytes > 0))

            throw new InvalidDataException("The inspected archive must contain a nonempty root llama-server.exe. Its contents have not been verified as executable.");

        if (!inspection.Entries.Any(IsLicenseFile)) throw new InvalidDataException("The archive has no recognized bundled license file to review.");

        if (inspection.Entries.Any(e => e.RelativePath.Split('/')[0].Equals(ReceiptName, StringComparison.OrdinalIgnoreCase)))

            throw new InvalidDataException("The archive conflicts with the reserved installation receipt path.");

        if (inspection.DeclaredExpandedBytes <= 0 || inspection.Entries.Count is < 1 or > 10000

            || inspection.DeclaredExpandedBytes != inspection.Entries.Sum(e => e.DeclaredBytes))

            throw new InvalidDataException("The inspected expanded-size evidence is invalid.");

        ExpectedOutputs(inspection, 0); // Also bounds implicit-directory metadata before filesystem writes.

        var destination = RuntimeSetupPlanner.ValidateNewDirectory(plan.Destination);

        if (destination != plan.Destination || Directory.Exists(destination) || File.Exists(destination))

            throw new IOException("Choose a new runtime folder; existing files and installations cannot be overwritten.");

        if (!Directory.Exists(Path.GetDirectoryName(destination)))

            throw new DirectoryNotFoundException("Choose a new runtime folder whose parent directory already exists.");

    }



    internal static Dictionary<string, (bool Directory, long Bytes)> ExpectedOutputs(RuntimeArchiveInspection inspection, long receiptBytes)

    {

        var paths = new Dictionary<string, (bool Directory, long Bytes)>(StringComparer.OrdinalIgnoreCase)

        { [ReceiptName] = (false, receiptBytes) };

        foreach (var entry in inspection.Entries)

        {

            paths[entry.RelativePath] = (entry.Directory, entry.DeclaredBytes);

            var parent = entry.RelativePath;

            while (parent.LastIndexOf('/') is var slash && slash >= 0)

            {

                parent = parent[..slash];

                if (paths.TryGetValue(parent, out var previous) && !previous.Directory)

                    throw new InvalidDataException("An installation output conflicts with an implicit directory.");

                paths[parent] = (true, 0);

                if (paths.Count > 20000) throw new InvalidDataException("Installation output and implicit-directory count exceeds safety policy.");

            }

            if (paths.Count > 20000) throw new InvalidDataException("Installation output count exceeds safety policy.");

        }

        return paths;

    }



    internal static bool IsLicenseFile(RuntimeArchiveEntry entry)

    {

        var name = entry.RelativePath.Split('/').Last();

        return !entry.Directory && entry.DeclaredBytes > 0

            && (name.StartsWith("LICENSE", StringComparison.OrdinalIgnoreCase) || name.StartsWith("COPYING", StringComparison.OrdinalIgnoreCase));

    }



    internal static string NormalizeLocalDirectory(string directory)

    {

        OfflineModelVerifier.RequireLocalDrivePath(directory);

        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));

        OfflineModelVerifier.CheckNoReparsePath(Path.Combine(full, ".ainexus-local-archive"));

        if (!Directory.Exists(full)) throw new DirectoryNotFoundException("Choose an existing local archive directory.");

        return full;

    }



    internal static void RequireSameInspection(RuntimeArchiveInspection expected, RuntimeArchiveInspection current)

    {

        if (expected.PlanId != current.PlanId || expected.Plan.PlanId != RuntimeFilePlanner.Fingerprint(current.Plan)

            || expected.DeclaredExpandedBytes != current.DeclaredExpandedBytes || expected.HasServer != current.HasServer

            || !expected.Entries.SequenceEqual(current.Entries))

            throw new InvalidDataException("The archive inspection changed. Prepare a new installation review; no previous approval applies.");

    }



    public static async Task<string> ReadLicenseTextsAsync(RuntimeInstallationPlan plan, CancellationToken token = default)

    {

        token.ThrowIfCancellationRequested();

        if (plan.PlanId != Fingerprint(plan)) throw new InvalidDataException("The installation plan changed; prepare a new review.");

        ValidateRecipe(plan.Inspection);

        NormalizeLocalDirectory(plan.LocalArchiveDirectory);

        var file = plan.Inspection.Plan.Files[0];

        var path = Path.Combine(plan.LocalArchiveDirectory, file.Name);

        OfflineModelVerifier.CheckNoReparsePath(path);

        await using var archive = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous);

        if (archive.Length != file.SizeBytes || !Convert.ToHexString(await SHA256.HashDataAsync(archive, token).ConfigureAwait(false)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))

            throw new InvalidDataException("The local ZIP changed. No bundled terms were read.");

        var current = await RuntimeArchiveInspector.InspectAsync(plan.Inspection.Plan, plan.LocalArchiveDirectory, token).ConfigureAwait(false);

        RequireSameInspection(plan.Inspection, current);

        var licenses = current.Entries.Where(IsLicenseFile).ToArray();

        if (licenses.Any(entry => entry.DeclaredBytes > 65536) || licenses.Sum(entry => entry.DeclaredBytes) > 262144)

            throw new InvalidDataException("Bundled terms exceed the viewer's 64 KiB per-file or 256 KiB total limit. Review the archive terms separately; no terms were silently omitted or accepted.");

        archive.Position = 0;

        using var zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true);

        var result = new StringBuilder("BUNDLED LICENSE FILES - READ FROM THE REVIEWED LOCAL ZIP\nNo license acceptance or completeness determination is inferred from viewing these files.\n");

        foreach (var license in licenses)

        {

            token.ThrowIfCancellationRequested();

            var entry = zip.Entries.Single(candidate => candidate.FullName.Replace('\\', '/').TrimEnd('/') == license.RelativePath);

            var bytes = new byte[checked((int)license.DeclaredBytes)];

            await using var source = entry.Open();

            await source.ReadExactlyAsync(bytes, token).ConfigureAwait(false);

            var extra = new byte[1];

            if (await source.ReadAsync(extra, token).ConfigureAwait(false) != 0) throw new InvalidDataException("Bundled terms exceed their inspected size.");

            string text;

            try { text = new UTF8Encoding(false, true).GetString(bytes); }

            catch (DecoderFallbackException ex) { throw new InvalidDataException("A bundled license is not supported UTF-8 text. Review it separately; no terms were silently accepted.", ex); }

            if (text.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t')))

                throw new InvalidDataException("A bundled license contains unsupported control bytes. Review it separately; no terms were silently accepted.");

            result.AppendLine("\nFILE: " + license.RelativePath);

            result.AppendLine(text);

        }

        return result.ToString();

    }



    public static string Describe(RuntimeInstallationPlan plan)

    {

        var text = new StringBuilder("LOCAL CPU RUNTIME EXTRACTION PLAN - APPROVAL REQUIRED\n");

        text.AppendLine($"Installation plan: {plan.PlanId}\nExact file review: {plan.Inspection.Plan.PlanId}\nLocal archives: {plan.LocalArchiveDirectory}\nNew destination: {plan.Destination}\nDeclared expanded bytes: {plan.DeclaredExpandedBytes:N0}\nAdditional receipt headroom: {ReceiptHeadroom:N0} bytes\nDeclared runtime license: {plan.Inspection.Plan.DeclaredLicense}");

        foreach (var file in plan.Inspection.Plan.Files) text.AppendLine($"Archive: {file.Name}\nRecorded bytes: {file.SizeBytes:N0}\nRecorded SHA-256: {file.Sha256}\nRecorded source: {file.DownloadUrl}");

        text.AppendLine("Bundled files requiring your separate license review:");

        foreach (var entry in plan.Inspection.Entries.Where(IsLicenseFile)) text.AppendLine("* " + entry.RelativePath);

        text.AppendLine("Approval must separately accept the declared license, confirm review of bundled component terms, and approve writes to this new folder. A filename is not proof of license coverage.");

        text.AppendLine("Existing ZIP bytes will be rehashed while held read-only, reinspected and decompressed into a new sibling staging directory. The receipt is written before publishing the new folder without overwriting. No network, installer, script, runtime or model is executed. Existing runtimes/models are preserved.");

        text.AppendLine("A successful extraction establishes neither publisher authenticity, dependency completeness, CPU instruction compatibility, nor working/offline inference. Configure and verify separately. Failed or cancelled extraction may retain its clearly identified staging folder for manual inspection; no automatic recursive deletion is attempted.");

        return text.ToString();

    }

}



/// <summary>Explicit approved extraction only. Never downloads, executes or changes system configuration.</summary>

public static class RuntimeInstallationService

{

    public static async Task<RuntimeInstallationResult> InstallAsync(RuntimeInstallationPlan plan,

        RuntimeInstallationApproval approval, CancellationToken token = default, IProgress<RuntimeInstallationProgress>? progress = null)

    {

        token.ThrowIfCancellationRequested();

        ArgumentNullException.ThrowIfNull(plan);

        if (approval is null || approval.PlanId != plan.PlanId || !approval.AcceptDeclaredLicense

            || !approval.ConfirmBundledLicenseReview || !approval.ApproveDestinationWrites)

            throw new InvalidOperationException("Explicit approval of this exact plan, declared license, bundled license review and destination writes is required.");

        if (plan.PlanId != RuntimeInstallationPlanner.Fingerprint(plan)) throw new InvalidDataException("The installation plan changed after review.");

        RuntimeInstallationPlanner.ValidateRecipe(plan.Inspection);

        if (RuntimeInstallationPlanner.NormalizeLocalDirectory(plan.LocalArchiveDirectory) != plan.LocalArchiveDirectory)

            throw new InvalidDataException("The reviewed local archive directory is not canonical.");

        var file = plan.Inspection.Plan.Files[0];

        var archivePath = Path.Combine(plan.LocalArchiveDirectory, file.Name);

        OfflineModelVerifier.CheckNoReparsePath(archivePath);

        await using var archive = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous);

        if (archive.Length != file.SizeBytes || !Convert.ToHexString(await SHA256.HashDataAsync(archive, token).ConfigureAwait(false)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))

            throw new InvalidDataException("The local archive no longer matches the approved size/hash; no destination was created.");

        var current = await RuntimeArchiveInspector.InspectAsync(plan.Inspection.Plan, plan.LocalArchiveDirectory, token).ConfigureAwait(false);

        RuntimeInstallationPlanner.RequireSameInspection(plan.Inspection, current);

        RuntimeInstallationPlanner.ValidateRecipe(current);

        var drive = new DriveInfo(Path.GetPathRoot(plan.Destination)!);

        if (drive.AvailableFreeSpace < checked(current.DeclaredExpandedBytes + RuntimeInstallationPlanner.ReceiptHeadroom))

            throw new IOException("Current free disk space cannot accommodate expanded files and the installation receipt.");

        var stage = Path.Combine(Path.GetDirectoryName(plan.Destination)!, ".ainexus-stage-" + Guid.NewGuid().ToString("N"));

        token.ThrowIfCancellationRequested();

        if (Directory.Exists(stage) || File.Exists(stage)) throw new IOException("The generated staging path already exists; nothing was overwritten.");

        string? retainedStage = null;

        try

        {

            Directory.CreateDirectory(stage);

            retainedStage = stage;

            progress?.Report(new("Staging", 0, current.DeclaredExpandedBytes));

            token.ThrowIfCancellationRequested();

            OfflineModelVerifier.CheckNoReparsePath(Path.Combine(stage, RuntimeInstallationPlanner.ReceiptName));

            archive.Position = 0;

            using (var zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true))

            {

                if (zip.Entries.Count != current.Entries.Count) throw new InvalidDataException("Archive entry count changed after inspection.");

                var buffer = new byte[131072];

                for (var index = 0; index < current.Entries.Count; index++)

                {

                    token.ThrowIfCancellationRequested();

                    var expected = current.Entries[index];

                    var entry = zip.Entries[index];

                    if (entry.FullName.Replace('\\', '/').TrimEnd('/') != expected.RelativePath || entry.Length != expected.DeclaredBytes)

                        throw new InvalidDataException("Archive entry identity changed after inspection.");

                    var output = Path.GetFullPath(Path.Combine(stage, expected.RelativePath.Replace('/', Path.DirectorySeparatorChar)));

                    if (!output.StartsWith(stage + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))

                        throw new InvalidDataException("An archive destination escaped its owned staging folder.");

                    OfflineModelVerifier.CheckNoReparsePath(output);

                    if (expected.Directory) { Directory.CreateDirectory(output); continue; }

                    Directory.CreateDirectory(Path.GetDirectoryName(output)!);

                    OfflineModelVerifier.CheckNoReparsePath(output);

                    await using var destination = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None, buffer.Length, FileOptions.Asynchronous);

                    await using var source = entry.Open();

                    long written = 0;

                    while (true)

                    {

                        token.ThrowIfCancellationRequested();

                        var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, expected.DeclaredBytes - written + 1)), token).ConfigureAwait(false);

                        if (read == 0) break;

                        if (read > expected.DeclaredBytes - written) throw new InvalidDataException("Decompressed file exceeded its inspected declared size.");

                        await destination.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);

                        written += read;

                    }

                    if (written != expected.DeclaredBytes) throw new InvalidDataException("Decompressed file size differs from its inspected declaration.");

                    await destination.FlushAsync(token).ConfigureAwait(false);

                }

            }

            token.ThrowIfCancellationRequested();

            var completed = DateTimeOffset.UtcNow;

            var receipt = JsonSerializer.SerializeToUtf8Bytes(new

            {

                SchemaVersion = 1, Plan = plan, Approval = approval, ApprovalConsumed = true, CompletedAt = completed,

                RuntimeExecutionVerified = false, PublisherAuthenticityVerified = false,

                Limits = "Local byte-identity checks and exact declared-size extraction only. No runtime was launched. Bundled license terms remain the user's reviewed responsibility."

            });

            if (receipt.Length > RuntimeInstallationPlanner.ReceiptHeadroom) throw new InvalidDataException("Installation receipt exceeded its bounded metadata budget.");

            var stagedReceipt = Path.Combine(stage, RuntimeInstallationPlanner.ReceiptName);

            OfflineModelVerifier.CheckNoReparsePath(stagedReceipt);

            await using (var output = new FileStream(stagedReceipt, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous))

            {

                await output.WriteAsync(receipt, token).ConfigureAwait(false);

                await output.FlushAsync(token).ConfigureAwait(false);

            }

            // Recheck every approved output path immediately before publishing. Do not recursively

            // delete on failure: a raced filesystem link must never make cleanup follow an external target.

            ValidateStagedOutputs(stage, current, receipt.Length, token);

            RuntimeInstallationPlanner.ValidateRecipe(current);

            token.ThrowIfCancellationRequested();

            Directory.Move(stage, plan.Destination);

            retainedStage = null;

            return new(plan.Destination, Path.Combine(plan.Destination, "llama-server.exe"),

                Path.Combine(plan.Destination, RuntimeInstallationPlanner.ReceiptName), completed);

        }

        catch (OperationCanceledException ex) when (token.IsCancellationRequested)

        {

            throw new OperationCanceledException("Extraction cancelled. The destination was not published. Any owned staging files were retained for inspection at: " + (retainedStage ?? stage), ex, token);

        }

        catch (Exception ex) when (ex is not OutOfMemoryException)

        {

            throw new IOException("Extraction could not publish the new runtime folder. No recursive cleanup was attempted; inspect any retained staging files at: " + (retainedStage ?? stage), ex);

        }

    }



    private static void ValidateStagedOutputs(string stage, RuntimeArchiveInspection inspection, long receiptBytes, CancellationToken token)

    {

        var expected = RuntimeInstallationPlanner.ExpectedOutputs(inspection, receiptBytes);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var pending = new Stack<string>();

        pending.Push(stage);

        while (pending.TryPop(out var directory))

        {

            token.ThrowIfCancellationRequested();

            OfflineModelVerifier.CheckNoReparsePath(directory);

            foreach (var path in Directory.EnumerateFileSystemEntries(directory))

            {

                token.ThrowIfCancellationRequested();

                OfflineModelVerifier.CheckNoReparsePath(path);

                var relative = Path.GetRelativePath(stage, path).Replace('\\', '/');

                if (!expected.TryGetValue(relative, out var item) || !seen.Add(relative))

                    throw new InvalidDataException("Staging contains an unexpected or duplicate output; nothing was published.");

                var isDirectory = File.GetAttributes(path).HasFlag(FileAttributes.Directory);

                if (isDirectory != item.Directory || !isDirectory && new FileInfo(path).Length != item.Bytes)

                    throw new InvalidDataException("A staged output no longer matches its approved type/size.");

                if (isDirectory) pending.Push(path);

            }

        }

        if (seen.Count != expected.Count) throw new InvalidDataException("An approved staging output is missing; nothing was published.");

    }

}

