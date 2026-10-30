using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AINexus.App;

namespace AINexus.Checks;

internal static class ImageSearchCoverageChecks
{
    private const string Revision = "1234567890123456789012345678901234567890";
    private const string Hash = "1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef";

    internal static async Task RunAsync(Action<bool, string> check)
    {
        var requests = new List<Uri>();
        var active = 0;
        var maximumActive = 0;
        var gate = new object();
        using (var http = new HttpClient(new Handler(async (request, token) =>
        {
            var uri = request.RequestUri!;
            lock (gate) requests.Add(uri);
            var current = Interlocked.Increment(ref active);
            lock (gate) maximumActive = Math.Max(maximumActive, current);
            try
            {
                await Task.Delay(5, token);
                if (uri.Host == "api.github.com") return Json("{\"items\":[]}");
                if (uri.AbsolutePath == "/api/models")
                {
                    string[] ids = uri.Query.Contains("sort=downloads", StringComparison.Ordinal)
                        ? ["Fixture/Empty0", "Fixture/Empty1", "Fixture/Empty2", "Fixture/Empty3", "Fixture/StableDiffusion", "Fixture/SDXL", "Fixture/Alternative"]
                        : uri.Query.Contains("sort=trendingScore", StringComparison.Ordinal) ? ["Fixture/QwenImage"] : ["Fixture/Recent"];
                    return Json(JsonSerializer.Serialize(ids.Select(id => new { id })));
                }
                var repository = string.Join('/', uri.AbsolutePath.Split('/').Skip(3).Take(2));
                if (!uri.AbsolutePath.Contains("/tree/", StringComparison.Ordinal)) return Info(repository);
                if (repository.Contains("Empty", StringComparison.Ordinal))
                    return Tree(["unet.safetensors", "text_encoder/model.safetensors"]);
                return Tree(["a.safetensors", "b.safetensors", "c.safetensors", "d.safetensors"]);
            }
            finally { Interlocked.Decrement(ref active); }
        })))
        {
            var snapshot = await new VerifiedOnlineDiscoveryProvider(http).SearchAsync(new(DiscoveryInterest.Images, "", 8));
            var models = snapshot.Items.Where(item => item.Artifact is not null).ToArray();
            var repositories = models.Select(item => item.Artifact!.Repository).ToArray();
            check(models.Length == 6 && repositories.Contains("Fixture/StableDiffusion") && repositories.Contains("Fixture/SDXL")
                && repositories.Contains("Fixture/QwenImage") && repositories.Contains("Fixture/Alternative"),
                "Image search reaches usable diffusion families beyond four component-only popular repositories and combines independent source feeds");
            check(repositories.Take(5).Distinct().Count() == 5 && repositories.Count(id => id == "Fixture/QwenImage") == 2,
                "Image artifact slots go to distinct available repositories before extra variants from a trending source");
            check(requests.Count == 22 && maximumActive is > 1 and <= 4
                && requests.Count(uri => uri.AbsolutePath == "/api/models") == 3
                && requests.All(uri => !uri.AbsolutePath.Contains("/resolve/", StringComparison.Ordinal)),
                "Broader image discovery keeps four concurrent reads and bounded metadata-only requests without downloading model bytes");
        }

        var count = 0;
        using (var http = new HttpClient(new Handler((request, _) =>
        {
            Interlocked.Increment(ref count);
            var uri = request.RequestUri!;
            if (uri.Host == "api.github.com") return Task.FromResult(Json("{\"items\":[]}"));
            if (uri.AbsolutePath == "/api/models")
            {
                if (uri.Query.Contains("sort=trendingScore", StringComparison.Ordinal))
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                var id = uri.Query.Contains("sort=downloads", StringComparison.Ordinal) ? "Fixture/StableDiffusion" : "Fixture/Recent";
                return Task.FromResult(Json(JsonSerializer.Serialize(new[] { new { id } })));
            }
            var repository = string.Join('/', uri.AbsolutePath.Split('/').Skip(3).Take(2));
            return Task.FromResult(uri.AbsolutePath.Contains("/tree/", StringComparison.Ordinal) ? Tree(["a.safetensors"]) : Info(repository));
        })))
        {
            var snapshot = await new VerifiedOnlineDiscoveryProvider(http).SearchAsync(new(DiscoveryInterest.Images, "", 8));
            check(snapshot.Items.Count(item => item.Artifact is not null) == 2 && count == 8
                && snapshot.Notes.Any(note => note.Contains("trendingScore unavailable", StringComparison.Ordinal)),
                "An unavailable image feed is disclosed while healthy popular and recent sources remain visible");
        }

        var boundedRequests = new List<Uri>();
        using (var http = new HttpClient(new Handler((request, _) =>
        {
            var uri = request.RequestUri!;
            lock (boundedRequests) boundedRequests.Add(uri);
            if (uri.Host == "api.github.com") return Task.FromResult(Json("{\"items\":[]}"));
            if (uri.AbsolutePath == "/api/models")
            {
                var prefix = uri.Query.Contains("sort=downloads", StringComparison.Ordinal) ? "Popular"
                    : uri.Query.Contains("sort=trendingScore", StringComparison.Ordinal) ? "Trending" : "Recent";
                return Task.FromResult(Json(JsonSerializer.Serialize(Enumerable.Range(0, 100).Select(index => new { id = $"Fixture/{prefix}{index}" }))));
            }
            var repository = string.Join('/', uri.AbsolutePath.Split('/').Skip(3).Take(2));
            return Task.FromResult(uri.AbsolutePath.Contains("/tree/", StringComparison.Ordinal) ? Tree(["a.safetensors"]) : Info(repository));
        })))
        {
            var snapshot = await new VerifiedOnlineDiscoveryProvider(http).SearchAsync(new(DiscoveryInterest.Images, "", 12));
            check(boundedRequests.Count == 52 && boundedRequests.Count(uri => uri.AbsolutePath.Contains("/tree/", StringComparison.Ordinal)) == 24
                && snapshot.Items.Count(item => item.Artifact is not null) == 10,
                "Three disjoint over-returning image feeds cannot exceed twenty-four inspected repositories or fifty-two metadata requests");
        }
        await CheckCacheTransitionAsync(check);
    }

    private static async Task CheckCacheTransitionAsync(Action<bool, string> check)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "fixtures", "image-coverage-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var request = new OnlineDiscoveryRequest(DiscoveryInterest.Images, "");
            var previousKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{request.Interest}\n{request.Query}\n{request.Limit}\nworkflow-task-metadata-v3\n{request.SearchOrder}")));
            var previousPath = Path.Combine(directory, previousKey + ".json");
            var oldSnapshot = new OnlineDiscoverySnapshot([], ["Earlier bounded image search"], DateTimeOffset.UtcNow);
            var previousBytes = JsonSerializer.SerializeToUtf8Bytes(new { Version = 1, Request = request, Snapshot = oldSnapshot });
            await System.IO.File.WriteAllBytesAsync(previousPath, previousBytes);
            var cache = new DiscoveryCacheStore(directory);
            check(await cache.LoadAsync(request) is null && System.IO.File.ReadAllBytes(previousPath).SequenceEqual(previousBytes),
                "Expanded image search does not reopen or delete prior four-repository cache evidence");
            var current = oldSnapshot with { Notes = ["Expanded image source evidence"] };
            await cache.SaveAsync(request, current);
            var reopened = await cache.LoadAsync(request);
            check(reopened is { IsCached: true } && reopened.Notes.SequenceEqual(current.Notes)
                && System.IO.File.ReadAllBytes(previousPath).SequenceEqual(previousBytes),
                "New image coverage evidence roundtrips offline under its own key while earlier saved results remain preserved");
        }
        finally
        {
            var fixtures = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "fixtures")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(directory).StartsWith(fixtures, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unsafe image cache fixture cleanup.");
            Directory.Delete(directory, recursive: true);
        }
    }

    internal static async Task RunImageCoverageAsync(Action<bool, string> check)
    {
        using var http = VerifiedOnlineDiscoveryProvider.CreateHttpClient();
        var snapshot = await new VerifiedOnlineDiscoveryProvider(http).SearchAsync(new(DiscoveryInterest.Images, "", 12));
        var models = snapshot.Items.Where(item => item.Artifact is not null).ToArray();
        check(models.Length >= 2 && models.Select(item => item.Publisher).Distinct(StringComparer.Ordinal).Count() >= 2,
            "The live blank Images search returns model artifacts from multiple source publishers rather than a single pinned family");
        check(models.All(item => item.Interest == DiscoveryInterest.Images && item.PipelineTag == "text-to-image"
                && item.Artifact is { Format: "Safetensors", Revision.Length: 40 })
            && models.Count(item => item.Artifact is { SizeBytes: > 0, Sha256.Length: 64 }) >= 2,
            "The live image results include exact source bytes and hashes while preserving missing facts and excluding text-assistant artifacts");
        foreach (var model in models) Console.WriteLine($"Live image artifact: {model.Artifact!.Repository}/{model.Artifact.FileName}");
        var diffusion = models.Any(item => item.Artifact!.Repository.Equals("stable-diffusion-v1-5/stable-diffusion-v1-5", StringComparison.Ordinal));
        var sdxl = models.Any(item => item.Artifact!.Repository.Equals("stabilityai/stable-diffusion-xl-base-1.0", StringComparison.Ordinal));
        Console.WriteLine($"Live current source coverage: official Stable Diffusion v1.5={diffusion}; official SDXL base={sdxl}. Presence is source-dependent, not a permanent model registry.");
        foreach (var note in snapshot.Notes) Console.WriteLine("Live image source note: " + note);
        var reports = Path.Combine(AppContext.BaseDirectory, "Reports", "ImageSearchCoverage");
        Directory.CreateDirectory(reports);
        await System.IO.File.WriteAllTextAsync(Path.Combine(reports, "last-run.json"), JsonSerializer.Serialize(new
        {
            Snapshot = snapshot, OfficialStableDiffusion15Present = diffusion, OfficialSdxlBasePresent = sdxl,
            Limitations = "Public live metadata only. No weight bytes acquired, no image engine started and no model quality or runtime compatibility inferred."
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static HttpResponseMessage Info(string id) => Json(JsonSerializer.Serialize(new
    { id, sha = Revision, gated = false, pipeline_tag = "text-to-image", cardData = new { license = "fixture-license" } }));
    private static HttpResponseMessage Tree(string[] files) => Json(JsonSerializer.Serialize(files.Select(path => new
    { type = "file", path, size = 1024, lfs = new { oid = Hash, size = 1024 } })));
    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token); }
}
