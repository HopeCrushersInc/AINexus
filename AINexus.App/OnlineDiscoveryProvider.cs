using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AINexus.App;

/// <summary>
/// Explicit, metadata-only online searches. Sources are fixed public APIs; source documents
/// are evidence, never executable instructions. No authentication, weights or installers are used.
/// </summary>
public sealed partial class VerifiedOnlineDiscoveryProvider(HttpClient httpClient) : IOnlineDiscoveryProvider
{
    internal const int MaximumResponseBytes = 4 * 1024 * 1024;
    private readonly HttpClient _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

    public static IReadOnlyList<DiscoveryWorkflow> Workflows { get; } = Array.AsReadOnly(new[]
    {
        new DiscoveryWorkflow(DiscoveryInterest.LocalAssistants, "Chat & writing", "Search popular, trending and recently updated Hugging Face text-generation GGUF repositories. Choose a search view or browse source files. Source signals are not quality ratings; hardware estimates remain unverified."),
        new DiscoveryWorkflow(DiscoveryInterest.Coding, "Coding", "Search GGUF model metadata; coding ability requires separate evaluation."),
        new DiscoveryWorkflow(DiscoveryInterest.Images, "Images & artwork", "Search public image-artifact and tool metadata. Checkpoint completeness and hardware fit require separate evidence."),
        new DiscoveryWorkflow(DiscoveryInterest.MusicAudio, "Music & audio", "Search online tool sources; automated installation support is not yet implemented for this workflow."),
        new DiscoveryWorkflow(DiscoveryInterest.Video, "Video", "Search public text-to-video and image-to-video model repositories and video tools. Complete model files, runtime setup, hardware fit and installation support require separate verification."),
        new DiscoveryWorkflow(DiscoveryInterest.Speech, "Speech & voice", "Search online tool sources; automated installation support is not yet implemented for this workflow."),
        new DiscoveryWorkflow(DiscoveryInterest.ThreeD, "3D art & animation", "Search online tool sources; automated installation support is not yet implemented for this workflow."),
        new DiscoveryWorkflow(DiscoveryInterest.Cad, "CAD design", "Search online tool sources; automated installation support is not yet implemented for this workflow."),
        new DiscoveryWorkflow(DiscoveryInterest.ThreeDPrinting, "3D printing & slicing", "Search online tool sources; automated installation support is not yet implemented for this workflow."),
        new DiscoveryWorkflow(DiscoveryInterest.GameDevelopment, "Game development", "Search online tool sources; automated installation support is not yet implemented for this workflow."),
        new DiscoveryWorkflow(DiscoveryInterest.ReverseEngineering, "Reverse engineering", "Search online tool sources; automated installation support is not yet implemented for this workflow."),
        new DiscoveryWorkflow(DiscoveryInterest.Streaming, "Streaming & recording", "Search online tool sources; automated installation support is not yet implemented for this workflow."),
        new DiscoveryWorkflow(DiscoveryInterest.Agents, "Automation & agents", "Search GGUF model metadata; tool-use ability is not inferred from a model name.")
    });

    public static bool Supports(DiscoveryInterest interest) => interest is
        DiscoveryInterest.LocalAssistants or DiscoveryInterest.Coding or DiscoveryInterest.Agents or DiscoveryInterest.Images or DiscoveryInterest.Video;

    /// <summary>Caller owns this dedicated unauthenticated metadata client.</summary>
    public static HttpClient CreateHttpClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(5)
    }) { Timeout = Timeout.InfiniteTimeSpan };

    public async Task<OnlineDiscoverySnapshot> SearchAsync(OnlineDiscoveryRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(request.Interest)) throw new ArgumentOutOfRangeException(nameof(request.Interest));
        var textSearch = request.Interest is DiscoveryInterest.LocalAssistants or DiscoveryInterest.Coding or DiscoveryInterest.Agents;
        var maximumResults = textSearch ? 48 : 12;
        if (request.Limit < 1 || request.Limit > maximumResults) throw new ArgumentOutOfRangeException(nameof(request.Limit), $"Choose between 1 and {maximumResults} results for this category.");
        if (!Enum.IsDefined(request.SearchOrder)) throw new ArgumentOutOfRangeException(nameof(request.SearchOrder));
        if (request.Query is null || request.Query.Length > 160 || request.Query.Any(char.IsControl))
            throw new ArgumentException("Search text must contain at most 160 characters without control characters.", nameof(request));
        if (request.RuntimeReleaseTag is not null && !RuntimeTag().IsMatch(request.RuntimeReleaseTag))
            throw new ArgumentException("Choose an explicit release tag using letters, digits, dots, underscores or hyphens (maximum 128 characters).", nameof(request));
        cancellationToken.ThrowIfCancellationRequested();
        if (_http.DefaultRequestHeaders.Authorization is not null)
            throw new InvalidOperationException("Use a dedicated unauthenticated client for public discovery.");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        if (request.Interest == DiscoveryInterest.Images && request.RuntimeReleaseTag is null)
            return await SearchImageArtifactsAndToolsAsync(request, deadline.Token, cancellationToken);
        if (request.Interest == DiscoveryInterest.Video && request.RuntimeReleaseTag is null)
            return await SearchVideoModelsAndToolsAsync(request, deadline.Token, cancellationToken);
        if (!Supports(request.Interest) && request.RuntimeReleaseTag is null)
        {
            try { return await SearchWorkflowToolsAsync(request, deadline.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { return Snapshot([], ["The online tool metadata search timed out. No tool was selected or installed."]); }
            catch (Exception ex) when (IsSourceFailure(ex))
            { return Snapshot([], ["Online tool source unavailable: " + FailureMessage(ex)]); }
        }
        var items = new List<DiscoveredItem>();
        var notes = new List<string>();
        if (request.RuntimeReleaseTag is { } requestedTag)
        {
            try { items.Add(await ReadRuntimeAsync(request.Interest, deadline.Token, requestedTag)); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { notes.Add("The release lookup deadline was reached. No alternate release was selected."); }
            catch (Exception ex) when (IsSourceFailure(ex))
            { notes.Add("The explicitly requested runtime release is unavailable: " + FailureMessage(ex)); }
            cancellationToken.ThrowIfCancellationRequested();
            notes.Add("Explicit runtime release metadata only. No model search, release substitution, download or installation was performed.");
            return Snapshot(items, notes);
        }
        try
        {
            var modelSlots = request.Limit;
            var exactRepositoryQuery = request.Query.Trim();
            if (IsRepositoryId(exactRepositoryQuery))
            {
                if (request.Interest is DiscoveryInterest.Coding or DiscoveryInterest.Agents)
                    notes.Add("You explicitly chose this repository. No coding, tool-use or agent capability is inferred or certified for its files.");
                notes.Add($"Exact Hugging Face repository lookup: {exactRepositoryQuery}. Up to {modelSlots} alphabetically listed standalone GGUF files; no broad search or repository substitution. No pagination is followed; split models and other formats require separate support.");
                notes.Add("File ordering is not a quality rating or recommendation. Parameter counts are not inferred from repository or file names.");
                items.AddRange(await ReadRepositoryAsync(exactRepositoryQuery, request.Interest, modelSlots, notes, deadline.Token));
            }
            else
            {
                await SearchBroadTextModelsAsync(request, modelSlots, items, notes, deadline.Token);
            }
            if (items.Count == 0) notes.Add("No supported standalone GGUF artifacts were found in the bounded search. Try a publisher or model name.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            notes.Add("The 60-second discovery deadline was reached. Results are partial.");
        }
        catch (Exception ex) when (IsSourceFailure(ex))
        {
            notes.Add("Online discovery could not complete: " + FailureMessage(ex));
        }
        cancellationToken.ThrowIfCancellationRequested();
        notes.Add("Metadata is publisher-supplied, not a trust or compatibility certification. No software or model weights were installed.");
        return Snapshot(items, notes);
    }

    private async Task<IReadOnlyList<DiscoveredItem>> ReadRepositoryAsync(string repository, DiscoveryInterest interest,
        int limit, List<string> notes, CancellationToken token)
    {
        var repoPath = EncodePath(repository);
        using var info = await GetJsonAsync(new Uri($"https://huggingface.co/api/models/{repoPath}"), token);
        var root = info.RootElement;
        var reportedId = ReadString(root, "id") ?? ReadString(root, "modelId");
        if (reportedId != repository) throw new InvalidDataException("The source metadata did not match the requested repository.");
        var revision = ReadString(root, "sha");
        if (revision is null || !RevisionRegex().IsMatch(revision)) throw new InvalidDataException("The source did not provide a pinned commit revision.");
        var license = root.TryGetProperty("cardData", out var card) ? ReadString(card, "license") : null;
        if (license is null && root.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
            license = tags.EnumerateArray().Where(tag => tag.ValueKind == JsonValueKind.String).Select(tag => tag.GetString())
                .FirstOrDefault(tag => tag?.StartsWith("license:", StringComparison.Ordinal) == true)?[8..];
        var gated = !root.TryGetProperty("gated", out var gate) || gate.ValueKind != JsonValueKind.False;
        var pipeline = ReadString(root, "pipeline_tag");
        var signals = ReadDiscoverySignals(root);
        var treeUri = new Uri($"https://huggingface.co/api/models/{repoPath}/tree/{revision}?recursive=true&expand=false&limit=1000");
        using var tree = await GetJsonAsync(treeUri, token);
        if (tree.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("The source repository tree was not a list.");
        var files = new List<(string Path, long? Bytes, string? Hash)>();
        var shardFound = false;
        foreach (var file in tree.RootElement.EnumerateArray().Take(1000))
        {
            var path = ReadString(file, "path");
            if (ReadString(file, "type") != "file" || path is null || !path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)) continue;
            if (!IsArtifactPath(path)) { notes.Add("An invalid artifact path was skipped."); continue; }
            if (ShardRegex().IsMatch(path)) { shardFound = true; continue; }
            if (Path.GetFileName(path).Contains("mmproj", StringComparison.OrdinalIgnoreCase))
            {
                notes.Add($"A named multimodal projector in {repository} was skipped; a companion file is not a complete text model.");
                continue;
            }
            if (TextCompanionName().IsMatch(Path.GetFileNameWithoutExtension(path)))
            {
                notes.Add($"A file named as an importance matrix, draft model or adapter in {repository} was skipped; it is not presented as the repository's standalone primary model.");
                continue;
            }
            var bytes = ReadPositiveBytes(file, "size");
            string? hash = null;
            if (file.TryGetProperty("lfs", out var lfs) && lfs.ValueKind == JsonValueKind.Object)
            {
                bytes ??= ReadPositiveBytes(lfs, "size");
                var oid = ReadString(lfs, "oid");
                if (oid is not null && HashRegex().IsMatch(oid)) hash = oid.ToLowerInvariant();
            }
            files.Add((path, bytes, hash));
        }
        if (shardFound) notes.Add($"Split GGUF shards in {repository} were skipped; a single shard is not a complete model.");
        if (tree.RootElement.GetArrayLength() >= 1000) notes.Add($"Only the first 1000 tree entries were checked for {repository}; results may be partial.");
        var readAt = DateTimeOffset.UtcNow;
        return files.OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .Take(limit).Select(file => new DiscoveredItem(
                $"huggingface:{repository}@{revision}:{file.Path}", repository + " / " + file.Path,
                repository.Split('/')[0], interest, "Model", license,
                new ModelArtifact(repository, revision, file.Path, file.Bytes, file.Hash, "GGUF", gated),
                Array.AsReadOnly(new[]
                {
                    new DiscoverySource("Hugging Face model card", new Uri($"https://huggingface.co/{repoPath}/blob/{revision}/README.md"), readAt),
                    new DiscoverySource("Pinned artifact metadata", new Uri($"https://huggingface.co/{repoPath}/blob/{revision}/{EncodePath(file.Path)}"), readAt)
                }), "Standalone GGUF artifact reported by the repository. Model contents, license obligations, architecture support, and runtime behavior require verification.", pipeline, Signals: signals))
            .ToList().AsReadOnly();
    }

    private async Task<DiscoveredItem> ReadRuntimeAsync(DiscoveryInterest interest, CancellationToken token, string? requestedTag = null)
    {
        using var repository = await GetJsonAsync(new Uri("https://api.github.com/repos/ggml-org/llama.cpp"), token);
        using var release = await GetJsonAsync(new Uri("https://api.github.com/repos/ggml-org/llama.cpp/releases/" +
            (requestedTag is null ? "latest" : "tags/" + Uri.EscapeDataString(requestedTag))), token);
        if (repository.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("The runtime repository metadata was not an object.");
        var tag = ReadString(release.RootElement, "tag_name");
        if (string.IsNullOrWhiteSpace(tag) || tag.Length > 128) throw new InvalidDataException("The runtime source did not provide a release tag.");
        if (requestedTag is not null && tag != requestedTag) throw new InvalidDataException("The runtime response does not match the explicitly requested release. No substitute was accepted.");
        var license = repository.RootElement.TryGetProperty("license", out var value) ? ReadString(value, "spdx_id") : null;
        if (license == "NOASSERTION") license = null;
        var at = DateTimeOffset.UtcNow;
        return new DiscoveredItem("github:ggml-org/llama.cpp@" + tag, "llama.cpp " + tag, "ggml-org", interest,
            "Runtime", license, null, Array.AsReadOnly(new[]
            {
                new DiscoverySource("Upstream runtime repository", new Uri("https://github.com/ggml-org/llama.cpp"), at),
                new DiscoverySource("Upstream release metadata", new Uri("https://github.com/ggml-org/llama.cpp/releases/tag/" + Uri.EscapeDataString(tag)), at)
            }), "Supporting runtime release metadata. Published archives are listed as source evidence; driver minima, dependencies and installation instructions have not been evaluated; no installation was performed.",
            RuntimeRelease: RuntimeReleaseParser.Parse(release.RootElement, tag));
    }

    private async Task<JsonDocument> GetJsonAsync(Uri uri, CancellationToken token)
    {
        if (uri.Scheme != "https" || uri.UserInfo.Length != 0 || uri.Port != 443 ||
            !(uri.Host == "huggingface.co" && uri.AbsolutePath.StartsWith("/api/models", StringComparison.Ordinal)
              || uri.Host == "api.github.com" && (uri.AbsolutePath.StartsWith("/repos/ggml-org/llama.cpp", StringComparison.Ordinal)
                  || uri.AbsolutePath == "/search/repositories")))
            throw new InvalidOperationException("The metadata request is outside the configured public source scope.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("AINexus-Discovery/1.0");
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.RequestMessage?.RequestUri is { } finalUri && finalUri != uri)
                throw new InvalidDataException("Source redirects are disabled.");
            if ((int)response.StatusCode is >= 300 and < 400) throw new InvalidDataException("Source redirects are disabled.");
            if (!response.IsSuccessStatusCode) throw new HttpRequestException("The public metadata source returned HTTP " + (int)response.StatusCode + ".", null, response.StatusCode);
            if (response.Content.Headers.ContentLength > MaximumResponseBytes) throw new InvalidDataException("Source metadata exceeds the 4 MiB limit.");
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var body = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(buffer, deadline.Token)) > 0)
            {
                if (body.Length + read > MaximumResponseBytes) throw new InvalidDataException("Source metadata exceeds the 4 MiB limit.");
                body.Write(buffer, 0, read);
            }
            return JsonDocument.Parse(body.GetBuffer().AsMemory(0, checked((int)body.Length)), new JsonDocumentOptions { MaxDepth = 32 });
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException("The public metadata request timed out."); }
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}\z", RegexOptions.CultureInvariant)]
    private static partial Regex RuntimeTag();

    private static OnlineDiscoverySnapshot Snapshot(IEnumerable<DiscoveredItem> items, IEnumerable<string> notes) =>
        new(items.ToList().AsReadOnly(), notes.Distinct(StringComparer.Ordinal).ToList().AsReadOnly(), DateTimeOffset.UtcNow);
    private static string? ReadString(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static long? ReadPositiveBytes(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var bytes) && bytes > 0 ? bytes : null;
    private static string EncodePath(string path) => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
    private static bool IsRepositoryId(string id) => id.Split('/') is [var owner, var name]
        && SegmentRegex().IsMatch(owner) && SegmentRegex().IsMatch(name) && owner is not ("." or "..") && name is not ("." or "..");
    private static bool IsArtifactPath(string path) => path.Length <= 512 && !path.Contains('\\')
        && path.Split('/').All(segment => segment.Length > 0 && segment is not ("." or "..") && !segment.Any(char.IsControl));
    private static bool IsSourceFailure(Exception ex) => ex is HttpRequestException or IOException or InvalidDataException or JsonException or TimeoutException;
    private static string FailureMessage(Exception ex) => ex is HttpRequestException http && http.StatusCode is { } status
        ? "HTTP " + (int)status + "." : ex is TimeoutException ? "Request timed out." : "Invalid, unavailable, or oversized source metadata.";

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,95}$", RegexOptions.CultureInvariant)] private static partial Regex SegmentRegex();
    [GeneratedRegex("^[a-fA-F0-9]{40}$", RegexOptions.CultureInvariant)] private static partial Regex RevisionRegex();
    [GeneratedRegex("^[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant)] private static partial Regex HashRegex();
    [GeneratedRegex(@"-\d+-of-\d+\.gguf$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex ShardRegex();
    [GeneratedRegex(@"(?:^|[-_.])(?:imatrix|draft|lora|adapter|controlvector)(?:[-_.]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TextCompanionName();
}
