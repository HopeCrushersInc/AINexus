using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using AINexus.App;

namespace AINexus.Checks;

internal static class RuntimeReleaseLookupChecks
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(send(request));
    }
    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };

    public static async Task RunAsync(Action<bool, string> check)
    {
        var paths = new List<string>();
        using var http = new HttpClient(new Handler(request =>
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            return Json(paths.Count == 1 ? "{\"license\":{\"spdx_id\":\"MIT\"}}" : "{\"tag_name\":\"fixture\",\"assets\":[]}");
        }));
        var provider = new VerifiedOnlineDiscoveryProvider(http);
        var request = new OnlineDiscoveryRequest(DiscoveryInterest.LocalAssistants, "", 1, "fixture");
        var result = await provider.SearchAsync(request);
        check(result.Items.Single().RuntimeRelease?.Tag == "fixture" && paths.SequenceEqual(new[]
            { "/repos/ggml-org/llama.cpp", "/repos/ggml-org/llama.cpp/releases/tags/fixture" }),
            "Explicit runtime lookup makes only bounded repository/tag metadata requests, without model search or latest fallback");
        var before = paths.Count;
        var rejected = false;
        try { await provider.SearchAsync(request with { RuntimeReleaseTag = "../other?tag=x" }); } catch (ArgumentException) { rejected = true; }
        check(rejected && paths.Count == before, "Unsafe explicit release tags are rejected before any network request");
        var controlRejected = false;
        try { await provider.SearchAsync(request with { RuntimeReleaseTag = "fixture\n" }); } catch (ArgumentException) { controlRejected = true; }
        check(controlRejected && paths.Count == before, "A trailing control character cannot bypass explicit release tag validation");
        var wrongPaths = new List<string>();
        using var wrong = new HttpClient(new Handler(r =>
        {
            wrongPaths.Add(r.RequestUri!.AbsolutePath);
            return Json(r.RequestUri.AbsolutePath.EndsWith("/fixture")
                ? "{\"tag_name\":\"substitute\"}" : "{\"license\":{\"spdx_id\":\"MIT\"}}");
        }));
        var mismatch = await new VerifiedOnlineDiscoveryProvider(wrong).SearchAsync(request);
        check(mismatch.Items.Count == 0 && mismatch.Notes.Any(n => n.Contains("unavailable"))
            && wrongPaths.Count == 2 && wrongPaths.All(path => !path.EndsWith("/latest")),
            "An upstream response for a different release cannot substitute for the user's explicit choice");
        var missingPaths = new List<string>();
        using var missing = new HttpClient(new Handler(r =>
        {
            missingPaths.Add(r.RequestUri!.AbsolutePath);
            return missingPaths.Count == 1 ? Json("{\"license\":{\"spdx_id\":\"MIT\"}}") : new(HttpStatusCode.NotFound);
        }));
        var unavailable = await new VerifiedOnlineDiscoveryProvider(missing).SearchAsync(request);
        check(unavailable.Items.Count == 0 && unavailable.Notes.Any(n => n.Contains("HTTP 404")) && missingPaths.Count == 2,
            "Unavailable explicit releases report failure without retrying latest or inventing archives");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var cancellationPropagated = false;
        try { await provider.SearchAsync(request, canceled.Token); } catch (OperationCanceledException) { cancellationPropagated = true; }
        check(cancellationPropagated && paths.Count == before, "Canceled explicit release lookups do not issue metadata requests");
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new DiscoveryCacheStore(root);
            var ordinary = request with { RuntimeReleaseTag = null };
            await cache.SaveAsync(ordinary, DiscoveryPipelineChecks.FixtureCatalog());
            await cache.SaveAsync(request, result);
            await cache.SaveAsync(request with { RuntimeReleaseTag = "other" }, result);
            check(Directory.GetFiles(root, "*.json").Length == 3
                && (await cache.LoadAsync(ordinary))!.Items[0].Kind == "Model"
                && (await cache.LoadAsync(request))!.Items[0].RuntimeRelease?.Tag == "fixture",
                "Explicit runtime releases have distinct saved-catalog keys and preserve existing model-search caches");
            var cacheRejected = false;
            try { await cache.LoadAsync(request with { RuntimeReleaseTag = "other" }); } catch (InvalidDataException) { cacheRejected = true; }
            check(cacheRejected, "Saved runtime metadata for another tag cannot replace an explicitly requested release");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    public static async Task RunLiveAsync(string tag, Action<bool, string> check)
    {
        using var http = VerifiedOnlineDiscoveryProvider.CreateHttpClient();
        var request = new OnlineDiscoveryRequest(DiscoveryInterest.LocalAssistants, "", 1, tag);
        var snapshot = await new VerifiedOnlineDiscoveryProvider(http).SearchAsync(request);
        var item = snapshot.Items.SingleOrDefault();
        check(item?.RuntimeRelease?.Tag == tag, "Live explicit runtime lookup returns the requested upstream release rather than latest");
        var machine = await HardwareCapabilityScanner.ScanAsync();
        var volume = Path.GetPathRoot(AppContext.BaseDirectory)!;
        var report = DiscoveryPipeline.Evaluate(snapshot, machine, volume);
        var runtime = item!.RuntimeRelease!;
        var candidates = runtime.Archives.Select(a => RuntimeSetupPlanner.Assess(a, tag, machine))
            .Where(a => a.Status == RuntimeArchiveStatus.PlatformMatch && a.Role == "Runtime archive" && a.Backend == "cpu").ToArray();
        check(runtime.Archives.Count > 0 || runtime.Notes.Any(note => note.Contains("no supported published")),
            "Live named release reports actual archive availability without downloading or inventing binaries");
        var folder = Path.Combine(AppContext.BaseDirectory, "Reports", "RuntimeRelease");
        Directory.CreateDirectory(folder);
        var destination = Path.Combine(folder, "UncreatedRuntime-" + Guid.NewGuid().ToString("N"));
        if (candidates.Length > 0)
        {
            var plan = RuntimeFilePlanner.Create(report, item.Id, destination, candidates[0].Asset.Name);
            check(plan.Files.Count == 1 && !Directory.Exists(destination), "Live exact CPU file review uses actual source/hardware evidence and never installs the runtime");
            await File.WriteAllTextAsync(Path.Combine(folder, "last-file-review.json"), JsonSerializer.Serialize(plan, new JsonSerializerOptions { WriteIndented = true }));
        }
        await File.WriteAllTextAsync(Path.Combine(folder, "last-release.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Live explicit release: {tag}; archives: {runtime.Archives.Count}; native CPU labels: {candidates.Length}.");
    }
}
