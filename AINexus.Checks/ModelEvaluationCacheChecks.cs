using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using AINexus.App;

namespace AINexus.Checks;

internal static class ModelEvaluationCacheChecks
{
    private const string Repository = "Fixture/Model";
    private const string Source = "{\"id\":\"Fixture/Model\",\"evalResults\":[{\"verified\":false,\"pullRequest\":46,\"data\":{\"dataset\":{\"id\":\"Fixture/Benchmark\",\"task_id\":\"task\"},\"value\":42.5,\"date\":\"2026-06-02\",\"source\":{\"url\":\"https://example.com/evidence\"}}}]}";

    internal static async Task RunLiveAsync(string repository, string directory, Action<bool, string> check)
    {
        var cache = new ModelEvaluationCache(directory);
        ModelEvaluationSnapshot live;
        using (var http = VerifiedOnlineDiscoveryProvider.CreateHttpClient())
            live = await new ModelEvaluationClient(http, cache).ReadAsync(repository);
        var reopened = await new ModelEvaluationCache(directory).LoadAsync(repository);
        check(live.Notes.Any(note => note.Contains("saved for offline")) && reopened is { IsCached: true }
            && reopened.ModelRepository == repository && reopened.RetrievedAt == live.RetrievedAt,
            "Real public evaluation snapshot reopens from disk after disposing its network client, without refreshing retrieval time");
        check(reopened is not null && reopened.Results.SequenceEqual(live.Results),
            "Offline reopening preserves actual benchmark/task values, nullable verification, dates and provenance without inferring new scores");
        Console.WriteLine($"Actual saved evidence: {repository}; rows {reopened?.Results.Count}; original retrieval {reopened?.RetrievedAt:O}; offline flag {reopened?.IsCached}.");
    }

    internal static async Task RunAsync(Action<bool, string> check)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "fixtures", "evaluation-cache-" + Guid.NewGuid().ToString("N"));
        var at = DateTimeOffset.Parse("2026-10-20T10:00:00Z");
        try
        {
            var cache = new ModelEvaluationCache(directory);
            check(await cache.LoadAsync(Repository) is null && !Directory.Exists(directory),
                "Missing saved evidence performs no writes, creates no folder and substitutes no online lookup");
            using var source = JsonDocument.Parse(Source);
            await cache.SaveAsync(Repository, source.RootElement, at);
            var saved = await new ModelEvaluationCache(directory).LoadAsync(Repository);
            check(saved is { IsCached: true } && saved.RetrievedAt == at && saved.Results.Single() is { Value: 42.5, SourceVerified: false, PullRequest: 46 }
                && saved.Results.Single().EvaluatedAt == DateTimeOffset.Parse("2026-06-02T00:00:00Z"),
                "Offline evidence survives a new cache instance with original retrieval/date, raw score and unverified PR provenance");
            check(await cache.LoadAsync("Fixture/Other") is null && Directory.GetFiles(directory, "*.json").Length == 1,
                "Saved benchmark snapshots are keyed to exact repository identity without borrowing another model's results");
            var path = Directory.GetFiles(directory, "*.json").Single();
            var before = await File.ReadAllBytesAsync(path);
            using var mismatch = JsonDocument.Parse("{\"id\":\"Other/Model\"}");
            var rejected = false;
            try { await cache.SaveAsync(Repository, mismatch.RootElement, at); } catch (InvalidDataException) { rejected = true; }
            check(rejected && before.SequenceEqual(await File.ReadAllBytesAsync(path)),
                "Mismatched source identity cannot replace a valid saved snapshot");
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            rejected = false;
            try { await cache.SaveAsync(Repository, source.RootElement, at, cancelled.Token); } catch (OperationCanceledException) { rejected = true; }
            check(rejected && before.SequenceEqual(await File.ReadAllBytesAsync(path)) && Directory.GetFiles(directory, "*.tmp").Length == 0,
                "Pre-cancelled evidence saving preserves the previous snapshot and leaves no partial files");
            var unsafePathRejected = false;
            try { _ = new ModelEvaluationCache(Path.GetPathRoot(directory)!); } catch (ArgumentException) { unsafePathRejected = true; }
            check(unsafePathRejected, "A saved-evidence cache cannot target an entire drive root");
            await File.WriteAllTextAsync(path, "{invalid source fixture");
            rejected = false;
            try { await cache.LoadAsync(Repository); } catch (JsonException) { rejected = true; }
            check(rejected && await File.ReadAllTextAsync(path) == "{invalid source fixture",
                "Corrupt saved evidence remains preserved and is not silently replaced with network data");
            await File.WriteAllBytesAsync(path, new byte[ModelEvaluationCache.MaximumCacheBytes + 1]);
            rejected = false;
            try { await cache.LoadAsync(Repository); } catch (InvalidDataException) { rejected = true; }
            check(rejected && new FileInfo(path).Length == ModelEvaluationCache.MaximumCacheBytes + 1,
                "Oversized offline evidence is rejected before parsing and preserved for inspection");
            var requests = 0;
            using var http = new HttpClient(new Handler((request, _) =>
            {
                requests++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Source, Encoding.UTF8, "application/json") });
            }));
            var live = await new ModelEvaluationClient(http, cache).ReadAsync(Repository);
            var offline = await cache.LoadAsync(Repository);
            check(requests == 1 && !live.IsCached && live.Notes.Any(note => note.Contains("saved for offline"))
                && offline is { IsCached: true } && offline.RetrievedAt == live.RetrievedAt,
                "An explicit live lookup saves its exact snapshot; a later offline load makes no second HTTP request or timestamp refresh");
            var marker = Path.Combine(directory, "occupied");
            await File.WriteAllTextAsync(marker, "unrelated file");
            var unsavable = new ModelEvaluationCache(marker);
            var sessionOnly = await new ModelEvaluationClient(http, unsavable).ReadAsync(Repository);
            check(sessionOnly.Results.Count == 1 && sessionOnly.Notes.Any(note => note.Contains("saving failed"))
                && await File.ReadAllTextAsync(marker) == "unrelated file",
                "A cache write failure preserves usable live evidence and unrelated files with an explicit session-only warning");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    internal static void RunNative(Action<bool, string> check)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "fixtures", "evaluation-offline-ui-" + Guid.NewGuid().ToString("N"));
        var window = new MainWindow { GuidedPersistPreferences = false };
        try
        {
            WorkloadDiscoveryChecks.Pump(async () =>
            {
                var cache = new ModelEvaluationCache(directory);
                using var source = JsonDocument.Parse(Source);
                await cache.SaveAsync(Repository, source.RootElement, DateTimeOffset.Parse("2026-10-20T10:00:00Z"));
                window.ModelEvidenceCacheOverride = cache;
                var onlineCalls = 0;
                window.ModelEvidenceReadOverride = (_, _) => { onlineCalls++; throw new InvalidOperationException("Offline action must not call live reader"); };
                var input = (TextBox)window.FindName("ModelEvidenceRepositoryText");
                var details = (TextBox)window.FindName("ModelEvidenceDetailsText");
                input.Text = Repository;
                await window.ReadModelEvidenceAsync(saved: true);
                check(onlineCalls == 0 && details.Text.Contains("SAVED evidence") && details.Text.Contains("Benchmark: Fixture/Benchmark")
                    && details.Text.Contains("Source verification flag: False") && details.Text.Contains("source may have changed"),
                    "Native saved-evidence action shows dated, source-reported benchmark facts without invoking live lookup");
                input.Text = "Fixture/Missing";
                await window.ReadModelEvidenceAsync(saved: true);
                check(onlineCalls == 0 && details.Text.Contains("No saved evidence exists") && details.Text.Contains("No online request was made")
                    && !window.OwnedLlamaServerRunning && ((CheckBox)window.FindName("AcquisitionApprovalCheck")).IsChecked != true,
                    "Missing offline evidence remains unknown and grants neither network fallback nor installation/runtime approval");
            });
        }
        finally { window.Close(); if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => action(request, token);
    }
}
