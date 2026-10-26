using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using AINexus.App;

namespace AINexus.Checks;

/// <summary>Deterministic fixtures only: no live daemon, model, HTTP connection, or hardware assumptions.</summary>
internal static class AgentChecks
{
    private static readonly Uri Endpoint = new("http://127.0.0.1:11434");

    public static async Task RunAsync(Action<bool, string> check)
    {
        CheckPolicy(check);
        CheckGenerationSchema(check);
        await CheckGenerationHttpAsync(check);
        await CheckSerialSchedulingAsync(check);
        await CheckBlockedSchedulingAsync(check);
        await CheckCancellationAsync(check);
        await CheckDisposalAsync(check);
    }

    private static ResourceSnapshot Healthy() => new(DateTimeOffset.Now, 25, 8, 16,
        [new("Fixture NVIDIA adapter", 25, 2, 8)], [], "All processors");

    private static AgentRequest Request(string name) => new(Guid.NewGuid(), name, Endpoint,
        "fixture-model", "Fixture system text", "Fixture prompt", new ResourcePolicy());

    private static void CheckPolicy(Action<bool, string> check)
    {
        var policy = new ResourcePolicy();
        var healthy = Healthy();
        check(policy.Evaluate(healthy).Allowed, "Agent admission accepts actual-like telemetry meeting configured thresholds");
        check(!policy.Evaluate(healthy with { CpuUsagePercent = null }).Allowed, "Unknown CPU blocks admission");
        check(!policy.Evaluate(healthy with { CpuUsagePercent = double.NaN }).Allowed, "Nonfinite CPU blocks admission");
        check(!policy.Evaluate(healthy with { CpuUsagePercent = 96 }).Allowed, "CPU above policy ceiling blocks admission");
        check(!policy.Evaluate(healthy with { CpuScope = "Processor group 1; not a whole-system CPU total" }).Allowed,
            "Group-scoped CPU cannot establish a whole-system admission ceiling");
        check(!policy.Evaluate(healthy with { RamUsedGb = null }).Allowed, "Unknown RAM blocks admission");
        check(!policy.Evaluate(healthy with { RamTotalGb = double.PositiveInfinity }).Allowed, "Nonfinite RAM blocks admission");
        check(!policy.Evaluate(healthy with { RamUsedGb = 17 }).Allowed, "Inconsistent RAM counters block admission");
        check(!policy.Evaluate(healthy with { RamUsedGb = 15.5 }).Allowed, "Insufficient available RAM blocks admission");
        check(policy.Evaluate(healthy with { CpuUsagePercent = 95, RamUsedGb = 15 }).Allowed,
            "Telemetry exactly at configured admission thresholds is accepted");
        check(policy.Evaluate(healthy with { Gpus = [] }).Allowed, "Disabled VRAM minimum permits missing GPU telemetry");
        var vramPolicy = policy with { MinimumAvailableVramGb = 1 };
        check(!vramPolicy.Evaluate(healthy with { Gpus = [] }).Allowed, "Enabled VRAM minimum requires driver telemetry");
        check(!vramPolicy.Evaluate(healthy with { Gpus = [new("Fixture", 0, null, 8)] }).Allowed,
            "Unknown available VRAM blocks an enabled VRAM gate");
        check(!vramPolicy.Evaluate(healthy with { Gpus = [new("Fixture A", 0, 7.5, 8), new("Fixture B", 0, 7.5, 8)] }).Allowed,
            "VRAM from separate adapters is never pooled to satisfy admission");
        check(vramPolicy.Evaluate(healthy with { Gpus = [new("Fixture A", 0, 7.5, 8), new("Fixture B", 0, 7, 8)] }).Allowed,
            "One monitored adapter meeting the VRAM minimum satisfies its gate");
        check(!vramPolicy.Evaluate(healthy with { Gpus = [new("Fixture", 0, 9, 8)] }).Allowed,
            "Inconsistent VRAM counters cannot establish availability");
        foreach (var invalid in new[]
        {
            policy with { MinimumAvailableRamGb = -1 }, policy with { MaximumCpuPercent = 101 },
            policy with { MaximumCpuPercent = double.NaN }, policy with { MinimumAvailableVramGb = double.PositiveInfinity }
        }) check(!invalid.Evaluate(healthy).Allowed, "Invalid admission configuration blocks a task safely");
    }

    private static void CheckGenerationSchema(Action<bool, string> check)
    {
        const string prompt = "Fixture \"quoted\" text\nUnicode: \u03bb";
        using var body = JsonDocument.Parse(OllamaGenerationClient.CreateRequestBody("fixture-model", "Fixture system", prompt));
        var root = body.RootElement;
        check(root.GetProperty("model").GetString() == "fixture-model"
              && root.GetProperty("system").GetString() == "Fixture system"
              && root.GetProperty("prompt").GetString() == prompt
              && root.GetProperty("stream").ValueKind == JsonValueKind.False,
            "Generation request serializes actual model and escaped prompts with nonstreaming output");
        check(Throws<ArgumentException>(() => OllamaGenerationClient.CreateRequestBody("", "", "Fixture")),
            "An empty model name cannot issue a generation request");
        check(Throws<ArgumentException>(() => OllamaGenerationClient.CreateRequestBody("fixture", "", " ")),
            "An empty prompt cannot accidentally request model loading alone");
        check(OllamaGenerationClient.ParseResponse("{\"response\":\"Fixture answer\",\"done\":true}") == "Fixture answer",
            "Only actual returned generation text is exposed");
        check(OllamaGenerationClient.ParseResponse("{\"response\":\"\",\"done\":true}") == "",
            "A genuinely empty completed response is preserved without fabricated text");
        foreach (var malformed in new[]
        {
            "[]", "{}", "{\"response\":\"partial\",\"done\":false}",
            "{\"response\":12,\"done\":true}", "{\"response\":\"text\",\"done\":\"true\"}",
            "{\"error\":\"Fixture error\",\"response\":\"text\",\"done\":true}"
        }) check(Throws<InvalidDataException>(() => OllamaGenerationClient.ParseResponse(malformed)),
            "Incomplete or erroneous generation responses cannot be reported as completed");
    }

    private static async Task CheckGenerationHttpAsync(Action<bool, string> check)
    {
        string? sentBody = null;
        Uri? sentUri = null;
        HttpMethod? sentMethod = null;
        using (var handler = new ProbeHandler(async (request, token) =>
        {
            sentBody = await request.Content!.ReadAsStringAsync(token);
            sentUri = request.RequestUri;
            sentMethod = request.Method;
            return Json("{\"done\":true,\"response\":\"Fixture HTTP answer\"}");
        }))
        using (var http = new HttpClient(handler))
        {
            var client = new OllamaGenerationClient(http);
            check(await client.GenerateAsync(Endpoint, "fixture-model", "Fixture system", "Fixture prompt") == "Fixture HTTP answer",
                "Injected HTTP response drives generation results without a real model");
            using var sentDocument = JsonDocument.Parse(sentBody ?? "{}");
            check(sentMethod == HttpMethod.Post && sentUri == new Uri(Endpoint, "/api/generate")
                  && sentDocument.RootElement.TryGetProperty("prompt", out var actualPrompt)
                  && actualPrompt.GetString() == "Fixture prompt",
                "Generation sends only the intended local POST endpoint and actual prompt body");
            check(await ThrowsAsync<ArgumentException>(() => client.GenerateAsync(new Uri("https://example.com"), "fixture", "", "Fixture"))
                  && handler.CallCount == 1, "Remote generation endpoints are rejected before any HTTP request");
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            check(await ThrowsAsync<OperationCanceledException>(() => client.GenerateAsync(Endpoint, "fixture", "", "Fixture", cancelled.Token))
                  && handler.CallCount == 1, "Precancelled generation sends no HTTP request");
        }

        using (var handler = new ProbeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
               { Content = new StringContent("Fixture private server body") })))
        using (var http = new HttpClient(handler))
        {
            try
            {
                await new OllamaGenerationClient(http).GenerateAsync(Endpoint, "fixture", "", "Fixture");
                check(false, "HTTP generation failures cannot succeed");
            }
            catch (HttpRequestException ex)
            {
                check(ex.StatusCode == HttpStatusCode.NotFound && !ex.Message.Contains("private server body"),
                    "HTTP failures retain status without exposing raw server body text");
            }
        }

        using (var handler = new ProbeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect))))
        using (var http = new HttpClient(handler))
            check(await ThrowsAsync<InvalidDataException>(() => new OllamaGenerationClient(http).GenerateAsync(Endpoint, "fixture", "", "Fixture")),
                "Generation rejects redirect responses");

        using (var handler = new ProbeHandler((_, _) =>
        {
            var response = Json("{}");
            response.Content.Headers.ContentLength = OllamaGenerationClient.MaximumResponseBytes + 1L;
            return Task.FromResult(response);
        }))
        using (var http = new HttpClient(handler))
            check(await ThrowsAsync<InvalidDataException>(() => new OllamaGenerationClient(http).GenerateAsync(Endpoint, "fixture", "", "Fixture")),
                "Oversized declared generation responses are rejected before reading their bodies");

        using (var handler = new ProbeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
               { Content = new UnmeasuredContent(OllamaGenerationClient.MaximumResponseBytes + 1) })))
        using (var http = new HttpClient(handler))
            check(await ThrowsAsync<InvalidDataException>(() => new OllamaGenerationClient(http).GenerateAsync(Endpoint, "fixture", "", "Fixture")),
                "Generation enforces its size cap even without a Content-Length header");

        var entered = Signal();
        using (var cancellation = new CancellationTokenSource())
        using (var handler = new ProbeHandler(async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return Json("{}");
        }))
        using (var http = new HttpClient(handler))
        {
            var pending = new OllamaGenerationClient(http).GenerateAsync(Endpoint, "fixture", "", "Fixture", cancellation.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            check(await ThrowsAsync<OperationCanceledException>(() => pending), "Cancellation interrupts an active generation HTTP request");
        }
    }

    private static async Task CheckSerialSchedulingAsync(Action<bool, string> check)
    {
        var firstEntered = Signal();
        var release = Signal();
        var calls = 0;
        using var handler = new ProbeHandler(async (_, token) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                firstEntered.TrySetResult();
                await release.Task.WaitAsync(token);
            }
            return Json("{\"done\":true,\"response\":\"Fixture scheduler answer\"}");
        });
        using var http = new HttpClient(handler);
        await using var scheduler = new AgentScheduler(new OllamaGenerationClient(http), _ => Task.FromResult(Healthy()));
        // A failed view observer must not prevent other observers or stop the worker.
        scheduler.Updated += _ => throw new InvalidOperationException("Fixture observer failure");
        var terminal = Observe(scheduler);
        var first = Request("Fixture serial A");
        var second = Request("Fixture serial B");
        check(scheduler.TryEnqueue(first), "First serial generation task is accepted");
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        check(scheduler.TryEnqueue(second), "Second generation task can queue behind a running request");
        check(!scheduler.TryEnqueue(first), "An active task identifier cannot be enqueued twice");
        await Task.Delay(400);
        check(handler.CallCount == 1, "Queued tasks cannot start HTTP generation while another task is running");
        release.TrySetResult();
        var firstResult = await TerminalAsync(terminal, first.Id);
        var secondResult = await TerminalAsync(terminal, second.Id);
        check(firstResult.Status == "Completed" && secondResult.Status == "Completed"
              && secondResult.Response == "Fixture scheduler answer", "Serial jobs complete with actual injected HTTP text despite a throwing observer");
        check(handler.CallCount == 2 && handler.MaximumActive == 1, "Scheduler permits at most one active generation request");
    }

    private static async Task CheckBlockedSchedulingAsync(Action<bool, string> check)
    {
        using var handler = new ProbeHandler((_, _) => Task.FromResult(Json("{}")));
        using var http = new HttpClient(handler);
        await using var scheduler = new AgentScheduler(new OllamaGenerationClient(http),
            _ => Task.FromResult(Healthy() with { CpuUsagePercent = null }));
        var terminal = Observe(scheduler);
        var request = Request("Fixture blocked");
        check(scheduler.TryEnqueue(request), "A task can enter the queue before resource admission is evaluated");
        var result = await TerminalAsync(terminal, request.Id);
        check(result.Status == "Blocked" && handler.CallCount == 0,
            "Failed resource admission blocks a queued task without sending any generation request");
    }

    private static async Task CheckCancellationAsync(Action<bool, string> check)
    {
        var entered = Signal();
        var calls = 0;
        using var handler = new ProbeHandler(async (_, token) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
            return Json("{\"done\":true,\"response\":\"Fixture recovery\"}");
        });
        using var http = new HttpClient(handler);
        await using var scheduler = new AgentScheduler(new OllamaGenerationClient(http), _ => Task.FromResult(Healthy()));
        var terminal = Observe(scheduler);
        var running = Request("Fixture cancel running");
        var queued = Request("Fixture cancel queued");
        check(scheduler.TryEnqueue(running), "Cancellation fixture starts a task");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        check(scheduler.TryEnqueue(queued) && scheduler.Cancel(queued.Id), "A queued task can be cancelled before admission");
        check(scheduler.Cancel(running.Id), "A running task accepts cancellation");
        var runningResult = await TerminalAsync(terminal, running.Id);
        var queuedResult = await TerminalAsync(terminal, queued.Id);
        check(runningResult.Status == "Cancelled" && queuedResult.Status == "Cancelled" && handler.CallCount == 1,
            "Running cancellation reaches HTTP and queued cancellation prevents its request from being sent");
        var recovery = Request("Fixture recovery");
        check(scheduler.TryEnqueue(recovery) && (await TerminalAsync(terminal, recovery.Id)).Status == "Completed",
            "The scheduler continues processing real responses after cancellation");
    }

    private static async Task CheckDisposalAsync(Action<bool, string> check)
    {
        var entered = Signal();
        using var handler = new ProbeHandler(async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return Json("{}");
        });
        using var http = new HttpClient(handler);
        await using var scheduler = new AgentScheduler(new OllamaGenerationClient(http), _ => Task.FromResult(Healthy()));
        scheduler.Updated += _ => throw new InvalidOperationException("Fixture observer failure during shutdown");
        var terminal = Observe(scheduler);
        var running = Request("Fixture dispose running");
        var queued = Request("Fixture dispose queued");
        check(scheduler.TryEnqueue(running), "Disposal fixture starts a running task");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        check(scheduler.TryEnqueue(queued), "Disposal fixture has queued work");
        await scheduler.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        check((await TerminalAsync(terminal, running.Id)).Status == "Cancelled"
              && (await TerminalAsync(terminal, queued.Id)).Status == "Cancelled"
              && handler.CallCount == 1, "Scheduler disposal cancels active HTTP and drains cancelled queued work despite observer errors");
        check(!scheduler.TryEnqueue(Request("Fixture after disposal")), "A disposed scheduler rejects new work");
    }

    private static ConcurrentDictionary<Guid, TaskCompletionSource<AgentJobUpdate>> Observe(AgentScheduler scheduler)
    {
        var results = new ConcurrentDictionary<Guid, TaskCompletionSource<AgentJobUpdate>>();
        scheduler.Updated += update =>
        {
            if (update.Status is "Completed" or "Cancelled" or "Blocked" or "Failed")
                results.GetOrAdd(update.Id, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult(update);
        };
        return results;
    }

    private static Task<AgentJobUpdate> TerminalAsync(
        ConcurrentDictionary<Guid, TaskCompletionSource<AgentJobUpdate>> results, Guid id) =>
        results.GetOrAdd(id, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).Task.WaitAsync(TimeSpan.FromSeconds(8));

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/json")
    };

    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }

    private static async Task<bool> ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action().WaitAsync(TimeSpan.FromSeconds(5)); return false; }
        catch (T) { return true; }
    }

    private sealed class ProbeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        private int _calls;
        private int _active;
        private int _maximum;
        public int CallCount => Volatile.Read(ref _calls);
        public int MaximumActive => Volatile.Read(ref _maximum);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            var active = Interlocked.Increment(ref _active);
            int previous;
            do { previous = Volatile.Read(ref _maximum); }
            while (active > previous && Interlocked.CompareExchange(ref _maximum, active, previous) != previous);
            try { return await send(request, cancellationToken); }
            finally { Interlocked.Decrement(ref _active); }
        }
    }

    private sealed class UnmeasuredContent(int length) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new NotSupportedException("Fixture is read through its bounded stream only.");
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new SpaceStream(length));
    }

    private sealed class SpaceStream(int remaining) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = Math.Min(count, remaining);
            buffer.AsSpan(offset, read).Fill((byte)' ');
            remaining -= read;
            return read;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = Math.Min(buffer.Length, remaining);
            buffer.Span[..read].Fill((byte)' ');
            remaining -= read;
            return ValueTask.FromResult(read);
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
