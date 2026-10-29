using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AINexus.App;

public sealed record ComfyPortableAsset(string Name, long SizeBytes, string Sha256, Uri DownloadUrl);
public sealed record ComfyPortableRelease(string Tag, Uri ReleaseUrl, Uri SourceTermsUrl, bool IsPrerelease,
    IReadOnlyList<ComfyPortableAsset> Assets, DateTimeOffset RetrievedAt);

/// <summary>Public release metadata only. Asset names do not establish hardware compatibility.</summary>
public sealed partial class ComfyPortableReleaseClient(HttpClient httpClient)
{
    private readonly HttpClient _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    internal const int MaximumMetadataBytes = 2 * 1024 * 1024;
    internal const long MaximumArchiveBytes = 32L * 1024 * 1024 * 1024;

    public static HttpClient CreateHttpClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseCookies = false, Credentials = null, DefaultProxyCredentials = null,
        AutomaticDecompression = DecompressionMethods.None, ConnectTimeout = TimeSpan.FromSeconds(10), MaxResponseHeadersLength = 64
    }) { Timeout = Timeout.InfiniteTimeSpan };

    public async Task<ComfyPortableRelease> ReadAsync(string tag, CancellationToken cancellationToken = default)
    {
        ValidateTag(tag);
        if (_http.DefaultRequestHeaders.Any()) throw new InvalidOperationException("Use a dedicated unauthenticated client for public release metadata.");
        var uri = new Uri("https://api.github.com/repos/Comfy-Org/ComfyUI/releases/" + (tag == "latest" ? "latest" : "tags/" + Uri.EscapeDataString(tag)));
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("AINexus-ComfyPortable/1.0");
        request.Headers.AcceptEncoding.ParseAdd("identity");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        var token = deadline.Token;
        var pending = _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        HttpResponseMessage response;
        try { response = await pending.WaitAsync(token).ConfigureAwait(false); }
        catch
        {
            _ = pending.ContinueWith(task => { if (task.Status == TaskStatus.RanToCompletion) task.Result.Dispose(); else _ = task.Exception; }, TaskScheduler.Default);
            throw;
        }
        using (response)
        {
            if (response.StatusCode != HttpStatusCode.OK || response.RequestMessage?.RequestUri is { } actual && actual != uri)
                throw new HttpRequestException("The exact official ComfyUI release metadata is unavailable; no alternate source was substituted.", null, response.StatusCode);
            if (response.Content.Headers.ContentLength is > MaximumMetadataBytes || response.Content.Headers.ContentEncoding.Any(value => value != "identity"))
                throw new InvalidDataException("The release metadata exceeds its size or encoding bounds.");
            using var source = await OpenBodyAsync(response.Content, token).ConfigureAwait(false);
            using var data = new MemoryStream();
            var buffer = new byte[16384];
            while (true)
            {
                var read = await source.ReadAsync(buffer, token).AsTask().WaitAsync(token).ConfigureAwait(false);
                if (read == 0) break;
                if (data.Length + read > MaximumMetadataBytes) throw new InvalidDataException("The release metadata exceeds its size bound.");
                data.Write(buffer, 0, read);
            }
            using var json = JsonDocument.Parse(data.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
            return Parse(json.RootElement, tag, DateTimeOffset.UtcNow);
        }
    }

    internal static ComfyPortableRelease Parse(JsonElement root, string requestedTag, DateTimeOffset retrievedAt)
    {
        ValidateTag(requestedTag);
        var tag = Read(root, "tag_name");
        ValidateTag(tag);
        if (tag == "latest" || requestedTag != "latest" && tag != requestedTag
            || !root.TryGetProperty("draft", out var draft) || draft.ValueKind != JsonValueKind.False
            || Read(root, "html_url") != ReleaseUrl(tag!).AbsoluteUri
            || !root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array || assets.GetArrayLength() > 256)
            throw new InvalidDataException("The release identity, publication state or asset list is invalid.");
        var found = new List<ComfyPortableAsset>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in assets.EnumerateArray())
        {
            var name = Read(asset, "name");
            if (name is null || !AssetName().IsMatch(name)) continue;
            if (!names.Add(name)) throw new InvalidDataException("The release has ambiguous duplicate portable archives.");
            var digest = Read(asset, "digest");
            if (Read(asset, "state") != "uploaded" || digest is null || !Digest().IsMatch(digest)
                || !asset.TryGetProperty("size", out var size) || size.ValueKind != JsonValueKind.Number || !size.TryGetInt64(out var bytes) || bytes <= 0 || bytes > MaximumArchiveBytes
                || Read(asset, "browser_download_url") != AssetUrl(tag!, name).AbsoluteUri) continue;
            found.Add(new(name, bytes, digest[7..].ToUpperInvariant(), AssetUrl(tag!, name)));
        }
        return new(tag!, ReleaseUrl(tag!), TermsUrl(tag!), root.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True,
            found.AsReadOnly(), retrievedAt);
    }

    internal static void ValidateTag(string? tag)
    {
        if (tag is null || !TagName().IsMatch(tag) || tag.Contains("..", StringComparison.Ordinal))
            throw new InvalidDataException("Enter a simple official release tag or explicitly request the current release.");
    }

    internal static async Task<Stream> OpenBodyAsync(HttpContent content, CancellationToken token)
    {
        var pending = content.ReadAsStreamAsync(token);
        try { return await pending.WaitAsync(token).ConfigureAwait(false); }
        catch
        {
            _ = pending.ContinueWith(task => { if (task.Status == TaskStatus.RanToCompletion) task.Result.Dispose(); else _ = task.Exception; }, TaskScheduler.Default);
            throw;
        }
    }

    internal static void ValidateRelease(ComfyPortableRelease release)
    {
        ValidateTag(release.Tag);
        if (release.Tag == "latest" || release.ReleaseUrl != ReleaseUrl(release.Tag) || release.SourceTermsUrl != TermsUrl(release.Tag)
            || release.Assets is null || release.Assets.Count > 256 || release.Assets.Select(asset => asset.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != release.Assets.Count)
            throw new InvalidDataException("The reviewed official release identity is invalid.");
        foreach (var asset in release.Assets) ValidateAsset(release.Tag, asset);
    }

    internal static void ValidateAsset(string tag, ComfyPortableAsset asset)
    {
        if (!AssetName().IsMatch(asset.Name) || asset.SizeBytes <= 0 || asset.SizeBytes > MaximumArchiveBytes
            || !Hash().IsMatch(asset.Sha256) || asset.DownloadUrl != AssetUrl(tag, asset.Name))
            throw new InvalidDataException("The selected official portable archive lacks an exact canonical source, positive size or SHA-256.");
    }

    private static Uri ReleaseUrl(string tag) => new("https://github.com/Comfy-Org/ComfyUI/releases/tag/" + Uri.EscapeDataString(tag));
    private static Uri TermsUrl(string tag) => new("https://github.com/Comfy-Org/ComfyUI/blob/" + Uri.EscapeDataString(tag) + "/LICENSE");
    private static Uri AssetUrl(string tag, string name) => new("https://github.com/Comfy-Org/ComfyUI/releases/download/" + Uri.EscapeDataString(tag) + "/" + Uri.EscapeDataString(name));
    private static string? Read(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9._-]{0,127}\z", RegexOptions.CultureInvariant)] private static partial Regex TagName();
    [GeneratedRegex(@"\AComfyUI_windows_portable_(?:nvidia(?:_arm64|_cu126)?|amd|intel)\.7z\z", RegexOptions.CultureInvariant)] private static partial Regex AssetName();
    [GeneratedRegex(@"\Asha256:[a-fA-F0-9]{64}\z", RegexOptions.CultureInvariant)] private static partial Regex Digest();
    [GeneratedRegex(@"\A[A-Fa-f0-9]{64}\z", RegexOptions.CultureInvariant)] private static partial Regex Hash();
}
