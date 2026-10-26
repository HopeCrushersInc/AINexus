using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AINexus.App;

/// <summary>Metadata only. An upstream archive is not an approved installation or a compatible runtime.</summary>
internal static partial class RuntimeReleaseParser
{
    public static RuntimeReleaseMetadata Parse(JsonElement release, string tag)
    {
        if (release.ValueKind != JsonValueKind.Object || string.IsNullOrWhiteSpace(tag)
            || tag.Length > 128 || tag.Any(char.IsControl))
            throw new InvalidDataException("The runtime release identity is invalid.");
        var assets = new List<RuntimeReleaseAsset>();
        var notes = new List<string>();
        if (release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True)
            throw new InvalidDataException("Draft runtime releases are not supported.");
        if (release.TryGetProperty("assets", out var array))
        {
            if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > 256)
                throw new InvalidDataException("The runtime release asset list is invalid or exceeds its bound.");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var asset in array.EnumerateArray())
            {
                var name = Read(asset, "name");
                if (name is null || !(name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))) continue;
                var canonical = "https://github.com/ggml-org/llama.cpp/releases/download/" + Uri.EscapeDataString(tag) + "/" + Uri.EscapeDataString(name);
                if (name.Length > 240 || name.Any(c => char.IsControl(c) || c is '/' or '\\' or ':')
                    || name.Contains("..", StringComparison.Ordinal) || !names.Add(name)
                    || Read(asset, "state") != "uploaded"
                    || !asset.TryGetProperty("size", out var size) || size.ValueKind != JsonValueKind.Number
                    || !size.TryGetInt64(out var bytes) || bytes <= 0
                    || !Uri.TryCreate(Read(asset, "browser_download_url"), UriKind.Absolute, out var url)
                    || !string.Equals(url.AbsoluteUri, canonical, StringComparison.Ordinal))
                {
                    notes.Add("An invalid, duplicate or noncanonical archive entry was excluded.");
                    continue;
                }
                var digest = Read(asset, "digest");
                var hash = digest is not null && Digest().IsMatch(digest) ? digest[7..].ToUpperInvariant() : null;
                assets.Add(new(name, bytes, hash, url));
            }
        }
        if (assets.Count == 0) notes.Add("This release has no supported published archive entries. No binary download or nightly fallback was inferred.");
        if (assets.Any(asset => asset.Sha256 is null)) notes.Add("Some archives lack a usable upstream SHA-256 digest; verified acquisition cannot be planned from this evidence alone.");
        notes.Add("Archive names are publisher labels. Architecture, acceleration backend, driver requirements, companion files and archive contents have not been verified. No archive was downloaded or installed.");
        return new(tag, release.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True,
            release.TryGetProperty("immutable", out var immutable) && immutable.ValueKind == JsonValueKind.True,
            assets.AsReadOnly(), notes.AsReadOnly());
    }

    private static string? Read(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    [GeneratedRegex(@"^sha256:[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Digest();
}
