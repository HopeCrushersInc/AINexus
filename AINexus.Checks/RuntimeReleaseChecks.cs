using System.IO;
using System.Text.Json;
using AINexus.App;

namespace AINexus.Checks;

internal static class RuntimeReleaseChecks
{
    public static void Run(Action<bool, string> check)
    {
        var hash = new string('a', 64);
        object Asset(string name, object size, string? digest, string? url = null) => new
        {
            name, size, digest, state = "uploaded",
            browser_download_url = url ?? "https://github.com/ggml-org/llama.cpp/releases/download/fixture/" + Uri.EscapeDataString(name)
        };
        RuntimeReleaseMetadata Parse(params object[] assets)
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { assets, prerelease = true, immutable = false }));
            return RuntimeReleaseParser.Parse(doc.RootElement, "fixture");
        }
        var report = Parse(Asset("llama-fixture-bin-win-cpu-x64.zip", 1234, "sha256:" + hash),
            Asset("llama-fixture-linux.tar.gz", 4321, null));
        check(report.Archives.Count == 2 && report.Archives[0].SizeBytes == 1234 && report.Archives[0].Sha256 == hash.ToUpperInvariant(),
            "Runtime release archives preserve actual publisher byte counts and usable digests");
        check(report.Prerelease && !report.Immutable && report.Archives[1].Sha256 is null
            && report.Notes.Any(n => n.Contains("lack a usable")), "Missing runtime digests and mutable prerelease status remain explicit");
        var invalid = Parse(Asset("../escape.zip", 1, null), Asset("external.zip", 1, null, "https://example.com/external.zip"),
            Asset("wrong-tag.zip", 1, null, "https://github.com/ggml-org/llama.cpp/releases/download/other/wrong-tag.zip"),
            Asset("zero.zip", 0, null), Asset("string.zip", "123", null), Asset("negative.zip", -1, null));
        check(invalid.Archives.Count == 0 && invalid.Notes.Any(n => n.Contains("no supported")),
            "Runtime metadata rejects unsafe paths, external origins, wrong releases and invalid sizes");
        var duplicates = Parse(Asset("same.zip", 1, "sha256:" + hash), Asset("same.zip", 2, "sha256:" + hash));
        check(duplicates.Archives.Count == 1 && duplicates.Notes.Any(n => n.Contains("duplicate")), "Duplicate runtime archives cannot create conflicting choices");
        check(Parse(Asset("nightly-tag.txt", 7, "sha256:" + hash)).Archives.Count == 0,
            "A release marker is not invented into a binary or automatically followed to a nightly release");
        check(Parse(Asset("bad-digest.zip", 1, "md5:" + hash)).Archives.Single().Sha256 is null,
            "Unsupported digest formats cannot masquerade as verified SHA-256 evidence");
        using var draft = JsonDocument.Parse("{\"draft\":true}");
        var rejected = false;
        try { RuntimeReleaseParser.Parse(draft.RootElement, "fixture"); } catch (InvalidDataException) { rejected = true; }
        check(rejected, "Draft runtime releases cannot be presented as published releases");
        var bounded = false;
        try { Parse(Enumerable.Range(0, 257).Select(i => Asset($"{i}.zip", 1, null)).ToArray()); }
        catch (InvalidDataException) { bounded = true; }
        check(bounded, "Runtime asset inspection is bounded independently of the HTTP response limit");
        var roundtrip = JsonSerializer.Deserialize<RuntimeReleaseMetadata>(JsonSerializer.Serialize(report));
        check(roundtrip?.Archives[0].Sha256 == report.Archives[0].Sha256, "Runtime asset evidence survives saved-catalog serialization");
    }
}
