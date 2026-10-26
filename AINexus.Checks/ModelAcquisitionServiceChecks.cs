using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using AINexus.App;

namespace AINexus.Checks;

internal static class ModelAcquisitionServiceChecks
{
    private static readonly byte[] Artifact = Encoding.UTF8.GetBytes("GGUF approved acquisition fixture bytes");

    internal static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await CheckApprovalAndReuseAsync(root, check);
            await CheckDownloadAndRedirectsAsync(root, check);
            await CheckFailuresAsync(root, check);
            await CheckCancellationAndConcurrencyAsync(root, check);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task CheckApprovalAndReuseAsync(string root, Action<bool, string> check)
    {
        var plan = CreatePlan(root, "approval");
        var calls = 0;
        using var http = new HttpClient(new FixtureHandler((_, _) => { calls++; return Task.FromResult(Bytes(Artifact)); }));
        var service = new ModelAcquisitionService(http);
        check(await RejectsAsync<InvalidOperationException>(() => service.AcquireAsync(plan, new(plan.PlanId, false, true)))
            && await RejectsAsync<InvalidOperationException>(() => service.AcquireAsync(plan, new(plan.PlanId, true, false)))
            && calls == 0 && !Directory.Exists(Path.GetDirectoryName(plan.DestinationPath)),
            "Model acquisition requires explicit license and destination-write approvals before files or HTTP");
        check(await RejectsAsync<InvalidOperationException>(() => service.AcquireAsync(plan, new("different-plan", true, true)))
            && calls == 0, "Approval cannot be reused for a different acquisition plan");
        check(await RejectsAsync<InvalidOperationException>(() => service.AcquireAsync(plan with { Name = "tampered fixture" }, Approve(plan)))
            && calls == 0, "Changing reviewed plan contents invalidates acquisition approval");

        Directory.CreateDirectory(Path.GetDirectoryName(plan.DestinationPath)!);
        await File.WriteAllBytesAsync(plan.DestinationPath, Artifact);
        var reused = await service.AcquireAsync(plan, Approve(plan));
        check(reused.ReusedExisting && reused.Evidence.Matches && calls == 0,
            "A matching existing model is verified for reuse without making a download request");
        var mismatch = Artifact.ToArray();
        mismatch[^1] ^= 1;
        await File.WriteAllBytesAsync(plan.DestinationPath, mismatch);
        check(await RejectsAsync<InvalidDataException>(() => service.AcquireAsync(plan, Approve(plan)))
            && calls == 0 && File.ReadAllBytes(plan.DestinationPath).SequenceEqual(mismatch),
            "An existing mismatching model is preserved without download or overwrite");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "fixture-token");
        check(await RejectsAsync<InvalidOperationException>(() => service.AcquireAsync(plan, Approve(plan))) && calls == 0,
            "A public acquisition client cannot silently forward an authorization header");
        http.DefaultRequestHeaders.Authorization = null;
        http.DefaultRequestHeaders.Add("X-Custom-Secret", "fixture-token");
        check(await RejectsAsync<InvalidOperationException>(() => service.AcquireAsync(plan, Approve(plan))) && calls == 0,
            "Unknown custom default headers cannot carry tokens into public CDN requests");
    }

    private static async Task CheckDownloadAndRedirectsAsync(string root, Action<bool, string> check)
    {
        var plan = CreatePlan(root, "success");
        var updates = new List<AcquisitionProgress>();
        var calls = 0;
        using (var http = new HttpClient(new FixtureHandler((request, _) =>
        {
            calls++;
            check(request.Method == HttpMethod.Get && request.Headers.Authorization is null,
                "Approved acquisition issues only an unauthenticated public GET request");
            return Task.FromResult(Bytes(Artifact));
        })))
        {
            var result = await new ModelAcquisitionService(http).AcquireAsync(plan, Approve(plan), new CallbackProgress(updates.Add));
            check(!result.ReusedExisting && result.Evidence.Matches && calls == 1 && File.ReadAllBytes(plan.DestinationPath).SequenceEqual(Artifact),
                "Only fully size/hash-verified downloaded bytes become the final model artifact");
            check(updates.First().CompletedBytes == 0 && updates.Last().CompletedBytes == Artifact.Length
                && updates.Last().Stage == "Artifact integrity verified" && updates.All(value => value.CompletedBytes <= value.ExpectedBytes),
                "Acquisition progress reports actual received bytes and a distinct verified-completion stage");
            check(!Directory.GetFiles(Path.GetDirectoryName(plan.DestinationPath)!, "*.partial").Any(),
                "Successful acquisition leaves no unchecked partial beside the verified model");
        }

        var redirectPlan = CreatePlan(root, "redirect");
        var redirectCalls = 0;
        using (var http = new HttpClient(new FixtureHandler((request, _) =>
        {
            redirectCalls++;
            if (redirectCalls == 1)
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri("https://cas-bridge.xethub.hf.co/fixture?X-Amz-Signature=fixture-signed-value");
                return Task.FromResult(redirect);
            }
            check(request.RequestUri!.Host == "cas-bridge.xethub.hf.co", "Acquisition follows an explicitly permitted HTTPS blob-CDN redirect");
            return Task.FromResult(Bytes(Artifact));
        })))
        {
            var result = await new ModelAcquisitionService(http).AcquireAsync(redirectPlan, Approve(redirectPlan));
            check(result.Evidence.Matches && redirectCalls == 2 && !result.Evidence.Description.Contains("fixture-signed-value"),
                "Signed CDN parameters do not enter the persisted artifact identity or displayed result");
        }
        foreach (var bad in new[] { "http://huggingface.co/fixture", "https://huggingface.co.evil.example/fixture", "https://example.com/fixture", "https://user:password@hf.co/fixture", "https://hf.co:8443/fixture" })
            check(!ModelAcquisitionService.IsPermittedSource(new Uri(bad)), "Acquisition rejects insecure, credential-bearing or foreign redirect origins");
    }

    private static async Task CheckFailuresAsync(string root, Action<bool, string> check)
    {
        foreach (var kind in new[] { "short", "oversize", "checksum", "compressed", "range", "foreign-redirect", "redirect-loop" })
        {
            var plan = CreatePlan(root, kind);
            var calls = 0;
            using var http = new HttpClient(new FixtureHandler((_, _) =>
            {
                calls++;
                var content = Artifact.ToArray();
                if (kind == "short") content = content[..^1];
                if (kind == "oversize") content = content.Concat(new byte[] { 1 }).ToArray();
                if (kind == "checksum") content[^1] ^= 1;
                var response = Bytes(content);
                // Hide length where necessary to exercise streaming bounds rather than headers alone.
                if (kind is "short" or "oversize") response.Content = new StreamContent(new LengthUnknownStream(content));
                if (kind == "compressed") response.Content.Headers.ContentEncoding.Add("gzip");
                if (kind == "range") response.StatusCode = HttpStatusCode.PartialContent;
                if (kind is "foreign-redirect" or "redirect-loop")
                {
                    response.StatusCode = HttpStatusCode.Found;
                    response.Headers.Location = new Uri(kind == "foreign-redirect" ? "https://example.com/fixture" : "https://hf.co/loop");
                }
                return Task.FromResult(response);
            }));
            var rejected = await RejectsAsync<Exception>(() => new ModelAcquisitionService(http).AcquireAsync(plan, Approve(plan)));
            check(rejected && !File.Exists(plan.DestinationPath)
                && (!Directory.Exists(Path.GetDirectoryName(plan.DestinationPath)) || !Directory.GetFiles(Path.GetDirectoryName(plan.DestinationPath)!, "*.partial").Any())
                && calls <= 6, $"Acquisition rejects {kind} responses, publishes no final file and cleans only its own partial");
        }

        var racePlan = CreatePlan(root, "race");
        var otherBytes = "Unrelated concurrently created fixture"u8.ToArray();
        using (var http = new HttpClient(new FixtureHandler((_, _) => Task.FromResult(Bytes(Artifact)))))
        {
            var progress = new CallbackProgress(value =>
            {
                if (value.CompletedBytes == Artifact.Length && value.Stage == "Downloading approved model data")
                    File.WriteAllBytes(racePlan.DestinationPath, otherBytes);
            });
            check(await RejectsAsync<IOException>(() => new ModelAcquisitionService(http).AcquireAsync(racePlan, Approve(racePlan), progress))
                && File.ReadAllBytes(racePlan.DestinationPath).SequenceEqual(otherBytes),
                "A destination created during download wins the race and is never overwritten");
        }

        var secretPlan = CreatePlan(root, "network-error");
        using (var http = new HttpClient(new FixtureHandler((_, _) => throw new HttpRequestException("fixture-signed-secret-token"))))
        {
            string error = "";
            try { await new ModelAcquisitionService(http).AcquireAsync(secretPlan, Approve(secretPlan)); }
            catch (HttpRequestException ex) { error = ex.ToString(); }
            check(error.Length > 0 && !error.Contains("fixture-signed-secret-token"), "Network errors do not leak underlying signed download URLs or tokens");
        }
        var streamFaultPlan = CreatePlan(root, "stream-error");
        using (var http = new HttpClient(new FixtureHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new FaultingReadStream())
        }))))
        {
            string error = "";
            try { await new ModelAcquisitionService(http).AcquireAsync(streamFaultPlan, Approve(streamFaultPlan)); }
            catch (IOException ex) { error = ex.ToString(); }
            check(error.Length > 0 && !error.Contains("fixture-signed-stream-secret") && !File.Exists(streamFaultPlan.DestinationPath)
                && !Directory.GetFiles(Path.GetDirectoryName(streamFaultPlan.DestinationPath)!, "*.partial").Any(),
                "Stream faults conceal transport URL details and clean their own unchecked partial");
        }
    }

    private static async Task CheckCancellationAndConcurrencyAsync(string root, Action<bool, string> check)
    {
        var plan = CreatePlan(root, "cancel-stream");
        using (var http = new HttpClient(new FixtureHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new CancelAfterChunkStream(Artifact))
        }))))
        using (var cancellation = new CancellationTokenSource())
        {
            var progress = new CallbackProgress(value => { if (value.CompletedBytes > 0) cancellation.Cancel(); });
            var service = new ModelAcquisitionService(http);
            check(await RejectsAsync<OperationCanceledException>(() => service.AcquireAsync(plan, Approve(plan), progress, cancellation.Token))
                && !service.IsBusy && !File.Exists(plan.DestinationPath)
                && !Directory.GetFiles(Path.GetDirectoryName(plan.DestinationPath)!, "*.partial").Any(),
                "Cancelling an active download removes its own partial and releases the acquisition gate");
        }

        var pendingPlan = CreatePlan(root, "concurrent");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var pendingHttp = new HttpClient(new FixtureHandler(async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Bytes(Artifact);
        }));
        using var pendingCancellation = new CancellationTokenSource();
        var pendingService = new ModelAcquisitionService(pendingHttp);
        var pending = pendingService.AcquireAsync(pendingPlan, Approve(pendingPlan), cancellationToken: pendingCancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        check(await RejectsAsync<InvalidOperationException>(() => pendingService.AcquireAsync(pendingPlan, Approve(pendingPlan))),
            "A service cannot start a second overlapping acquisition");
        pendingCancellation.Cancel();
        check(await RejectsAsync<OperationCanceledException>(() => pending) && !pendingService.IsBusy,
            "Cancelling a pending source request does not leave the service permanently busy");
    }

    private static ModelAcquisitionPlan CreatePlan(string root, string name)
    {
        var folder = Path.Combine(root, name);
        var report = AcquisitionPlanChecks.FixtureReport(folder, Artifact);
        return ModelAcquisitionPlanner.Create(report, report.Catalog.Items.Single().Id, folder);
    }
    private static AcquisitionApproval Approve(ModelAcquisitionPlan plan) => new(plan.PlanId, true, true);
    private static HttpResponseMessage Bytes(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    private static async Task<bool> RejectsAsync<T>(Func<Task> operation) where T : Exception
    {
        try { await operation(); }
        catch (T) { return true; }
        return false;
    }
    private sealed class CallbackProgress(Action<AcquisitionProgress> update) : IProgress<AcquisitionProgress>
    {
        public void Report(AcquisitionProgress value) => update(value);
    }
    private sealed class FixtureHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
    private sealed class LengthUnknownStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
    private sealed class CancelAfterChunkStream(byte[] bytes) : MemoryStream(bytes)
    {
        private bool _read;
        public override bool CanSeek => false;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_read) return new ValueTask<int>(WaitForCancellationAsync(cancellationToken));
            _read = true;
            return base.ReadAsync(buffer[..Math.Min(5, buffer.Length)], cancellationToken);
        }
        private static async Task<int> WaitForCancellationAsync(CancellationToken token)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return 0;
        }
    }
    private sealed class FaultingReadStream : MemoryStream
    {
        public override bool CanSeek => false;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException("fixture-signed-stream-secret"));
    }
}
