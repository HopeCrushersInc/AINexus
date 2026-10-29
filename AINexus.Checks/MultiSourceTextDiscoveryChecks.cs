using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using AINexus.App;

namespace AINexus.Checks;

internal static class MultiSourceTextDiscoveryChecks
{
    private const string Revision = "1234567890123456789012345678901234567890";
    private const string Hash = "1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef";

    internal static async Task RunAsync(Action<bool, string> check)
    {
        var requests = new List<Uri>();
        var requestGate = new object();
        var inFlight = 0;
        var maximumInFlight = 0;
        using (var http = new HttpClient(new Handler(async (request, token) =>
        {
            var uri = request.RequestUri!;
            lock (requestGate) requests.Add(uri);
            var active = Interlocked.Increment(ref inFlight);
            lock (requestGate) maximumInFlight = Math.Max(maximumInFlight, active);
            try
            {
                await Task.Delay(5, token);
                if (uri.AbsolutePath == "/api/models")
                {
                    var ids = uri.Query.Contains("sort=downloads") ? new[] { "Fixture/Popular", "Fixture/Shared", "Fixture/DownloadOnly" }
                        : uri.Query.Contains("sort=trendingScore") ? ["Fixture/Trend", "Fixture/Shared"]
                        : new[] { "Fixture/New", "Fixture/Shared" };
                    return Json(JsonSerializer.Serialize(ids.Select(id => new { id, downloads = 999999 })));
                }
                return Reply(uri);
            }
            finally { Interlocked.Decrement(ref inFlight); }
        })))
        {
            var result = await new VerifiedOnlineDiscoveryProvider(http).SearchAsync(new(DiscoveryInterest.LocalAssistants, ""));
            var models = result.Items.Where(item => item.Artifact is not null).ToArray();
            check(requests.Count(uri => uri.AbsolutePath == "/api/models") == 3
                && models.Take(5).Select(item => item.Artifact!.Repository).SequenceEqual(new[]
                    { "Fixture/Popular", "Fixture/Trend", "Fixture/New", "Fixture/Shared", "Fixture/DownloadOnly" }),
                "Broad text discovery interleaves popular, trending and recently modified source lists instead of hiding new sources behind a single ranking");
            check(models.Length == 10 && models.Select(item => item.Id).Distinct().Count() == models.Length
                && requests.Count(uri => uri.AbsolutePath == "/api/models/Fixture/Shared") == 1,
                "Cross-feed repository deduplication avoids repeat metadata reads while preserving two distinct file variants per source");
            check(requests.Where(uri => uri.AbsolutePath == "/api/models").All(uri =>
                uri.Query.Contains("filter=gguf") && uri.Query.Contains("pipeline_tag=text-generation"))
                && maximumInFlight is >= 2 and <= 4,
                "Text feeds explicitly request text-generation GGUF metadata with bounded concurrent reads");
            check(models.All(item => item.Signals is { Downloads: 10, Likes: 2, ParameterCount: 32_000_000_000 }
                    && item.Signals.UpdatedAt == DateTimeOffset.Parse("2026-10-20T12:00:00Z"))
                && result.Notes.Any(note => note.Contains("not a quality rating"))
                && result.Notes.Any(note => note.Contains("not the full model catalog")),
                "Displayed signals use inspected repository metadata rather than ranking-list claims and remain separate from quality judgments");
        }

        using (var http = new HttpClient(new Handler((request, _) =>
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath == "/api/models") return Task.FromResult(uri.Query.Contains("sort=trendingScore")
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : Json("[{\"id\":\"Fixture/Good\"},{\"id\":\"Fixture/ChangedTask\"}]"));
            if (uri.AbsolutePath == "/api/models/Fixture/ChangedTask")
                return Task.FromResult(Json(Info("Fixture/ChangedTask").Replace("text-generation", "text-to-image")));
            return Task.FromResult(Reply(uri));
        })))
        {
            var result = await new VerifiedOnlineDiscoveryProvider(http).SearchAsync(new(DiscoveryInterest.LocalAssistants, "explicit fixture query"));
            check(result.Items.Any(item => item.Artifact?.Repository == "Fixture/Good")
                && result.Items.All(item => item.Artifact?.Repository != "Fixture/ChangedTask")
                && result.Notes.Any(note => note.Contains("trendingScore feed unavailable"))
                && result.Notes.Any(note => note.Contains("different task")),
                "One failed feed and a changed source task preserve successful text results without passing image artifacts through the text recipe");
        }

        var boundedRequests = 0;
        using (var http = new HttpClient(new Handler((request, _) =>
        {
            Interlocked.Increment(ref boundedRequests);
            return Task.FromResult(request.RequestUri!.AbsolutePath == "/api/models"
                ? Json(JsonSerializer.Serialize(Enumerable.Range(1, 50).Select(index => new { id = $"Fixture/Model{index}" })))
                : Reply(request.RequestUri));
        })))
        {
            var result = await new VerifiedOnlineDiscoveryProvider(http).SearchAsync(new(DiscoveryInterest.LocalAssistants, "explicit fixture query", 48));
            check(result.Items.Count == 48 && result.Items.Count(item => item.Artifact is not null) == 48
                && result.Items.Where(item => item.Artifact is not null).Select(item => item.Artifact!.Repository).Distinct().Count() == 24
                && boundedRequests == 51 && result.Items.All(item => item.Kind == "Model"),
                "Expanded text discovery fills all forty-eight slots with models from at most twenty-four repositories and fifty-one metadata requests");
        }

        using (var http = new HttpClient(new Handler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath == "/api/models"
                ? Json("[{\"id\":\"Fixture/Unknown\"}]")
                : request.RequestUri.AbsolutePath == "/api/models/Fixture/Unknown"
                    ? Json(Info("Fixture/Unknown").Replace("\"downloads\":10,\"likes\":2,\"lastModified\":\"2026-10-20T12:00:00Z\",\"gguf\":{\"total\":32000000000}",
                        "\"downloads\":-1,\"likes\":\"bad\",\"lastModified\":\"not a date\",\"gguf\":{\"total\":0}"))
                    : Reply(request.RequestUri)))))
        {
            var result = await new VerifiedOnlineDiscoveryProvider(http).SearchAsync(new(DiscoveryInterest.LocalAssistants, "", SearchOrder: TextSearchOrder.RecentlyUpdated));
            check(result.Items.Any(item => item.Artifact is not null)
                && result.Items.Where(item => item.Artifact is not null).All(item => item.Signals is
                    { Downloads: null, Likes: null, UpdatedAt: null, ParameterCount: null }),
                "Invalid source counts, dates and parameter totals remain unknown instead of being guessed from model names");
        }
    }

    private static string Info(string repository) => "{\"id\":\"" + repository + "\",\"sha\":\"" + Revision
        + "\",\"gated\":false,\"pipeline_tag\":\"text-generation\",\"cardData\":{\"license\":\"apache-2.0\"},"
        + "\"downloads\":10,\"likes\":2,\"lastModified\":\"2026-10-20T12:00:00Z\",\"gguf\":{\"total\":32000000000}}";

    private static HttpResponseMessage Reply(Uri uri)
    {
        if (uri.Host == "api.github.com") return Json(uri.AbsolutePath.EndsWith("/latest")
            ? "{\"tag_name\":\"fixture-release\"}" : "{\"license\":{\"spdx_id\":\"MIT\"}}");
        if (uri.AbsolutePath.Contains("/tree/")) return Json("["
            + "{\"type\":\"file\",\"path\":\"a.gguf\",\"size\":1024,\"lfs\":{\"oid\":\"" + Hash + "\"}},"
            + "{\"type\":\"file\",\"path\":\"b.gguf\",\"size\":2048,\"lfs\":{\"oid\":\"" + Hash + "\"}}]");
        return Json(Info(uri.AbsolutePath["/api/models/".Length..]));
    }

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => reply(request, token);
    }
}
