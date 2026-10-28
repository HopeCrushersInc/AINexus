using System.IO;
using System.Text.Json;

namespace AINexus.App;

public sealed partial class VerifiedOnlineDiscoveryProvider
{
    private async Task<OnlineDiscoverySnapshot> SearchVideoModelsAndToolsAsync(OnlineDiscoveryRequest request,
        CancellationToken deadline, CancellationToken caller)
    {
        if (_http.DefaultRequestHeaders.Contains("Cookie"))
            throw new InvalidOperationException("Use a dedicated unauthenticated video metadata client.");
        var items = new List<DiscoveredItem>();
        var notes = new List<string>();
        var query = request.Query.Trim();
        var exactRepository = IsRepositoryId(query);
        var toolSlots = exactRepository || request.Limit == 1 ? 0 : Math.Min(2, Math.Max(1, request.Limit / 3));
        var modelSlots = request.Limit - toolSlots;
        try
        {
            string[] repositories;
            if (exactRepository) repositories = [query];
            else
            {
                var feedLimit = Math.Min(4, modelSlots);
                var text = await ReadVideoRepositoryFeedAsync("text-to-video", query, feedLimit, notes, deadline);
                var image = await ReadVideoRepositoryFeedAsync("image-to-video", query, feedLimit, notes, deadline);
                var interleaved = new List<string>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var rank = 0; rank < feedLimit; rank++)
                    foreach (var feed in new[] { text, image })
                        if (rank < feed.Count && seen.Add(feed[rank])) interleaved.Add(feed[rank]);
                repositories = interleaved.Take(8).ToArray();
            }
            foreach (var repository in repositories)
            {
                if (items.Count >= modelSlots) break;
                deadline.ThrowIfCancellationRequested();
                try { items.Add(await ReadVideoRepositoryAsync(repository, deadline)); }
                catch (Exception ex) when (IsSourceFailure(ex))
                { notes.Add($"Video repository {repository} could not be inspected: {FailureMessage(ex)}"); }
            }
        }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested)
        { notes.Add("The video metadata deadline was reached; results are partial."); }
        catch (Exception ex) when (IsSourceFailure(ex))
        { notes.Add("Video model discovery could not complete: " + FailureMessage(ex)); }

        if (toolSlots > 0 && !deadline.IsCancellationRequested)
        {
            try
            {
                var tools = await SearchWorkflowToolsAsync(request with { Limit = toolSlots }, deadline);
                items.AddRange(tools.Items.Take(toolSlots));
                notes.AddRange(tools.Notes);
            }
            catch (OperationCanceledException) when (!caller.IsCancellationRequested)
            { notes.Add("The video-tool metadata deadline was reached; results are partial."); }
            catch (Exception ex) when (IsSourceFailure(ex))
            { notes.Add("Video tool source unavailable: " + FailureMessage(ex)); }
        }
        caller.ThrowIfCancellationRequested();
        if (!items.Any(item => item.Kind == "Model"))
            notes.Add("No video model repositories were established in this bounded lookup. No image or text model was substituted.");
        notes.Add(exactRepository
            ? "Exact video repository metadata only. No broad search or alternative repository was substituted."
            : "Bounded video search: two task feeds, up to four repositories per task and eight inspected repositories, plus one tool search; at most eleven metadata requests. No pagination is followed.");
        notes.Add("Video results are repository-level candidates, not downloadable complete models or hardware-fit recommendations. Complete file inventory, companion assets, runtime requirements and installation support remain unverified.");
        notes.Add("No weight files or installers were requested. No video download, installation, model selection, inference or engine start occurred. Video GGUF files are not assigned to the text-generation runtime.");
        return Snapshot(items, notes);
    }

    private async Task<IReadOnlyList<string>> ReadVideoRepositoryFeedAsync(string task, string query, int limit,
        List<string> notes, CancellationToken token)
    {
        try
        {
            using var response = await GetJsonAsync(new Uri("https://huggingface.co/api/models?pipeline_tag=" + task
                + "&full=true&sort=downloads&direction=-1&limit=" + limit + "&search=" + Uri.EscapeDataString(query)), token);
            if (response.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("The video repository source did not return a list.");
            var repositories = new List<string>();
            foreach (var entry in response.RootElement.EnumerateArray().Take(limit))
            {
                var id = ReadString(entry, "id");
                if (id is not null && IsRepositoryId(id)) repositories.Add(id);
                else notes.Add("An invalid video repository identifier was skipped.");
            }
            return repositories;
        }
        catch (Exception ex) when (IsSourceFailure(ex))
        { notes.Add($"Video {task} source unavailable: {FailureMessage(ex)}"); return []; }
    }

    private async Task<DiscoveredItem> ReadVideoRepositoryAsync(string repository, CancellationToken token)
    {
        var path = EncodePath(repository);
        using var response = await GetJsonAsync(new Uri("https://huggingface.co/api/models/" + path), token);
        var root = response.RootElement;
        if ((ReadString(root, "id") ?? ReadString(root, "modelId")) != repository)
            throw new InvalidDataException("Video metadata does not match the requested repository.");
        var revision = ReadString(root, "sha");
        if (revision is null || !RevisionRegex().IsMatch(revision))
            throw new InvalidDataException("Video metadata did not provide a pinned revision.");
        if (!root.TryGetProperty("private", out var privacy) || privacy.ValueKind != JsonValueKind.False)
            throw new InvalidDataException("Video repository public-access state is not confirmed.");
        var task = ReadString(root, "pipeline_tag");
        if (task is not ("text-to-video" or "image-to-video"))
            throw new InvalidDataException("The repository does not declare a supported video-generation task.");
        var license = root.TryGetProperty("cardData", out var card) ? ReadString(card, "license") : null;
        if (license is null && root.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
            license = tags.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String)
                .Select(value => value.GetString()).FirstOrDefault(value => value?.StartsWith("license:", StringComparison.Ordinal) == true)?[8..];
        if (string.IsNullOrWhiteSpace(license) || license.Length > 256 || license.Any(char.IsControl)
            || license.Equals("unknown", StringComparison.OrdinalIgnoreCase)
            || license.Equals("NOASSERTION", StringComparison.OrdinalIgnoreCase)) license = null;
        var gated = !root.TryGetProperty("gated", out var access) || access.ValueKind != JsonValueKind.False;
        var at = DateTimeOffset.UtcNow;
        return new("huggingface-repository:" + repository + "@" + revision, repository, repository.Split('/')[0],
            DiscoveryInterest.Video, "Model", license, null,
            [new("Pinned video repository model card", new Uri($"https://huggingface.co/{path}/blob/{revision}/README.md"), at)],
            "Public " + task + " repository candidate. Complete file inventory, companion assets, runtime setup, hardware fit and installation support are unverified. "
                + (gated ? "Artifact access is gated or unconfirmed; no sign-in or access grant was attempted." : "Source reports ungated access; no download was attempted."),
            task, Signals: ReadDiscoverySignals(root));
    }
}
