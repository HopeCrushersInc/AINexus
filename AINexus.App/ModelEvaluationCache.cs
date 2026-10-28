using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AINexus.App;

/// <summary>Private, bounded source snapshots. Reading this store never makes a network request.</summary>
public sealed class ModelEvaluationCache
{
    public const int MaximumSourceBytes = 1024 * 1024;
    public const int MaximumCacheBytes = MaximumSourceBytes + 8192;
    private readonly string _directory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private sealed record Envelope(int Version, string Repository, DateTimeOffset RetrievedAt, JsonElement Source);
    private static readonly JsonSerializerOptions JsonOptions = new() { MaxDepth = 26 };

    public ModelEvaluationCache(string directory)
    {
        OfflineModelVerifier.RequireLocalDrivePath(directory);
        _directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (string.Equals(_directory, Path.TrimEndingDirectorySeparator(Path.GetPathRoot(_directory)!), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose a private catalog subfolder, not a drive root.", nameof(directory));
        foreach (var protectedPath in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.SystemDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
        }.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(protectedPath));
            if (_directory.Equals(root, StringComparison.OrdinalIgnoreCase)
                || _directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Evaluation evidence belongs in a private catalog folder, outside system-wide application directories.");
        }
        OfflineModelVerifier.CheckNoReparsePath(_directory);
    }

    public async Task SaveAsync(string repository, JsonElement source, DateTimeOffset retrievedAt, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        ValidateSource(repository, source, retrievedAt);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new Envelope(1, repository, retrievedAt, source), JsonOptions);
        if (payload.Length > MaximumCacheBytes) throw new InvalidDataException("Saved evaluation evidence exceeds the cache size limit.");
        var path = CachePath(repository);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        string? temporary = null;
        try
        {
            token.ThrowIfCancellationRequested();
            OfflineModelVerifier.CheckNoReparsePath(_directory);
            OfflineModelVerifier.CheckNoReparsePath(path);
            Directory.CreateDirectory(_directory);
            OfflineModelVerifier.CheckNoReparsePath(_directory);
            temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            OfflineModelVerifier.CheckNoReparsePath(temporary);
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                8192, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(payload, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            OfflineModelVerifier.CheckNoReparsePath(_directory);
            OfflineModelVerifier.CheckNoReparsePath(path);
            File.Move(temporary, path, overwrite: true);
            temporary = null; // Publication succeeded; late cancellation must not describe it as undone.
        }
        finally
        {
            try { if (temporary is not null && File.Exists(temporary)) File.Delete(temporary); }
            finally { _gate.Release(); }
        }
    }

    public async Task<ModelEvaluationSnapshot?> LoadAsync(string repository, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var path = CachePath(repository);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            OfflineModelVerifier.CheckNoReparsePath(_directory);
            OfflineModelVerifier.CheckNoReparsePath(path);
            if (Directory.Exists(path)) throw new InvalidDataException("A directory occupies the saved evidence path; it was preserved.");
            FileStream stream;
            try { stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.Asynchronous); }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            await using (stream)
            {
                if (stream.Length > MaximumCacheBytes) throw new InvalidDataException("Saved evaluation evidence exceeds the cache size limit.");
                using var buffer = new MemoryStream();
                var chunk = new byte[8192];
                int read;
                while ((read = await stream.ReadAsync(chunk, token).ConfigureAwait(false)) > 0)
                {
                    if (buffer.Length + read > MaximumCacheBytes) throw new InvalidDataException("Saved evaluation evidence exceeds the cache size limit.");
                    buffer.Write(chunk, 0, read);
                }
                token.ThrowIfCancellationRequested();
                using var document = JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, checked((int)buffer.Length)), new JsonDocumentOptions { MaxDepth = 26 });
                var envelope = document.RootElement.Deserialize<Envelope>(JsonOptions)
                    ?? throw new InvalidDataException("Saved evaluation evidence is missing.");
                if (envelope.Version != 1 || envelope.Repository != repository)
                    throw new InvalidDataException("Saved evaluation evidence belongs to another repository or schema.");
                return ValidateSource(repository, envelope.Source, envelope.RetrievedAt) with { IsCached = true };
            }
        }
        finally { _gate.Release(); }
    }

    private string CachePath(string repository)
    {
        _ = ModelEvaluationClient.RequestUri(repository);
        return Path.Combine(_directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(repository))).ToLowerInvariant() + ".json");
    }

    private static ModelEvaluationSnapshot ValidateSource(string repository, JsonElement source, DateTimeOffset retrievedAt)
    {
        _ = ModelEvaluationClient.RequestUri(repository);
        if (retrievedAt == DateTimeOffset.MinValue || retrievedAt > DateTimeOffset.UtcNow.AddMinutes(1))
            throw new InvalidDataException("Saved evaluation evidence requires its original, non-future retrieval timestamp.");
        if (source.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Saved evaluation source must be a model metadata object.");
        var text = source.GetRawText();
        if (Encoding.UTF8.GetByteCount(text) > MaximumSourceBytes) throw new InvalidDataException("Evaluation source exceeds one MiB.");
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 24 });
        return ModelEvaluationParser.Parse(repository, document.RootElement, retrievedAt);
    }
}
