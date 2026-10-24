using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using AINexus.App;

namespace AINexus.Checks;

internal static class LlamaCppChecks
{
    private static readonly Uri Endpoint = new("http://127.0.0.1:8080");
    private const string Finished = "{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"role\":\"assistant\",\"content\":\"Fixture llama.cpp answer\"}}]}";

    public static async Task RunAsync(Action<bool, string> check)
    {
        CheckSchema(check);
        await CheckHttpAsync(check);
        await CheckBoundsAndCancellationAsync(check);
        await CheckSchedulerRoutingAsync(check);
    }

    /// <summary>Explicit opt-in only. Uses a pre-existing server, its reported model ID, and actual Windows telemetry.</summary>
    public static async Task RunLiveAsync(Uri endpoint, string model, Action<bool, string> check)
    {
        using var http = OllamaClient.CreateLocalHttpClient();
        var client = new LlamaCppClient(http);
        var models = await client.GetModelsAsync(endpoint);
        check(models.Contains(model, StringComparer.Ordinal), "Live llama.cpp server reports the requested actual model identifier");
        var monitor = new ResourceMonitor();
        await using var scheduler = new AgentScheduler(client, monitor.SampleAsync, client);
        var completion = new TaskCompletionSource<AgentJobUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = new AgentRequest(Guid.NewGuid(), "Live llama.cpp scheduler smoke test", endpoint, model,
            "Answer briefly in plain text. No tool use.", "Reply with exactly these two words: Portal ready.",
            new ResourcePolicy(), LocalGenerationRuntime.LlamaCpp);
        scheduler.Updated += update =>
        {
            if (update.Id == request.Id && update.Status is "Completed" or "Blocked" or "Failed" or "Cancelled")
                completion.TrySetResult(update);
        };
        check(scheduler.TryEnqueue(request), "Live llama.cpp request enters the serial agent queue");
        var result = await completion.Task.WaitAsync(TimeSpan.FromMinutes(3));
        check(result.Status == "Completed" && !string.IsNullOrWhiteSpace(result.Response),
            $"Live llama.cpp scheduler returns actual generated text after telemetry admission ({result.Status}: {result.Detail})");
        Console.WriteLine($"Live llama.cpp smoke-test response: {result.Response}");
    }

    private static void CheckSchema(Action<bool, string> check)
    {
        var models = LlamaCppClient.ParseModels("{\"data\":[{\"id\":\"fixture-model\"},{\"id\":\"fixture-model\"},{\"id\":\"Fixture-model\"}]}");
        check(models.SequenceEqual(new[] { "fixture-model", "Fixture-model" }), "llama.cpp inventory preserves actual case-sensitive model IDs without duplicate entries");
        check(LlamaCppClient.ParseModels("{\"data\":[]}").Count == 0, "An empty llama.cpp inventory stays empty");
        foreach (var invalid in new[] { "[]", "{}", "{\"data\":[{}]}", "{\"data\":[{\"id\":12}]}" })
            check(Throws<InvalidDataException>(() => LlamaCppClient.ParseModels(invalid)), "Malformed llama.cpp inventory cannot fabricate a model identity");

        const string prompt = "Fixture \"quoted\" input\nUnicode: \u03bb";
        using var document = JsonDocument.Parse(LlamaCppClient.CreateRequestBody("fixture-model", "Fixture system", prompt));
        var body = document.RootElement;
        check(body.GetProperty("model").GetString() == "fixture-model" && body.GetProperty("stream").ValueKind == JsonValueKind.False
              && body.GetProperty("max_tokens").GetInt32() == 128
              && body.GetProperty("messages")[0].GetProperty("role").GetString() == "system"
              && body.GetProperty("messages")[0].GetProperty("content").GetString() == "Fixture system"
              && body.GetProperty("messages")[1].GetProperty("role").GetString() == "user"
              && body.GetProperty("messages")[1].GetProperty("content").GetString() == prompt,
            "llama.cpp requests contain actual messages, no streaming, and a bounded output-token policy");
        check(Throws<ArgumentException>(() => LlamaCppClient.CreateRequestBody("", "", "Fixture"))
              && Throws<ArgumentException>(() => LlamaCppClient.CreateRequestBody("fixture", "", " "))
              && Throws<ArgumentOutOfRangeException>(() => LlamaCppClient.CreateRequestBody("fixture", "", "Fixture", 0)),
            "llama.cpp requests require an actual model, prompt, and positive output-token policy");
        check(LlamaCppClient.ParseResponse(Finished) == "Fixture llama.cpp answer", "llama.cpp exposes actual assistant text only after a normal stop");
        foreach (var invalid in new[]
        {
            "{}", "{\"choices\":[]}", "{\"choices\":[{}]}",
            "{\"choices\":[{\"finish_reason\":\"length\",\"message\":{\"content\":\"truncated\"}}]}",
            "{\"choices\":[{\"finish_reason\":\"tool_calls\",\"message\":{\"content\":\"text\"}}]}",
            "{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":null}}]}",
            "{\"error\":\"Fixture server error\",\"choices\":[]}"
        }) check(Throws<InvalidDataException>(() => LlamaCppClient.ParseResponse(invalid)),
            "Missing, truncated, tool-request, or erroneous llama.cpp responses cannot claim completed text generation");
    }

    private static async Task CheckHttpAsync(Action<bool, string> check)
    {
        var requests = new List<string>();
        string? sentBody = null;
        using (var http = new HttpClient(new FixtureHandler(async (request, token) =>
        {
            requests.Add(request.Method + " " + request.RequestUri!.AbsolutePath);
            if (request.Content is not null) sentBody = await request.Content.ReadAsStringAsync(token);
            return Json(request.Method == HttpMethod.Get ? "{\"data\":[{\"id\":\"fixture-model\"}]}" : Finished);
        })))
        {
            var client = new LlamaCppClient(http);
            check((await client.GetModelsAsync(Endpoint)).Single() == "fixture-model" && requests.SequenceEqual(new[] { "GET /v1/models" }),
                "llama.cpp discovery only reads the local model inventory and never starts inference");
            check(await client.GenerateAsync(Endpoint, "fixture-model", "Fixture system", "Fixture prompt") == "Fixture llama.cpp answer"
                  && requests.Last() == "POST /v1/chat/completions", "Explicit llama.cpp generation uses the intended local chat endpoint");
            using var body = JsonDocument.Parse(sentBody!);
            check(body.RootElement.GetProperty("model").GetString() == "fixture-model", "llama.cpp HTTP generation uses the selected actual model identifier");
            var before = requests.Count;
            check(await ThrowsAsync<ArgumentException>(() => client.GetModelsAsync(new Uri("https://example.com")))
                  && await ThrowsAsync<ArgumentException>(() => client.GenerateAsync(new Uri("http://user:password@localhost:8080"), "fixture", "", "Fixture"))
                  && requests.Count == before, "llama.cpp rejects remote and credential-bearing endpoints before HTTP");
        }

        using (var http = new HttpClient(new FixtureHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
               { Content = new StringContent("Fixture private server error body") }))))
        {
            var client = new LlamaCppClient(http);
            check(await ThrowsAsync<HttpRequestException>(() => client.GetModelsAsync(Endpoint))
                  && await ThrowsAsync<HttpRequestException>(() => client.GenerateAsync(Endpoint, "fixture", "", "Fixture")),
                "Unavailable llama.cpp endpoints report HTTP failure rather than fake inventory or generation");
        }
        using (var http = new HttpClient(new FixtureHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect)))))
        {
            var client = new LlamaCppClient(http);
            check(await ThrowsAsync<InvalidDataException>(() => client.GetModelsAsync(Endpoint))
                  && await ThrowsAsync<InvalidDataException>(() => client.GenerateAsync(Endpoint, "fixture", "", "Fixture")),
                "llama.cpp inventory and generation both reject redirects");
        }
    }

    private static async Task CheckBoundsAndCancellationAsync(Action<bool, string> check)
    {
        foreach (var generation in new[] { false, true })
        {
            var cap = generation ? LlamaCppClient.MaximumGenerationResponseBytes : LlamaCppClient.MaximumModelResponseBytes;
            using (var http = new HttpClient(new FixtureHandler((_, _) =>
            {
                var response = Json("{}");
                response.Content.Headers.ContentLength = cap + 1L;
                return Task.FromResult(response);
            })))
            {
                var client = new LlamaCppClient(http);
                check(await ThrowsAsync<InvalidDataException>(() => generation
                    ? client.GenerateAsync(Endpoint, "fixture", "", "Fixture") : client.GetModelsAsync(Endpoint)),
                    "llama.cpp rejects an oversized declared response before reading it");
            }
            using (var http = new HttpClient(new FixtureHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                   { Content = new UnmeasuredContent(cap + 1) }))))
            {
                var client = new LlamaCppClient(http);
                check(await ThrowsAsync<InvalidDataException>(() => generation
                    ? client.GenerateAsync(Endpoint, "fixture", "", "Fixture") : client.GetModelsAsync(Endpoint)),
                    "llama.cpp enforces response bounds without relying on Content-Length");
            }
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancellation = new CancellationTokenSource();
            using var cancelledHttp = new HttpClient(new FixtureHandler(async (_, token) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
                return Json("{}");
            }));
            var cancelledClient = new LlamaCppClient(cancelledHttp);
            Task pending = generation ? cancelledClient.GenerateAsync(Endpoint, "fixture", "", "Fixture", cancellation.Token)
                : cancelledClient.GetModelsAsync(Endpoint, cancellation.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            check(await ThrowsAsync<OperationCanceledException>(() => pending), "llama.cpp propagates cancellation into active local HTTP requests");
        }
    }

    private static async Task CheckSchedulerRoutingAsync(Action<bool, string> check)
    {
        var routes = new ConcurrentQueue<string>();
        using var http = new HttpClient(new FixtureHandler((request, _) =>
        {
            routes.Enqueue(request.RequestUri!.AbsolutePath);
            return Task.FromResult(Json(request.RequestUri.AbsolutePath == "/api/generate"
                ? "{\"done\":true,\"response\":\"Fixture Ollama answer\"}" : Finished));
        }));
        var llama = new LlamaCppClient(http);
        var ollama = new OllamaGenerationClient(http);
        static Task<ResourceSnapshot> Sample(CancellationToken _) => Task.FromResult(new ResourceSnapshot(
            DateTimeOffset.Now, 25, 8, 16, [], [], "All processors")); // Explicit actual-like test fixture, never live telemetry.
        await using var scheduler = new AgentScheduler(ollama, Sample, llama);
        var results = new ConcurrentDictionary<Guid, TaskCompletionSource<AgentJobUpdate>>();
        scheduler.Updated += update =>
        {
            if (update.Status is "Completed" or "Failed" or "Blocked" or "Cancelled")
                results.GetOrAdd(update.Id, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult(update);
        };
        var first = new AgentRequest(Guid.NewGuid(), "Fixture Ollama route", Endpoint, "fixture-model", "", "Fixture", new ResourcePolicy());
        var second = first with { Id = Guid.NewGuid(), Name = "Fixture llama.cpp route", Runtime = LocalGenerationRuntime.LlamaCpp };
        check(scheduler.TryEnqueue(first) && scheduler.TryEnqueue(second), "One serial scheduler accepts explicitly captured Ollama and llama.cpp tasks");
        var firstResult = await results.GetOrAdd(first.Id, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).Task.WaitAsync(TimeSpan.FromSeconds(8));
        var secondResult = await results.GetOrAdd(second.Id, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).Task.WaitAsync(TimeSpan.FromSeconds(8));
        check(firstResult.Status == "Completed" && firstResult.Response == "Fixture Ollama answer"
              && secondResult.Status == "Completed" && secondResult.Response == "Fixture llama.cpp answer"
              && routes.SequenceEqual(new[] { "/api/generate", "/v1/chat/completions" }),
            "Queued tasks retain their selected runtime and return that backend's actual response in order");
        await using var unconfigured = new AgentScheduler(ollama, Sample);
        check(Throws<ArgumentException>(() => unconfigured.TryEnqueue(second))
              && Throws<ArgumentException>(() => scheduler.TryEnqueue(second with { Id = Guid.NewGuid(), Runtime = (LocalGenerationRuntime)999 })),
            "Unconfigured or unknown scheduler backends cannot silently fall back to a different runtime");
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
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

    private sealed class FixtureHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class UnmeasuredContent(int length) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new NotSupportedException();
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
