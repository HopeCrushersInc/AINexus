using System.IO;
using AINexus.App;

namespace AINexus.Checks;

internal static class DiscoveryPipelineChecks
{
    internal static async Task RunLiveAsync(Action<bool, string> check)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var machine = await HardwareCapabilityScanner.ScanAsync(deadline.Token);
        var target = machine.Volumes.Single(volume => string.Equals(volume.Path,
            Path.GetPathRoot(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase));
        var folder = Path.Combine(AppContext.BaseDirectory, "Reports", "Discovery");
        using var http = VerifiedOnlineDiscoveryProvider.CreateHttpClient();
        var pipeline = new DiscoveryPipeline(new VerifiedOnlineDiscoveryProvider(http), new DiscoveryCacheStore(Path.Combine(folder, "Cache")));
        var request = new OnlineDiscoveryRequest(DiscoveryInterest.LocalAssistants, "Qwen2.5-1.5B-Instruct-GGUF", 3);
        var report = await pipeline.SearchOnlineAsync(request, machine, target.Path, token: deadline.Token);
        var candidates = report.Recommendations.Where(result => result.Item.Artifact is not null).ToArray();
        check(candidates.Length > 0 && candidates.All(result => result.Item.Sources.Count > 0),
            "Live discovery pipeline evaluates source-backed model artifacts against the actual machine and selected volume");
        check(candidates.All(result => result.Reasons.Count > 0 && result.Limitations.Any(note => note.Contains("unverified resource estimate"))),
            "Live recommendations explain their evidence and never turn estimates into verified execution claims");
        var saved = await pipeline.LoadSavedAsync(request, machine, target.Path, token: deadline.Token);
        check(saved.Catalog.IsCached && saved.Catalog.Items.Select(item => item.Id).SequenceEqual(report.Catalog.Items.Select(item => item.Id)),
            "Live discovery evidence survives offline catalog reload without another online search");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "last-live-report.json");
        await File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(report,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), deadline.Token);
        foreach (var result in candidates)
            Console.WriteLine($"Live assessment: {result.Item.Name}: {result.Status}; {result.ExecutionPath}; estimated memory {result.EstimatedMemoryGiB:N2} GiB; storage target {target.Path}");
        Console.WriteLine($"Saved live discovery evidence: {path}");
    }

    internal static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var request = new OnlineDiscoveryRequest(DiscoveryInterest.LocalAssistants, "fixture");
            var snapshot = FixtureCatalog();
            var provider = new FixtureProvider(snapshot);
            var cache = new DiscoveryCacheStore(root);
            var pipeline = new DiscoveryPipeline(provider, cache);
            var machine = FixtureMachine();
            var report = await pipeline.SearchOnlineAsync(request, machine, "C:\\");
            check(provider.Calls == 1 && report.Catalog.Items.Count == 1 && !report.Catalog.IsCached,
                "Online discovery composes source metadata and local evaluation without claiming cached data is fresh");
            check(report.Recommendations.Count == 1 && report.Recommendations[0].Status == RecommendationStatus.ResourceCandidate,
                "Complete source evidence and sufficient fixture resources produce an unverified resource candidate");
            var offline = await pipeline.LoadSavedAsync(request, machine, "C:\\");
            check(provider.Calls == 1 && offline.Catalog.IsCached && offline.Catalog.RetrievedAt == snapshot.RetrievedAt,
                "Saved-search review makes no provider call and preserves the original retrieval timestamp");
            check(offline.Catalog.Items[0].Artifact == snapshot.Items[0].Artifact,
                "Offline catalog preserves revision, actual size and checksum evidence");
            var busy = machine with { Resources = machine.Resources with { RamUsedGb = machine.Resources.RamTotalGb, Gpus = [] } };
            var reevaluated = await pipeline.LoadSavedAsync(request, busy, "C:\\");
            check(reevaluated.Recommendations[0].Status == RecommendationStatus.InsufficientResources,
                "Saved source evidence is evaluated against current supplied resources rather than a cached fit verdict");
            var otherDrive = await pipeline.LoadSavedAsync(request, machine, "Z:\\");
            check(otherDrive.Recommendations[0].Status == RecommendationStatus.NeedsDetails,
                "An unavailable selected volume cannot silently borrow capacity from another drive");
            var missing = await pipeline.LoadSavedAsync(request with { Query = "different fixture" }, machine, "C:\\");
            check(missing.Catalog.Items.Count == 0 && provider.Calls == 1 && missing.Catalog.IsCached,
                "Missing saved searches do not silently issue online requests");

            var cacheFile = Directory.GetFiles(root, "*.json").Single();
            var runtime = new RuntimeReleaseMetadata("fixture", false, false,
                [new("runtime.zip", 123, new string('a', 64), new Uri("https://github.com/ggml-org/llama.cpp/releases/download/fixture/runtime.zip"))],
                ["Fixture metadata, not an installed runtime."]);
            await cache.SaveAsync(request, snapshot with { Items = [snapshot.Items[0] with { RuntimeRelease = runtime }] });
            check((await cache.LoadAsync(request))?.Items[0].RuntimeRelease?.Archives[0].SizeBytes == 123,
                "Saved discovery catalogs preserve runtime archive identity alongside model evidence");
            await cache.SaveAsync(request, snapshot with { Items = [snapshot.Items[0] with
                { RuntimeRelease = runtime with { Archives = [runtime.Archives[0] with { DownloadUrl = new Uri("https://example.com/runtime.zip") }] } }] });
            var unsafeRuntimeRejected = false;
            try { await cache.LoadAsync(request); } catch (InvalidDataException) { unsafeRuntimeRejected = true; }
            check(unsafeRuntimeRejected, "Saved runtime metadata cannot introduce a foreign download origin on reload");
            File.WriteAllText(cacheFile, "{invalid fixture");
            var corruptRejected = false;
            try { await cache.LoadAsync(request); }
            catch (System.Text.Json.JsonException) { corruptRejected = true; }
            check(corruptRejected && File.ReadAllText(cacheFile) == "{invalid fixture",
                "Corrupt evidence is rejected and preserved rather than presented as a valid saved catalog");

            var marker = Path.Combine(root, "not-a-cache-directory");
            File.WriteAllText(marker, "Unrelated fixture");
            var unsaved = await new DiscoveryPipeline(provider, new DiscoveryCacheStore(marker)).SearchOnlineAsync(request, machine, "C:\\");
            check(unsaved.Catalog.Items.Count == 1 && unsaved.Catalog.Notes.Any(note => note.Contains("session only"))
                && File.ReadAllText(marker) == "Unrelated fixture", "Cache write failures preserve live results and unrelated files");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var cancelled = false;
            try { await pipeline.SearchOnlineAsync(request, machine, "C:\\", token: cancellation.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            check(cancelled, "Discovery cancellation propagates without turning into a success report");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    internal static MachineCapabilities FixtureMachine() => new(
        new("Fixture CPU", 16, [], [new("Fixture drive", 30, 50)]),
        new(DateTimeOffset.Now, 10, 2, 16, [], [], "All processors"),
        "Microsoft Windows fixture", "X64", 4, true, true, [],
        [new("C:\\", 30L * 1_073_741_824, 50L * 1_073_741_824)], DateTimeOffset.Now, []);

    internal static OnlineDiscoverySnapshot FixtureCatalog() => new(
        [new("fixture/model@revision:fixture.gguf", "Fixture model", "Fixture publisher", DiscoveryInterest.LocalAssistants, "Model", "apache-2.0",
            new("fixture/model", new string('a', 40), "fixture.gguf", 1_073_741_824, new string('b', 64), "GGUF", false),
            [new("Fixture source metadata", new Uri("https://huggingface.co/fixture/model"), DateTimeOffset.Parse("2025-01-01T00:00:00Z"))],
            "Deterministic fixture metadata; not a real online result.", "text-generation")], [], DateTimeOffset.Parse("2025-01-01T00:00:00Z"));

    private sealed class FixtureProvider(OnlineDiscoverySnapshot snapshot) : IOnlineDiscoveryProvider
    {
        public int Calls { get; private set; }
        public Task<OnlineDiscoverySnapshot> SearchAsync(OnlineDiscoveryRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(snapshot);
        }
    }
}
