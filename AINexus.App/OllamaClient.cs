using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace AINexus.App;

public sealed record OllamaModelInfo(
    string Name,
    long? SizeBytes,
    string? ParameterSize,
    string? QuantizationLevel,
    long? VramSizeBytes,
    DateTimeOffset? ModifiedAt,
    DateTimeOffset? ExpiresAt);

public sealed record OllamaSnapshot(
    DateTimeOffset ScannedAt,
    IReadOnlyList<OllamaModelInfo> AvailableModels,
    IReadOnlyList<OllamaModelInfo> RunningModels,
    bool AvailableModelsSucceeded,
    bool RunningModelsSucceeded,
    IReadOnlyList<string> Notes);

/// <summary>
/// Read-only inventory of an already running, local Ollama server. The caller owns the
/// injected HttpClient and MUST disable automatic redirects and proxies on its handler.
/// Use CreateLocalHttpClient for the supported handler configuration. This class never
/// launches a server, runs inference, downloads models, or changes the model library.
/// </summary>
public sealed class OllamaClient
{
    internal const int MaximumResponseBytes = 4 * 1024 * 1024;
    private readonly HttpClient _client;

    public OllamaClient(HttpClient client) => _client = client ?? throw new ArgumentNullException(nameof(client));

    /// <summary>The caller must dispose the returned client when no scans remain active.</summary>
    public static HttpClient CreateLocalHttpClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(3)
    }) { Timeout = Timeout.InfiniteTimeSpan };

    public async Task<OllamaSnapshot> ScanAsync(Uri endpoint, CancellationToken cancellationToken = default)
    {
        ValidateEndpoint(endpoint);
        cancellationToken.ThrowIfCancellationRequested();
        var scannedAt = DateTimeOffset.Now;
        // Only these two read-only endpoints are permitted by this inventory service.
        var availableTask = ReadEndpointAsync(new Uri(endpoint, "/api/tags"), "Available models", cancellationToken);
        var runningTask = ReadEndpointAsync(new Uri(endpoint, "/api/ps"), "Running models", cancellationToken);
        await Task.WhenAll(availableTask, runningTask);
        cancellationToken.ThrowIfCancellationRequested();
        var available = await availableTask;
        var running = await runningTask;
        var notes = new[] { available.Note, running.Note }.Where(note => note is not null).Select(note => note!).ToArray();
        return new OllamaSnapshot(scannedAt, available.Models, running.Models,
            available.Succeeded, running.Succeeded, Array.AsReadOnly(notes));
    }

    internal static void ValidateEndpoint(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme is not ("http" or "https") || !endpoint.IsLoopback
            || !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Query)
            || !string.IsNullOrEmpty(endpoint.Fragment) || endpoint.AbsolutePath != "/")
            throw new ArgumentException("Use a loopback HTTP or HTTPS server address with a port and no credentials, path, query, or fragment.", nameof(endpoint));
    }

    private async Task<(IReadOnlyList<OllamaModelInfo> Models, bool Succeeded, string? Note)> ReadEndpointAsync(
        Uri uri, string label, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            // This also detects a misconfigured injected handler, but the handler must reject
            // redirects itself so a local response cannot cause a request outside loopback.
            if (response.RequestMessage?.RequestUri is { } finalUri && finalUri != uri)
                throw new InvalidDataException("The server redirected the inventory request.");
            if ((int)response.StatusCode is >= 300 and < 400)
                throw new InvalidDataException("Redirects are not permitted for local inventory.");
            if (!response.IsSuccessStatusCode)
                return (Array.Empty<OllamaModelInfo>(), false, $"{label} unavailable: local server returned HTTP {(int)response.StatusCode}.");
            if (response.Content.Headers.ContentLength > MaximumResponseBytes)
                throw new InvalidDataException("The inventory response exceeds the 4 MiB size limit.");

            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var body = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(buffer, deadline.Token)) > 0)
            {
                if (body.Length + read > MaximumResponseBytes)
                    throw new InvalidDataException("The inventory response exceeds the 4 MiB size limit.");
                body.Write(buffer, 0, read);
            }
            using var json = JsonDocument.Parse(body.GetBuffer().AsMemory(0, checked((int)body.Length)),
                new JsonDocumentOptions { MaxDepth = 32 });
            return (ParseModels(json.RootElement), true, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (Array.Empty<OllamaModelInfo>(), false, $"{label} unavailable: the local request timed out.");
        }
        catch (OperationCanceledException) { throw; }
        catch (HttpRequestException)
        {
            return (Array.Empty<OllamaModelInfo>(), false, $"{label} unavailable: could not read the local Ollama server. Start Ollama separately if you want to use its model inventory.");
        }
        catch (JsonException)
        {
            return (Array.Empty<OllamaModelInfo>(), false, $"{label} unavailable: the local server returned invalid JSON.");
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            return (Array.Empty<OllamaModelInfo>(), false, $"{label} unavailable: {ex.Message}");
        }
    }

    // Official schemas: https://docs.ollama.com/api/tags and https://docs.ollama.com/api/ps
    internal static IReadOnlyList<OllamaModelInfo> ParseModels(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        return ParseModels(document.RootElement);
    }

    private static IReadOnlyList<OllamaModelInfo> ParseModels(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("models", out var models)
            || models.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The response does not contain a models array.");
        var result = new List<OllamaModelInfo>();
        foreach (var model in models.EnumerateArray())
        {
            if (model.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("The models array contains an invalid entry.");
            var name = ReadText(model, "name") ?? ReadText(model, "model");
            if (string.IsNullOrWhiteSpace(name))
                throw new InvalidDataException("The models array contains an entry without a model name.");
            var details = model.TryGetProperty("details", out var value) && value.ValueKind == JsonValueKind.Object
                ? value : default;
            result.Add(new OllamaModelInfo(name, ReadBytes(model, "size"), ReadText(details, "parameter_size"),
                ReadText(details, "quantization_level"), ReadBytes(model, "size_vram"),
                ReadDate(model, "modified_at"), ReadDate(model, "expires_at")));
        }
        return result.AsReadOnly();
    }

    private static string? ReadText(JsonElement model, string name) =>
        model.ValueKind == JsonValueKind.Object && model.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString() : null;

    private static long? ReadBytes(JsonElement model, string name) =>
        model.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var bytes) && bytes >= 0 ? bytes : null;

    private static DateTimeOffset? ReadDate(JsonElement model, string name) =>
        DateTimeOffset.TryParse(ReadText(model, name), CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date : null;
}
