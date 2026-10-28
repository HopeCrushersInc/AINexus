using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using AINexus.App;

namespace AINexus.Checks;

internal static class ImageArtifactDiscoveryChecks
{
    private const string Revision = "1234567890123456789012345678901234567890";
    private const string Hash = "1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef";

    internal static async Task RunLiveAsync(string query, Action<bool, string> check)
    {
        using var http = VerifiedOnlineDiscoveryProvider.CreateHttpClient();
        var snapshot = await new VerifiedOnlineDiscoveryProvider(http).SearchAsync(new(DiscoveryInterest.Images, query));
        var models = snapshot.Items.Where(item => item.Artifact is not null).ToArray();
        check(models.Length > 0 && models.All(item => item.Interest == DiscoveryInterest.Images && item.PipelineTag == "text-to-image"
            && item.Artifact is { Format: "Safetensors", SizeBytes: > 0, Sha256: not null }),
            "The explicitly queried live image source returns pinned artifacts with actual bytes and SHA-256 metadata");
        var machine = await HardwareCapabilityScanner.ScanAsync();
        var volume = machine.Volumes.FirstOrDefault()?.Path ?? Path.GetPathRoot(AppContext.BaseDirectory)!;
        var report = DiscoveryPipeline.Evaluate(snapshot, machine, volume);
        check(report.Recommendations.All(result => result.Status == RecommendationStatus.NeedsDetails && result.EstimatedMemoryGiB is null),
            "Live image artifact metadata remains unassessed against actual hardware without a fabricated memory rule or runtime proof");
        var output = Path.Combine(AppContext.BaseDirectory, "Reports", "ImageDiscovery");
        Directory.CreateDirectory(output);
        await System.IO.File.WriteAllTextAsync(Path.Combine(output, "last-metadata-search.json"), JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Image metadata only: {models.Length} artifact candidates; {snapshot.Items.Count(item => item.Kind == "Tool")} tool listings. No weights, runtime or installers acquired.");
    }

    internal static async Task RunAsync(Action<bool, string> check)
    {
        var requests = new List<Uri>();
        var metadataOnly = true;
        var info = Info("Fixture/Image");
        var tree = JsonSerializer.Serialize(new object[]
        {
            File("candidate-a.safetensors", 1024, Hash), File("candidate-b.safetensors", 2048),
            File("candidate-c.safetensors", null), File("model-00001-of-00002.safetensors", 100),
            File("unet.safetensors", 100), File("adapter_model.safetensors", 100), File("lora.safetensors", 100),
            File("diffusion_pytorch_model.safetensors", 100), File("nested/model.safetensors", 100),
            File("../outside.safetensors", 100), File("CON.safetensors", 100), File("bad:name.safetensors", 100),
            File("bad#name.safetensors", 100), File("candidate-a.safetensors", 1024, Hash),
            new { type = "file", path = "conflicting.safetensors", size = 1024, lfs = new { oid = Hash, size = 2048 } }
        });
        using var http = new HttpClient(new Handler((request, _) =>
        {
            requests.Add(request.RequestUri!);
            metadataOnly &= request.Method == HttpMethod.Get && request.Content is null
                && request.Headers.Authorization is null && !request.Headers.Contains("Cookie")
                && !request.RequestUri!.AbsolutePath.Contains("/resolve/", StringComparison.Ordinal);
            return Task.FromResult(Reply(request.RequestUri!, info, tree));
        }));
        var provider = new VerifiedOnlineDiscoveryProvider(http);
        check(requests.Count == 0, "Constructing image discovery performs no query, selection or engine action");
        var snapshot = await provider.SearchAsync(new(DiscoveryInterest.Images, "Fixture/Image", 8));
        var models = snapshot.Items.Where(item => item.Artifact is not null).ToArray();
        check(models.Length == 3 && snapshot.Items.Count(item => item.Kind == "Tool") == 2
            && snapshot.Items.All(item => item.Interest == DiscoveryInterest.Images),
            "Image discovery keeps source-backed model candidates and tool repositories as distinct result types");
        check(requests.Count == 3 && !requests.Any(uri => uri.Host == "huggingface.co" && uri.AbsolutePath == "/api/models")
            && requests.Single(uri => uri.AbsolutePath.Contains("/tree/")).Query.Contains("recursive=false", StringComparison.Ordinal),
            "An exact image repository is inspected directly without fuzzy replacement or recursive component discovery");
        check(models[0].Artifact is { Repository: "Fixture/Image", Revision: Revision, FileName: "candidate-a.safetensors",
                SizeBytes: 1024, Sha256: Hash, Format: "Safetensors", Gated: false }
            && models[0].License == "fixture-license" && models[0].PipelineTag == "text-to-image",
            "Image artifacts preserve exact source revision, file, bytes, SHA-256, format, task, license and access evidence");
        check(models.All(item => item.Sources.All(source => source.Url.AbsoluteUri.Contains(Revision, StringComparison.Ordinal)))
            && models[1].Artifact!.Sha256 is null && models[2].Artifact!.SizeBytes is null,
            "Pinned image provenance survives while missing hashes and byte counts remain unknown rather than zero");
        check(snapshot.Notes.Any(note => note.Contains("component", StringComparison.Ordinal))
            && snapshot.Notes.Any(note => note.Contains("conflicting byte", StringComparison.Ordinal))
            && snapshot.Notes.Any(note => note.Contains("unsafe or nested", StringComparison.Ordinal))
            && snapshot.Notes.Any(note => note.Contains("Duplicate", StringComparison.Ordinal)),
            "Image metadata excludes obvious components, shards, duplicates, conflicting sizes and unsafe paths with explanations");
        check(models.All(item => item.Description.Contains("does not prove", StringComparison.Ordinal))
            && snapshot.Notes.Any(note => note.Contains("source metadata", StringComparison.Ordinal)
                && note.Contains("fit-only filter", StringComparison.Ordinal)) && metadataOnly,
            "Image discovery discloses unproven completeness and its explicit fit-only filter, using metadata GETs only");
        var machine = DiscoveryPipelineChecks.FixtureMachine();
        var report = DiscoveryPipeline.Evaluate(snapshot, machine, machine.Volumes[0].Path);
        check(report.Recommendations.All(result => result.Status == RecommendationStatus.NeedsDetails
                && result.EstimatedMemoryGiB is null && result.RequiredStorageGiB is null),
            "Neither image file size nor an image task label invokes GGUF memory rules or fabricates hardware-fit matches");
        var savedRoot = Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(savedRoot);
        try
        {
            var cache = new DiscoveryCacheStore(Path.Combine(savedRoot, "cache"));
            var request = new OnlineDiscoveryRequest(DiscoveryInterest.Images, "Fixture/Image");
            await cache.SaveAsync(request, snapshot);
            var saved = await cache.LoadAsync(request);
            var store = new CatalogDatabase(Path.Combine(savedRoot, "catalog.db"));
            await store.UpsertDiscoveryAsync(snapshot);
            var catalog = await store.ReadAsync(interest: DiscoveryInterest.Images);
            check(saved is { IsCached: true } && saved.Items.Where(item => item.Artifact is not null).Select(item => item.Artifact)
                    .SequenceEqual(models.Select(item => item.Artifact))
                && catalog.Count == snapshot.Items.Count && catalog.Count(item => item.Item.Artifact is not null) == 3,
                "Generic image source artifacts round-trip through saved discovery and SQLite without a featured-model registry or schema replacement");
        }
        finally
        {
            if (!Path.GetFullPath(savedRoot).StartsWith(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "fixtures"))
                + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe image fixture cleanup.");
            Directory.Delete(savedRoot, recursive: true);
        }

        requests.Clear();
        info = Info("Fixture/Image", gated: true, license: null);
        snapshot = await provider.SearchAsync(new(DiscoveryInterest.Images, "Fixture/Image", 1));
        check(snapshot.Items.Single().Artifact!.Gated && snapshot.Items.Single().License is null
            && requests.Count == 2 && snapshot.Notes.Any(note => note.Contains("gated or unconfirmed", StringComparison.Ordinal)),
            "Gated access and missing license are exposed as source limitations without sign-in, substitution or a supporting-tool overflow");
        info = Info("Other/Repository");
        snapshot = await provider.SearchAsync(new(DiscoveryInterest.Images, "Fixture/Image", 1));
        check(snapshot.Items.Count == 0 && snapshot.Notes.Any(note => note.Contains("source unavailable", StringComparison.OrdinalIgnoreCase)),
            "A mismatched image repository identity cannot be accepted or replaced by another model");
        requests.Clear();
        info = Info("Fixture/Image", pipeline: "text-generation");
        snapshot = await provider.SearchAsync(new(DiscoveryInterest.Images, "Fixture/Image", 1));
        check(snapshot.Items.Count == 0 && requests.Count == 1,
            "A non-image repository cannot become a checkpoint solely because its files have a safetensors extension");
        info = Info("Fixture/Image");
        tree = JsonSerializer.Serialize(new object[]
        {
            File("Colorado.safetensors", 1024, Hash), File("LoRAmerge.safetensors", 1024, Hash),
            File("conflicting-size.safetensors", 1024, Hash), File("conflicting-size.safetensors", 2048, Hash),
            File("conflicting-hash.safetensors", 1024, Hash), File("conflicting-hash.safetensors", 1024, new string('f', 64)),
            File("conflicting-hash.safetensors", 1024, Hash), File("identical.safetensors", 1024, Hash), File("identical.safetensors", 1024, Hash)
        });
        snapshot = await provider.SearchAsync(new(DiscoveryInterest.Images, "Fixture/Image", 8));
        models = snapshot.Items.Where(item => item.Artifact is not null).ToArray();
        check(models.Select(item => item.Artifact!.FileName).Order().SequenceEqual(new[]
            { "Colorado.safetensors", "LoRAmerge.safetensors", "identical.safetensors" }.Order()),
            "Image candidates are not excluded by embedded LoRA text while exact duplicate facts are deduplicated and conflicting size/hash identities are removed");
        check(snapshot.Notes.Any(note => note.Contains("Conflicting duplicate", StringComparison.Ordinal))
            && !models.Any(item => item.Artifact!.FileName.StartsWith("conflicting-", StringComparison.Ordinal)),
            "Later duplicate source facts cannot restore an artifact after its identity was contradicted");
        await BroadBudgetAsync(check);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var before = requests.Count;
        var cancellationObserved = false;
        try { await provider.SearchAsync(new(DiscoveryInterest.Images, "", 8), cancelled.Token); }
        catch (OperationCanceledException) { cancellationObserved = true; }
        check(cancellationObserved && requests.Count == before, "Cancelled image discovery makes no metadata request");
    }

    private static async Task BroadBudgetAsync(Action<bool, string> check)
    {
        var requests = new List<Uri>();
        var tree = JsonSerializer.Serialize(Enumerable.Range(0, 30).Select(index => File($"candidate-{index:D2}.safetensors", 1024, Hash)));
        using var http = new HttpClient(new Handler((request, _) =>
        {
            var uri = request.RequestUri!; requests.Add(uri);
            if (uri.Host == "huggingface.co" && uri.AbsolutePath == "/api/models")
                return Task.FromResult(Json(JsonSerializer.Serialize(Enumerable.Range(0, 100).Select(index => new { id = "Fixture/Image" + index }))));
            var repository = uri.AbsolutePath.Split('/').Skip(3).Take(2);
            return Task.FromResult(Reply(uri, Info(string.Join('/', repository)), tree));
        }));
        var result = await new VerifiedOnlineDiscoveryProvider(http).SearchAsync(new(DiscoveryInterest.Images, "art & size=99", 8));
        check(result.Items.Count <= 8 && result.Items.Count(item => item.Artifact is not null) == 6 && requests.Count == 8,
            "Over-returning image sources remain bounded to four repositories, per-repository file shares and the combined model/tool result limit");
        check(requests[0].Query.Contains("pipeline_tag=text-to-image", StringComparison.Ordinal)
            && requests[0].AbsoluteUri.Contains("art%20%26%20size%3D99", StringComparison.Ordinal)
            && requests.All(uri => uri.Host is "huggingface.co" or "api.github.com")
            && result.Items.All(item => item.Artifact is null || item.Artifact.Repository.StartsWith("Fixture/Image", StringComparison.Ordinal)),
            "Broad image queries use the chosen text with public task filtering and no embedded preferred provider or model");
    }

    private static Dictionary<string, object?> File(string name, long? bytes, string? hash = null)
    {
        var value = new Dictionary<string, object?> { ["type"] = "file", ["path"] = name, ["size"] = bytes };
        if (hash is not null) value["lfs"] = new { oid = hash, size = bytes };
        return value;
    }
    private static string Info(string id, bool gated = false, string? license = "fixture-license", string pipeline = "text-to-image")
        => JsonSerializer.Serialize(new { id, sha = Revision, gated, pipeline_tag = pipeline, cardData = new { license } });
    private static HttpResponseMessage Reply(Uri uri, string info, string tree)
    {
        if (uri.Host == "api.github.com") return Json("{\"items\":[{\"full_name\":\"Fixture/ImageTool\"},{\"full_name\":\"Fixture/AnotherTool\"}]}");
        return Json(uri.AbsolutePath.Contains("/tree/", StringComparison.Ordinal) ? tree : info);
    }
    private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK)
        { Content = new StringContent(content, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => callback(request, token); }
}
