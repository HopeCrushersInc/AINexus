using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AINexus.App;

public sealed record RuntimeDownloadPlan(string PlanId, DiscoveryReport Report, RuntimeFilePlan Files,
    string ArchiveDirectory, string ArchivePath, DateTimeOffset CreatedAt);
public sealed record RuntimeDownloadApproval(string PlanId, bool AcceptDeclaredLicense, bool ApproveDestinationWrites,
    bool AcknowledgeSeparateBundledTermsReview);
public sealed record RuntimeDownloadResult(string ArchiveDirectory, string ArchivePath, string ReceiptPath,
    bool ReusedExisting, DateTimeOffset CompletedAt);

/// <summary>Exact review of one native CPU runtime ZIP. No network, extraction or execution capability.</summary>
public static class RuntimeDownloadPlanner
{
    internal const string ReceiptDirectoryName = ".ainexus-download-receipts";
    internal const int MaximumMetadataBytes = 4 * 1024 * 1024;

    public static RuntimeDownloadPlan Create(DiscoveryReport report, RuntimeFilePlan files, string archiveDirectory)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(files);
        ValidateEvidence(report, files);
        var directory = ResolveDirectory(archiveDirectory);
        var path = ResolveArchivePath(directory, files.Files[0].Name);
        if (!string.Equals(Path.GetPathRoot(directory), report.TargetVolume, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Choose an archive folder on the storage volume assessed by this report.");
        var plan = new RuntimeDownloadPlan("", report, files, directory, path, DateTimeOffset.UtcNow);
        return plan with { PlanId = Fingerprint(plan) };
    }

    internal static void Validate(RuntimeDownloadPlan plan, string approvedPlanId)
    {
        if (string.IsNullOrWhiteSpace(approvedPlanId) || approvedPlanId != plan.PlanId || plan.PlanId != Fingerprint(plan))
            throw new InvalidOperationException("Approval does not match this exact runtime download plan. Review a new plan after any change.");
        var now = DateTimeOffset.UtcNow;
        if (!Fresh(plan.CreatedAt, TimeSpan.FromMinutes(30), now)) throw new InvalidOperationException("The runtime download plan expired or has invalid dates.");
        ValidateEvidence(plan.Report, plan.Files);
        if (ResolveDirectory(plan.ArchiveDirectory) != plan.ArchiveDirectory
            || ResolveArchivePath(plan.ArchiveDirectory, plan.Files.Files[0].Name) != plan.ArchivePath
            || !string.Equals(Path.GetPathRoot(plan.ArchiveDirectory), plan.Report.TargetVolume, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The approved archive destination no longer matches the reviewed local folder/volume.");
    }

    internal static string Fingerprint(RuntimeDownloadPlan plan) => Convert.ToHexString(SHA256.HashData(SerializeBounded(plan with { PlanId = "" })));

    private static void ValidateEvidence(DiscoveryReport report, RuntimeFilePlan files)
    {
        var now = DateTimeOffset.UtcNow;
        if (report.Catalog.IsCached || !Fresh(report.Catalog.RetrievedAt, TimeSpan.FromHours(24), now)
            || !Fresh(report.Machine.ScannedAt, TimeSpan.FromMinutes(5), now))
            throw new InvalidOperationException("Refresh online runtime metadata and the hardware scan before reviewing a download; cached, stale and future-dated evidence cannot authorize acquisition.");
        if (files.Files.Count != 1 || files.PlanId != RuntimeFilePlanner.Fingerprint(files))
            throw new InvalidDataException("Download requires one unchanged exact runtime archive review.");
        var file = files.Files[0];
        RuntimeFilePlanner.ValidateIdentity(file, files.Release);
        if (file.Sha256 is null || file.SizeBytes <= 0 || files.CompressedBytes != file.SizeBytes)
            throw new InvalidDataException("An exact positive byte count and recorded SHA-256 are required.");
        var native = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
        var assessment = RuntimeSetupPlanner.Assess(file, files.Release, report.Machine);
        if (!OperatingSystem.IsWindows() || native is not ("x64" or "arm64")
            || !report.Machine.OsArchitecture.Equals(native, StringComparison.OrdinalIgnoreCase)
            || assessment.Role != "Runtime archive" || assessment.Backend != "cpu"
            || assessment.Status != RuntimeArchiveStatus.PlatformMatch || !assessment.Architecture.Equals(native, StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Runtime downloading currently supports one native Windows CPU x64/arm64 ZIP, not GPU, support or alternate-architecture archives.");
        var item = report.Catalog.Items.SingleOrDefault(item => item.Id == files.RuntimeItemId)
            ?? throw new InvalidDataException("The selected runtime is not in the reviewed source report.");
        if (item.Kind != "Runtime" || item.RuntimeRelease is null || item.RuntimeRelease.Tag != files.Release
            || !item.RuntimeRelease.Archives.Contains(file) || item.License != files.DeclaredLicense
            || string.IsNullOrWhiteSpace(item.License) || new[] { "unknown", "unavailable", "not specified" }.Contains(item.License.Trim(), StringComparer.OrdinalIgnoreCase)
            || item.Sources.Count == 0 || item.Sources.Any(source => !Fresh(source.RetrievedAt, TimeSpan.FromHours(24), now)))
            throw new InvalidDataException("Complete matching release, archive, fresh source and declared-license evidence is required.");
        // Recreate the exact-file review from this report. Only the creation instant is retained;
        // machine, source, file selection, release flags and destination must all match it exactly.
        var expected = RuntimeFilePlanner.Create(report, files.RuntimeItemId, files.Destination, file.Name) with { CreatedAt = files.CreatedAt };
        if (RuntimeFilePlanner.Fingerprint(expected) != files.PlanId)
            throw new InvalidDataException("The exact file review does not belong to this report's hardware/source evidence.");
    }

    private static bool Fresh(DateTimeOffset time, TimeSpan maximumAge, DateTimeOffset now) => time <= now.AddMinutes(1) && now - time <= maximumAge;

    internal static string ResolveDirectory(string directory)
    {
        var full = RuntimeSetupPlanner.ValidateNewDirectory(directory);
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException("Choose an existing local download folder; this plan does not create its parent folders.");
        foreach (var protectedRoot in new[] { Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.SystemDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData) }.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            var root = Path.TrimEndingDirectorySeparator(protectedRoot);
            if (full.Equals(root, StringComparison.OrdinalIgnoreCase) || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Choose a local archive folder outside Windows and system-wide application folders.");
        }
        return full;
    }

    internal static string ResolveArchivePath(string directory, string name)
    {
        var full = ResolveDirectory(directory);
        if (string.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidDataException("The selected archive filename is unsafe.");
        var path = Path.GetFullPath(Path.Combine(full, name));
        if (!path.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Archive destination escaped its reviewed folder.");
        OfflineModelVerifier.CheckNoReparsePath(path);
        if (Directory.Exists(path)) throw new IOException("An existing directory occupies the archive filename. It was preserved.");
        return path;
    }

    internal static byte[] SerializeBounded(object value)
    {
        using var stream = new BoundedMetadataStream();
        JsonSerializer.Serialize(stream, value);
        return stream.ToArray();
    }
    private sealed class BoundedMetadataStream : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) { Check(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); base.Write(buffer); }
        public override void WriteByte(byte value) { Check(1); base.WriteByte(value); }
        private void Check(int count) { if (Length + count > MaximumMetadataBytes) throw new InvalidDataException("Runtime download review/receipt exceeds the bounded metadata budget."); }
    }

    public static string Describe(RuntimeDownloadPlan plan)
    {
        var file = plan.Files.Files[0];
        return $"RUNTIME ZIP DOWNLOAD PLAN - APPROVAL REQUIRED\nPlan: {plan.PlanId}\nRelease: {plan.Files.Release}\nSource: {file.DownloadUrl}\nArchive: {file.Name}\nExpected bytes: {file.SizeBytes:N0}\nRecorded SHA-256: {file.Sha256}\nLocal archive folder: {plan.ArchiveDirectory}\nDestination: {plan.ArchivePath}\nDeclared runtime license: {plan.Files.DeclaredLicense}\n" +
            "Approve this exact download and declared license separately from bundled dependency terms. Bundled terms must be inspected and reviewed before any later extraction.\n" +
            "Only the selected ZIP and a local receipt are written. Existing matching bytes may be rehashed for reuse; mismatches are preserved. No extraction, installation, runtime/model launch or Windows configuration changes occur. Hash matching does not prove publisher authenticity or executable compatibility.\n" +
            "At execution, source/hardware freshness, local paths and current disk space are checked again. A receipt failure preserves verified ZIP bytes and reports incomplete recording rather than success.";
    }
}

/// <summary>Approved public CPU archive acquisition only. Never extracts, executes or changes system configuration.</summary>
public sealed class RuntimeDownloadService(HttpClient httpClient)
{
    private readonly HttpClient _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly SemaphoreSlim _operation = new(1, 1);
    public bool IsBusy => _operation.CurrentCount == 0;

    public static HttpClient CreateHttpClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseCookies = false, Credentials = null, DefaultProxyCredentials = null,
        AutomaticDecompression = DecompressionMethods.None, ConnectTimeout = TimeSpan.FromSeconds(10), MaxResponseHeadersLength = 64
    }) { Timeout = Timeout.InfiniteTimeSpan };

    public async Task<RuntimeDownloadResult> AcquireAsync(RuntimeDownloadPlan plan, RuntimeDownloadApproval approval,
        IProgress<AcquisitionProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(approval);
        cancellationToken.ThrowIfCancellationRequested();
        if (!approval.AcceptDeclaredLicense || !approval.ApproveDestinationWrites || !approval.AcknowledgeSeparateBundledTermsReview)
            throw new InvalidOperationException("Accept the declared runtime license, approve destination writes and acknowledge separate future bundled-terms review for this exact download plan.");
        RuntimeDownloadPlanner.Validate(plan, approval.PlanId);
        if (_http.DefaultRequestHeaders.Any()) throw new InvalidOperationException("Use a dedicated header-free unauthenticated client for public runtime ZIP acquisition.");
        if (!await _operation.WaitAsync(0, cancellationToken).ConfigureAwait(false)) throw new InvalidOperationException("A runtime download is already active. Wait for it to finish.");
        string? partial = null;
        var partialCreated = false;
        var published = false;
        try
        {
            using var overall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            overall.CancelAfter(TimeSpan.FromMinutes(45));
            var token = overall.Token;
            var file = plan.Files.Files[0];
            var destination = RuntimeDownloadPlanner.ResolveArchivePath(plan.ArchiveDirectory, file.Name);
            if (File.Exists(destination))
            {
                Report(progress, new(0, file.SizeBytes, "Rehashing existing runtime ZIP"));
                await using var existing = OpenRead(destination);
                await RequireExactAsync(existing, file, token).ConfigureAwait(false);
                var completed = DateTimeOffset.UtcNow;
                var receipt = await SaveReceiptAsync(plan, true, completed, token).ConfigureAwait(false);
                Report(progress, new(file.SizeBytes, file.SizeBytes, "Existing runtime ZIP and receipt verified"));
                return new(plan.ArchiveDirectory, destination, receipt, true, completed);
            }
            if (new DriveInfo(Path.GetPathRoot(destination)!).AvailableFreeSpace < checked(file.SizeBytes + RuntimeDownloadPlanner.MaximumMetadataBytes))
                throw new IOException("Current available disk space cannot accommodate the approved ZIP and receipt.");
            using var response = await OpenSourceAsync(file.DownloadUrl, file.SizeBytes, token).ConfigureAwait(false);
            RuntimeDownloadPlanner.ResolveArchivePath(plan.ArchiveDirectory, file.Name);
            partial = Path.Combine(plan.ArchiveDirectory, ".ainexus-runtime-" + Guid.NewGuid().ToString("N") + ".partial");
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous))
            {
                partialCreated = true;
                using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                using var openDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                openDeadline.CancelAfter(TimeSpan.FromSeconds(30));
                using var source = await OpenBodyAsync(response.Content, openDeadline.Token).ConfigureAwait(false);
                var buffer = new byte[131072];
                long count = 0;
                var last = Stopwatch.GetTimestamp();
                Report(progress, new(0, file.SizeBytes, "Downloading approved runtime ZIP"));
                while (true)
                {
                    using var stall = CancellationTokenSource.CreateLinkedTokenSource(token);
                    stall.CancelAfter(TimeSpan.FromSeconds(30));
                    int read;
                    try { read = await source.ReadAsync(buffer, stall.Token).AsTask().WaitAsync(stall.Token).ConfigureAwait(false); }
                    catch (IOException) { throw new IOException("The approved public runtime ZIP stream could not be read."); }
                    if (read == 0) break;
                    if (count > file.SizeBytes - read) throw new InvalidDataException("Runtime ZIP response exceeded its approved byte count. No final archive was published.");
                    await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    digest.AppendData(buffer.AsSpan(0, read));
                    count += read;
                    if (count == read || count == file.SizeBytes || Stopwatch.GetElapsedTime(last) >= TimeSpan.FromMilliseconds(100))
                    { Report(progress, new(count, file.SizeBytes, "Downloading approved runtime ZIP")); last = Stopwatch.GetTimestamp(); }
                }
                if (count != file.SizeBytes || !Convert.ToHexString(digest.GetHashAndReset()).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Runtime ZIP bytes did not match the approved size/SHA-256. No final archive was published.");
                await output.FlushAsync(token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            RuntimeDownloadPlanner.ResolveArchivePath(plan.ArchiveDirectory, file.Name);
            File.Move(partial, destination, overwrite: false);
            partialCreated = false;
            published = true;
            // Hold and rehash the final file while recording evidence; publication alone is not success.
            await using var verified = OpenRead(destination);
            await RequireExactAsync(verified, file, token).ConfigureAwait(false);
            var finished = DateTimeOffset.UtcNow;
            var saved = await SaveReceiptAsync(plan, false, finished, token).ConfigureAwait(false);
            Report(progress, new(file.SizeBytes, file.SizeBytes, "Runtime ZIP and receipt verified"));
            return new(plan.ArchiveDirectory, destination, saved, false, finished);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(published
                ? "Runtime download cancelled after ZIP publication; the archive was preserved but receipt completion was not established. Rehash/reuse it in a new approved attempt."
                : "Runtime download cancelled before publication. Cleanup was attempted only for this operation's owned partial; no unchecked final ZIP was published.", cancellationToken);
        }
        catch (OperationCanceledException)
        { throw new TimeoutException(published ? "Runtime acquisition timed out after ZIP publication. The archive was preserved; receipt completion was not established." : "Runtime acquisition exceeded its header/stall/overall deadline. No unchecked final ZIP was published."); }
        catch (HttpRequestException ex)
        { throw new HttpRequestException("The approved public runtime source could not be read. No unchecked final ZIP was published.", null, ex.StatusCode); }
        finally
        {
            if (partialCreated && partial is not null) DeleteOwnPartial(partial, plan.ArchiveDirectory);
            _operation.Release();
        }
    }

    private async Task<HttpResponseMessage> OpenSourceAsync(Uri initial, long expected, CancellationToken token)
    {
        var current = initial;
        for (var count = 0; count <= 5; count++)
        {
            if (!IsPermittedSource(current, initial)) throw new InvalidDataException("The runtime source redirected outside the exact approved origin and permitted public release-asset hosts.");
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.UserAgent.ParseAdd("AINexus-RuntimeAcquisition/1.0");
            request.Headers.AcceptEncoding.ParseAdd("identity");
            using var headers = CancellationTokenSource.CreateLinkedTokenSource(token);
            headers.CancelAfter(TimeSpan.FromSeconds(30));
            var pending = _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headers.Token);
            HttpResponseMessage response;
            try { response = await pending.WaitAsync(headers.Token).ConfigureAwait(false); }
            catch
            {
                _ = pending.ContinueWith(task => { if (task.Status == TaskStatus.RanToCompletion) task.Result.Dispose(); else _ = task.Exception; }, TaskScheduler.Default);
                throw;
            }
            try
            {
                if (response.RequestMessage?.RequestUri is { } actual && actual != current)
                    throw new InvalidDataException("Automatic HTTP redirects must be disabled for runtime acquisition.");
                if (IsRedirect(response.StatusCode))
                {
                    if (count == 5 || response.Headers.Location is not { } location) throw new InvalidDataException("Runtime source redirect limit exceeded or redirect location is missing.");
                    current = location.IsAbsoluteUri ? location : new Uri(current, location);
                    continue;
                }
                if (response.StatusCode != HttpStatusCode.OK) throw new HttpRequestException("Runtime source returned HTTP " + (int)response.StatusCode + ".", null, response.StatusCode);
                if (response.Content.Headers.ContentEncoding.Any(value => !value.Equals("identity", StringComparison.OrdinalIgnoreCase))
                    || response.Content.Headers.ContentLength is { } bytes && bytes != expected)
                    throw new InvalidDataException("Runtime source encoding/Content-Length does not match the exact-byte acquisition plan.");
                return response;
            }
            catch { response.Dispose(); throw; }
            finally { if (IsRedirect(response.StatusCode)) response.Dispose(); }
        }
        throw new InvalidDataException("Runtime source exceeded its redirect limit.");
    }

    internal static bool IsPermittedSource(Uri uri, Uri initial) => uri is { IsAbsoluteUri: true }
        && uri.Scheme == "https" && uri.Port == 443 && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0
        && (uri == initial && uri.IdnHost.Equals("github.com", StringComparison.OrdinalIgnoreCase) && uri.Query.Length == 0
            || uri.IdnHost.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
            || uri.IdnHost.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase));
    private static bool IsRedirect(HttpStatusCode code) => code is HttpStatusCode.MovedPermanently or HttpStatusCode.Found
        or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static async Task<Stream> OpenBodyAsync(HttpContent content, CancellationToken token)
    {
        var pending = content.ReadAsStreamAsync(token);
        try { return await pending.WaitAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            _ = pending.ContinueWith(task => { if (task.Status == TaskStatus.RanToCompletion) task.Result.Dispose(); else _ = task.Exception; }, TaskScheduler.Default);
            throw;
        }
        catch (IOException) { throw new IOException("The approved public runtime ZIP stream could not be opened."); }
    }
    private static FileStream OpenRead(string path)
    {
        OfflineModelVerifier.CheckNoReparsePath(path);
        return new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
    }
    private static async Task RequireExactAsync(FileStream stream, RuntimeReleaseAsset file, CancellationToken token)
    {
        if (stream.Length != file.SizeBytes || !Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The existing/published runtime ZIP does not match the approved byte count/SHA-256. It was preserved; choose another location or review its origin.");
    }
    private static async Task<string> SaveReceiptAsync(RuntimeDownloadPlan plan, bool reused, DateTimeOffset completed, CancellationToken token)
    {
        try
        {
            RuntimeDownloadPlanner.ResolveArchivePath(plan.ArchiveDirectory, plan.Files.Files[0].Name);
            var directory = Path.Combine(plan.ArchiveDirectory, RuntimeDownloadPlanner.ReceiptDirectoryName);
            OfflineModelVerifier.CheckNoReparsePath(Path.Combine(directory, "receipt.json"));
            Directory.CreateDirectory(directory);
            OfflineModelVerifier.CheckNoReparsePath(Path.Combine(directory, "receipt.json"));
            var bytes = RuntimeDownloadPlanner.SerializeBounded(new
            {
                SchemaVersion = 1, Plan = plan, CompletedAt = completed, ReusedExisting = reused,
                ArchiveBytesAndHashMatched = true, ExtractionPerformed = false, RuntimeExecutionVerified = false,
                PublisherAuthenticityVerified = false, ApprovalConsumed = true,
                Limits = "ZIP size/SHA-256 matched recorded source metadata. Bundled licenses, archive paths/contents, dependencies and runtime functionality require separate inspection and approval."
            });
            var path = Path.Combine(directory, plan.PlanId + "-" + Guid.NewGuid().ToString("N") + ".json");
            OfflineModelVerifier.CheckNoReparsePath(path);
            await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous);
            await stream.WriteAsync(bytes, token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);
            return path;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        { throw new IOException("The verified runtime ZIP was preserved, but its acquisition receipt could not be completed. No recorded-success result was returned; rehash/reuse it in a new approved attempt."); }
    }
    private static void DeleteOwnPartial(string partial, string reviewedDirectory)
    {
        try
        {
            var directory = RuntimeDownloadPlanner.ResolveDirectory(reviewedDirectory);
            var full = Path.GetFullPath(partial);
            var name = Path.GetFileName(full);
            if (!string.Equals(Path.GetDirectoryName(full), directory, StringComparison.OrdinalIgnoreCase)
                || !name.StartsWith(".ainexus-runtime-", StringComparison.Ordinal) || !name.EndsWith(".partial", StringComparison.Ordinal)
                || !Guid.TryParseExact(name[17..^8], "N", out _)) return;
            OfflineModelVerifier.CheckNoReparsePath(full);
            if (File.Exists(full)) File.Delete(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        { /* Never follow a changed parent/reparse point or recursively remove anything. */ }
    }
    private static void Report(IProgress<AcquisitionProgress>? progress, AcquisitionProgress update)
    { try { progress?.Report(update); } catch (Exception ex) when (ex is not OutOfMemoryException) { } }
}
