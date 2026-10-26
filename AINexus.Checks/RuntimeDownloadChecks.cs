using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using AINexus.App;

namespace AINexus.Checks;

internal static class RuntimeDownloadChecks
{
    private static readonly byte[] Bytes = "PK inert runtime download fixture; never extracted or executed."u8.ToArray();

    internal static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await ApprovalAndReuseAsync(root, check);
            await SourcesAndFailuresAsync(root, check);
            await CancellationAndReceiptsAsync(root, check);
        }
        finally
        {
            OfflineModelVerifier.CheckNoReparsePath(root);
            Directory.Delete(root, recursive: true); // This run's GUID fixture only; no link fixtures or real archives.
        }
    }

    private static async Task ApprovalAndReuseAsync(string root, Action<bool, string> check)
    {
        var plan = Plan(root, "approval");
        var calls = 0;
        using var http = new HttpClient(new FixtureHandler((_, _) => { calls++; return Task.FromResult(Response(Bytes)); }));
        var service = new RuntimeDownloadService(http);
        check(await RejectsAsync<InvalidOperationException>(() => service.AcquireAsync(plan, new(plan.PlanId, false, true, true)))
            && await RejectsAsync<InvalidOperationException>(() => service.AcquireAsync(plan, new(plan.PlanId, true, false, true)))
            && await RejectsAsync<InvalidOperationException>(() => service.AcquireAsync(plan, new(plan.PlanId, true, true, false)))
            && await RejectsAsync<InvalidOperationException>(() => service.AcquireAsync(plan, new("other-plan", true, true, true)))
            && calls == 0 && !Directory.EnumerateFileSystemEntries(plan.ArchiveDirectory).Any(),
            "Runtime ZIP acquisition requires exact-plan declared-license, destination and future bundled-terms acknowledgement before HTTP/files");
        check(await RejectsAsync<InvalidOperationException>(() => service.AcquireAsync(plan with { ArchivePath = plan.ArchivePath + "-changed" }, Approve(plan)))
            && await RejectsAsync<InvalidOperationException>(() => service.AcquireAsync(plan with { Report = plan.Report with { TargetVolume = "Z:\\" } }, Approve(plan)))
            && calls == 0, "Runtime download approval binds the full report and canonical archive destination");
        bool RejectCreate(DiscoveryReport report)
        {
            try { RuntimeDownloadPlanner.Create(report, plan.Files, plan.ArchiveDirectory); return false; }
            catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException) { return true; }
        }
        check(RejectCreate(plan.Report with { Catalog = plan.Report.Catalog with { IsCached = true } })
            && RejectCreate(plan.Report with { Catalog = plan.Report.Catalog with { RetrievedAt = DateTimeOffset.UtcNow.AddHours(-25) } })
            && RejectCreate(plan.Report with { Machine = plan.Report.Machine with { ScannedAt = DateTimeOffset.UtcNow.AddMinutes(-6) } })
            && RejectCreate(plan.Report with { Machine = plan.Report.Machine with { ScannedAt = DateTimeOffset.UtcNow.AddMinutes(2) } }),
            "Runtime download planning rejects cached/stale sources and stale/future hardware evidence");
        var expired = plan with { CreatedAt = DateTimeOffset.UtcNow.AddHours(-1) };
        expired = expired with { PlanId = RuntimeDownloadPlanner.Fingerprint(expired) };
        check(await RejectsAsync<InvalidOperationException>(() => service.AcquireAsync(expired, Approve(expired))) && calls == 0,
            "Recomputed but expired runtime plans still cannot authorize a download");
        var gpuAsset = plan.Files.Files[0] with { Name = "llama-b11669-bin-win-cuda-12.4-x64.zip",
            DownloadUrl = new Uri("https://github.com/ggml-org/llama.cpp/releases/download/b11669/llama-b11669-bin-win-cuda-12.4-x64.zip") };
        var gpu = plan.Files with { Files = [gpuAsset] };
        gpu = gpu with { PlanId = RuntimeFilePlanner.Fingerprint(gpu) };
        check(await RejectsAsync<NotSupportedException>(() => Task.FromResult(RuntimeDownloadPlanner.Create(plan.Report, gpu, plan.ArchiveDirectory))),
            "Runtime acquisition cannot substitute GPU archives into its native CPU-only recipe");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "fixture-secret");
        check(await RejectsAsync<InvalidOperationException>(() => service.AcquireAsync(plan, Approve(plan))) && calls == 0,
            "Runtime acquisition refuses credential-bearing default headers on its dedicated public client");
        http.DefaultRequestHeaders.Clear();
        var progress = new List<AcquisitionProgress>();
        var result = await service.AcquireAsync(plan, Approve(plan), new InlineProgress(progress.Add));
        check(!result.ReusedExisting && File.ReadAllBytes(result.ArchivePath).SequenceEqual(Bytes) && File.Exists(result.ReceiptPath)
            && !Directory.Exists(plan.Files.Destination) && !File.Exists(Path.Combine(plan.ArchiveDirectory, "llama-server.exe"))
            && !Directory.GetFiles(plan.ArchiveDirectory, "*.partial").Any() && progress.Last().Stage == "Runtime ZIP and receipt verified",
            "Approved CPU runtime download publishes exact inert ZIP bytes and a receipt without extraction or execution");
        var text = await File.ReadAllTextAsync(result.ReceiptPath);
        check(text.Contains("\"ExtractionPerformed\":false") && text.Contains("\"RuntimeExecutionVerified\":false")
            && text.Contains("\"PublisherAuthenticityVerified\":false"),
            "Runtime download receipt records byte identity without claiming extraction, publisher authenticity or working inference");
        var previousCalls = calls;
        var reused = await service.AcquireAsync(plan, Approve(plan));
        check(reused.ReusedExisting && calls == previousCalls && File.Exists(reused.ReceiptPath),
            "Existing exact runtime ZIPs are independently rehashed and receipted for reuse without HTTP");
        var changed = Bytes.ToArray(); changed[^1] ^= 1;
        await File.WriteAllBytesAsync(plan.ArchivePath, changed);
        check(await RejectsAsync<InvalidDataException>(() => service.AcquireAsync(plan, Approve(plan))) && calls == previousCalls
            && File.ReadAllBytes(plan.ArchivePath).SequenceEqual(changed),
            "Existing mismatching runtime ZIPs remain untouched and cannot trigger overwrite or replacement downloads");
    }

    private static async Task SourcesAndFailuresAsync(string root, Action<bool, string> check)
    {
        foreach (var host in new[] { "release-assets.githubusercontent.com", "objects.githubusercontent.com" })
        {
            var plan = Plan(root, host);
            var calls = 0;
            using var http = new HttpClient(new FixtureHandler((request, _) =>
            {
                calls++;
                if (calls == 1) return Task.FromResult(Redirect(new Uri("https://" + host + "/fixture?signature=fixture-secret")));
                check(request.RequestUri!.IdnHost == host && request.Headers.Authorization is null
                    && request.Headers.AcceptEncoding.Single().Value == "identity",
                    "Runtime CDN redirect preserves an unauthenticated exact-byte GET on " + host);
                return Task.FromResult(Response(Bytes));
            }));
            var result = await new RuntimeDownloadService(http).AcquireAsync(plan, Approve(plan));
            check(calls == 2 && File.Exists(result.ReceiptPath), "Runtime acquisition manually follows the permitted " + host + " redirect");
        }
        var denied = new[] { "https://example.com/asset", "http://release-assets.githubusercontent.com/asset",
            "https://release-assets.githubusercontent.com:444/asset", "https://user@objects.githubusercontent.com/asset",
            "https://objects.githubusercontent.com/asset#fragment", "https://release-assets.githubusercontent.com.evil.test/asset",
            "https://github.com/foreign/repo/releases/download/tag/asset.zip" };
        for (var index = 0; index < denied.Length; index++)
        {
            var plan = Plan(root, "denied-" + index);
            var calls = 0;
            using var http = new HttpClient(new FixtureHandler((_, _) => { calls++; return Task.FromResult(Redirect(new Uri(denied[index]))); }));
            check(await RejectsAsync<InvalidDataException>(() => new RuntimeDownloadService(http).AcquireAsync(plan, Approve(plan)))
                && calls == 1 && !Directory.EnumerateFileSystemEntries(plan.ArchiveDirectory).Any(),
                "Runtime redirect policy rejects foreign origin, insecure port, credentials or fragment case " + index + " before another request/write");
        }
        var autoPlan = Plan(root, "automatic-redirect");
        using (var http = new HttpClient(new FixtureHandler((_, _) =>
        {
            var response = Response(Bytes);
            response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://objects.githubusercontent.com/unapproved-auto-follow");
            return Task.FromResult(response);
        })))
            check(await RejectsAsync<InvalidDataException>(() => new RuntimeDownloadService(http).AcquireAsync(autoPlan, Approve(autoPlan))),
                "A transport that silently follows redirects is detected and rejected");
        var loopPlan = Plan(root, "redirect-loop");
        var loopCalls = 0;
        using (var http = new HttpClient(new FixtureHandler((_, _) =>
        { loopCalls++; return Task.FromResult(Redirect(new Uri("https://objects.githubusercontent.com/loop"))); })))
            check(await RejectsAsync<InvalidDataException>(() => new RuntimeDownloadService(http).AcquireAsync(loopPlan, Approve(loopPlan))) && loopCalls == 6,
                "Runtime release downloads enforce a bounded five-redirect policy");
        for (var scenario = 0; scenario < 5; scenario++)
        {
            var plan = Plan(root, "bad-bytes-" + scenario);
            using var http = new HttpClient(new FixtureHandler((_, _) =>
            {
                var bytes = Bytes.ToArray();
                if (scenario == 0) bytes[^1] ^= 1;
                if (scenario == 1) bytes = bytes[..^1];
                if (scenario == 2) bytes = [.. bytes, 1];
                var response = scenario is 1 or 2
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new LengthUnknownStream(bytes)) }
                    : Response(bytes);
                if (scenario == 3) response.Content.Headers.ContentLength = Bytes.Length + 1;
                if (scenario == 4) response.Content.Headers.ContentEncoding.Add("gzip");
                return Task.FromResult(response);
            }));
            check(await RejectsAsync<InvalidDataException>(() => new RuntimeDownloadService(http).AcquireAsync(plan, Approve(plan)))
                && !File.Exists(plan.ArchivePath) && !Directory.GetFiles(plan.ArchiveDirectory, "*.partial").Any(),
                "Runtime acquisition rejects bad hash, short/long stream, mismatched length or encoded response case " + scenario + " and cleans only its partial");
        }
        var errorPlan = Plan(root, "secret-error");
        using (var http = new HttpClient(new FixtureHandler((_, _) => throw new HttpRequestException("secret-signed-query-value"))))
        {
            string error = "";
            try { await new RuntimeDownloadService(http).AcquireAsync(errorPlan, Approve(errorPlan)); }
            catch (HttpRequestException ex) { error = ex.ToString(); }
            check(error.Length > 0 && !error.Contains("secret-signed-query-value"),
                "Runtime transport errors do not expose signed release-asset URL details");
        }
    }

    private static async Task CancellationAndReceiptsAsync(string root, Action<bool, string> check)
    {
        var plan = Plan(root, "cancel-stream");
        using (var http = new HttpClient(new FixtureHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(new ChunkThenWaitStream(Bytes)) }))))
        using (var cancelled = new CancellationTokenSource())
        {
            var service = new RuntimeDownloadService(http);
            check(await RejectsAsync<OperationCanceledException>(() => service.AcquireAsync(plan, Approve(plan),
                new InlineProgress(value => { if (value.CompletedBytes > 0) cancelled.Cancel(); }), cancelled.Token))
                && !service.IsBusy && !File.Exists(plan.ArchivePath) && !Directory.GetFiles(plan.ArchiveDirectory, "*.partial").Any(),
                "Cancelling streamed runtime ZIP acquisition removes only its own partial and releases the service gate");
        }
        var pendingPlan = Plan(root, "concurrency");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var http = new HttpClient(new FixtureHandler(async (_, token) =>
        { entered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); return Response(Bytes); })))
        using (var cancelled = new CancellationTokenSource())
        {
            var service = new RuntimeDownloadService(http);
            var pending = service.AcquireAsync(pendingPlan, Approve(pendingPlan), cancellationToken: cancelled.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            check(await RejectsAsync<InvalidOperationException>(() => service.AcquireAsync(pendingPlan, Approve(pendingPlan))),
                "A runtime acquisition service rejects overlapping requests instead of interleaving approved writes");
            cancelled.Cancel();
            check(await RejectsAsync<OperationCanceledException>(() => pending) && !service.IsBusy
                && !Directory.EnumerateFileSystemEntries(pendingPlan.ArchiveDirectory).Any(),
                "Cancelling before runtime response headers produces no partial or final archive");
        }
        var receiptPlan = Plan(root, "receipt-failure");
        var metadataBlocker = Path.Combine(receiptPlan.ArchiveDirectory, RuntimeDownloadPlanner.ReceiptDirectoryName);
        await File.WriteAllTextAsync(metadataBlocker, "Unrelated existing file; preserve it.");
        using (var http = new HttpClient(new FixtureHandler((_, _) => Task.FromResult(Response(Bytes)))))
        {
            var service = new RuntimeDownloadService(http);
            string error = "";
            try { await service.AcquireAsync(receiptPlan, Approve(receiptPlan)); }
            catch (IOException ex) { error = ex.Message; }
            check(error.Contains("receipt could not be completed") && File.ReadAllBytes(receiptPlan.ArchivePath).SequenceEqual(Bytes)
                && await File.ReadAllTextAsync(metadataBlocker) == "Unrelated existing file; preserve it." && !service.IsBusy,
                "Receipt failure returns no recorded success while preserving the verified ZIP and preexisting metadata blocker");
        }
    }

    private static RuntimeDownloadPlan Plan(string root, string name)
    {
        var directory = Path.Combine(root, name);
        Directory.CreateDirectory(directory);
        var native = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
        var archiveName = $"llama-b11669-bin-win-cpu-{native}.zip";
        var asset = new RuntimeReleaseAsset(archiveName, Bytes.Length, Convert.ToHexString(SHA256.HashData(Bytes)),
            new Uri("https://github.com/ggml-org/llama.cpp/releases/download/b11669/" + archiveName));
        var machine = RuntimeSetupChecks.Machine() with { OsArchitecture = native };
        var report = RuntimeSetupChecks.Report(machine, asset);
        var item = report.Catalog.Items.Single() with
        {
            RuntimeRelease = new("b11669", false, false, [asset], []),
            Sources = [new("Fixture runtime metadata", new Uri("https://api.github.com/repos/ggml-org/llama.cpp/releases/tags/b11669"), DateTimeOffset.UtcNow)]
        };
        report = DiscoveryPipeline.Evaluate(report.Catalog with { Items = [item], RetrievedAt = DateTimeOffset.UtcNow }, machine, report.TargetVolume);
        var files = RuntimeFilePlanner.Create(report, item.Id, Path.Combine(root, name + "-uncreated-runtime"), archiveName);
        return RuntimeDownloadPlanner.Create(report, files, directory);
    }
    private static RuntimeDownloadApproval Approve(RuntimeDownloadPlan plan) => new(plan.PlanId, true, true, true);
    private static HttpResponseMessage Response(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    private static HttpResponseMessage Redirect(Uri location)
    { var response = new HttpResponseMessage(HttpStatusCode.Found); response.Headers.Location = location; return response; }
    private static async Task<bool> RejectsAsync<T>(Func<Task> operation) where T : Exception
    { try { await operation(); return false; } catch (T) { return true; } }
    private sealed class FixtureHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken); }
    private sealed class InlineProgress(Action<AcquisitionProgress> report) : IProgress<AcquisitionProgress>
    { public void Report(AcquisitionProgress value) => report(value); }
    private sealed class LengthUnknownStream(byte[] bytes) : MemoryStream(bytes) { public override bool CanSeek => false; }
    private sealed class ChunkThenWaitStream(byte[] bytes) : MemoryStream(bytes)
    {
        private bool _read;
        public override bool CanSeek => false;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (!_read) { _read = true; return base.ReadAsync(buffer[..Math.Min(5, buffer.Length)], token); }
            return new ValueTask<int>(WaitAsync(token));
        }
        private static async Task<int> WaitAsync(CancellationToken token) { await Task.Delay(Timeout.InfiniteTimeSpan, token); return 0; }
    }
}
