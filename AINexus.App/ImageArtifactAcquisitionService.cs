using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace AINexus.App;

public sealed record ImageArtifactAcquisitionResult(string Path, string ReceiptPath, bool ReusedExisting,
    long ActualBytes, string ActualSha256, DateTimeOffset CompletedAt)
{
    public bool RuntimeExecutionVerified => false;
    public bool CompleteCheckpointVerified => false;
    public bool HardwareFitVerified => false;
}

public sealed record ImageArtifactAcquisitionReceipt(int SchemaVersion, ImageArtifactAcquisitionPlan Plan,
    bool ReusedExisting, long ActualBytes, string ActualSha256, DateTimeOffset CompletedAt)
{
    public string ArtifactKind => "ImageArtifactCandidate";
    public bool RuntimeExecutionVerified => false;
    public bool CompleteCheckpointVerified => false;
    public bool HardwareFitVerified => false;
    public bool PublisherAuthenticityVerified => false;
}

/// <summary>Acquires exactly approved image artifact DATA. It never runs an engine, changes engine
/// configuration, infers complete checkpoint contents, or overwrites conflicting files.</summary>
public sealed class ImageArtifactAcquisitionService(HttpClient http)
{
    internal const string ReceiptDirectoryName = ".ainexus-image-artifact-receipts";
    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
    private readonly SemaphoreSlim _operation = new(1, 1);
    public bool IsBusy => _operation.CurrentCount == 0;
    public static HttpClient CreateHttpClient() => ModelAcquisitionService.CreateHttpClient();

    public async Task<ImageArtifactAcquisitionResult> AcquireAsync(ImageArtifactAcquisitionPlan plan, AcquisitionApproval approval,
        IProgress<AcquisitionProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(approval);
        cancellationToken.ThrowIfCancellationRequested();
        if (!approval.AcceptDeclaredLicense || !approval.ApproveDestinationWrites)
            throw new InvalidOperationException("Accept the displayed source license and approve this exact image-data destination before acquisition.");
        ImageArtifactAcquisitionPlanner.ValidateForAcquisition(plan, approval.PlanId);
        if (_http.DefaultRequestHeaders.Any()) throw new InvalidOperationException("Use a dedicated header-free unauthenticated image acquisition client.");
        if (!await _operation.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("An image artifact acquisition is already running.");
        string? partial = null;
        var partialCreated = false;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromHours(2));
            var token = deadline.Token;
            var expected = plan.Artifact.SizeBytes!.Value;
            ImageArtifactAcquisitionPlanner.ValidateIdentity(plan);
            if (File.Exists(plan.DestinationPath))
            {
                CheckCurrentSpace(plan, ImageArtifactAcquisitionPlanner.ReceiptReserveBytes);
                Report(progress, new(0, expected, "Hashing existing image artifact data"));
                await using var existing = OpenRead(plan.DestinationPath);
                var hash = await HashMatchingAsync(existing, expected, plan.Artifact.Sha256!, token).ConfigureAwait(false);
                var receipt = await SaveReceiptAsync(plan, true, hash, token).ConfigureAwait(false);
                Report(progress, new(expected, expected, "Image artifact data and receipt verified; execution unverified"));
                return new(plan.DestinationPath, receipt, true, expected, hash, DateTimeOffset.UtcNow);
            }
            if (plan.ExistingFileAtReview)
                throw new InvalidDataException("The file reviewed for reuse is no longer present. Review again before approving a replacement download.");
            CheckCurrentSpace(plan, expected + ImageArtifactAcquisitionPlanner.ReceiptReserveBytes);
            // The shared transport enforces public HTTPS origins, bounded manual redirects,
            // exact content length/encoding and signed-URL redaction. No file exists yet.
            var transport = new ModelAcquisitionService(_http);
            using var response = await transport.OpenSourceAsync(plan.DownloadUrl, expected, token).ConfigureAwait(false);
            ImageArtifactAcquisitionPlanner.ValidateForAcquisition(plan, approval.PlanId);
            CheckCurrentSpace(plan, expected + ImageArtifactAcquisitionPlanner.ReceiptReserveBytes);
            partial = Path.Combine(plan.DestinationDirectory, ".ainexus-image-" + Guid.NewGuid().ToString("N") + ".partial");
            OfflineModelVerifier.CheckNoReparsePath(partial);
            string actualHash;
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                131072, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                partialCreated = true;
                await using var source = await ModelAcquisitionService.OpenBodyAsync(response.Content, token).ConfigureAwait(false);
                using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[131072];
                long count = 0;
                Report(progress, new(0, expected, "Downloading approved image artifact data"));
                while (true)
                {
                    using var stall = CancellationTokenSource.CreateLinkedTokenSource(token);
                    stall.CancelAfter(TimeSpan.FromSeconds(45));
                    int read;
                    try { read = await ReadSourceAsync(source, buffer, stall.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    { throw new TimeoutException("The approved image artifact source stalled for 45 seconds."); }
                    if (read == 0) break;
                    if (count > expected - read) throw new InvalidDataException("Image data exceeded the approved byte count. No final artifact was published.");
                    await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    digest.AppendData(buffer, 0, read);
                    count += read;
                    Report(progress, new(count, expected, "Downloading approved image artifact data"));
                }
                if (count != expected) throw new InvalidDataException("Image data ended before the approved byte count. No final artifact was published.");
                actualHash = Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant();
                if (!actualHash.Equals(plan.Artifact.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Image artifact SHA-256 differed from the reviewed source. No final artifact was published.");
                await output.FlushAsync(token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            ImageArtifactAcquisitionPlanner.ValidateIdentity(plan);
            OfflineModelVerifier.CheckNoReparsePath(partial);
            File.Move(partial, plan.DestinationPath, overwrite: false);
            partialCreated = false;
            await using var published = OpenRead(plan.DestinationPath);
            // Bind the receipt to the actual published bytes while holding a read lease.
            actualHash = await HashMatchingAsync(published, expected, plan.Artifact.Sha256!, token).ConfigureAwait(false);
            var receiptPath = await SaveReceiptAsync(plan, false, actualHash, token).ConfigureAwait(false);
            Report(progress, new(expected, expected, "Image artifact data and receipt verified; execution unverified"));
            return new(plan.DestinationPath, receiptPath, false, expected, actualHash, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("Image data acquisition exceeded its two-hour limit. Any verified published data was preserved; execution is unverified."); }
        catch (HttpRequestException ex)
        { throw new HttpRequestException("The approved public image artifact source could not be read. No unchecked completion was reported.", null, ex.StatusCode); }
        finally
        {
            if (partialCreated && partial is not null) TryDeleteOwnedPartial(partial, plan.DestinationDirectory, ".ainexus-image-");
            _operation.Release();
        }
    }

    private static void CheckCurrentSpace(ImageArtifactAcquisitionPlan plan, long required)
    {
        ImageArtifactAcquisitionPlanner.ValidateIdentity(plan);
        var drive = new DriveInfo(plan.TargetVolume);
        if (!drive.IsReady || drive.AvailableFreeSpace < required)
            throw new IOException("The selected volume no longer has the required image-data/receipt space. No other drive was substituted.");
    }
    private static FileStream OpenRead(string path)
    {
        OfflineModelVerifier.CheckNoReparsePath(path);
        return new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
    }
    private static async Task<string> HashMatchingAsync(FileStream source, long expected, string expectedHash, CancellationToken token)
    {
        if (source.Length != expected) throw new InvalidDataException("The existing image artifact has different bytes and was preserved.");
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(source, token).ConfigureAwait(false)).ToLowerInvariant();
        if (!hash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The existing image artifact hash differs and its bytes were preserved.");
        return hash;
    }
    private static async Task<int> ReadSourceAsync(Stream source, byte[] buffer, CancellationToken token)
    {
        try { return await source.ReadAsync(buffer.AsMemory(), token).AsTask().WaitAsync(token).ConfigureAwait(false); }
        catch (IOException) { throw new IOException("The approved image source stream could not be read. No unchecked completion was reported."); }
    }

    private static async Task<string> SaveReceiptAsync(ImageArtifactAcquisitionPlan plan, bool reused, string hash, CancellationToken token)
    {
        ImageArtifactAcquisitionPlanner.ValidateIdentity(plan);
        var directory = Path.Combine(plan.DestinationDirectory, ReceiptDirectoryName);
        var id = Guid.NewGuid().ToString("N");
        var final = Path.Combine(directory, id + ".json");
        var temporary = Path.Combine(directory, ".ainexus-image-receipt-" + id + ".partial");
        var created = false;
        try
        {
            OfflineModelVerifier.CheckNoReparsePath(directory);
            CheckCurrentSpace(plan, ImageArtifactAcquisitionPlanner.ReceiptReserveBytes);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new ImageArtifactAcquisitionReceipt(1, plan, reused,
                plan.Artifact.SizeBytes!.Value, hash, DateTimeOffset.UtcNow), new JsonSerializerOptions { WriteIndented = true });
            if (bytes.Length > 65_536) throw new InvalidDataException("Image acquisition receipt exceeds its bounded metadata budget.");
            Directory.CreateDirectory(directory);
            OfflineModelVerifier.CheckNoReparsePath(temporary);
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.Asynchronous))
            {
                created = true;
                await output.WriteAsync(bytes, token).ConfigureAwait(false);
                await output.FlushAsync(token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            OfflineModelVerifier.CheckNoReparsePath(temporary);
            OfflineModelVerifier.CheckNoReparsePath(final);
            File.Move(temporary, final, overwrite: false);
            created = false;
            return final;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new IOException("Verified image artifact data was preserved, but its private image-specific receipt could not be completed."); }
        finally { if (created) TryDeleteOwnedPartial(temporary, directory, ".ainexus-image-receipt-"); }
    }
    private static void TryDeleteOwnedPartial(string path, string expectedDirectory, string prefix)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var name = Path.GetFileName(full);
            if (!string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(expectedDirectory), StringComparison.OrdinalIgnoreCase)
                || !name.StartsWith(prefix, StringComparison.Ordinal) || !name.EndsWith(".partial", StringComparison.Ordinal)
                || !Guid.TryParseExact(name[prefix.Length..^8], "N", out _)) return;
            OfflineModelVerifier.CheckNoReparsePath(full);
            if (File.Exists(full)) File.Delete(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException)
        { /* Preserve the real failure; never follow a changed link during cleanup. */ }
    }
    private static void Report(IProgress<AcquisitionProgress>? progress, AcquisitionProgress value)
    {
        try { progress?.Report(value); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { /* Presentation cannot change acquisition integrity. */ }
    }
}
