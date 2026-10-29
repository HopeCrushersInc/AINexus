using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using AINexus.App;

namespace AINexus.Checks;

internal static class TaskTextDiscoveryChecks
{
    private const string Revision = "1234567890123456789012345678901234567890";
    private const string Hash = "1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef";

    internal static async Task RunAsync(Action<bool, string> check)
    {
        await PurposeAsync(DiscoveryInterest.Coding, check);
        await PurposeAsync(DiscoveryInterest.Agents, check);
        var requests = new List<Uri>();
        using (var http = new HttpClient(new Handler((request, _) =>
        {
            requests.Add(request.RequestUri!);
            return Task.FromResult(request.RequestUri!.AbsolutePath == "/api/models"
                ? Json("[{\"id\":\"Fixture/Unlabelled\",\"tags\":[\"function-calling\",\"tool-use\",\"tool-calling\"]},{\"id\":\"Fixture/PlainChat\"}]") : Reply(request.RequestUri));
        })))
        {
            var result = await new VerifiedOnlineDiscoveryProvider(http).SearchAsync(new(DiscoveryInterest.Agents, "my & model", 3, SearchOrder: TextSearchOrder.Popular));
            var queries = requests.Where(uri => uri.AbsolutePath == "/api/models").ToArray();
            check(result.Items.Count == 2 && requests.Count == 5 && queries.Length == 3
                && queries.All(uri => uri.Query.Contains("search=my%20%26%20model", StringComparison.Ordinal)
                    && RoleTag(uri) is "function-calling" or "tool-use" or "tool-calling")
                && requests.All(uri => uri.AbsolutePath != "/api/models/Fixture/PlainChat")
                && result.Notes.Any(note => note.Contains("Explicit search text is used unchanged")),
                "Explicit agent search text stays literal while conjoined with source-purpose filters; unlabelled chat is not substituted");
            requests.Clear();
            var exact = await new VerifiedOnlineDiscoveryProvider(http).SearchAsync(new(DiscoveryInterest.Coding, "Fixture/Unlabelled", 3));
            check(exact.Items.Count == 2 && requests.Count == 2 && requests.All(uri => uri.AbsolutePath != "/api/models"),
                "An explicit repository is inspected directly even when task-purpose labels are absent");
        }
        var codingQueries = new List<Uri>();
        using (var http = new HttpClient(new Handler((request, _) =>
        {
            codingQueries.Add(request.RequestUri!);
            return Task.FromResult(request.RequestUri!.AbsolutePath == "/api/models"
                ? Json("[{\"id\":\"Fixture/Model-Coder-32B\",\"tags\":[\"code\",\"coding\"]},{\"id\":\"Fixture/PlainChat-32B\"},{\"id\":\"Fixture/Encoder-32B\"}]")
                : Reply(request.RequestUri));
        })))
        {
            var result = await new VerifiedOnlineDiscoveryProvider(http).SearchAsync(new(DiscoveryInterest.Coding, "32B", 12, SearchOrder: TextSearchOrder.Popular));
            var queries = codingQueries.Where(uri => uri.AbsolutePath == "/api/models").ToArray();
            check(queries.Length == 3 && queries.All(uri => uri.Query.Contains("search=32B", StringComparison.Ordinal))
                && result.Items.Count == 2 && result.Items.All(item => item.Artifact?.Repository == "Fixture/Model-Coder-32B")
                && !codingQueries.Any(uri => uri.AbsolutePath.Contains("PlainChat", StringComparison.Ordinal) || uri.AbsolutePath.Contains("Encoder", StringComparison.Ordinal)),
                "Coding plus 32B keeps the literal size query and coding-purpose conjunction instead of returning arbitrary 32B chat or encoder repositories");
        }

        foreach (var interest in new[] { DiscoveryInterest.Coding, DiscoveryInterest.Agents })
        {
            var calls = 0;
            using var emptyHttp = new HttpClient(new Handler((request, _) =>
            {
                calls++;
                return Task.FromResult(Json("[]"));
            }));
            var empty = await new VerifiedOnlineDiscoveryProvider(emptyHttp).SearchAsync(new(interest, "", 12));
            check(calls == 9 && empty.Items.Count == 0 && empty.Notes.Any(note => note.Contains("no general-chat fallback")),
                $"Empty {interest} purpose feeds stay empty instead of substituting general chat models or runtimes");
        }

        var bounded = 0;
        using (var http = new HttpClient(new Handler((request, _) =>
        {
            Interlocked.Increment(ref bounded);
            var uri = request.RequestUri!;
            return Task.FromResult(uri.AbsolutePath == "/api/models"
                ? Json(JsonSerializer.Serialize(Enumerable.Range(1, 50).Select(index => new
                    { id = "Fixture/Model" + index, tags = new[] { "function-calling", "tool-use", "tool-calling" } })))
                : Reply(uri));
        })))
        {
            var result = await new VerifiedOnlineDiscoveryProvider(http).SearchAsync(new(DiscoveryInterest.Agents, "", 48));
            check(bounded == 57 && result.Items.Count == 48
                && result.Items.Select(item => item.Artifact!.Repository).Distinct().Count() == 24,
                "Task-specific broad coverage remains bounded to nine lists, twenty-four repositories and fifty-seven metadata requests");
        }
    }

    private static async Task PurposeAsync(DiscoveryInterest interest, Action<bool, string> check)
    {
        var requests = new List<Uri>();
        var requestGate = new object();
        var inFlight = 0; var maximum = 0;
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            var uri = request.RequestUri!;
            lock (requestGate) requests.Add(uri);
            var active = Interlocked.Increment(ref inFlight);
            lock (requestGate) maximum = Math.Max(maximum, active);
            try
            {
                await Task.Delay(2, token);
                if (uri.AbsolutePath != "/api/models") return Reply(uri);
                var tag = RoleTag(uri);
                var repository = tag is null ? "Fixture/Model-Coder-7B" : "Fixture/" + tag;
                if (interest == DiscoveryInterest.Agents && tag == "tool-use") return new(HttpStatusCode.ServiceUnavailable);
                return Json(JsonSerializer.Serialize(new[]
                {
                    new { id = repository, tags = tag is null ? Array.Empty<string>() : new[] { tag } },
                    new { id = "Fixture/Generic", tags = new[] { "text-generation", "gguf" } },
                    new { id = "Fixture/Encoder", tags = new[] { "text-generation" } },
                    new { id = "CoderOwner/GenericModel", tags = new[] { "text-generation" } },
                    new { id = "Fixture/StarCoder2", tags = Array.Empty<string>() },
                    new { id = "Fixture/Decoder", tags = Array.Empty<string>() }
                }));
            }
            finally { Interlocked.Decrement(ref inFlight); }
        }));
        var result = await new VerifiedOnlineDiscoveryProvider(http).SearchAsync(new(interest, "", 12));
        var expected = interest == DiscoveryInterest.Coding
            ? new[] { "Fixture/code", "Fixture/coding", "Fixture/Model-Coder-7B", "Fixture/StarCoder2" }
            : new[] { "Fixture/function-calling", "Fixture/tool-calling" };
        check(requests.Count(uri => uri.AbsolutePath == "/api/models") == 9 && maximum is >= 2 and <= 4
            && result.Items.Select(item => item.Artifact!.Repository).Distinct().SequenceEqual(expected),
            $"Default {interest} search interleaves its explicit source-purpose scopes within four concurrent reads");
        check(result.Items.All(item => item.Kind == "Model" && item.Description.Contains("Source-purpose evidence")
                && item.Description.Contains("not pinned capability"))
            && requests.All(uri => uri.Host == "huggingface.co")
            && !requests.Any(uri => uri.AbsolutePath is "/api/models/Fixture/Generic" or "/api/models/Fixture/Encoder" or "/api/models/Fixture/Decoder" or "/api/models/CoderOwner/GenericModel"),
            $"{interest} rejects unlabelled listings and encoder/owner-name false positives before reading files; relevance facts are not capability claims");
        var lists = requests.Where(uri => uri.AbsolutePath == "/api/models").ToArray();
        check(lists.All(uri => uri.Query.Contains("filter=gguf") && uri.Query.Contains("pipeline_tag=text-generation"))
            && (interest != DiscoveryInterest.Coding || lists.Count(uri => uri.Query.Contains("search=coder")) == 3)
            && (interest != DiscoveryInterest.Agents || lists.All(uri => RoleTag(uri) is "function-calling" or "tool-use" or "tool-calling")),
            $"{interest} source queries keep GGUF/text task restrictions and the intended source-purpose tags or literal coder label");
        if (interest == DiscoveryInterest.Agents)
            check(result.Items.Count > 0 && result.Notes.Any(note => note.Contains("feed unavailable")),
                "A failed agent-purpose feed preserves successful matching-purpose results without a general-chat fallback");
    }

    internal static async Task RunLiveAsync(Action<bool, string> check)
    {
        foreach (var interest in new[] { DiscoveryInterest.Coding, DiscoveryInterest.Agents })
        {
            using var http = VerifiedOnlineDiscoveryProvider.CreateHttpClient();
            var result = await new VerifiedOnlineDiscoveryProvider(http).SearchAsync(new(interest, "", 3, SearchOrder: TextSearchOrder.Popular));
            check(result.Items.Count > 0 && result.Items.All(item => item.Kind == "Model" && item.Description.Contains("Source-purpose evidence"))
                && result.Notes.Any(note => note.Contains("Task-relevant source scopes")),
                $"Live {interest} source-purpose search returned actual model-file choices with explicit relevance facts; no weights downloaded or capability certified");
        }
    }

    private static string? RoleTag(Uri uri) => uri.Query.Contains("filter=function-calling") ? "function-calling"
        : uri.Query.Contains("filter=tool-calling") ? "tool-calling" : uri.Query.Contains("filter=tool-use") ? "tool-use"
        : uri.Query.Contains("filter=coding") ? "coding" : uri.Query.Contains("filter=code") ? "code" : null;
    private static HttpResponseMessage Reply(Uri uri) => uri.AbsolutePath.Contains("/tree/")
        ? Json("[{\"type\":\"file\",\"path\":\"a.gguf\",\"size\":1024,\"lfs\":{\"oid\":\"" + Hash + "\"}},{\"type\":\"file\",\"path\":\"b.gguf\",\"size\":2048,\"lfs\":{\"oid\":\"" + Hash + "\"}}]")
        : Json("{\"id\":\"" + uri.AbsolutePath["/api/models/".Length..] + "\",\"sha\":\"" + Revision
            + "\",\"gated\":false,\"pipeline_tag\":\"text-generation\",\"cardData\":{\"license\":\"apache-2.0\"}}");
    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => reply(request, token);
    }
}
