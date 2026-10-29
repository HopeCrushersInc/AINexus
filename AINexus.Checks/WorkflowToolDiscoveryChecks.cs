using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using AINexus.App;

namespace AINexus.Checks;

internal static class WorkflowToolDiscoveryChecks
{
    internal static async Task RunLiveAsync(Action<bool, string> check)
    {
        foreach (var interest in new[] { DiscoveryInterest.Cad, DiscoveryInterest.ThreeDPrinting })
        {
            using var http = VerifiedOnlineDiscoveryProvider.CreateHttpClient();
            var result = await new VerifiedOnlineDiscoveryProvider(http).SearchWorkflowToolsAsync(new(interest, "", 3));
            check(result.Items.Count > 0 && result.Items.All(item => item.Interest == interest && item.Kind == "Tool" && item.Artifact is null)
                && result.Notes.Any(note => note.Contains("Search role:")),
                $"Live {interest} search returns task-labelled tool sources only; no installer or model artifacts are invented");
        }
    }
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var calls = new List<HttpRequestMessage>();
        var fixture = "{\"incomplete_results\":true,\"items\":["
            + "{\"full_name\":\"Fixture/AudioTool\",\"description\":\"Actual public audio creation source description\",\"license\":{\"spdx_id\":\"MIT\"}},"
            + "{\"full_name\":\"Fixture/UnknownLicense\",\"topics\":[\"audio\"],\"license\":{\"spdx_id\":\"NOASSERTION\"}},"
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
        var scopedQuery = Query(calls[0].RequestUri!);
        check(scopedQuery == "\"composer\" \"&\" \"x?limit=99\" audio in:name,description,topics archived:false fork:false"
            && requestUrl.Contains("per_page=4", StringComparison.Ordinal),
            "User query and chosen music workflow are escaped as one bounded source search value");
        check(!requestUrl.Contains("hardware", StringComparison.OrdinalIgnoreCase) && !requestUrl.Contains("F%3A", StringComparison.OrdinalIgnoreCase)
            && calls[0].Content is null,
            "Workflow search sends no local hardware, filesystem paths or file contents");
        check(result.Items.Count == 2 && result.Items[0].Name == "Fixture/AudioTool"
            && result.Items[0].Description == "Actual public audio creation source description" && result.Items[0].License == "MIT"
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
            check(calls.Count == before + 1 && tools.Items.Count == 0 && tools.Notes.Any(note => note.Contains("Excluded")),
                "A different workflow excludes returned audio-only sources instead of relabeling them for the selected task");
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
        await VerifyRelevanceAsync(check);
    }

    private static async Task VerifyRelevanceAsync(Action<bool, string> check)
    {
        foreach (var (interest, terms, description) in new[]
        {
            (DiscoveryInterest.Images, "image generation", "Image generation and editing"),
            (DiscoveryInterest.MusicAudio, "audio", "Music and audio processing"),
            (DiscoveryInterest.Video, "video generation", "Video generation tool"),
            (DiscoveryInterest.Speech, "speech", "Speech recognition only"),
            (DiscoveryInterest.ThreeD, "3d modeling", "3D modeling and mesh editing"),
            (DiscoveryInterest.Cad, "topic:cad", "Parametric CAD design"),
            (DiscoveryInterest.ThreeDPrinting, "topic:3d-printing", "3D printing slicer and printer tools"),
            (DiscoveryInterest.GameDevelopment, "game engine", "Game engine development"),
            (DiscoveryInterest.ReverseEngineering, "\"reverse engineering\"", "Reverse engineering and decompiler"),
            (DiscoveryInterest.Streaming, "topic:live-streaming", "Live streaming and broadcasting")
        })
        {
            var requests = new List<Uri>();
            using var http = new HttpClient(new Handler((request, _) =>
            {
                requests.Add(request.RequestUri!);
                return Task.FromResult(Json(JsonSerializer.Serialize(new
                {
                    items = new object[]
                    {
                        new { full_name = "Fixture/TaskTool", description, archived = false, disabled = false, fork = false },
                        new { full_name = "Fixture/Irrelevant", description = "Streaming database and message queue utilities" },
                        new { full_name = "Fixture/Archived", description, archived = true },
                        new { full_name = "Fixture/Disabled", description, disabled = true },
                        new { full_name = "Fixture/Fork", description, fork = true }
                    }
                })));
            }));
            var result = await new VerifiedOnlineDiscoveryProvider(http).SearchWorkflowToolsAsync(new(interest, "", 5));
            check(requests.Count == 1 && Query(requests[0]) == terms + " in:name,description,topics archived:false fork:false"
                && result.Items.Count == 1 && result.Items[0].Id == "github-tool:Fixture/TaskTool" && result.Items[0].Interest == interest,
                $"{interest} uses its own fixed task scope and excludes unrelated, archived, disabled and fork sources");
            check(result.Notes.Any(note => note.Contains("Search role:")) && result.Notes.Any(note => note.Contains("not independently verified")),
                $"{interest} exposes the source query and metadata relevance without inventing tool capability or hardware fit");
        }
        Uri? sent = null;
        using var literalHttp = new HttpClient(new Handler((request, _) => { sent = request.RequestUri; return Task.FromResult(Json("{\"items\":[]}")); }));
        var provider = new VerifiedOnlineDiscoveryProvider(literalHttp);
        await provider.SearchWorkflowToolsAsync(new(DiscoveryInterest.Cad, "fork:true archived:true OR audio", 1));
        check(sent is not null && Query(sent) == "\"fork:true\" \"archived:true\" \"OR\" \"audio\" topic:cad in:name,description,topics archived:false fork:false",
            "User words that resemble source qualifiers/operators remain quoted literals and cannot replace the task restrictions");
        var before = sent;
        check(await RejectsAsync<ArgumentException>(() => provider.SearchWorkflowToolsAsync(new(DiscoveryInterest.Cad, "\" escape"))) && sent == before,
            "Unsupported literal quotation syntax is rejected before sending a broader or substituted source query");
    }

    private static string Query(Uri uri) => Uri.UnescapeDataString(uri.Query[3..].Split('&')[0]);

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
