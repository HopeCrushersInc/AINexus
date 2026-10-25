using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AINexus.App;

public sealed record OfflineArtifactAudit(ArtifactAcquisitionReceipt Receipt, ModelIntegrityEvidence Integrity, DateTimeOffset AuditedAt);
public sealed record RuntimeFileFingerprint(string RelativePath, long SizeBytes, string Sha256);

public sealed record OfflineVerificationEvidence(
    string VerificationId, string AcquisitionPlanId, string ModelPath, string ModelSha256,
    string RuntimeExecutable, IReadOnlyList<RuntimeFileFingerprint> RuntimeFiles, int ContextSize,
    DateTimeOffset StartedAt, DateTimeOffset CompletedAt, string ActualModelId, string Response,
    MachineCapabilities Machine, ResourceAdmission Admission, CompatibilityRecommendation CapacityAssessment,
    Uri Endpoint, IReadOnlyList<string> Arguments, string SavedEvidencePath, IReadOnlyList<string> Limitations)
{
    public bool OfflineModeRequested => true;
    public bool OSNetworkIsolationVerified => false;
}

/// <summary>
/// Audits local acquisition evidence and, only when VerifyAsync is explicitly invoked, runs one
/// local offline-mode smoke test. No external HTTP, downloads, installers or Windows changes occur.
/// The selected model/runtime files are held read-only during execution. Only this test's child is stopped.
/// </summary>
public static partial class OfflineModelVerifier
{
    internal const int MaximumReceiptBytes = 1024 * 1024;
    private const long MaximumRuntimeBytes = 4L * 1024 * 1024 * 1024;
    private const int MaximumRuntimeFiles = 256;

    public static async Task<OfflineArtifactAudit> AuditReceiptAsync(string receiptPath, CancellationToken cancellationToken = default)
    {
        var receipt = await ReadReceiptAsync(receiptPath, cancellationToken).ConfigureAwait(false);
        var integrity = await ModelArtifactVerifier.VerifyExistingAsync(receipt.Plan, cancellationToken).ConfigureAwait(false);
        return new(receipt, integrity, DateTimeOffset.UtcNow);
    }

    public static async Task<OfflineVerificationEvidence> VerifyAsync(string receiptPath, string executablePath,
        ResourcePolicy? policy = null, int contextSize = 4096, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (contextSize is < 512 or > 32768) throw new ArgumentOutOfRangeException(nameof(contextSize));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            return await VerifyCoreAsync(receiptPath, executablePath, policy, contextSize, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Local offline verification exceeded its five-minute limit. No successful verification evidence was recorded.");
        }
    }

    private static async Task<OfflineVerificationEvidence> VerifyCoreAsync(string receiptPath, string executablePath,
        ResourcePolicy? policy, int contextSize, CancellationToken token)
    {
        var started = DateTimeOffset.UtcNow;
        var receipt = await ReadReceiptAsync(receiptPath, token).ConfigureAwait(false);
        CheckNoReparsePath(receipt.Plan.DestinationPath);
        await using var modelLease = OpenReadLease(receipt.Plan.DestinationPath);
        if (modelLease.Length != receipt.Plan.Artifact.SizeBytes)
            throw new InvalidDataException("The current model byte count differs from the acquisition receipt. No runtime was launched.");
        var modelHash = Convert.ToHexString(await SHA256.HashDataAsync(modelLease, token).ConfigureAwait(false));
        if (!modelHash.Equals(receipt.Plan.Artifact.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The current model hash differs from the acquisition receipt. No runtime was launched.");

        using var runtime = await RuntimeLease.CaptureAsync(executablePath, token).ConfigureAwait(false);
        if (!await SupportsOfflineAsync(executablePath, receipt.Plan.DestinationPath, contextSize, token).ConfigureAwait(false))
            throw new NotSupportedException("The selected runtime does not advertise --offline. Select a compatible runtime; no model was launched.");

        var machine = await HardwareCapabilityScanner.ScanAsync(token).ConfigureAwait(false);
        var monitor = new ResourceMonitor();
        await monitor.SampleAsync(token).ConfigureAwait(false);
        await Task.Delay(250, token).ConfigureAwait(false);
        machine = machine with { Resources = await monitor.SampleAsync(token).ConfigureAwait(false), ScannedAt = DateTimeOffset.UtcNow };
        var admission = (policy ?? new ResourcePolicy()).Evaluate(machine.Resources);
        if (!admission.Allowed) throw new InvalidOperationException("Local verification was not admitted: " + admission.Reason);
        var capacity = EvaluateCapacity(receipt, machine);
        if (capacity.Status != RecommendationStatus.ResourceCandidate)
            throw new InvalidOperationException("Current capacity does not admit this smoke test: " + string.Join(" ", capacity.Reasons));

        var port = SelectLoopbackPort();
        var arguments = LlamaServerController.CreateStartInfo(executablePath, receipt.Plan.DestinationPath, port, contextSize, offlineMode: true).ArgumentList.ToArray();
        Uri endpoint;
        string modelId;
        string response;
        await using (var controller = new LlamaServerController())
        {
            try
            {
                await controller.StartAsync(executablePath, receipt.Plan.DestinationPath, port, contextSize, token, offlineMode: true).ConfigureAwait(false);
                endpoint = controller.Endpoint ?? throw new InvalidOperationException("The owned verification server has no ready endpoint.");
                using var http = OllamaClient.CreateLocalHttpClient();
                var client = new LlamaCppClient(http);
                var models = await client.GetModelsAsync(endpoint, token).ConfigureAwait(false);
                if (models.Count != 1) throw new InvalidDataException("The owned verification server did not report exactly one model identity.");
                modelId = models[0];
                response = await client.GenerateAsync(endpoint, modelId, "Answer briefly in plain text. Do not invoke tools.",
                    "Reply with exactly these two words: Portal ready.", token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(response)) throw new InvalidDataException("The local server completed without a nonempty smoke-test response.");
            }
            finally
            {
                // Cancellation never interrupts ownership cleanup or targets another server.
                await controller.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        token.ThrowIfCancellationRequested();
        CheckNoReparsePath(receipt.Plan.DestinationPath);
        var id = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(Path.GetDirectoryName(receipt.Plan.DestinationPath)!, ".ainexus", "verification");
        CheckNoReparseParents(directory);
        Directory.CreateDirectory(directory);
        CheckNoReparseParents(directory);
        var destination = Path.Combine(directory, id + ".json");
        var evidence = new OfflineVerificationEvidence(id, receipt.Plan.PlanId, receipt.Plan.DestinationPath, modelHash,
            Path.GetFullPath(executablePath), runtime.Fingerprints, contextSize, started, DateTimeOffset.UtcNow,
            modelId, response, machine, admission, capacity, endpoint, arguments, destination,
            Array.AsReadOnly(new[]
            {
                "The selected runtime was launched with its advertised --offline option and an explicit local model. Verification HTTP used loopback only, with redirects and proxies disabled.",
                "This was not an operating-system network-isolation or firewall test. No Windows firewall, service, environment-variable or system-wide configuration was changed.",
                "Runtime fingerprints cover the selected executable and its top-level DLL files, not the complete Windows/driver dependency closure or publisher authenticity.",
                "The smoke test establishes normally completed nonempty text generation, not response quality, tool-use capability, future performance or continued validity after model/runtime/hardware changes.",
                "Admission and capacity estimates are point-in-time policy checks, not resource reservations or guarantees of model fit. Only the owned test server was stopped."
            }));
        await SaveEvidenceAsync(evidence, token).ConfigureAwait(false);
        return evidence;
    }

    internal static CompatibilityRecommendation EvaluateCapacity(ArtifactAcquisitionReceipt receipt, MachineCapabilities machine)
    {
        // Acquisition plans originate in the standalone GGUF text-generation recipe. This
        // reuses that recipe's disclosed memory policy; no metadata is refreshed or invented.
        var plan = receipt.Plan;
        var item = new DiscoveredItem(plan.ItemId, plan.Name, plan.Artifact.Repository.Split('/')[0],
            DiscoveryInterest.LocalAssistants, "Model", plan.DeclaredLicense, plan.Artifact,
            [new("Recorded pinned acquisition source", plan.LicenseReference, plan.MetadataRetrievedAt)],
            "Previously acquired artifact from the standalone GGUF text-generation recipe; source metadata was not refreshed offline.", "text-generation");
        // The model was just read and verified. Budget scratch/evidence space, not a
        // second download of bytes already on disk. The allowance is policy, not a hardware spec.
        return HardwareCompatibilityEngine.Evaluate(item, machine, plan.TargetVolume,
            new CompatibilityPolicy(StorageReserveGiB: 0.0625), artifactAlreadyPresent: true);
    }

    internal static async Task<ArtifactAcquisitionReceipt> ReadReceiptAsync(string receiptPath, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(receiptPath);
        if (!Path.IsPathFullyQualified(receiptPath)) throw new ArgumentException("Select an absolute acquisition receipt path.", nameof(receiptPath));
        CheckNoReparsePath(receiptPath);
        await using var file = OpenReadLease(receiptPath);
        if (file.Length > MaximumReceiptBytes) throw new InvalidDataException("The acquisition receipt exceeds its 1 MiB limit.");
        using var data = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await file.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            if (data.Length + read > MaximumReceiptBytes) throw new InvalidDataException("The acquisition receipt exceeds its 1 MiB limit.");
            data.Write(buffer, 0, read);
        }
        using var json = JsonDocument.Parse(data.GetBuffer().AsMemory(0, checked((int)data.Length)), new JsonDocumentOptions { MaxDepth = 32 });
        if (json.RootElement.ValueKind != JsonValueKind.Object
            || json.RootElement.TryGetProperty("RuntimeExecutionVerified", out var execution) && execution.ValueKind != JsonValueKind.False)
            throw new InvalidDataException("An acquisition receipt cannot claim a runtime execution result.");
        var receipt = json.RootElement.Deserialize<ArtifactAcquisitionReceipt>()
            ?? throw new InvalidDataException("The acquisition receipt is empty.");
        ValidateReceipt(receipt);
        token.ThrowIfCancellationRequested();
        return receipt;
    }

    internal static void ValidateReceipt(ArtifactAcquisitionReceipt receipt)
    {
        if (receipt is null || receipt.SchemaVersion != 1 || receipt.Plan is null || receipt.Acquisition?.Evidence is not { } evidence)
            throw new InvalidDataException("Unsupported or incomplete acquisition receipt.");
        CheckNoReparsePath(receipt.Plan.DestinationPath);
        ModelAcquisitionPlanner.ValidateIdentity(receipt.Plan);
        if (!evidence.Exists || !evidence.Matches || evidence.ActualBytes != receipt.Plan.Artifact.SizeBytes
            || !string.Equals(evidence.Path, receipt.Plan.DestinationPath, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(evidence.ActualSha256, receipt.Plan.Artifact.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The acquisition receipt lacks matching artifact integrity evidence.");
    }

    internal static bool AdvertisesOffline(string help) => OfflineOption().IsMatch(help);
    [GeneratedRegex(@"(?m)^\s*--offline(?:\s|$)", RegexOptions.CultureInvariant)] private static partial Regex OfflineOption();

    private static async Task<bool> SupportsOfflineAsync(string executable, string model, int context, CancellationToken token)
    {
        using var process = new Process { StartInfo = LlamaServerController.CreateStartInfo(executable, model, 1024, context) };
        process.StartInfo.ArgumentList.Clear();
        process.StartInfo.ArgumentList.Add("--help");
        token.ThrowIfCancellationRequested();
        if (!process.Start()) throw new InvalidOperationException("The selected runtime help query could not start.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var stdout = ReadBoundedHelpAsync(process.StandardOutput, timeout.Token);
        var stderr = ReadBoundedHelpAsync(process.StandardError, timeout.Token);
        var all = Task.WhenAll(process.WaitForExitAsync(timeout.Token), stdout, stderr);
        try
        {
            await all.WaitAsync(timeout.Token).ConfigureAwait(false);
            if (process.ExitCode != 0) throw new InvalidOperationException("The selected runtime help query failed.");
            return AdvertisesOffline(await stdout.ConfigureAwait(false)) || AdvertisesOffline(await stderr.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            try { await all.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { }
            if (!all.IsCompleted) _ = all.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
            token.ThrowIfCancellationRequested();
            throw new TimeoutException("The selected runtime help query exceeded its time limit.");
        }
    }

    private static async Task<string> ReadBoundedHelpAsync(StreamReader reader, CancellationToken token)
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            if (text.Length < 262144) text.Append(buffer, 0, Math.Min(read, 262144 - text.Length));
        }
        return text.ToString();
    }

    private static int SelectLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Server.ExclusiveAddressUse = true;
        try { listener.Start(); return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    private static FileStream OpenReadLease(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read,
        131072, FileOptions.Asynchronous | FileOptions.SequentialScan);

    internal static void CheckNoReparsePath(string path)
    {
        RequireLocalDrivePath(path);
        CheckNoReparseParents(Path.GetDirectoryName(Path.GetFullPath(path))!);
        try
        {
            if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException("Verification cannot follow file reparse points.");
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }
    private static void CheckNoReparseParents(string directory)
    {
        RequireLocalDrivePath(directory);
        var full = Path.GetFullPath(directory);
        var root = Path.GetPathRoot(full)!;
        var current = root;
        foreach (var segment in full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (FileNotFoundException) { return; }
            catch (DirectoryNotFoundException) { return; }
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException("Verification cannot follow directory links or junctions.");
            if (!attributes.HasFlag(FileAttributes.Directory)) throw new InvalidDataException("A verification parent path is not a directory.");
        }
    }

    internal static void RequireLocalDrivePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length < 3 || !char.IsAsciiLetter(path[0])
            || path[1] != ':' || path[2] is not ('\\' or '/') || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("Choose an absolute local drive path. UNC, network and device paths require a separately authorized workflow.");
        var drive = new DriveInfo(Path.GetPathRoot(path)!);
        if (drive.DriveType is DriveType.Network or DriveType.Unknown or DriveType.NoRootDirectory)
            throw new InvalidOperationException("Verification requires an available local drive; network-mapped drives are not authorized by this workflow.");
    }

    internal static async Task<IReadOnlyList<RuntimeFileFingerprint>> FingerprintRuntimeAsync(string executable, CancellationToken token = default)
    {
        using var runtime = await RuntimeLease.CaptureAsync(executable, token).ConfigureAwait(false);
        return runtime.Fingerprints;
    }

    private static async Task SaveEvidenceAsync(OfflineVerificationEvidence evidence, CancellationToken token)
    {
        var temporary = evidence.SavedEvidencePath + ".partial";
        var created = false;
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.Asynchronous))
            {
                created = true;
                await JsonSerializer.SerializeAsync(stream, evidence, new JsonSerializerOptions { WriteIndented = true }, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            CheckNoReparseParents(Path.GetDirectoryName(evidence.SavedEvidencePath)!);
            File.Move(temporary, evidence.SavedEvidencePath, overwrite: false);
            created = false;
        }
        finally
        {
            if (created)
            {
                try { CheckNoReparseParents(Path.GetDirectoryName(temporary)!); File.Delete(temporary); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { }
            }
        }
    }

    private sealed class RuntimeLease : IDisposable
    {
        private readonly List<FileStream> _files = [];
        public IReadOnlyList<RuntimeFileFingerprint> Fingerprints { get; private set; } = [];
        public static async Task<RuntimeLease> CaptureAsync(string executable, CancellationToken token)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(executable);
            if (!Path.IsPathFullyQualified(executable) || !Path.GetFileName(executable).Equals("llama-server.exe", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Select an absolute llama-server.exe path.");
            CheckNoReparsePath(executable);
            if (!File.Exists(executable)) throw new FileNotFoundException("The selected local runtime does not exist.");
            executable = Path.GetFullPath(executable);
            var directory = Path.GetDirectoryName(executable)!;
            var paths = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFullPath)
                .Where(path => path.Equals(executable, StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).Equals(".dll", StringComparison.OrdinalIgnoreCase))
                .Take(MaximumRuntimeFiles + 1).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            if (paths.Length > MaximumRuntimeFiles) throw new InvalidDataException("The runtime directory exceeds the 256-file identity limit.");
            if (!paths.Contains(executable, StringComparer.OrdinalIgnoreCase)) throw new FileNotFoundException("The selected runtime executable disappeared before identity inspection.");
            var lease = new RuntimeLease();
            try
            {
                var fingerprints = new List<RuntimeFileFingerprint>();
                long total = 0;
                foreach (var path in paths)
                {
                    token.ThrowIfCancellationRequested();
                    CheckNoReparsePath(path);
                    var stream = OpenReadLease(path);
                    lease._files.Add(stream);
                    if (stream.Length > MaximumRuntimeBytes - total) throw new InvalidDataException("Runtime identity input exceeds the 4 GiB limit.");
                    total += stream.Length;
                    var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
                    fingerprints.Add(new(Path.GetFileName(path), stream.Length, hash));
                }
                lease.Fingerprints = fingerprints.AsReadOnly();
                return lease;
            }
            catch { lease.Dispose(); throw; }
        }
        public void Dispose() { foreach (var stream in _files) stream.Dispose(); }
    }
}
