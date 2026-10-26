using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace AINexus.App;

/// <summary>
/// Sends an explicitly requested prompt to an already running local Ollama server.
/// The caller owns the injected HttpClient; its handler MUST disable automatic redirects
/// and proxies (OllamaClient.CreateLocalHttpClient supplies this configuration).
/// Canceling stops the HTTP request; it does not guarantee that the server unloads a model
/// or immediately releases its resources. Prompts and responses are never logged here.
/// </summary>
public sealed class OllamaGenerationClient : ILocalGenerationClient
{
    internal const int MaximumResponseBytes = 8 * 1024 * 1024;
    private readonly HttpClient _client;

    public OllamaGenerationClient(HttpClient client) => _client = client ?? throw new ArgumentNullException(nameof(client));

    public async Task<string> GenerateAsync(Uri endpoint, string model, string systemPrompt,
        string prompt, CancellationToken cancellationToken = default)
    {
        OllamaClient.ValidateEndpoint(endpoint);
        var body = CreateRequestBody(model, systemPrompt, prompt);
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            var uri = new Uri(endpoint, "/api/generate");
            using var request = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.RequestMessage?.RequestUri is { } finalUri && finalUri != uri)
                throw new InvalidDataException("The local server redirected the generation request. Redirects are not permitted.");
            if ((int)response.StatusCode is >= 300 and < 400)
                throw new InvalidDataException("Redirects are not permitted for local generation requests.");
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Local Ollama generation returned HTTP {(int)response.StatusCode}.", null, response.StatusCode);
            if (response.Content.Headers.ContentLength > MaximumResponseBytes)
                throw new InvalidDataException("The generation response exceeds the 8 MiB size limit.");

            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var data = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(buffer, deadline.Token)) > 0)
            {
                if (data.Length + read > MaximumResponseBytes)
                    throw new InvalidDataException("The generation response exceeds the 8 MiB size limit.");
                data.Write(buffer, 0, read);
            }
            using var json = JsonDocument.Parse(data.GetBuffer().AsMemory(0, checked((int)data.Length)),
                new JsonDocumentOptions { MaxDepth = 32 });
            deadline.Token.ThrowIfCancellationRequested();
            return ParseResponse(json.RootElement);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The local generation request exceeded its time limit. Server-side model unloading or resource release has not been verified.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The local server returned invalid JSON for generation.", ex);
        }
    }

    // Official request and response schema: https://docs.ollama.com/api/generate
    internal static string CreateRequestBody(string model, string systemPrompt, string prompt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentNullException.ThrowIfNull(systemPrompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        return JsonSerializer.Serialize(new { model, system = systemPrompt, prompt, stream = false });
    }

    internal static string ParseResponse(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        return ParseResponse(document.RootElement);
    }

    private static string ParseResponse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The generation response must be a JSON object.");
        if (root.TryGetProperty("error", out var error) && error.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            throw new InvalidDataException("The local Ollama server reported a generation error; no completed response was returned.");
        if (!root.TryGetProperty("done", out var done) || done.ValueKind != JsonValueKind.True)
            throw new InvalidDataException("The local server did not confirm a completed generation.");
        if (!root.TryGetProperty("response", out var response) || response.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("The completed generation did not contain a text response.");
        return response.GetString()!;
    }
}
