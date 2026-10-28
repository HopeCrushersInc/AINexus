using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AINexus.App;

/// <summary>Reads attribution and scores only; never follows reported source URLs or loads a model.</summary>
public sealed partial class ModelEvaluationClient(HttpClient http, ModelEvaluationCache? cache = null)
{
    public static Uri RequestUri(string repository)
    {
        if (repository is null || !RepositoryName().IsMatch(repository))
            throw new ArgumentException("Enter an exact publisher/model repository identifier.", nameof(repository));
        return new("https://huggingface.co/api/models/" + string.Join('/', repository.Split('/').Select(Uri.EscapeDataString))
            + "?expand=evalResults");
    }

    public async Task<ModelEvaluationSnapshot> ReadAsync(string repository, CancellationToken token = default)
    {
        var uri = RequestUri(repository);
        if (http.DefaultRequestHeaders.Authorization is not null || http.DefaultRequestHeaders.Contains("Cookie"))
            throw new InvalidOperationException("Use a dedicated unauthenticated public metadata client.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("AINexus-Evidence/1.0");
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (response.RequestMessage?.RequestUri is { } final && final != uri
            || (int)response.StatusCode is >= 300 and < 400)
            throw new InvalidDataException("Evaluation source redirects are not accepted.");
        response.EnsureSuccessStatusCode();
        const int maximumBytes = 1024 * 1024;
        if (response.Content.Headers.ContentLength > maximumBytes)
            throw new InvalidDataException("Evaluation metadata exceeds the one MiB limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, deadline.Token)) > 0)
        {
            if (buffer.Length + read > maximumBytes) throw new InvalidDataException("Evaluation metadata exceeds the one MiB limit.");
            buffer.Write(chunk, 0, read);
        }
        using var json = JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, checked((int)buffer.Length)), new JsonDocumentOptions { MaxDepth = 24 });
        token.ThrowIfCancellationRequested();
        var snapshot = ModelEvaluationParser.Parse(repository, json.RootElement, DateTimeOffset.UtcNow);
        if (cache is not null)
        {
            try
            {
                await cache.SaveAsync(repository, json.RootElement, snapshot.RetrievedAt, token);
                snapshot = snapshot with { Notes = snapshot.Notes.Append("Source snapshot saved for offline review with its original retrieval time. Saved evidence is not refreshed or independently verified by Nexus.").ToArray() };
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                snapshot = snapshot with { Notes = snapshot.Notes.Append("Evidence is available in this session, but saving failed. Previous saved evidence was preserved where possible; no saved refresh is claimed.").ToArray() };
            }
        }
        return snapshot;
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,95}/[A-Za-z0-9][A-Za-z0-9._-]{0,95}$", RegexOptions.CultureInvariant)]
    private static partial Regex RepositoryName();
}
