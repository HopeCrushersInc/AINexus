using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using AINexus.App;

namespace AINexus.Checks;

internal static class OnlineDiscoveryChecks
{
    private const string Revision = "1234567890123456789012345678901234567890";
    private const string Hash = "1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef";
    private const string ModelInfo = "{\"id\":\"Fixture/Model\",\"sha\":\"" + Revision + "\",\"gated\":false,\"pipeline_tag\":\"text-generation\",\"cardData\":{\"license\":\"apache-2.0\"}}";
    private const string Tree = "[{\"type\":\"file\",\"path\":\"fixture-q4_k_m.gguf\",\"size\":1024,\"lfs\":{\"oid\":\"" + Hash + "\",\"size\":1024}}," +
        "{\"type\":\"file\",\"path\":\"fixture-q8.gguf\",\"size\":2048}," +
        "{\"type\":\"file\",\"path\":\"fixture-00001-of-00002.gguf\",\"size\":999}," +
        "{\"type\":\"file\",\"path\":\"mmproj-companion.gguf\",\"size\":512}," +
        "{\"type\":\"file\",\"path\":\"../unsafe.gguf\",\"size\":1}]";

    public static async Task RunAsync(Action<bool, string> check)
    {
        var requests = new List<Uri>();
        using (var http = new HttpClient(new FixtureHandler((request, _) =>
        {
            requests.Add(request.RequestUri!);
            check(request.Method == HttpMethod.Get && request.Headers.Authorization is null, "Online discovery issues read-only unauthenticated metadata GET requests");
            return Task.FromResult(ReplyFor(request.RequestUri!));
        })))
        {
            var snapshot = await new VerifiedOnlineDiscoveryProvider(http).SearchAsync(new(DiscoveryInterest.LocalAssistants, "hello & x?search=", 3));
            var models = snapshot.Items.Where(item => item.Kind == "Model").ToArray();
            check(models.Length == 2 && snapshot.Items.Count == 3, "Bounded discovery returns actual fixture artifacts and supporting release metadata");
            check(models[0].Artifact is { SizeBytes: 1024, Revision: Revision, Sha256: Hash, Gated: false, Format: "GGUF" },
                "Online artifacts preserve reported byte size, SHA-256, revision, format and access state");
            check(models[0].License == "apache-2.0" && models[0].Publisher == "Fixture", "License and publisher come from source metadata");
            check(models.All(item => item.Sources.All(source => source.Url.Scheme == "https" && source.Url.AbsoluteUri.Contains(Revision))),
                "Model sources point to the pinned repository revision");
            check(snapshot.Items.Single(item => item.Kind == "Runtime").Name == "llama.cpp fixture-release", "Supporting runtime release name comes from the upstream response");
            check(snapshot.Notes.Any(note => note.Contains("shard")) && snapshot.Notes.Any(note => note.Contains("invalid artifact")),
                "Sharded and unsafe artifact paths are excluded with explicit notes");
            check(models.All(item => !item.Artifact!.FileName.Contains("00001") && !item.Artifact.FileName.Contains("..")),
                "A single GGUF shard is never presented as complete model weights");
            check(models.All(item => !item.Artifact!.FileName.Contains("mmproj", StringComparison.OrdinalIgnoreCase))
                && snapshot.Notes.Any(note => note.Contains("projector", StringComparison.OrdinalIgnoreCase)),
                "A named multimodal projector is excluded from standalone text-model search results with an explicit explanation");
            check(requests.Count == 5 && requests.All(uri => uri.Host is "huggingface.co" or "api.github.com"), "Discovery stays inside its fixed public sources and bounded request budget");
            check(requests[0].OriginalString.Contains("search=hello%20%26%20x%3Fsearch%3D", StringComparison.Ordinal), "User search text is escaped rather than interpreted as API parameters");
            check(requests.Any(uri => uri.AbsolutePath.Contains("/tree/" + Revision)), "Artifact metadata is read at the reported immutable revision");
        }

        var cadRequests = 0;
        using (var http = new HttpClient(new FixtureHandler((request, _) =>
        {
            cadRequests++;
            check(request.Method == HttpMethod.Get && request.RequestUri is { Host: "api.github.com", AbsolutePath: "/search/repositories" }
                && request.Headers.Authorization is null,
                "CAD discovery requests actual public repository metadata without authentication or an installer");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"total_count\":0,\"incomplete_results\":false,\"items\":[]}", Encoding.UTF8, "application/json")
            });
        })))
        {
            var snapshot = await new VerifiedOnlineDiscoveryProvider(http).SearchAsync(new(DiscoveryInterest.Cad, "fixture"));
            check(cadRequests == 1 && snapshot.Items.Count == 0 && !snapshot.IsCached,
                "A category without an installation adapter can search live metadata without fabricating artifacts or installation results");
        }
        check(VerifiedOnlineDiscoveryProvider.Workflows.Count == Enum.GetValues<DiscoveryInterest>().Length, "Workflow catalog explicitly accounts for every requested interest");

        var exactRepositoryRequests = new List<Uri>();
        using (var http = new HttpClient(new FixtureHandler((request, _) =>
        {
            exactRepositoryRequests.Add(request.RequestUri!);
            if (request.RequestUri!.AbsolutePath == "/api/models")
                return Task.FromResult(Json("[]"));
            if (request.RequestUri!.AbsolutePath.Contains("/tree/"))
                return Task.FromResult(Json(Tree[..^1] + ", {\"type\":\"file\",\"path\":\"third.gguf\",\"size\":4096}]"));
            return Task.FromResult(ReplyFor(request.RequestUri));
        })))
        {
            var catalog = await new VerifiedOnlineDiscoveryProvider(http).SearchAsync(new(DiscoveryInterest.Coding, "Fixture/Model", 8));
            check(catalog.Items.Count(item => item.Artifact is not null) == 3 && catalog.Items.Any(item => item.Artifact?.FileName == "third.gguf"),
                "An exact repository search exposes further file variants within the requested budget instead of restricting user choice to two files");
            check(exactRepositoryRequests.Count == 4 && exactRepositoryRequests.All(uri => uri.AbsolutePath != "/api/models")
                && exactRepositoryRequests[0].AbsolutePath == "/api/models/Fixture/Model"
                && catalog.Notes.Any(note => note.Contains("no broad search or repository substitution", StringComparison.Ordinal)),
                "An explicitly chosen repository is inspected directly even when a broad source search would omit it, with no substitute repository");
        }
        await CheckRepositoryCoverageAsync(check);
        await CheckPartialResultsAsync(check);
        await CheckMalformedMetadataAsync(check);
        await CheckLimitsAndCancellationAsync(check);
    }

    private static async Task CheckRepositoryCoverageAsync(Action<bool, string> check)
    {
        var requests = new List<Uri>();
        var repositories = Enumerable.Range(1, 12)
            .Select(index => index == 7 ? "Fixture/Writer-32B" : $"Fixture/Model{index:D2}").ToArray();
        using var http = new HttpClient(new FixtureHandler((request, _) =>
        {
            var uri = request.RequestUri!;
            requests.Add(uri);
            if (uri.AbsolutePath == "/api/models")
                return Task.FromResult(Json("[" + string.Join(",", repositories.Select(repository => "{\"id\":\"" + repository + "\"}")) + "]"));
            if (uri.Host == "huggingface.co" && !uri.AbsolutePath.Contains("/tree/"))
            {
                var repository = uri.AbsolutePath["/api/models/".Length..];
                return Task.FromResult(Json(ModelInfo.Replace("Fixture/Model", repository, StringComparison.Ordinal)));
            }
            return Task.FromResult(ReplyFor(uri));
        }));
        var snapshot = await new VerifiedOnlineDiscoveryProvider(http).SearchAsync(new(DiscoveryInterest.LocalAssistants, "", 12));
        var models = snapshot.Items.Where(item => item.Kind == "Model").ToArray();
        check(models.Length == 11 && models.Select(item => item.Artifact!.Repository).Distinct().Count() == 11
            && models.Any(item => item.Artifact!.Repository == "Fixture/Writer-32B"),
            "Broad discovery reaches a source repository beyond the first four, including a source-named 32B entry, before allocating repeat variants");
        check(snapshot.Items.Count == 12 && snapshot.Items.Count(item => item.Kind == "Runtime") == 1
            && requests.Count <= 27 && requests[0].Query.Contains("limit=12", StringComparison.Ordinal)
            && requests[0].Query.Contains("sort=downloads&direction=-1", StringComparison.Ordinal),
            "Expanded discovery requests explicit download ordering while retaining the twelve-result and twenty-seven-request bounds");
        check(snapshot.Notes.Any(note => note.Contains("No pagination", StringComparison.Ordinal))
            && snapshot.Notes.Any(note => note.Contains("not a quality rating", StringComparison.Ordinal))
            && snapshot.Notes.Any(note => note.Contains("Parameter counts are not inferred", StringComparison.Ordinal))
            && models.All(item => !item.Description.Contains("32 billion", StringComparison.OrdinalIgnoreCase)),
            "Discovery discloses bounded coverage and popularity ordering without inventing quality ratings or parameter counts from source names");
    }

    public static async Task RunLiveAsync(Action<bool, string> check)
    {
        using var http = VerifiedOnlineDiscoveryProvider.CreateHttpClient();
        var snapshot = await new VerifiedOnlineDiscoveryProvider(http).SearchAsync(new(DiscoveryInterest.LocalAssistants, "Qwen2.5-1.5B-Instruct-GGUF", 3));
        var artifact = snapshot.Items.FirstOrDefault(item => item.Artifact?.Repository == "Qwen/Qwen2.5-1.5B-Instruct-GGUF"
            && item.Artifact.FileName.Equals("qwen2.5-1.5b-instruct-q4_k_m.gguf", StringComparison.OrdinalIgnoreCase));
        check(artifact?.Artifact is { SizeBytes: > 0, Sha256: not null } && artifact.Sources.Count > 0,
            "Live online discovery retrieves real upstream Qwen artifact size/hash/revision without downloading weights");
        var runtime = snapshot.Items.FirstOrDefault(item => item.Kind == "Runtime")?.RuntimeRelease;
        check(runtime is not null && runtime.Archives.All(asset => asset.SizeBytes > 0 && asset.DownloadUrl.Host == "github.com")
            && runtime.Notes.Any(note => note.Contains("No archive was downloaded")),
            "Live runtime discovery reads published archive evidence without assuming binaries exist or installing software");
        Console.WriteLine($"Live runtime metadata: {runtime?.Tag}; published archives: {runtime?.Archives.Count}.");
        Console.WriteLine($"Live discovery: {snapshot.Items.Count} metadata entries, {snapshot.Notes.Count} notes.");
    }

    private static async Task CheckPartialResultsAsync(Action<bool, string> check)
    {
        using var http = new HttpClient(new FixtureHandler((request, _) =>
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath == "/api/models") return Task.FromResult(Json("[{\"id\":\"Fixture/Unavailable\"},{\"id\":\"Fixture/Model\"}]"));
            if (uri.AbsolutePath.EndsWith("/Unavailable")) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            return Task.FromResult(ReplyFor(uri));
        }));
        var snapshot = await new VerifiedOnlineDiscoveryProvider(http).SearchAsync(new(DiscoveryInterest.Coding, "fixture", 3));
        check(snapshot.Items.Any(item => item.Artifact?.Repository == "Fixture/Model") && snapshot.Notes.Any(note => note.Contains("HTTP 404")),
            "A repository failure preserves successful recommendations and reports source status");
    }

    private static async Task CheckMalformedMetadataAsync(Action<bool, string> check)
    {
        using (var http = new HttpClient(new FixtureHandler((request, _) =>
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath == "/api/models") return Task.FromResult(Json("[{\"id\":\"https://attacker.example/model\"}]"));
            return Task.FromResult(ReplyFor(uri));
        })))
        {
            var snapshot = await new VerifiedOnlineDiscoveryProvider(http).SearchAsync(new(DiscoveryInterest.Agents, "fixture", 3));
            check(snapshot.Items.All(item => item.Artifact is null) && snapshot.Notes.Any(note => note.Contains("invalid repository")),
                "Source-controlled repository strings cannot redirect metadata requests to arbitrary hosts");
        }
        using (var http = new HttpClient(new FixtureHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/tree/"))
                return Task.FromResult(Json("[{\"type\":\"file\",\"path\":\"unknown.gguf\",\"size\":\"unavailable\",\"lfs\":{\"oid\":\"not-a-hash\"}}]"));
            if (request.RequestUri.AbsolutePath == "/api/models/Fixture/Model")
                return Task.FromResult(Json("{\"id\":\"Fixture/Model\",\"sha\":\"" + Revision + "\"}"));
            return Task.FromResult(ReplyFor(request.RequestUri));
        })))
        {
            var snapshot = await new VerifiedOnlineDiscoveryProvider(http).SearchAsync(new(DiscoveryInterest.LocalAssistants, "fixture", 1));
            check(snapshot.Items.Single().Artifact is { SizeBytes: null, Sha256: null, Gated: true } && snapshot.Items[0].License is null,
                "Missing sizes, invalid hashes, unknown access and missing licenses remain unknown or conservative");
        }
    }

    private static async Task CheckLimitsAndCancellationAsync(Action<bool, string> check)
    {
        foreach (var status in new[] { HttpStatusCode.Redirect, HttpStatusCode.TooManyRequests })
        {
            using var http = new HttpClient(new FixtureHandler((_, _) => Task.FromResult(new HttpResponseMessage(status))));
            var snapshot = await new VerifiedOnlineDiscoveryProvider(http).SearchAsync(new(DiscoveryInterest.LocalAssistants, "fixture"));
            check(snapshot.Items.Count == 0 && snapshot.Notes.Count > 0, "Redirected or rate-limited sources do not produce fabricated catalog entries");
        }
        foreach (var declared in new[] { true, false })
        {
            using var http = new HttpClient(new FixtureHandler((_, _) =>
            {
                var response = Json("[]");
                if (declared) response.Content.Headers.ContentLength = VerifiedOnlineDiscoveryProvider.MaximumResponseBytes + 1;
                else
                {
                    response.Content = new StreamContent(new NonSeekableStream(new byte[VerifiedOnlineDiscoveryProvider.MaximumResponseBytes + 1]));
                }
                return Task.FromResult(response);
            }));
            var snapshot = await new VerifiedOnlineDiscoveryProvider(http).SearchAsync(new(DiscoveryInterest.LocalAssistants, "fixture"));
            check(snapshot.Items.Count == 0 && snapshot.Notes.Any(note => note.Contains("metadata")), "Metadata response bounds apply with or without a declared size");
        }
        using (var http = new HttpClient(new FixtureHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Json("[]");
        })))
        using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50)))
        {
            var cancelled = false;
            try { await new VerifiedOnlineDiscoveryProvider(http).SearchAsync(new(DiscoveryInterest.LocalAssistants, "fixture"), cancellation.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            check(cancelled, "User cancellation interrupts public metadata requests instead of returning a fake completed search");
        }
    }

    private static HttpResponseMessage ReplyFor(Uri uri)
    {
        if (uri.Host == "api.github.com") return uri.AbsolutePath.EndsWith("/latest")
            ? Json("{\"tag_name\":\"fixture-release\"}") : Json("{\"license\":{\"spdx_id\":\"MIT\"}}");
        if (uri.AbsolutePath == "/api/models") return Json("[{\"id\":\"Fixture/Model\"}]");
        if (uri.AbsolutePath.Contains("/tree/")) return Json(Tree);
        return Json(ModelInfo);
    }

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private sealed class FixtureHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class NonSeekableStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _stream = new(bytes);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _stream.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => _stream.ReadAsync(buffer, token);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _stream.Dispose(); base.Dispose(disposing); }
    }
}
