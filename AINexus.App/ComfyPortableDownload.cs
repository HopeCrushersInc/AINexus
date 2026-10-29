using System.IO;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace AINexus.App;

public sealed record ComfyPortableDownloadPlan(string PlanId, ComfyPortableRelease Release, ComfyPortableAsset Asset,
    string ArchiveDirectory, string ArchivePath, DateTimeOffset CreatedAt);
public sealed record ComfyPortableDownloadApproval(string PlanId, bool AcceptSourceTerms, bool ApproveDestinationWrites,
    bool AcknowledgeArchiveNotInstalled);
public sealed record ComfyPortableDownloadResult(string ArchivePath, string ReceiptPath, bool ReusedExisting, DateTimeOffset CompletedAt);

/// <summary>Review for archive acquisition only. No extraction, execution, compatibility claim or installed-engine registration.</summary>
public static class ComfyPortableDownloadPlanner
{
    public static ComfyPortableDownloadPlan Create(ComfyPortableRelease release, ComfyPortableAsset asset, string existingFolder)
    {
        ArgumentNullException.ThrowIfNull(release); ArgumentNullException.ThrowIfNull(asset);
        var directory = RuntimeDownloadPlanner.ResolveDirectory(existingFolder);
        var path = RuntimeDownloadPlanner.ResolveArchivePath(directory, asset.Name);
        var plan = new ComfyPortableDownloadPlan("", release, asset, directory, path, DateTimeOffset.UtcNow);
        ValidateEvidence(plan);
        CheckSpace(plan);
        return plan with { PlanId = Fingerprint(plan) };
    }

    internal static string Fingerprint(ComfyPortableDownloadPlan plan) => Convert.ToHexString(SHA256.HashData(Serialize(plan with { PlanId = "" })));
    internal static byte[] Serialize(object value)
    {
        var bytes = RuntimeDownloadPlanner.SerializeBounded(value);
        if (bytes.Length > ComfyPortableReleaseClient.MaximumMetadataBytes) throw new InvalidDataException("The portable archive review/receipt exceeds its metadata bound.");
        return bytes;
    }

    internal static void Validate(ComfyPortableDownloadPlan plan, string approvedPlanId)
    {
        if (approvedPlanId != plan.PlanId || string.IsNullOrWhiteSpace(approvedPlanId) || Fingerprint(plan) != plan.PlanId)
            throw new InvalidOperationException("Review and approve this exact portable archive plan again after any change.");
        ValidateEvidence(plan);
    }

    private static void ValidateEvidence(ComfyPortableDownloadPlan plan)
    {
        ComfyPortableReleaseClient.ValidateRelease(plan.Release);
        ComfyPortableReleaseClient.ValidateAsset(plan.Release.Tag, plan.Asset);
        var now = DateTimeOffset.UtcNow;
        if (plan.CreatedAt > now.AddMinutes(1) || now - plan.CreatedAt > TimeSpan.FromMinutes(30)
            || plan.Release.RetrievedAt > now.AddMinutes(1) || now - plan.Release.RetrievedAt > TimeSpan.FromHours(24))
            throw new InvalidOperationException("Refresh the official release and exact archive review; the evidence is stale or future-dated.");
        if (!plan.Release.Assets.Contains(plan.Asset)) throw new InvalidDataException("The selected archive does not belong to the reviewed release.");
        var directory = RuntimeDownloadPlanner.ResolveDirectory(plan.ArchiveDirectory);
        if (directory != plan.ArchiveDirectory || RuntimeDownloadPlanner.ResolveArchivePath(directory, plan.Asset.Name) != plan.ArchivePath)
            throw new InvalidDataException("The archive destination changed after review.");
    }

    internal static void CheckSpace(ComfyPortableDownloadPlan plan)
    {
        // Existing matching bytes are verified before reuse. Extraction needs an independent expanded-size review.
        var needed = File.Exists(plan.ArchivePath) ? ComfyPortableReleaseClient.MaximumMetadataBytes : checked(plan.Asset.SizeBytes + ComfyPortableReleaseClient.MaximumMetadataBytes);
        if (new DriveInfo(Path.GetPathRoot(plan.ArchiveDirectory)!).AvailableFreeSpace < needed)
            throw new IOException("The chosen drive lacks space for the archive and its receipt. This is storage, not a GPU compatibility assessment.");
    }

    public static string Describe(ComfyPortableDownloadPlan plan) =>
        $"COMFYUI PORTABLE ARCHIVE — DOWNLOAD REVIEW\nPlan: {plan.PlanId}\nRelease: {plan.Release.Tag}\nSource release: {plan.Release.ReleaseUrl}\nArchive: {plan.Asset.Name}\nDownload: {plan.Asset.DownloadUrl}\nExact bytes: {plan.Asset.SizeBytes:N0}\nSHA-256: {plan.Asset.Sha256}\nDestination: {plan.ArchivePath}\nSource terms: {plan.Release.SourceTermsUrl}\n" +
        "This only acquires a hash-verified archive and a private local receipt. It does not extract, install, configure, launch or register an engine. Archive/backend names do not establish GPU/driver compatibility.\n" +
        "Review the source terms before approving. Bundled Python/PyTorch/CUDA/other dependency terms and archive contents require separate inspection and approval before any future extraction. No model weights are selected. Existing different files are preserved; matching existing archives can be rehashed for reuse.";
}

public sealed class ComfyPortableDownloadService(HttpClient httpClient)
{
    private readonly HttpClient _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly SemaphoreSlim _operation = new(1, 1);
    public bool IsBusy => _operation.CurrentCount == 0;
    public static HttpClient CreateHttpClient() => ComfyPortableReleaseClient.CreateHttpClient();

    public async Task<ComfyPortableDownloadResult> AcquireAsync(ComfyPortableDownloadPlan plan, ComfyPortableDownloadApproval approval,
        IProgress<AcquisitionProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan); ArgumentNullException.ThrowIfNull(approval);
        cancellationToken.ThrowIfCancellationRequested();
        if (!approval.AcceptSourceTerms || !approval.ApproveDestinationWrites || !approval.AcknowledgeArchiveNotInstalled)
            throw new InvalidOperationException("Accept source terms, approve this destination and acknowledge archive-only acquisition with separate future bundled-terms review.");
        ComfyPortableDownloadPlanner.Validate(plan, approval.PlanId);
        if (_http.DefaultRequestHeaders.Any()) throw new InvalidOperationException("Use a dedicated header-free unauthenticated public archive client.");
        if (!await _operation.WaitAsync(0, cancellationToken).ConfigureAwait(false)) throw new InvalidOperationException("An archive operation is already active.");
        string? partial = null;
        var ownsPartial = false;
        var published = false;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromMinutes(60));
            var token = deadline.Token;
            ComfyPortableDownloadPlanner.CheckSpace(plan);
            if (File.Exists(plan.ArchivePath))
            {
                Report(progress, new(0, plan.Asset.SizeBytes, "Verifying existing portable archive"));
                await using var existing = OpenRead(plan.ArchivePath);
                await VerifyAsync(existing, plan.Asset, token).ConfigureAwait(false);
                var completed = DateTimeOffset.UtcNow;
                var receipt = await SaveReceiptAsync(plan, true, completed, token).ConfigureAwait(false);
                Report(progress, new(plan.Asset.SizeBytes, plan.Asset.SizeBytes, "Archive verified; engine not installed"));
                return new(plan.ArchivePath, receipt, true, completed);
            }
            using var response = await OpenSourceAsync(plan.Asset.DownloadUrl, plan.Asset.SizeBytes, token).ConfigureAwait(false);
            ComfyPortableDownloadPlanner.Validate(plan, approval.PlanId);
            ComfyPortableDownloadPlanner.CheckSpace(plan);
            partial = Path.Combine(plan.ArchiveDirectory, ".ainexus-comfy-" + Guid.NewGuid().ToString("N") + ".partial");
            OfflineModelVerifier.CheckNoReparsePath(partial);
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous))
            {
                ownsPartial = true;
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                using var bodyDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                bodyDeadline.CancelAfter(TimeSpan.FromSeconds(30));
                using var source = await ComfyPortableReleaseClient.OpenBodyAsync(response.Content, bodyDeadline.Token).ConfigureAwait(false);
                var buffer = new byte[131072];
                long total = 0;
                var last = Stopwatch.GetTimestamp();
                while (true)
                {
                    using var stall = CancellationTokenSource.CreateLinkedTokenSource(token);
                    stall.CancelAfter(TimeSpan.FromSeconds(30));
                    var read = await source.ReadAsync(buffer, stall.Token).AsTask().WaitAsync(stall.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    if (total > plan.Asset.SizeBytes - read) throw new InvalidDataException("The archive response exceeded the approved byte count.");
                    await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    hash.AppendData(buffer.AsSpan(0, read)); total += read;
                    if (total == read || total == plan.Asset.SizeBytes || Stopwatch.GetElapsedTime(last) >= TimeSpan.FromMilliseconds(100))
                    { Report(progress, new(total, plan.Asset.SizeBytes, "Downloading approved portable archive")); last = Stopwatch.GetTimestamp(); }
                }
                if (total != plan.Asset.SizeBytes || !Convert.ToHexString(hash.GetHashAndReset()).Equals(plan.Asset.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The archive size/SHA-256 did not match the approved source. No final archive was published.");
                await output.FlushAsync(token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            // Do not expire an approved long-running transfer solely because its 30-minute start window elapsed.
            RuntimeDownloadPlanner.ResolveArchivePath(plan.ArchiveDirectory, plan.Asset.Name);
            File.Move(partial, plan.ArchivePath, overwrite: false);
            ownsPartial = false; published = true;
            await using var verified = OpenRead(plan.ArchivePath);
            await VerifyAsync(verified, plan.Asset, token).ConfigureAwait(false);
            var finished = DateTimeOffset.UtcNow;
            var saved = await SaveReceiptAsync(plan, false, finished, token).ConfigureAwait(false);
            Report(progress, new(plan.Asset.SizeBytes, plan.Asset.SizeBytes, "Archive verified; engine not installed"));
            return new(plan.ArchivePath, saved, false, finished);
        }
        catch (Exception error) when (published && error is IOException or OperationCanceledException or UnauthorizedAccessException)
        {
            throw new IOException("Verified archive bytes were preserved, but acquisition recording did not complete. The engine is not installed.", error);
        }
        finally
        {
            try
            {
                if (ownsPartial && partial is not null)
                {
                    OfflineModelVerifier.CheckNoReparsePath(partial);
                    File.Delete(partial);
                }
            }
            finally { _operation.Release(); }
        }
    }

    private async Task<HttpResponseMessage> OpenSourceAsync(Uri initial, long expected, CancellationToken token)
    {
        var current = initial;
        for (var redirects = 0; redirects <= 5; redirects++)
        {
            if (!RuntimeDownloadService.IsPermittedSource(current, initial)) throw new InvalidDataException("The archive redirected outside the approved public release hosts.");
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.UserAgent.ParseAdd("AINexus-ComfyPortable/1.0");
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
                if (response.RequestMessage?.RequestUri is { } actual && actual != current) throw new InvalidDataException("Disable automatic redirects for archive acquisition.");
                if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                {
                    if (redirects == 5 || response.Headers.Location is not { } location) throw new InvalidDataException("The archive redirect limit or location is invalid.");
                    current = location.IsAbsoluteUri ? location : new Uri(current, location);
                    response.Dispose();
                    continue;
                }
                if (response.StatusCode != HttpStatusCode.OK) throw new HttpRequestException("The archive source returned HTTP " + (int)response.StatusCode + ".", null, response.StatusCode);
                if (response.Content.Headers.ContentLength is { } bytes && bytes != expected || response.Content.Headers.ContentEncoding.Any(value => value != "identity"))
                    throw new InvalidDataException("The archive response length/encoding differs from the approved bytes.");
                return response;
            }
            catch { response.Dispose(); throw; }
        }
        throw new InvalidDataException("The archive source exceeded the redirect limit.");
    }

    private static FileStream OpenRead(string path)
    {
        OfflineModelVerifier.CheckNoReparsePath(path);
        return new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
    }
    private static async Task VerifyAsync(FileStream file, ComfyPortableAsset asset, CancellationToken token)
    {
        if (file.Length != asset.SizeBytes || !Convert.ToHexString(await SHA256.HashDataAsync(file, token).ConfigureAwait(false)).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Existing/published archive bytes differ from the approved size/SHA-256. The file was preserved.");
    }
    private static async Task<string> SaveReceiptAsync(ComfyPortableDownloadPlan plan, bool reused, DateTimeOffset completed, CancellationToken token)
    {
        RuntimeDownloadPlanner.ResolveArchivePath(plan.ArchiveDirectory, plan.Asset.Name);
        var directory = Path.Combine(plan.ArchiveDirectory, ".ainexus-comfy-download-receipts");
        OfflineModelVerifier.CheckNoReparsePath(Path.Combine(directory, "receipt.json"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, plan.PlanId + "-" + Guid.NewGuid().ToString("N") + ".json");
        OfflineModelVerifier.CheckNoReparsePath(path);
        var bytes = ComfyPortableDownloadPlanner.Serialize(new
        {
            SchemaVersion = 1, Plan = plan, CompletedAt = completed, ReusedExisting = reused,
            ArchiveBytesAndHashMatched = true, EngineInstalled = false, ExtractionPerformed = false,
            RuntimeExecutionVerified = false, PublisherAuthenticityVerified = false,
            Limits = "Archive acquisition only. Separate safe archive inspection, bundled-terms review, extraction approval and engine verification are required."
        });
        var ownsReceipt = false;
        try
        {
            await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16384, FileOptions.Asynchronous))
            {
                ownsReceipt = true;
                await stream.WriteAsync(bytes, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
            }
            ownsReceipt = false;
            return path;
        }
        finally
        {
            if (ownsReceipt) { OfflineModelVerifier.CheckNoReparsePath(path); File.Delete(path); }
        }
    }
    private static void Report(IProgress<AcquisitionProgress>? progress, AcquisitionProgress update)
    {
        try { progress?.Report(update); } catch (Exception error) when (error is not OutOfMemoryException) { }
    }
}
