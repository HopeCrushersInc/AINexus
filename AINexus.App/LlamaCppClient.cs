using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace AINexus.App;

/// <summary>
/// Local llama.cpp server inventory and explicitly requested chat generation. The caller owns
/// the injected HttpClient and MUST disable redirects and proxies. OllamaClient.CreateLocalHttpClient
/// provides that transport configuration. This adapter never starts a server or downloads models.
/// Cancellation closes the request and does not guarantee immediate server-side resource release.
/// </summary>
public sealed class LlamaCppClient : ILocalGenerationClient
{
    internal const int MaximumModelResponseBytes = 4 * 1024 * 1024;
    internal const int MaximumGenerationResponseBytes = 8 * 1024 * 1024;
    private readonly HttpClient _client;
    private readonly int _maximumTokens;

    /// <param name="maximumTokens">Output token policy, not a hardware specification. Defaults to a short 128-token response.</param>
    public LlamaCppClient(HttpClient client, int maximumTokens = 128)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumTokens);
        _maximumTokens = maximumTokens;
    }

    public async Task<IReadOnlyList<string>> GetModelsAsync(Uri endpoint, CancellationToken cancellationToken = default)
    {
        OllamaClient.ValidateEndpoint(endpoint);
        using var document = await SendJsonAsync(HttpMethod.Get, new Uri(endpoint, "/v1/models"), null,
            MaximumModelResponseBytes, TimeSpan.FromSeconds(10), cancellationToken);
        return ParseModels(document.RootElement);
    }

    public async Task<string> GenerateAsync(Uri endpoint, string model, string systemPrompt,
        string prompt, CancellationToken cancellationToken = default)
    {
        OllamaClient.ValidateEndpoint(endpoint);
        var body = CreateRequestBody(model, systemPrompt, prompt, _maximumTokens);
        using var document = await SendJsonAsync(HttpMethod.Post, new Uri(endpoint, "/v1/chat/completions"), body,
            MaximumGenerationResponseBytes, TimeSpan.FromMinutes(2), cancellationToken);
        return ParseResponse(document.RootElement);
    }

    private async Task<JsonDocument> SendJsonAsync(HttpMethod method, Uri uri, string? body,
        int maximumBytes, TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            using var request = new HttpRequestMessage(method, uri);
            if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.RequestMessage?.RequestUri is { } finalUri && finalUri != uri)
                throw new InvalidDataException("The local runtime redirected the request. Redirects are not permitted.");
            if ((int)response.StatusCode is >= 300 and < 400)
                throw new InvalidDataException("Redirects are not permitted for local runtime requests.");
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Local llama.cpp server returned HTTP {(int)response.StatusCode}.", null, response.StatusCode);
            if (response.Content.Headers.ContentLength > maximumBytes)
                throw new InvalidDataException("The local runtime response exceeds its size limit.");

            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var data = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(buffer, deadline.Token)) > 0)
            {
                if (data.Length + read > maximumBytes)
                    throw new InvalidDataException("The local runtime response exceeds its size limit.");
                data.Write(buffer, 0, read);
            }
            deadline.Token.ThrowIfCancellationRequested();
            return JsonDocument.Parse(data.GetBuffer().AsMemory(0, checked((int)data.Length)),
                new JsonDocumentOptions { MaxDepth = 32 });
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The local runtime request exceeded its time limit. Server-side resource release has not been verified.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The local llama.cpp server returned invalid JSON.", ex);
        }
    }

    // Primary server API: https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md
    internal static string CreateRequestBody(string model, string systemPrompt, string prompt, int maximumTokens = 128)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentNullException.ThrowIfNull(systemPrompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumTokens);
        return JsonSerializer.Serialize(new
        {
            model,
            messages = new[] { new { role = "system", content = systemPrompt }, new { role = "user", content = prompt } },
            stream = false,
            max_tokens = maximumTokens
        });
    }

    internal static IReadOnlyList<string> ParseModels(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        return ParseModels(document.RootElement);
    }

    private static IReadOnlyList<string> ParseModels(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var models)
            || models.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The local runtime response does not contain a model data array.");
        var result = new List<string>();
        foreach (var model in models.EnumerateArray())
        {
            if (model.ValueKind != JsonValueKind.Object || !model.TryGetProperty("id", out var id)
                || id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString()))
                throw new InvalidDataException("The local runtime returned a model without an actual identifier.");
            var name = id.GetString()!;
            if (!result.Contains(name, StringComparer.Ordinal)) result.Add(name);
        }
        return result.AsReadOnly();
    }

    internal static string ParseResponse(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        return ParseResponse(document.RootElement);
    }

    private static string ParseResponse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The local runtime generation response must be a JSON object.");
        if (root.TryGetProperty("error", out var error) && error.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            throw new InvalidDataException("The local runtime reported a generation error; no completed response was returned.");
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            throw new InvalidDataException("The local runtime returned no generation choices.");
        var choice = choices[0];
        if (choice.ValueKind != JsonValueKind.Object || !choice.TryGetProperty("finish_reason", out var finish)
            || finish.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("The local runtime did not confirm a completed generation.");
        if (finish.GetString() == "length")
            throw new InvalidDataException("Generation reached the configured output token limit and was truncated. It cannot be reported as a completed response.");
        if (finish.GetString() != "stop")
            throw new InvalidDataException("The local runtime did not confirm a normally completed text generation.");
        if (!choice.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("The completed generation did not contain an actual text response.");
        return content.GetString()!;
    }
}
