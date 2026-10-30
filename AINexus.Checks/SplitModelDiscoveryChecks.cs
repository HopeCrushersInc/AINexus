using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using AINexus.App;

namespace AINexus.Checks;

internal static class SplitModelDiscoveryChecks
{
    private const string Revision = "1234567890123456789012345678901234567890";
    private const string Hash = "1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef";

    internal static async Task RunAsync(Action<bool, string> check)
    {
        var two = new[] { Entry("folder/model-00002-of-00002.gguf", 20), Entry("folder/model-00001-of-00002.gguf", 10) };
        var snapshot = await SearchAsync(two, check);
        var artifact = snapshot.Items.Single().Artifact!;
        check(artifact.FileName == "folder/model-00001-of-00002.gguf" && artifact.SizeBytes == 10 && artifact.Sha256 == Hash
            && artifact.AdditionalFiles is [{ FileName: "folder/model-00002-of-00002.gguf", SizeBytes: 20, Sha256: Hash }]
            && ModelFileSet.TotalBytes(artifact) == 30,
            "Pinned split-model discovery emits one ordered complete inventory while preserving each file's exact identity and aggregate bytes");
        check(snapshot.Items[0].Description.Contains("2 files, 30 total bytes", StringComparison.Ordinal)
            && snapshot.Items[0].Description.Contains("unverified", StringComparison.Ordinal)
            && snapshot.Items[0].Sources.All(source => source.Url.AbsoluteUri.Contains(Revision, StringComparison.Ordinal)),
            "Split-model discovery discloses complete inventory size and pinned provenance without claiming verified runtime compatibility");

        var incompleteCases = new (string Name, Dictionary<string, object?>[] Files)[]
        {
            ("missing middle", [Entry("model-00001-of-00003.gguf"), Entry("model-00003-of-00003.gguf")]),
            ("missing first", [Entry("model-00002-of-00002.gguf")]),
            ("missing hash", [Entry("model-00001-of-00002.gguf"), Entry("model-00002-of-00002.gguf", hash: null)]),
            ("invalid hash", [Entry("model-00001-of-00002.gguf"), Entry("model-00002-of-00002.gguf", hash: "not-a-hash")]),
            ("missing byte count", [Entry("model-00001-of-00002.gguf"), Entry("model-00002-of-00002.gguf", bytes: null)]),
            ("zero byte count", [Entry("model-00001-of-00002.gguf"), Entry("model-00002-of-00002.gguf", bytes: 0)]),
            ("mixed folders", [Entry("a/model-00001-of-00002.gguf"), Entry("b/model-00002-of-00002.gguf")]),
            ("mixed counts", [Entry("model-00001-of-00002.gguf"), Entry("model-00002-of-00003.gguf")]),
            ("duplicate index", [Entry("model-00001-of-00002.gguf"), Entry("model-00002-of-00002.gguf"), Entry("model-00002-of-00002.gguf")]),
            ("noncanonical names", [Entry("model-1-of-2.gguf"), Entry("model-2-of-2.gguf")]),
            ("Unicode shard digits", [Entry("model-00001-of-٠٠٠٠٢.gguf"), Entry("model-00002-of-٠٠٠٠٢.gguf")]),
            ("aggregate overflow", [Entry("model-00001-of-00002.gguf", long.MaxValue), Entry("model-00002-of-00002.gguf", 1)])
        };
        foreach (var test in incompleteCases)
        {
            var result = await SearchAsync(test.Files, check);
            check(result.Items.Count == 0 && result.Notes.Any(note => note.Contains("shard", StringComparison.OrdinalIgnoreCase)),
                $"Split-model discovery excludes {test.Name} rather than offering a partial or invented model");
        }

        var maxSet = Enumerable.Range(1, 512).Select(index => Entry($"large- {index:00000}-of-00512.gguf".Replace("- ", "-"), index)).Reverse().ToArray();
        var bounded = await SearchAsync(maxSet, check);
        check(bounded.Items.Count == 1 && bounded.Items[0].Artifact?.AdditionalFiles?.Count == 511
            && ModelFileSet.TotalBytes(bounded.Items[0].Artifact!) == 131_328,
            "A complete512-file inventory remains one model result and sums all files within the bounded repository tree");

        var large = await SearchAsync([Entry("large-00001-of-00002.gguf", 100L * 1_073_741_824), Entry("large-00002-of-00002.gguf", 1_073_741_824)], check);
        check(large.Items.Count == 1 && ModelFileSet.TotalBytes(large.Items[0].Artifact!) == 101L * 1_073_741_824,
            "Complete large model inventories retain101GiB of source bytes for separate local capacity assessment instead of a parameter-size filter");

        var mixed = await SearchAsync([.. two, Entry("z-single.gguf", 100)], check, 1);
        check(mixed.Items.Count == 1 && mixed.Items[0].Artifact?.AdditionalFiles?.Count == 1,
            "The result limit counts a complete split inventory as one selectable model rather than truncating its shard list");

        var beyondBound = Enumerable.Range(0, 999).Select(index => Entry($"ignored-{index:000}.txt"))
            .Concat([Entry("bounded-00001-of-00002.gguf"), Entry("bounded-00002-of-00002.gguf")]).ToArray();
        var truncated = await SearchAsync(beyondBound, check);
        check(truncated.Items.Count == 0 && truncated.Notes.Any(note => note.Contains("first 1000", StringComparison.Ordinal)),
            "A split set crossing the repository-tree inspection limit stays unavailable and partial-source coverage is disclosed");
    }

    private static Dictionary<string, object?> Entry(string path, long? bytes = 1, string? hash = Hash) => new()
    {
        ["type"] = "file", ["path"] = path, ["size"] = bytes, ["lfs"] = new Dictionary<string, object?> { ["oid"] = hash }
    };

    private static async Task<OnlineDiscoverySnapshot> SearchAsync(Dictionary<string, object?>[] entries, Action<bool, string> check, int limit = 48)
    {
        var requests = new List<Uri>();
        using var http = new HttpClient(new Handler(request =>
        {
            requests.Add(request.RequestUri!);
            return request.RequestUri!.AbsolutePath.Contains("/tree/", StringComparison.Ordinal)
                ? JsonSerializer.Serialize(entries)
                : "{\"id\":\"Fixture/Split\",\"sha\":\"" + Revision + "\",\"gated\":false,\"pipeline_tag\":\"text-generation\",\"cardData\":{\"license\":\"apache-2.0\"}}";
        }));
        var result = await new VerifiedOnlineDiscoveryProvider(http).SearchAsync(new(DiscoveryInterest.LocalAssistants, "Fixture/Split", limit));
        check(requests.Count == 2 && requests.All(uri => uri.Host == "huggingface.co")
            && requests[1].AbsolutePath.Contains("/tree/" + Revision, StringComparison.Ordinal),
            "Split inventory discovery uses only the same two pinned metadata requests without downloading model data or following pagination");
        return result;
    }

    private sealed class Handler(Func<HttpRequestMessage, string> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(reply(request), Encoding.UTF8, "application/json") });
    }
}
