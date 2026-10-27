using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using AINexus.App;

namespace AINexus.Checks;

internal static class FeaturedModelCatalogChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var general = FeaturedModelCatalog.Visible(false);
        var all = FeaturedModelCatalog.Visible(true);
        check(general.Count == 3 && general.All(model => !model.Nsfw) && all.Count == 5 && all.Count(model => model.Nsfw) == 2,
            "Featured models hide adult-oriented and less-filtered entries until explicit opt-in");
        check(FeaturedModelCatalog.Visible(false).SequenceEqual(general) && all.Select(model => model.Id).Distinct().Count() == all.Count,
            "Catalog visibility has no remembered opt-in or duplicate model identities");
        var qwen = general.Single(model => model.Id == "qwen-32b");
        check(qwen.Repository == "bartowski/Qwen2.5-32B-Instruct-GGUF"
            && qwen.PreferredFile == "Qwen2.5-32B-Instruct-Q4_K_M.gguf"
            && qwen.SourceUrl.AbsoluteUri == "https://huggingface.co/bartowski/Qwen2.5-32B-Instruct-GGUF",
            "The Qwen 32B card points to its reviewed standalone GGUF source preference, not an invented installed model");
        check(all.Where(model => model.Nsfw).All(model => model.Name.Contains("NSFW") && model.LicenseNote.Length > 0)
            && all.Single(model => model.Id == "stheno-8b").LicenseNote.Contains("noncommercial")
            && FeaturedModelCatalog.NsfwWarning.Contains("not a guarantee"),
            "Optional adult-oriented cards disclose NSFW status, noncommercial terms and publisher-description limitations");
        check(general is IList<FeaturedModel> list && list.IsReadOnly,
            "Featured catalog callers cannot mutate the returned model collection");
        var coder = general.Single(model => model.Id == "qwen-coder-7b");
        check(coder.Repository == "Qwen/Qwen2.5-Coder-7B-Instruct-GGUF"
            && coder.PreferredFile == "qwen2.5-coder-7b-instruct-q4_k_m.gguf" && !coder.Nsfw,
            "Coding catalog entry prefers Qwen's official standalone GGUF without implying installed or verified state");

        const string revision = "1234567890123456789012345678901234567890";
        const string hash = "1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef";
        var requests = new List<Uri>();
        using (var http = Client((request, _) =>
        {
            requests.Add(request.RequestUri!);
            check(request.Method == HttpMethod.Get && request.Headers.Authorization is null,
                "Featured lookup makes only unauthenticated metadata GET requests");
            return Task.FromResult(requests.Count == 1 ? Info(qwen.Repository, revision) : Tree(qwen.PreferredFile, hash));
        }))
        {
            var snapshot = await new VerifiedOnlineDiscoveryProvider(http).LookupFeaturedModelAsync(qwen);
            var item = snapshot.Items.Single();
            check(requests.Count == 2 && requests[0].AbsolutePath == "/api/models/" + qwen.Repository
                && requests[1].AbsolutePath == "/api/models/" + qwen.Repository + "/tree/" + revision
                && requests.All(uri => uri.Host == "huggingface.co" && !uri.AbsolutePath.Contains("/resolve/")),
                "Featured lookup bypasses broad search and requests exactly its repository and pinned file tree");
            check(!snapshot.IsCached && item.Artifact is { SizeBytes: 4096, Sha256: hash, Revision: revision, Gated: false }
                && item.Artifact.FileName == qwen.PreferredFile && item.License == "apache-2.0"
                && item.Sources.All(source => source.Url.AbsoluteUri.Contains(revision)),
                "Featured lookup returns fresh source-reported identity and licensing with revision-pinned evidence");
        }

        var noCalls = 0;
        using (var http = Client((_, _) => { noCalls++; throw new InvalidOperationException("Unexpected request"); }))
        {
            var provider = new VerifiedOnlineDiscoveryProvider(http);
            check(await RejectsAsync<ArgumentException>(() => provider.LookupFeaturedModelAsync(qwen with { Repository = "Hostile/Source" })) && noCalls == 0,
                "A modified featured entry cannot redirect metadata requests to an arbitrary repository");
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            check(await RejectsAsync<OperationCanceledException>(() => provider.LookupFeaturedModelAsync(qwen, cancel.Token)) && noCalls == 0,
                "Precancelled featured lookup issues no metadata request");
        }

        foreach (var first in new[] { Info("Other/Model", revision), Info(qwen.Repository, "main"), Info(qwen.Repository, revision, gated: true),
            Info(qwen.Repository, revision, license: "unknown"), Info(qwen.Repository, revision, pipeline: "image-to-text") })
        {
            var calls = 0;
            using var http = Client((_, _) => { calls++; return Task.FromResult(first); });
            check(await RejectsAsync<InvalidDataException>(() => new VerifiedOnlineDiscoveryProvider(http).LookupFeaturedModelAsync(qwen)) && calls == 1,
                "Changed identity, unpinned revision, gated access, unknown terms or unsupported workflow blocks featured acquisition evidence");
        }

        foreach (var second in new[] { Json("[]"), Tree("different.gguf", hash), Tree(qwen.PreferredFile, "bad-hash"),
            Tree(qwen.PreferredFile, hash, bytes: 4096, lfsBytes: 4097),
            Json("[" + Entry(qwen.PreferredFile, hash) + "," + Entry(qwen.PreferredFile, hash) + "]") })
        {
            var calls = 0;
            using var http = Client((_, _) => Task.FromResult(++calls == 1 ? Info(qwen.Repository, revision) : second));
            check(await RejectsAsync<InvalidDataException>(() => new VerifiedOnlineDiscoveryProvider(http).LookupFeaturedModelAsync(qwen)) && calls == 2,
                "Missing, substituted, inconsistent or duplicate featured artifacts fail without selecting another model");
        }
        using (var http = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect)
            { Headers = { Location = new Uri("https://example.com/metadata") } })))
            check(await RejectsAsync<InvalidDataException>(() => new VerifiedOnlineDiscoveryProvider(http).LookupFeaturedModelAsync(qwen)),
                "Featured metadata redirects are rejected rather than followed");
    }

    private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => new(new Handler(send));
    private static HttpResponseMessage Info(string id, string revision, bool gated = false, string license = "apache-2.0", string pipeline = "text-generation") =>
        Json(JsonSerializer.Serialize(new { id, sha = revision, gated, pipeline_tag = pipeline, cardData = new { license } }));
    private static string Entry(string file, string hash, long bytes = 4096, long lfsBytes = 4096) =>
        JsonSerializer.Serialize(new { type = "file", path = file, size = bytes, lfs = new { oid = hash, size = lfsBytes } });
    private static HttpResponseMessage Tree(string file, string hash, long bytes = 4096, long lfsBytes = 4096) =>
        Json("[" + Entry(file, hash, bytes, lfsBytes) + "]");
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static async Task<bool> RejectsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); return false; } catch (T) { return true; }
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
}
