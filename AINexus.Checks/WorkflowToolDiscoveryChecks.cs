using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using AINexus.App;

namespace AINexus.Checks;

internal static class WorkflowToolDiscoveryChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var calls = new List<HttpRequestMessage>();
        var fixture = "{\"incomplete_results\":true,\"items\":["
            + "{\"full_name\":\"Fixture/AudioTool\",\"description\":\"Actual public source description\",\"license\":{\"spdx_id\":\"MIT\"}},"
            + "{\"full_name\":\"Fixture/UnknownLicense\",\"license\":{\"spdx_id\":\"NOASSERTION\"}},"
            + "{\"full_name\":\"../unsafe\",\"description\":\"Do not trust this path\"},"
            + "{\"full_name\":\"Fixture/AudioTool\"}]}";
        using var http = new HttpClient(new Handler((request, _) =>
        {
            calls.Add(request);
            return Task.FromResult(Json(fixture));
        }));
        var provider = new VerifiedOnlineDiscoveryProvider(http);
        check(calls.Count == 0, "Constructing workflow discovery never automatically searches or acquires tools");
        var result = await provider.SearchWorkflowToolsAsync(new(DiscoveryInterest.MusicAudio, "composer & x?limit=99", 4));
        check(calls.Count == 1 && calls[0].Method == HttpMethod.Get && calls[0].Headers.Authorization is null
            && calls[0].RequestUri?.Host == "api.github.com" && calls[0].RequestUri?.AbsolutePath == "/search/repositories",
            "Workflow tool discovery makes one unauthenticated metadata GET to the fixed repository search endpoint");
        var requestUrl = calls[0].RequestUri!.OriginalString;
        check(requestUrl.Contains("composer%20%26%20x%3Flimit%3D99%20AI%20music%20generation", StringComparison.Ordinal)
            && requestUrl.Contains("per_page=4", StringComparison.Ordinal),
            "User query and chosen music workflow are escaped as one bounded source search value");
        check(!requestUrl.Contains("hardware", StringComparison.OrdinalIgnoreCase) && !requestUrl.Contains("F%3A", StringComparison.OrdinalIgnoreCase)
            && calls[0].Content is null,
            "Workflow search sends no local hardware, filesystem paths or file contents");
        check(result.Items.Count == 2 && result.Items[0].Name == "Fixture/AudioTool"
            && result.Items[0].Description == "Actual public source description" && result.Items[0].License == "MIT"
            && result.Items.All(item => item.Kind == "Tool" && item.Artifact is null && item.Interest == DiscoveryInterest.MusicAudio),
            "Repository names, descriptions and licenses come from source metadata without inventing model artifacts or compatibility");
        check(result.Items[1].License is null && result.Items.All(item => item.Sources.Single().Url.Host == "github.com")
            && result.Items.All(item => item.Sources.Single().RetrievedAt == result.RetrievedAt) && !result.IsCached,
            "Unknown source licensing remains unknown and live source timestamps remain explicit");
        check(result.Notes.Any(note => note.Contains("not compatibility")) && result.Notes.Any(note => note.Contains("incomplete")),
            "Workflow results disclose unsupported installation/compatibility and source-marked partial search results");

        foreach (var workflow in new[] { DiscoveryInterest.Images, DiscoveryInterest.Video, DiscoveryInterest.Speech,
            DiscoveryInterest.ThreeD, DiscoveryInterest.Cad, DiscoveryInterest.ThreeDPrinting,
            DiscoveryInterest.GameDevelopment, DiscoveryInterest.ReverseEngineering, DiscoveryInterest.Streaming })
        {
            var before = calls.Count;
            var tools = await provider.SearchWorkflowToolsAsync(new(workflow, "", 1));
            check(calls.Count == before + 1 && tools.Items.All(item => item.Interest == workflow),
                "Each non-text category performs its own explicit source search without selecting a model or installing a tool");
        }
        using var invalid = new HttpClient(new Handler((_, _) => Task.FromResult(Json("[]"))));
        check(await RejectsAsync<InvalidDataException>(() => new VerifiedOnlineDiscoveryProvider(invalid)
                .SearchWorkflowToolsAsync(new(DiscoveryInterest.Images, "", 1))),
            "Malformed workflow source responses are rejected rather than fabricated as tool results");
        using var limited = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests))));
        check(await RejectsAsync<HttpRequestException>(() => new VerifiedOnlineDiscoveryProvider(limited)
                .SearchWorkflowToolsAsync(new(DiscoveryInterest.ReverseEngineering, "Ghidra", 1))),
            "Workflow source rate limits remain actual errors and do not produce cached or invented success");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var lastCount = calls.Count;
        check(await RejectsAsync<OperationCanceledException>(() => provider.SearchWorkflowToolsAsync(new(DiscoveryInterest.MusicAudio, ""), cancelled.Token))
            && calls.Count == lastCount,
            "Cancelled workflow discovery makes no request");
        check(await RejectsAsync<ArgumentException>(() => provider.SearchWorkflowToolsAsync(new(DiscoveryInterest.Images, "", 1, "specific-release")))
            && calls.Count == lastCount,
            "Workflow discovery cannot replace an explicitly named runtime release with a generic tool search");
    }

    private static async Task<bool> RejectsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); return false; }
        catch (T) { return true; }
    }
    private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK)
        { Content = new StringContent(content, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => handle(request, token);
    }
}
