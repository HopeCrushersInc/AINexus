using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using AINexus.App;

namespace AINexus.Checks;

internal static class LocalChatChecks
{
    private static readonly Uri Endpoint = new("http://127.0.0.1:8080");

    internal static async Task RunAsync(Action<bool, string> check)
    {
        var requests = new List<string>();
        var mode = "stop";
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            check(request.Method == HttpMethod.Post && request.RequestUri == new Uri(Endpoint, "/v1/chat/completions")
                && request.Headers.Authorization is null, "Local chat sends only explicit unauthenticated completion requests to loopback");
            requests.Add(await request.Content!.ReadAsStringAsync(token));
            return mode == "http-fail" ? new(HttpStatusCode.ServiceUnavailable) : Reply(mode, "Fixture reply " + requests.Count);
        }));
        var session = new LocalChatSession(new LocalChatClient(http));
        check(await RejectsAsync<InvalidOperationException>(() => session.SendAsync("hello")),
            "Chat cannot generate until an endpoint and actual model are configured");
        session.Configure(Endpoint, "actual-local-id", "Fixture system instruction");
        check(requests.Count == 0 && session.History.Count == 0,
            "Configuring local chat makes no inference request and starts with empty session history");
        var first = await session.SendAsync("First prompt");
        check(!first.Truncated && first.Text == "Fixture reply 1" && session.History.Count == 2,
            "Successful local chat commits one real user/assistant pair");
        await session.SendAsync("Second prompt");
        using (var body = JsonDocument.Parse(requests[1]))
        {
            var messages = body.RootElement.GetProperty("messages");
            check(messages.GetArrayLength() == 4 && messages[0].GetProperty("role").GetString() == "system"
                && messages[1].GetProperty("content").GetString() == "First prompt"
                && messages[2].GetProperty("content").GetString() == first.Text
                && messages[3].GetProperty("content").GetString() == "Second prompt",
                "The second real chat request includes the prior conversation and configured system instruction");
            check(body.RootElement.GetProperty("max_tokens").GetInt32() == 512
                && body.RootElement.GetProperty("stream").GetBoolean() == false,
                "Interactive chat uses a bounded 512-token output policy without claiming streaming");
        }
        mode = "http-fail";
        var savedHistory = session.History.ToArray();
        check(await RejectsAsync<HttpRequestException>(() => session.SendAsync("Failed prompt"))
            && session.History.SequenceEqual(savedHistory),
            "Failed local chat does not create phantom prompt or assistant history");
        mode = "length";
        check((await session.SendAsync("Bounded response")).Truncated,
            "A length-limited local reply is returned with explicit truncation evidence");
        session.Configure(Endpoint, "actual-local-id", "Fixture system instruction");
        check(session.History.Count == 6, "Reusing unchanged model configuration preserves conversation history");
        session.Configure(Endpoint, "different-model", "Fixture system instruction");
        check(session.History.Count == 0, "Changing the selected model clears the previous model's conversation");
        await session.SendAsync("New model prompt");
        session.Configure(new Uri("http://127.0.0.1:8081"), "different-model");
        check(session.History.Count == 0, "Changing local endpoints clears conversation history");
        session.Clear();
        check(session.History.Count == 0, "New conversation clears session-only history without network activity");

        var client = new LocalChatClient(http);
        var beforeInvalid = requests.Count;
        foreach (var endpoint in new[] { "https://example.com", "http://127.0.0.1:8080/path", "http://user:password@127.0.0.1:8080", "http://127.0.0.1:8080/?q=x" })
            check(await RejectsAsync<ArgumentException>(() => client.SendAsync(new(endpoint), "fixture", [new("user", "hello")])),
                "Local chat rejects remote, credential-bearing and decorated endpoints before sending");
        check(await RejectsAsync<ArgumentException>(() => client.SendAsync(Endpoint, "fixture", [new("tool", "output")]))
            && await RejectsAsync<ArgumentException>(() => client.SendAsync(Endpoint, "fixture", [new("assistant", "not a prompt")]))
            && await RejectsAsync<ArgumentException>(() => client.SendAsync(Endpoint, "fixture", [new("user", new string('x', LocalChatClient.MaximumCharacters + 1))]))
            && await RejectsAsync<ArgumentException>(() => client.SendAsync(Endpoint, "fixture", Enumerable.Repeat(new ChatMessage("user", "x"), LocalChatClient.MaximumMessages + 1).ToArray()))
            && requests.Count == beforeInvalid,
            "Local chat rejects unsupported roles, absent final user prompts and over-limit history before HTTP");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            check(await RejectsAsync<OperationCanceledException>(() => client.SendAsync(Endpoint, "fixture", [new("user", "hello")], cancelled.Token))
                && requests.Count == beforeInvalid, "Precancelled chat makes no request");
        }

        await CheckResponseFailuresAsync(check);
        await CheckActiveCancellationAsync(check);
    }

    private static async Task CheckResponseFailuresAsync(Action<bool, string> check)
    {
        foreach (var content in new[]
        {
            "{bad json",
            "{\"choices\":[]}",
            "{\"choices\":[{\"finish_reason\":\"tool_calls\",\"message\":{\"role\":\"assistant\",\"content\":\"not text completion\"}}]}",
            "{\"choices\":[{\"finish_reason\":null,\"message\":{\"role\":\"assistant\",\"content\":\"unfinished\"}}]}",
            "{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"role\":123,\"content\":\"invalid role\"}}]}",
            "{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"role\":\"assistant\",\"content\":\" \"}}]}"
        })
        {
            using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(content, Encoding.UTF8, "application/json") })));
            var session = new LocalChatSession(new LocalChatClient(http));
            session.Configure(Endpoint, "fixture");
            check(await RejectsAsync<InvalidDataException>(() => session.SendAsync("hello")) && session.History.Count == 0,
                "Malformed, empty or unsupported local chat output cannot become conversation history");
        }
        using (var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect)))))
            check(await RejectsAsync<InvalidDataException>(() => new LocalChatClient(http).SendAsync(Endpoint, "fixture", [new("user", "hello")])),
                "Local chat rejects redirects rather than following server-supplied destinations");
        using (var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new ByteArrayContent(new byte[LocalChatClient.MaximumResponseBytes + 1]) }))))
            check(await RejectsAsync<InvalidDataException>(() => new LocalChatClient(http).SendAsync(Endpoint, "fixture", [new("user", "hello")])),
                "Local chat enforces its response byte limit before parsing oversized output");
    }

    private static async Task CheckActiveCancellationAsync(Action<bool, string> check)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Handler(async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return Reply("stop", "unreachable");
        }));
        var session = new LocalChatSession(new LocalChatClient(http));
        session.Configure(Endpoint, "fixture");
        using var cancellation = new CancellationTokenSource();
        var pending = session.SendAsync("Pending prompt", cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        check(await RejectsAsync<InvalidOperationException>(() => session.SendAsync("Concurrent prompt")),
            "One conversation cannot issue concurrent generation requests");
        var changeRejected = false;
        try { session.Configure(Endpoint, "another"); } catch (InvalidOperationException) { changeRejected = true; }
        check(changeRejected, "An active chat cannot silently switch models mid-response");
        cancellation.Cancel();
        check(await RejectsAsync<OperationCanceledException>(() => pending) && session.History.Count == 0,
            "Cancelling active chat preserves empty history and creates no phantom reply");
        session.Configure(Endpoint, "another");
        check(session.Model == "another", "Chat configuration becomes editable after cancellation completes");
    }

    private static HttpResponseMessage Reply(string finish, string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new
        { choices = new[] { new { finish_reason = finish, message = new { role = "assistant", content } } } }), Encoding.UTF8, "application/json")
    };

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
