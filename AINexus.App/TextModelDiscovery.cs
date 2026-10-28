using System.Globalization;
using System.IO;
using System.Text.Json;

namespace AINexus.App;

public sealed partial class VerifiedOnlineDiscoveryProvider
{
    private const int MaximumTextRepositories = 24;
    private const int TextMetadataConcurrency = 4;

    private async Task SearchBroadTextModelsAsync(OnlineDiscoveryRequest request, int modelSlots,
        List<DiscoveredItem> items, List<string> notes, CancellationToken token)
    {
        // HF's official SDK maps last_modified and trending_score to these wire names:
        // https://github.com/huggingface/huggingface_hub/blob/main/src/huggingface_hub/hf_api.py
        string[] sorts = request.SearchOrder switch
        {
            TextSearchOrder.Popular => ["downloads"],
            TextSearchOrder.Trending => ["trendingScore"],
            TextSearchOrder.RecentlyUpdated => ["lastModified"],
            _ => ["downloads", "trendingScore", "lastModified"]
        };
        var repositoryLimit = Math.Min(MaximumTextRepositories, request.Limit);
        notes.Add($"Bounded Hugging Face text-generation GGUF search: up to {repositoryLimit} repositories inspected and {request.Limit} total results, including supporting runtime metadata. No pagination is followed; this is not the full model catalog.");
        notes.Add("Source ordering: " + string.Join(", ", sorts) + ". Broad view interleaves the independent source lists and removes duplicate repository IDs; it does not compute a combined quality score.");
        notes.Add("Download popularity, trending position and repository modification time are not a quality rating, benchmark, writing assessment, release date or recommendation. Parameter counts are not inferred from names.");
        notes.Add("Broad searches use the source's text-generation task tag for the current GGUF text recipe. Other tasks and multimodal requirements can be inspected by an exact repository lookup; they are not declared unusable.");
        notes.Add("Broad searches show one alphabetically listed standalone file per repository before additional variants. Search an exact owner/repository to browse more of its file variants; search text such as 32B matches repository names, not verified parameter counts.");

        // Independent reads run concurrently. Each task owns its notes; aggregate only in
        // deterministic source order after awaiting the group, never into shared Lists.
        var feeds = await Task.WhenAll(sorts.Select(sort => ReadTextFeedAsync(sort, request.Query.Trim(), repositoryLimit, token)));
        for (var index = 0; index < feeds.Length; index++)
        {
            notes.Add($"Hugging Face {sorts[index]} feed: {feeds[index].Repositories.Count} repository IDs returned within the {repositoryLimit}-ID limit.");
            notes.AddRange(feeds[index].Notes);
        }
        token.ThrowIfCancellationRequested();
        var repositories = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var rank = 0; rank < repositoryLimit && repositories.Count < repositoryLimit; rank++)
            foreach (var feed in feeds)
            {
                if (rank >= feed.Repositories.Count) continue;
                var repository = feed.Repositories[rank];
                if (!seen.Add(repository)) continue;
                repositories.Add(repository);
                if (repositories.Count == repositoryLimit) break;
            }

        var variants = new List<IReadOnlyList<DiscoveredItem>>();
        for (var offset = 0; offset < repositories.Count; offset += TextMetadataConcurrency)
        {
            token.ThrowIfCancellationRequested();
            var group = repositories.Skip(offset).Take(TextMetadataConcurrency).ToArray();
            var results = await Task.WhenAll(group.Select(repository => ReadTextCandidateAsync(repository, request.Interest,
                Math.Min(2, modelSlots), token)));
            foreach (var result in results)
            {
                notes.AddRange(result.Notes);
                if (result.Items.Count == 0) continue;
                if (items.Count < modelSlots) items.Add(result.Items[0]);
                variants.Add(result.Items);
            }
            // Keep successful candidates from an interrupted batch as partial results.
            // SearchAsync distinguishes caller cancellation from its metadata deadline.
            token.ThrowIfCancellationRequested();
            if (items.Count >= modelSlots) break;
        }
        foreach (var repositoryVariants in variants)
        {
            foreach (var variant in repositoryVariants.Skip(1))
            {
                if (items.Count >= modelSlots) return;
                items.Add(variant);
            }
        }
    }

    private sealed record TextFeed(IReadOnlyList<string> Repositories, IReadOnlyList<string> Notes);
    private sealed record TextCandidates(IReadOnlyList<DiscoveredItem> Items, IReadOnlyList<string> Notes);

    private async Task<TextFeed> ReadTextFeedAsync(string sort, string query, int limit, CancellationToken token)
    {
        try
        {
            var url = "https://huggingface.co/api/models?filter=gguf&pipeline_tag=text-generation&full=true&limit=" + limit
                + "&sort=" + sort + "&direction=-1&search=" + Uri.EscapeDataString(query);
            using var results = await GetJsonAsync(new Uri(url), token);
            if (results.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("The model search response was not a list.");
            var repositories = new List<string>();
            var notes = new List<string>();
            foreach (var model in results.RootElement.EnumerateArray().Take(limit))
            {
                var id = ReadString(model, "id");
                if (id is null || !IsRepositoryId(id))
                {
                    notes.Add("A source returned an invalid repository identifier; it was skipped.");
                    continue;
                }
                repositories.Add(id);
            }
            return new(repositories, notes);
        }
        catch (Exception ex) when (IsSourceFailure(ex))
        { return new([], [$"Hugging Face {sort} feed unavailable: {FailureMessage(ex)}"]); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { return new([], [$"Hugging Face {sort} feed interrupted; no alternate source was substituted."]); }
    }

    private async Task<TextCandidates> ReadTextCandidateAsync(string repository, DiscoveryInterest interest,
        int fileLimit, CancellationToken token)
    {
        var notes = new List<string>();
        try
        {
            var found = await ReadRepositoryAsync(repository, interest, fileLimit, notes, token);
            // A repository may change between source listing and pinned metadata. Do not
            // present a known non-text task as a text-generation result. Unknown stays unknown.
            if (found.Any(item => item.PipelineTag is not null && item.PipelineTag != "text-generation"))
            {
                notes.Add($"Pinned metadata for {repository} reports a different task from text-generation; those files were skipped.");
                found = found.Where(item => item.PipelineTag is null || item.PipelineTag == "text-generation").ToArray();
            }
            return new(found, notes);
        }
        catch (Exception ex) when (IsSourceFailure(ex))
        { notes.Add($"Model metadata could not be read for {repository}: {FailureMessage(ex)}"); return new([], notes); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { notes.Add($"Model metadata for {repository} was interrupted; results are partial."); return new([], notes); }
    }

    private static DiscoverySignals ReadDiscoverySignals(JsonElement root)
    {
        static long? Count(JsonElement element, string property) => element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var count) && count >= 0 ? count : null;
        var rawDate = ReadString(root, "lastModified");
        DateTimeOffset? updated = rawDate is { Length: <= 64 }
            && DateTimeOffset.TryParse(rawDate, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed : null;
        var parameters = root.TryGetProperty("gguf", out var gguf) && gguf.ValueKind == JsonValueKind.Object
            && gguf.TryGetProperty("total", out var total) && total.ValueKind == JsonValueKind.Number
            && total.TryGetInt64(out var count) && count > 0 ? (long?)count : null;
        return new(Count(root, "downloads"), Count(root, "likes"), updated, parameters);
    }
}
