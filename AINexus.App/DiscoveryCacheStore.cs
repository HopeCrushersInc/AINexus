using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AINexus.App;

public sealed class DiscoveryCacheStore(string directory)
{
    private const int MaximumBytes = 4 * 1024 * 1024;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private sealed record Envelope(int Version, OnlineDiscoveryRequest Request, OnlineDiscoverySnapshot Snapshot);

    public static DiscoveryCacheStore ForCurrentUser() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AINexus", "Discovery"));

    public async Task SaveAsync(OnlineDiscoveryRequest request, OnlineDiscoverySnapshot snapshot, CancellationToken token = default)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new Envelope(1, request, snapshot with { IsCached = false }));
        if (payload.Length > MaximumBytes) throw new InvalidDataException("Discovery evidence exceeds the cache size limit.");
        await _gate.WaitAsync(token);
        try
        {
            Directory.CreateDirectory(directory);
            var destination = CachePath(request);
            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllBytesAsync(temporary, payload, token);
            File.Move(temporary, destination, overwrite: true);
        }
        finally { _gate.Release(); }
    }

    public async Task<OnlineDiscoverySnapshot?> LoadAsync(OnlineDiscoveryRequest request, CancellationToken token = default)
    {
        var path = CachePath(request);
        if (!File.Exists(path)) return null;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.Asynchronous);
        using var data = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, token)) > 0)
        {
            if (data.Length + count > MaximumBytes) throw new InvalidDataException("Saved discovery evidence exceeds the cache size limit.");
            data.Write(buffer, 0, count);
        }
        var envelope = JsonSerializer.Deserialize<Envelope>(data.GetBuffer().AsSpan(0, checked((int)data.Length)),
            new JsonSerializerOptions { MaxDepth = 32 });
        if (envelope is null || envelope.Version != 1 || envelope.Request != request || envelope.Snapshot is null ||
            envelope.Snapshot.Items is null || envelope.Snapshot.Notes is null || envelope.Snapshot.Items.Count > 100)
            throw new InvalidDataException("Saved discovery evidence is invalid or belongs to another query.");
        foreach (var item in envelope.Snapshot.Items)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.Name) ||
                item.Sources is null || item.Sources.Count == 0 || item.Sources.Any(source =>
                    source is null || source.Url is null || !source.Url.IsAbsoluteUri || source.Url.Scheme != "https" || source.Url.UserInfo.Length > 0))
                throw new InvalidDataException("Saved catalog entries have incomplete provenance.");
            if (item.RuntimeRelease is { } runtime &&
                (string.IsNullOrWhiteSpace(runtime.Tag) || runtime.Tag.Length > 128 || runtime.Tag.Any(char.IsControl)
                || runtime.Archives is null || runtime.Archives.Count > 256 || runtime.Notes is null
                || runtime.Notes.Any(note => note is null)
                || runtime.Archives.Any(asset => asset is null || string.IsNullOrWhiteSpace(asset.Name)
                    || asset.Name.Length > 240 || asset.Name.Any(c => char.IsControl(c) || c is '/' or '\\' or ':')
                    || asset.Name.Contains("..", StringComparison.Ordinal) || asset.SizeBytes <= 0
                    || asset.Sha256 is not null && (asset.Sha256.Length != 64 || asset.Sha256.Any(c => !Uri.IsHexDigit(c)))
                    || asset.DownloadUrl is null || !asset.DownloadUrl.IsAbsoluteUri
                    || asset.DownloadUrl.AbsoluteUri != "https://github.com/ggml-org/llama.cpp/releases/download/"
                        + Uri.EscapeDataString(runtime.Tag) + "/" + Uri.EscapeDataString(asset.Name))))
                throw new InvalidDataException("Saved runtime release evidence is invalid.");
        }
        return envelope.Snapshot with { IsCached = true };
    }

    private string CachePath(OnlineDiscoveryRequest request)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{request.Interest}\n{request.Query}\n{request.Limit}")));
        return Path.Combine(directory, key + ".json");
    }
}
