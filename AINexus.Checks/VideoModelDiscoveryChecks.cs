using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using AINexus.App;

namespace AINexus.Checks;

internal static class VideoModelDiscoveryChecks
{
    private const string Revision = "1234567890123456789012345678901234567890";

    internal static async Task RunAsync(Action<bool, string> check)
    {
        var requests = new List<Uri>();
        using (var http = new HttpClient(new Handler((request, _) =>
        {
            requests.Add(request.RequestUri!);
            if (request.RequestUri!.AbsolutePath == "/api/models")
                return Task.FromResult(request.RequestUri.Query.Contains("pipeline_tag=text-to-video")
                    ? Json("[{\"id\":\"Fixture/Text\"},{\"id\":\"Fixture/Shared\"},{\"id\":\"Fixture/Extra\"}]")
                    : Json("[{\"id\":\"Fixture/Image\"},{\"id\":\"Fixture/Shared\"}]"));
            return Task.FromResult(Reply(request.RequestUri));
        })))
        {
            var result = await new VerifiedOnlineDiscoveryProvider(http).SearchAsync(new(DiscoveryInterest.Video, "motion & x?limit=99"));
            var models = result.Items.Where(item => item.Kind == "Model").ToArray();
            check(models.Select(item => item.Name).SequenceEqual(new[] { "Fixture/Text", "Fixture/Image", "Fixture/Shared", "Fixture/Extra" })
                && requests.Count(uri => uri.AbsolutePath == "/api/models/Fixture/Shared") == 1,
                "Video discovery interleaves text-to-video and image-to-video repository sources and deduplicates repeated identities");
            check(models.All(item => item.Interest == DiscoveryInterest.Video && item.Artifact is null
                    && item.PipelineTag is "text-to-video" or "image-to-video"
                    && item.Id == "huggingface-repository:" + item.Name + "@" + Revision
                    && item.Sources.Single().Url.AbsoluteUri.Contains("/blob/" + Revision + "/README.md")),
                "Video source candidates retain pinned repository provenance without fabricating a complete model artifact or download size");
            check(models.All(item => item.Description.Contains("unverified")
                    && HardwareCompatibilityEngine.Evaluate(item, DiscoveryPipelineChecks.FixtureMachine(), "C:\\") is
                        { Status: RecommendationStatus.NeedsDetails, EstimatedMemoryGiB: null, RequiredStorageGiB: null }),
                "Video repository facts never inherit text-model memory estimates, image compatibility or installation support");
            check(result.Items.Any(item => item.Kind == "Tool") && requests.Count == 7
                && requests.Count(uri => uri.AbsolutePath == "/api/models") == 2
                && requests.All(uri => !uri.AbsolutePath.Contains("/tree/") && !uri.AbsolutePath.Contains("/resolve/"))
                && requests.Where(uri => uri.AbsolutePath == "/api/models").All(uri => uri.OriginalString.Contains("search=motion%20%26%20x%3Flimit%3D99")),
                "Video discovery stays metadata-only, escapes query terms and includes a bounded tool search without reading file trees or weights");
        }

        requests.Clear();
        using (var http = new HttpClient(new Handler((request, _) =>
        {
            requests.Add(request.RequestUri!);
            return Task.FromResult(Reply(request.RequestUri!));
        })))
        {
            var result = await new VerifiedOnlineDiscoveryProvider(http).SearchAsync(new(DiscoveryInterest.Video, "Fixture/Text"));
            check(requests.Count == 1 && result.Items.Single() is { Kind: "Model", Artifact: null, Name: "Fixture/Text" }
                && result.Notes.Any(note => note.Contains("No broad search or alternative")),
                "An exact video repository lookup reads only that repository and never substitutes a broad result or tool");
        }

        foreach (var invalid in new[]
        {
            Info("Other/Model", "text-to-video"),
            Info("Fixture/Text", "text-generation"),
            Info("Fixture/Text", "text-to-video").Replace(Revision, "main", StringComparison.Ordinal),
            Info("Fixture/Text", "text-to-video").Replace("\"private\":false", "\"private\":true", StringComparison.Ordinal)
        })
        {
            using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Json(invalid))));
            var result = await new VerifiedOnlineDiscoveryProvider(http).SearchAsync(new(DiscoveryInterest.Video, "Fixture/Text"));
            check(result.Items.Count == 0 && result.Notes.Any(note => note.Contains("could not be inspected")),
                "Wrong identity, task, mutable revision or private metadata cannot become a public video model candidate");
        }

        using (var http = new HttpClient(new Handler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/models")
                return Task.FromResult(request.RequestUri.Query.Contains("pipeline_tag=text-to-video")
                    ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    : Json("[{\"id\":\"Fixture/Image\"}]"));
            return Task.FromResult(Reply(request.RequestUri));
        })))
        {
            var result = await new VerifiedOnlineDiscoveryProvider(http).SearchAsync(new(DiscoveryInterest.Video, ""));
            check(result.Items.Any(item => item.Name == "Fixture/Image") && result.Notes.Any(note => note.Contains("text-to-video source unavailable")),
                "Failure of one video task source preserves successful repositories from the other task");
        }

        var budget = 0;
        using (var http = new HttpClient(new Handler((request, _) =>
        {
            budget++;
            if (request.RequestUri!.AbsolutePath == "/api/models")
            {
                var prefix = request.RequestUri.Query.Contains("pipeline_tag=text-to-video") ? "Text" : "Image";
                return Task.FromResult(Json(JsonSerializer.Serialize(Enumerable.Range(1, 50).Select(index => new { id = "Fixture/" + prefix + index }))));
            }
            return Task.FromResult(Reply(request.RequestUri));
        })))
        {
            var result = await new VerifiedOnlineDiscoveryProvider(http).SearchAsync(new(DiscoveryInterest.Video, ""));
            check(budget == 11 && result.Items.Count(item => item.Kind == "Model") == 8 && result.Items.Count <= 12,
                "Video metadata discovery inspects no more than eight repositories across two four-item feeds and eleven total requests");
        }

        using (var http = new HttpClient(new Handler((_, _) => Task.FromResult(Json(Info("Fixture/Text", "text-to-video")
            .Replace("\"gated\":false", "\"gated\":true", StringComparison.Ordinal).Replace("\"license\":\"apache-2.0\"", "\"license\":null", StringComparison.Ordinal))))))
        {
            var result = await new VerifiedOnlineDiscoveryProvider(http).SearchAsync(new(DiscoveryInterest.Video, "Fixture/Text"));
            check(result.Items.Single() is { Artifact: null, License: null }
                && result.Items.Single().Description.Contains("gated or unconfirmed"),
                "Video access gates and unknown licenses remain explicit metadata rather than implicit download authorization");
        }
    }

    private static HttpResponseMessage Reply(Uri uri)
    {
        if (uri.Host == "api.github.com") return Json("{\"items\":[{\"full_name\":\"Fixture/VideoTool\",\"description\":\"Video generation tool fixture\",\"license\":{\"spdx_id\":\"MIT\"}}]}");
        var repository = uri.AbsolutePath["/api/models/".Length..];
        return Json(Info(repository, repository.Contains("Image", StringComparison.Ordinal) ? "image-to-video" : "text-to-video"));
    }

    private static string Info(string repository, string task) => JsonSerializer.Serialize(new
    {
        id = repository, sha = Revision, pipeline_tag = task, @private = false, gated = false,
        cardData = new { license = "apache-2.0" }, downloads = 123, likes = 4
    });

    private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK)
    { Content = new StringContent(content, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => handle(request, token);
    }
}
