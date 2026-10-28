using System.IO;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;

namespace AINexus.App;

public sealed record AcquisitionApproval(string PlanId, bool AcceptDeclaredLicense, bool ApproveDestinationWrites);
public sealed record AcquisitionProgress(long CompletedBytes, long ExpectedBytes, string Stage);
public sealed record ModelAcquisitionResult(ModelIntegrityEvidence Evidence, bool ReusedExisting, DateTimeOffset CompletedAt);

/// <summary>
/// Acquires approved model data only. This service never installs a runtime, executes code,
/// changes Windows configuration, overwrites an existing target, or trusts an unchecked file.
/// Use CreateHttpClient for the supported unauthenticated, nonredirecting transport.
/// </summary>
public sealed class ModelAcquisitionService(HttpClient httpClient)
{
    private readonly HttpClient _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly SemaphoreSlim _operation = new(1, 1);
    public bool IsBusy => _operation.CurrentCount == 0;

    public static HttpClient CreateHttpClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        Credentials = null,
        DefaultProxyCredentials = null,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        MaxResponseHeadersLength = 64
    }) { Timeout = Timeout.InfiniteTimeSpan };

    public async Task<ModelAcquisitionResult> AcquireAsync(ModelAcquisitionPlan plan, AcquisitionApproval approval,
        IProgress<AcquisitionProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(approval);
        cancellationToken.ThrowIfCancellationRequested();
        if (!approval.AcceptDeclaredLicense || !approval.ApproveDestinationWrites)
            throw new InvalidOperationException("Accept the declared license and approve this exact destination before acquiring model data.");
        ModelAcquisitionPlanner.ValidateForAcquisition(plan, approval.PlanId);
        if (_http.DefaultRequestHeaders.Any())
            throw new InvalidOperationException("Use a dedicated header-free, unauthenticated client for public model acquisition.");
        if (!await _operation.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("A model acquisition is already running. Wait for it to finish.");

        string? partial = null;
        var partialCreated = false;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromMinutes(45));
            var token = deadline.Token;
            var destination = plan.DestinationPath;
            RecheckDestination(destination);
            if (Directory.Exists(destination)) throw new IOException("The selected model destination is an existing directory. It was preserved.");
            if (File.Exists(destination))
            {
                Report(progress, new(0, plan.Artifact.SizeBytes!.Value, "Verifying existing file"));
                var existing = await ModelArtifactVerifier.VerifyExistingAsync(plan, token).ConfigureAwait(false);
                if (!existing.Matches)
                    throw new InvalidDataException("The existing destination does not match the approved artifact. It was preserved; choose another location.");
                Report(progress, new(existing.ActualBytes!.Value, plan.Artifact.SizeBytes.Value, "Existing artifact verified"));
                return new(existing, true, DateTimeOffset.UtcNow);
            }

            var expected = plan.Artifact.SizeBytes!.Value;
            var volume = new DriveInfo(Path.GetPathRoot(destination)!);
            if (!volume.IsReady || volume.AvailableFreeSpace < expected)
                throw new IOException("The selected volume does not currently have enough available space for the approved artifact.");

            // Open the remote source before creating a destination directory or partial file.
            using var response = await OpenSourceAsync(plan.DownloadUrl, expected, token).ConfigureAwait(false);
            var parent = Path.GetDirectoryName(destination)!;
            RecheckDestination(destination);
            Directory.CreateDirectory(parent);
            RecheckDestination(destination);
            partial = Path.Combine(parent, ".ainexus-" + Guid.NewGuid().ToString("N") + ".partial");
            long completed = 0;
            string hash;
            await using (var file = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                131072, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                partialCreated = true;
                using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using var source = await OpenBodyAsync(response.Content, token).ConfigureAwait(false);
                var buffer = new byte[131072];
                var lastProgress = Stopwatch.GetTimestamp();
                Report(progress, new(0, expected, "Downloading approved model data"));
                while (true)
                {
                    using var stall = CancellationTokenSource.CreateLinkedTokenSource(token);
                    stall.CancelAfter(TimeSpan.FromSeconds(30));
                    var read = await ReadSourceAsync(source, buffer, stall.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    if (completed > expected - read)
                        throw new InvalidDataException("The source sent more bytes than the approved artifact size. No final file was published.");
                    await file.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    digest.AppendData(buffer.AsSpan(0, read));
                    completed += read;
                    if (completed == read || completed == expected || Stopwatch.GetElapsedTime(lastProgress) >= TimeSpan.FromMilliseconds(100))
                    {
                        Report(progress, new(completed, expected, "Downloading approved model data"));
                        lastProgress = Stopwatch.GetTimestamp();
                    }
                }
                if (completed != expected)
                    throw new InvalidDataException("The source ended before the approved artifact size was reached. No final file was published.");
                hash = Convert.ToHexString(digest.GetHashAndReset());
                if (!string.Equals(hash, plan.Artifact.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The downloaded bytes do not match the approved SHA-256. No final file was published.");
                await file.FlushAsync(token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            RecheckDestination(destination);
            // No overwrite: a file created by another actor while downloading is preserved.
            File.Move(partial, destination, overwrite: false);
            partialCreated = false;
            var evidence = new ModelIntegrityEvidence(destination, true, true, completed, hash, DateTimeOffset.UtcNow,
                "Model data matches the approved byte count and SHA-256. No runtime was installed and inference has not been verified by this acquisition.");
            Report(progress, new(completed, expected, "Artifact integrity verified"));
            return new(evidence, false, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Model acquisition timed out before verified completion. No unchecked final artifact was published.");
        }
        catch (HttpRequestException ex)
        {
            // An underlying network exception can contain a signed redirect URI. Do not
            // forward its message, inner exception, response headers, or source query.
            throw new HttpRequestException("The approved public model source could not be read. No unchecked final artifact was published.", null, ex.StatusCode);
        }
        finally
        {
            if (partialCreated && partial is not null) DeleteOwnPartial(partial, plan.DestinationPath);
            _operation.Release();
        }
    }

    internal async Task<HttpResponseMessage> OpenSourceAsync(Uri source, long expectedBytes, CancellationToken token)
    {
        var current = source;
        for (var redirects = 0; redirects <= 5; redirects++)
        {
            if (!IsPermittedSource(current)) throw new InvalidDataException("The model source redirected outside the permitted HTTPS download origins.");
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.UserAgent.ParseAdd("AINexus-Acquisition/1.0");
            request.Headers.AcceptEncoding.ParseAdd("identity");
            using var headersDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            headersDeadline.CancelAfter(TimeSpan.FromSeconds(30));
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headersDeadline.Token).ConfigureAwait(false);
            try
            {
                if (response.RequestMessage?.RequestUri is { } final && final != current)
                    throw new InvalidDataException("Automatic redirects must be disabled on the acquisition transport.");
                if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
                    or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                {
                    if (redirects == 5 || response.Headers.Location is not { } location)
                        throw new InvalidDataException("The model source exceeded its redirect limit or returned an invalid redirect.");
                    current = location.IsAbsoluteUri ? location : new Uri(current, location);
                    continue;
                }
                if (response.StatusCode != HttpStatusCode.OK)
                    throw new HttpRequestException("The model source returned HTTP " + (int)response.StatusCode + ".", null, response.StatusCode);
                if (response.Content.Headers.ContentEncoding.Any(encoding => !encoding.Equals("identity", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("Compressed source responses are not accepted for exact-byte artifact acquisition.");
                if (response.Content.Headers.ContentLength is { } length && length != expectedBytes)
                    throw new InvalidDataException("The source Content-Length does not match the approved artifact size.");
                return response;
            }
            catch { response.Dispose(); throw; }
            finally
            {
                if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
                    or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect) response.Dispose();
            }
        }
        throw new InvalidDataException("The model source exceeded its redirect limit.");
    }

    internal static bool IsPermittedSource(Uri uri) => uri is { IsAbsoluteUri: true }
        && uri.Scheme == "https" && uri.Port == 443 && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0
        && (uri.IdnHost.Equals("huggingface.co", StringComparison.OrdinalIgnoreCase)
            || uri.IdnHost.EndsWith(".huggingface.co", StringComparison.OrdinalIgnoreCase)
            || uri.IdnHost.Equals("hf.co", StringComparison.OrdinalIgnoreCase)
            || uri.IdnHost.EndsWith(".hf.co", StringComparison.OrdinalIgnoreCase));

    internal static async Task<Stream> OpenBodyAsync(HttpContent content, CancellationToken token)
    {
        try { return await content.ReadAsStreamAsync(token).ConfigureAwait(false); }
        catch (IOException) { throw new IOException("The approved source stream could not be opened. No unchecked final artifact was published."); }
    }

    private static async Task<int> ReadSourceAsync(Stream source, byte[] buffer, CancellationToken token)
    {
        try { return await source.ReadAsync(buffer.AsMemory(), token).AsTask().WaitAsync(token).ConfigureAwait(false); }
        catch (IOException) { throw new IOException("The approved source stream could not be read. No unchecked final artifact was published."); }
    }

    private static void RecheckDestination(string path) =>
        ModelAcquisitionPlanner.ResolveDestination(Path.GetDirectoryName(path)!, Path.GetFileName(path));

    private static void DeleteOwnPartial(string partial, string destination)
    {
        try
        {
            var parent = Path.GetDirectoryName(Path.GetFullPath(destination))!;
            var own = Path.GetFullPath(partial);
            var name = Path.GetFileName(own);
            if (!string.Equals(Path.GetDirectoryName(own), parent, StringComparison.OrdinalIgnoreCase)
                || !name.StartsWith(".ainexus-", StringComparison.Ordinal) || !name.EndsWith(".partial", StringComparison.Ordinal)
                || !Guid.TryParseExact(name[9..^8], "N", out _)) return;
            RecheckDestination(destination);
            if (File.Exists(own) && !File.GetAttributes(own).HasFlag(FileAttributes.ReparsePoint)) File.Delete(own);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            // Cleanup must not override the real failure or follow a changed parent link.
            // A retained partial is never presented as an acquired/verified artifact.
        }
    }

    private static void Report(IProgress<AcquisitionProgress>? progress, AcquisitionProgress value)
    {
        try { progress?.Report(value); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { /* Presentation cannot break artifact integrity. */ }
    }
}
