using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AINexus.App;

namespace AINexus.Checks;

internal static class ComfyPortableChecks
{
    private static readonly byte[] Bytes = "Inert Comfy portable archive fixture; never extracted or executed."u8.ToArray();
    private const string Tag = "v0.43.0";
    private const string Name = "ComfyUI_windows_portable_nvidia.7z";
    private static string Hash => Convert.ToHexString(SHA256.HashData(Bytes));

    internal static async Task RunAsync(Action<bool, string> check)
    {
        await MetadataAsync(check);
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", "comfy-portable-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await ApprovalAndReuseAsync(root, check);
            await FailuresAsync(root, check);
        }
        finally
        {
            OfflineModelVerifier.CheckNoReparsePath(root);
            Directory.Delete(root, recursive: true); // Only this run's new GUID fixture directory.
        }
    }

    internal static async Task RunLiveAsync(Action<bool, string> check)
    {
        using var http = ComfyPortableReleaseClient.CreateHttpClient();
        var release = await new ComfyPortableReleaseClient(http).ReadAsync("latest");
        check(release.Tag != "latest" && release.Assets.Count > 0 && release.ReleaseUrl.Host == "github.com"
            && release.Assets.All(asset => asset.SizeBytes > 0 && asset.Sha256.Length == 64 && asset.DownloadUrl.AbsolutePath.StartsWith("/Comfy-Org/ComfyUI/releases/download/", StringComparison.Ordinal)),
            "Live official ComfyUI release metadata supplies a concrete tag and digest-bearing portable archive choices; no binaries downloaded or compatibility claimed");
    }

    private static async Task MetadataAsync(Action<bool, string> check)
    {
        var requests = new List<string>();
        using var http = new HttpClient(new Handler((request, _) =>
        {
            requests.Add(request.RequestUri!.AbsoluteUri);
            check(request.Headers.Authorization is null && request.Headers.UserAgent.Count > 0, "Comfy metadata request is unauthenticated and identifies Nexus");
            return Task.FromResult(Response(Encoding.UTF8.GetBytes(Json())));
        }));
        var client = new ComfyPortableReleaseClient(http);
        var latest = await client.ReadAsync("latest");
        var exact = await client.ReadAsync(Tag);
        check(latest.Tag == Tag && exact.Tag == Tag && latest.Assets.Single().Sha256 == Hash
            && requests.SequenceEqual(new[] { "https://api.github.com/repos/Comfy-Org/ComfyUI/releases/latest", "https://api.github.com/repos/Comfy-Org/ComfyUI/releases/tags/" + Tag }),
            "Explicit current-release and tag lookups retain the actual published tag and canonical size/hash source");
        check(await RejectsAsync<InvalidDataException>(() => client.ReadAsync("../evil"))
            && await RejectsAsync<InvalidDataException>(() => client.ReadAsync(Tag + "\n")) && requests.Count == 2,
            "Unsafe Comfy release tags cannot trigger HTTP");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "fixture");
        check(await RejectsAsync<InvalidOperationException>(() => client.ReadAsync(Tag)) && requests.Count == 2,
            "Comfy metadata refuses credential-bearing default headers");
        foreach (var json in new[] { Json().Replace("\"draft\":false", "\"draft\":true"), Json().Replace("\"tag_name\":\"v0.43.0\"", "\"tag_name\":\"v0.44.0\""), Json().Replace("https://github.com/Comfy-Org/ComfyUI/releases/tag/", "https://example.com/releases/tag/") })
        {
            check(RejectParse(json), "Comfy metadata rejects a draft, mismatched tag or substituted release page");
        }
        foreach (var json in new[] { Json().Replace("sha256:", "sha512:"), Json().Replace("/releases/download/", "/other/download/"), Json().Replace("\"state\":\"uploaded\"", "\"state\":\"new\""), Json().Replace("\"size\":" + Bytes.Length, "\"size\":0") })
        {
            using var doc = JsonDocument.Parse(json);
            check(ComfyPortableReleaseClient.Parse(doc.RootElement, Tag, DateTimeOffset.UtcNow).Assets.Count == 0,
                "Comfy archive choices exclude missing digest, wrong URL, unfinished upload or invalid size");
        }
        using var redirectHttp = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://example.com/release") } })));
        check(await RejectsAsync<HttpRequestException>(() => new ComfyPortableReleaseClient(redirectHttp).ReadAsync(Tag)), "Comfy metadata does not follow redirects or substitute providers");
    }

    private static async Task ApprovalAndReuseAsync(string root, Action<bool, string> check)
    {
        var plan = Plan(root, "approval");
        var calls = 0;
        using var http = new HttpClient(new Handler((_, _) => { calls++; return Task.FromResult(Response(Bytes)); }));
        var service = new ComfyPortableDownloadService(http);
        check(await RejectsAsync<InvalidOperationException>(() => service.AcquireAsync(plan, new(plan.PlanId, false, true, true)))
            && await RejectsAsync<InvalidOperationException>(() => service.AcquireAsync(plan, new(plan.PlanId, true, false, true)))
            && await RejectsAsync<InvalidOperationException>(() => service.AcquireAsync(plan, new(plan.PlanId, true, true, false)))
            && await RejectsAsync<InvalidOperationException>(() => service.AcquireAsync(plan, new("different", true, true, true)))
            && calls == 0 && !Directory.EnumerateFileSystemEntries(plan.ArchiveDirectory).Any(),
            "Comfy archive download requires exact-plan terms, destination and archive-only acknowledgement before network or writes");
        check(await RejectsAsync<InvalidOperationException>(() => service.AcquireAsync(plan with { ArchivePath = plan.ArchivePath + "-changed" }, Approve(plan))) && calls == 0,
            "Comfy download approval binds the exact destination");
        var stale = plan with { CreatedAt = DateTimeOffset.UtcNow.AddHours(-1) };
        stale = stale with { PlanId = ComfyPortableDownloadPlanner.Fingerprint(stale) };
        check(await RejectsAsync<InvalidOperationException>(() => service.AcquireAsync(stale, Approve(stale))) && calls == 0,
            "Recomputed stale Comfy plans still require a fresh review");
        foreach (var retrieved in new[] { DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddMinutes(2) })
            check(RejectPlan(plan.Release with { RetrievedAt = retrieved }, plan.Asset, plan.ArchiveDirectory), "Comfy planning rejects stale/future release evidence");
        http.DefaultRequestHeaders.Add("X-Private", "fixture");
        check(await RejectsAsync<InvalidOperationException>(() => service.AcquireAsync(plan, Approve(plan))) && calls == 0, "Comfy acquisition refuses header-bearing shared clients");
        http.DefaultRequestHeaders.Clear();
        var result = await service.AcquireAsync(plan, Approve(plan));
        check(calls == 1 && !result.ReusedExisting && File.ReadAllBytes(result.ArchivePath).SequenceEqual(Bytes) && File.Exists(result.ReceiptPath)
            && !Directory.Exists(Path.Combine(plan.ArchiveDirectory, "ComfyUI_windows_portable")) && !Directory.GetFiles(plan.ArchiveDirectory, "*.partial").Any(),
            "Approved Comfy archive acquisition publishes only verified fixture bytes and a receipt; no extraction or execution");
        var receipt = await File.ReadAllTextAsync(result.ReceiptPath);
        check(receipt.Contains("\"EngineInstalled\":false") && receipt.Contains("\"ExtractionPerformed\":false") && receipt.Contains("\"RuntimeExecutionVerified\":false"),
            "Comfy receipt explicitly records uninstalled and unexecuted engine state");
        var reused = await service.AcquireAsync(plan, Approve(plan));
        check(reused.ReusedExisting && calls == 1 && File.Exists(reused.ReceiptPath), "Comfy matching existing archive is rehashed without HTTP");
        var mismatch = Plan(root, "preserve");
        await File.WriteAllTextAsync(mismatch.ArchivePath, "existing user bytes");
        check(await RejectsAsync<InvalidDataException>(() => service.AcquireAsync(mismatch, Approve(mismatch))) && calls == 1
            && await File.ReadAllTextAsync(mismatch.ArchivePath) == "existing user bytes", "Comfy acquisition preserves a conflicting existing archive without requesting a replacement");
    }

    private static async Task FailuresAsync(string root, Action<bool, string> check)
    {
        var index = 0;
        foreach (var bytes in new[] { Bytes[..^1], Bytes.Concat(new byte[] { 0 }).ToArray(), Enumerable.Repeat((byte)0, Bytes.Length).ToArray() })
        {
            var plan = Plan(root, "bad-stream-" + index++);
            using var http = new HttpClient(new Handler((_, _) => { var response = Response(bytes); response.Content.Headers.ContentLength = Bytes.Length; return Task.FromResult(response); }));
            check(await RejectsAsync<InvalidDataException>(() => new ComfyPortableDownloadService(http).AcquireAsync(plan, Approve(plan)))
                && !Directory.EnumerateFileSystemEntries(plan.ArchiveDirectory).Any(), "Comfy truncated, oversized and hash-mismatched bodies never publish and clean their owned partials");
        }
        var redirected = Plan(root, "redirect");
        var redirects = 0;
        using var evil = new HttpClient(new Handler((_, _) => { redirects++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://example.com/private") } }); }));
        check(await RejectsAsync<InvalidDataException>(() => new ComfyPortableDownloadService(evil).AcquireAsync(redirected, Approve(redirected)))
            && redirects == 1 && !Directory.EnumerateFileSystemEntries(redirected.ArchiveDirectory).Any(), "Comfy archive redirect to unrelated host is rejected before follow-up HTTP or writes");
        var allowed = Plan(root, "allowed-cdn");
        var allowedCalls = 0;
        using var cdn = new HttpClient(new Handler((request, _) =>
        {
            allowedCalls++;
            return Task.FromResult(request.RequestUri!.Host == "github.com"
                ? new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://release-assets.githubusercontent.com/fixture?token=public-signed-fixture") } }
                : Response(Bytes));
        }));
        var cdnResult = await new ComfyPortableDownloadService(cdn).AcquireAsync(allowed, Approve(allowed));
        check(allowedCalls == 2 && File.Exists(cdnResult.ArchivePath), "Comfy acquisition follows a bounded permitted GitHub asset redirect while verifying exact bytes");
        var canceled = Plan(root, "cancel");
        using var cancellation = new CancellationTokenSource();
        using var cancelHttp = new HttpClient(new Handler((_, _) => Task.FromResult(Response(Bytes))));
        var cancelService = new ComfyPortableDownloadService(cancelHttp);
        check(await RejectsAsync<OperationCanceledException>(() => cancelService.AcquireAsync(canceled, Approve(canceled), new InlineProgress(_ => cancellation.Cancel()), cancellation.Token))
            && !Directory.EnumerateFileSystemEntries(canceled.ArchiveDirectory).Any() && !cancelService.IsBusy,
            "Canceling Comfy archive streaming removes only its owned partial and releases the operation gate");
        var raced = Plan(root, "race");
        using var raceHttp = new HttpClient(new Handler((_, _) => { File.WriteAllText(raced.ArchivePath, "concurrent user file"); return Task.FromResult(Response(Bytes)); }));
        check(await RejectsAsync<IOException>(() => new ComfyPortableDownloadService(raceHttp).AcquireAsync(raced, Approve(raced)))
            && File.ReadAllText(raced.ArchivePath) == "concurrent user file" && !Directory.GetFiles(raced.ArchiveDirectory, "*.partial").Any(),
            "Comfy publication refuses a concurrently created destination and preserves it");
    }

    private static ComfyPortableDownloadPlan Plan(string root, string folder)
    {
        var directory = Path.Combine(root, folder); Directory.CreateDirectory(directory);
        using var doc = JsonDocument.Parse(Json());
        var release = ComfyPortableReleaseClient.Parse(doc.RootElement, Tag, DateTimeOffset.UtcNow);
        return ComfyPortableDownloadPlanner.Create(release, release.Assets.Single(), directory);
    }
    private static string Json() => "{\"tag_name\":\"" + Tag + "\",\"html_url\":\"https://github.com/Comfy-Org/ComfyUI/releases/tag/" + Tag + "\",\"draft\":false,\"prerelease\":false,\"assets\":[{\"name\":\"" + Name + "\",\"state\":\"uploaded\",\"size\":" + Bytes.Length + ",\"digest\":\"sha256:" + Hash.ToLowerInvariant() + "\",\"browser_download_url\":\"https://github.com/Comfy-Org/ComfyUI/releases/download/" + Tag + "/" + Name + "\"}]}";
    private static HttpResponseMessage Response(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    private static ComfyPortableDownloadApproval Approve(ComfyPortableDownloadPlan plan) => new(plan.PlanId, true, true, true);
    private static bool RejectParse(string json) { try { using var doc = JsonDocument.Parse(json); ComfyPortableReleaseClient.Parse(doc.RootElement, Tag, DateTimeOffset.UtcNow); return false; } catch (InvalidDataException) { return true; } }
    private static bool RejectPlan(ComfyPortableRelease release, ComfyPortableAsset asset, string folder) { try { ComfyPortableDownloadPlanner.Create(release, asset, folder); return false; } catch (InvalidOperationException) { return true; } }
    private static async Task<bool> RejectsAsync<T>(Func<Task> action) where T : Exception { try { await action(); return false; } catch (T) { return true; } }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken); }
    private sealed class InlineProgress(Action<AcquisitionProgress> report) : IProgress<AcquisitionProgress> { public void Report(AcquisitionProgress value) => report(value); }
}
