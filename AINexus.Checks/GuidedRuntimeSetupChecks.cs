using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using AINexus.App;

namespace AINexus.Checks;

internal static class GuidedRuntimeSetupChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var requests = new List<Uri>();
            var native = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
            var name = $"llama-{GuidedRuntimeSetup.ReleaseTag}-bin-win-cpu-{native}.zip";
            var machine = RuntimeSetupChecks.Machine() with { OsArchitecture = native };
            using var http = new HttpClient(new Handler((request, _) =>
            {
                requests.Add(request.RequestUri!);
                var json = request.RequestUri!.AbsolutePath == "/repos/ggml-org/llama.cpp"
                    ? "{\"license\":{\"spdx_id\":\"MIT\"}}"
                    : Release(name);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(json, Encoding.UTF8, "application/json") });
            }));
            var provider = new VerifiedOnlineDiscoveryProvider(http);
            var plan = await GuidedRuntimeSetup.ReviewDownloadAsync(provider, machine, root);
            check(plan.Files.Release == GuidedRuntimeSetup.ReleaseTag && plan.Files.Files.Count == 1
                && plan.Files.Files[0].Name == name && plan.ArchiveDirectory == root
                && plan.Files.Destination.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                "Guided runtime review selects one explicit native CPU archive and a separate new portable destination");
            check(requests.Count == 2 && requests.All(uri => uri.Host == "api.github.com")
                && requests.Any(uri => uri.AbsolutePath.EndsWith("/releases/tags/" + GuidedRuntimeSetup.ReleaseTag))
                && !requests.Any(uri => uri.AbsolutePath.EndsWith("/latest")),
                "Guided runtime review refreshes the explicit reviewed release without a latest-release fallback");
            check(!Directory.EnumerateFileSystemEntries(root).Any(),
                "Guided runtime review downloads no bytes and creates no application or archive files");
            var second = await GuidedRuntimeSetup.ReviewDownloadAsync(provider, machine, root);
            check(second.Files.Destination != plan.Files.Destination && second.PlanId != plan.PlanId,
                "Each guided runtime review binds its own new no-overwrite installation destination");
            var beforeUnsafe = requests.Count;
            check(await RejectsAsync<ArgumentException>(() => GuidedRuntimeSetup.ReviewDownloadAsync(provider, machine, @"\\server\share"))
                && await RejectsAsync<ArgumentException>(() => GuidedRuntimeSetup.ReviewDownloadAsync(provider, machine, Path.GetPathRoot(root)!))
                && requests.Count == beforeUnsafe,
                "Unsafe or whole-drive runtime folders are rejected before online metadata requests");
            var mismatched = new RuntimeDownloadResult(root, Path.Combine(root, "different.zip"), Path.Combine(root, "receipt.json"), false, DateTimeOffset.UtcNow);
            check(await RejectsAsync<InvalidDataException>(() => GuidedRuntimeSetup.ReviewDownloadedAsync(plan, mismatched))
                && !Directory.Exists(plan.Files.Destination) && !Directory.EnumerateFiles(root).Any(),
                "Guided runtime inspection rejects an unrelated downloaded-path result without extracting or writing files");
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            check(await RejectsAsync<OperationCanceledException>(() => GuidedRuntimeSetup.ReviewDownloadAsync(provider, machine, root, cancelled.Token))
                && await RejectsAsync<OperationCanceledException>(() => GuidedRuntimeSetup.ReviewDownloadedAsync(plan, mismatched, cancelled.Token)),
                "Cancelled guided runtime reviews perform no acquisition or local archive inspection");

            using var unavailable = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));
            check(await RejectsAsync<InvalidOperationException>(() => GuidedRuntimeSetup.ReviewDownloadAsync(new(unavailable), machine, root)),
                "An unavailable explicit CPU release does not silently select another runtime");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string Release(string name) => JsonSerializer.Serialize(new
    {
        tag_name = GuidedRuntimeSetup.ReleaseTag,
        prerelease = false,
        immutable = false,
        assets = new[]
        {
            new { name, size = 1024L, state = "uploaded", digest = "sha256:" + new string('a', 64),
                browser_download_url = "https://github.com/ggml-org/llama.cpp/releases/download/" + GuidedRuntimeSetup.ReleaseTag + "/" + name }
        }
    });

    private static async Task<bool> RejectsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); return false; }
        catch (T) { return true; }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
