using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace AINexus.App;

public sealed record ChatMessage(string Role, string Content);
public sealed record ChatReply(string Text, bool Truncated);

/// <summary>Explicit, bounded local chat. The caller owns the client and must disable redirects,
/// cookies and proxies; CreateHttpClient supplies that configuration. No runtime is started.
/// Cancelling HTTP does not guarantee immediate server-side generation/resource release.</summary>
public sealed class LocalChatClient(HttpClient http)
{
    public const int MaximumMessages = 40;
    public const int MaximumCharacters = 32768;
    internal const int MaximumResponseBytes = 1024 * 1024;
    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));

    public static HttpClient CreateHttpClient() => OllamaClient.CreateLocalHttpClient();

    public async Task<ChatReply> SendAsync(Uri endpoint, string model, IReadOnlyList<ChatMessage> messages,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        OllamaClient.ValidateEndpoint(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        if (model.Length > 256 || model.Any(char.IsControl)) throw new ArgumentException("Choose a valid reported model ID.", nameof(model));
        ValidateMessages(messages);
        if (_http.DefaultRequestHeaders.Authorization is not null || _http.DefaultRequestHeaders.Contains("Cookie"))
            throw new InvalidOperationException("Use the dedicated unauthenticated local chat client.");
        // Capture the caller's messages before the first await.
        var captured = messages.Select(message => new { role = message.Role, content = message.Content }).ToArray();
        var body = JsonSerializer.Serialize(new { model, messages = captured, stream = false, max_tokens = 512 });
        var uri = new Uri(endpoint, "/v1/chat/completions");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(180));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, uri)
            { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (response.RequestMessage?.RequestUri is { } finalUri && finalUri != uri
                || (int)response.StatusCode is >= 300 and < 400)
                throw new InvalidDataException("Redirects are not permitted for local chat.");
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Local chat returned HTTP {(int)response.StatusCode}.", null, response.StatusCode);
            if (response.Content.Headers.ContentLength > MaximumResponseBytes)
                throw new InvalidDataException("The local chat response exceeds its one-MiB limit.");
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            using var data = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) > 0)
            {
                if (data.Length + read > MaximumResponseBytes)
                    throw new InvalidDataException("The local chat response exceeds its one-MiB limit.");
                data.Write(buffer, 0, read);
            }
            using var document = JsonDocument.Parse(data.GetBuffer().AsMemory(0, checked((int)data.Length)),
                new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() != 1)
                throw new InvalidDataException("Local chat did not return one completed response.");
            var choice = choices[0];
            if (choice.ValueKind != JsonValueKind.Object || !choice.TryGetProperty("finish_reason", out var finish)
                || finish.ValueKind != JsonValueKind.String || finish.GetString() is not ("stop" or "length")
                || !choice.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object
                || !message.TryGetProperty("role", out var role) || role.ValueKind != JsonValueKind.String || role.GetString() != "assistant"
                || !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(content.GetString()))
                throw new InvalidDataException("Local chat returned empty, unsupported or incomplete output.");
            deadline.Token.ThrowIfCancellationRequested();
            return new(content.GetString()!, finish.GetString() == "length");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException("Local chat exceeded 180 seconds. Immediate server-side resource release is not verified."); }
        catch (JsonException ex)
        { throw new InvalidDataException("Local chat returned invalid JSON.", ex); }
    }

    internal static void ValidateMessages(IReadOnlyList<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (messages.Count is < 1 or > MaximumMessages)
            throw new ArgumentException("A chat request supports 1–40 messages. Start a new conversation when full.", nameof(messages));
        long characters = 0;
        foreach (var message in messages)
        {
            if (message is null || message.Role is not ("system" or "user" or "assistant") || message.Content is null
                || message.Role != "system" && string.IsNullOrWhiteSpace(message.Content))
                throw new ArgumentException("Chat messages must contain system, user or assistant text.", nameof(messages));
            characters += message.Content.Length;
        }
        if (characters > MaximumCharacters)
            throw new ArgumentException("Conversation text exceeds 32768 characters. Start a new conversation.", nameof(messages));
        if (messages[^1].Role != "user") throw new ArgumentException("The last chat message must be a user prompt.", nameof(messages));
    }
}

/// <summary>Session-only history. Failed/cancelled requests never add phantom user or assistant turns.</summary>
public sealed class LocalChatSession(LocalChatClient client)
{
    private readonly LocalChatClient _client = client ?? throw new ArgumentNullException(nameof(client));
    private readonly object _gate = new();
    private readonly List<ChatMessage> _history = [];
    private bool _busy;
    public Uri? Endpoint { get; private set; }
    public string? Model { get; private set; }
    public string SystemPrompt { get; private set; } = "";
    public IReadOnlyList<ChatMessage> History { get { lock (_gate) return Array.AsReadOnly(_history.ToArray()); } }

    public void Configure(Uri endpoint, string model, string systemPrompt = "")
    {
        OllamaClient.ValidateEndpoint(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentNullException.ThrowIfNull(systemPrompt);
        if (model.Length > 256 || model.Any(char.IsControl) || systemPrompt.Length > LocalChatClient.MaximumCharacters)
            throw new ArgumentException("Chat configuration exceeds its text limits.");
        lock (_gate)
        {
            if (_busy) throw new InvalidOperationException("Finish or cancel the active message before changing models.");
            if (Endpoint != endpoint || Model != model || SystemPrompt != systemPrompt) _history.Clear();
            Endpoint = endpoint;
            Model = model;
            SystemPrompt = systemPrompt;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            if (_busy) throw new InvalidOperationException("Finish or cancel the active message before clearing the conversation.");
            _history.Clear();
        }
    }

    public async Task<ChatReply> SendAsync(string prompt, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        Uri endpoint;
        string model;
        List<ChatMessage> request;
        lock (_gate)
        {
            if (_busy) throw new InvalidOperationException("A message is already running in this conversation.");
            endpoint = Endpoint ?? throw new InvalidOperationException("Select a running local model before chatting.");
            model = Model ?? throw new InvalidOperationException("Select a running local model before chatting.");
            request = [];
            if (SystemPrompt.Length > 0) request.Add(new("system", SystemPrompt));
            request.AddRange(_history);
            request.Add(new("user", prompt));
            LocalChatClient.ValidateMessages(request);
            if (request.Count >= LocalChatClient.MaximumMessages)
                throw new InvalidOperationException("Conversation message limit reached. Start a new conversation.");
            _busy = true;
        }
        try
        {
            var reply = await _client.SendAsync(endpoint, model, request, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            lock (_gate)
            {
                _history.Add(new("user", prompt));
                _history.Add(new("assistant", reply.Text));
            }
            return reply;
        }
        finally { lock (_gate) _busy = false; }
    }
}
