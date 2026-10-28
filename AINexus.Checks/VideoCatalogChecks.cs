using System.IO;
using AINexus.App;

namespace AINexus.Checks;

internal static class VideoCatalogChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "fixtures", "video-catalog-" + Guid.NewGuid().ToString("N"));
        var revision = new string('a', 40);
        var now = DateTimeOffset.UtcNow;
        var item = new DiscoveredItem("huggingface-repository:Fixture/Video@" + revision, "Fixture/Video", "Fixture",
            DiscoveryInterest.Video, "Model", "fixture-license", null,
            [new("Pinned source card", new Uri("https://huggingface.co/Fixture/Video/blob/" + revision + "/README.md"), now)],
            "Synthetic video repository; complete inventory and runtime not established.", "text-to-video");
        try
        {
            var store = new CatalogDatabase(Path.Combine(folder, "catalog.db"));
            await store.UpsertDiscoveryAsync(new([item], [], now));
            var saved = (await store.ReadAsync()).Single().Item;
            check(saved.Id == item.Id && saved.Artifact is null && saved.PipelineTag == "text-to-video",
                "Video repository candidates persist without inventing a downloadable model file or aggregate size");
            var result = HardwareCompatibilityEngine.Evaluate(saved, DiscoveryPipelineChecks.FixtureMachine(), "C:\\");
            check(result.Status == RecommendationStatus.NeedsDetails && result.EstimatedMemoryGiB is null && result.RequiredStorageGiB is null,
                "Saved video pipelines remain unassessed without borrowing GGUF text-model memory or storage recipes");
            var rejected = false;
            try { _ = CatalogDatabase.CanonicalId(item with { Sources = [new("Unpinned", new Uri("https://huggingface.co/Fixture/Video/blob/main/README.md"), now)] }); }
            catch (InvalidDataException) { rejected = true; }
            check(rejected, "Video repository catalog identity rejects a mutable branch in place of a pinned source revision");
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
    }

    internal static async Task RunLiveAsync(Action<bool, string> check)
    {
        using var http = VerifiedOnlineDiscoveryProvider.CreateHttpClient();
        var snapshot = await new VerifiedOnlineDiscoveryProvider(http).SearchAsync(new(DiscoveryInterest.Video, ""));
        var models = snapshot.Items.Where(item => item.Kind == "Model").ToArray();
        check(models.Length > 0 && models.All(item => item.Artifact is null && (item.PipelineTag is "text-to-video" or "image-to-video")
                && CatalogDatabase.CanonicalId(item).StartsWith("huggingface-repository:", StringComparison.Ordinal)),
            "Live video discovery returns real pinned repository candidates from video tasks without fabricating complete weight files");
        check(DiscoveryPipeline.Evaluate(snapshot, DiscoveryPipelineChecks.FixtureMachine(), "C:\\").Recommendations
                .All(result => result.Status == RecommendationStatus.NeedsDetails && result.EstimatedMemoryGiB is null),
            "Live video repositories and tools remain explicitly unverified for runtime, installation and hardware fit");
        foreach (var model in models) Console.WriteLine($"Actual video repository: {model.Name} | {model.PipelineTag} | {model.Sources[0].Url}");
        foreach (var note in snapshot.Notes) Console.WriteLine("Video source note: " + note);
    }
}
