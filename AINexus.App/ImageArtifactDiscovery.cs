using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AINexus.App;

public sealed partial class VerifiedOnlineDiscoveryProvider
{
    // Source/file metadata only. This adapter neither reads weight bytes nor infers
    // complete checkpoint contents, architecture support or GPU memory from filenames.
    private async Task<OnlineDiscoverySnapshot> SearchImageArtifactsAndToolsAsync(OnlineDiscoveryRequest request,
        CancellationToken deadline, CancellationToken caller)
    {
        if (_http.DefaultRequestHeaders.Contains("Cookie"))
            throw new InvalidOperationException("Use a dedicated unauthenticated image metadata client.");
        var items = new List<DiscoveredItem>();
        var notes = new List<string>();
        var toolSlots = request.Limit == 1 ? 0 : Math.Min(2, Math.Max(1, request.Limit / 3));
        var modelSlots = request.Limit - toolSlots;
        var query = request.Query.Trim();
        var exactRepository = IsRepositoryId(query);
        try
        {
            string[] repositories;
            if (exactRepository) repositories = [query];
            else
            {
                var count = Math.Min(4, modelSlots);
                using var results = await GetJsonAsync(new Uri("https://huggingface.co/api/models?pipeline_tag=text-to-image&full=true&limit="
                    + count + "&search=" + Uri.EscapeDataString(query)), deadline);
                if (results.RootElement.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("The image model source did not return a repository list.");
                repositories = results.RootElement.EnumerateArray().Take(count).Select(value => ReadString(value, "id"))
                    .Where(id => id is not null && IsRepositoryId(id)).Cast<string>().Distinct(StringComparer.Ordinal).ToArray();
            }
            foreach (var repository in repositories)
            {
                deadline.ThrowIfCancellationRequested();
                if (items.Count >= modelSlots) break;
                try
                {
                    var fileSlots = exactRepository ? modelSlots - items.Count : Math.Min(2, modelSlots - items.Count);
                    items.AddRange(await ReadImageArtifactRepositoryAsync(repository, fileSlots, notes, deadline));
                }
                catch (Exception ex) when (IsSourceFailure(ex))
                { notes.Add($"Image artifact source unavailable for {repository}: {FailureMessage(ex)}"); }
            }
        }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested)
        { notes.Add("The image metadata deadline was reached; results are partial."); }
        catch (Exception ex) when (IsSourceFailure(ex))
        { notes.Add("Image artifact discovery could not complete: " + FailureMessage(ex)); }

        if (toolSlots > 0 && !deadline.IsCancellationRequested)
        {
            try
            {
                var tools = await SearchWorkflowToolsAsync(request with { Limit = toolSlots }, deadline);
                items.AddRange(tools.Items.Take(toolSlots));
                notes.AddRange(tools.Notes);
            }
            catch (OperationCanceledException) when (!caller.IsCancellationRequested)
            { notes.Add("The image-tool metadata deadline was reached; results are partial."); }
            catch (Exception ex) when (IsSourceFailure(ex))
            { notes.Add("Image tool source unavailable: " + FailureMessage(ex)); }
        }
        caller.ThrowIfCancellationRequested();
        if (!items.Any(item => item.Artifact is not null))
            notes.Add("No root image artifact candidates were found in this bounded source search. No model or file was substituted.");
        notes.Add("Image files and tools are shown by default as source metadata, not hardware-fit matches. The optional fit-only filter hides unassessed results; a missing installation recipe does not make a tool incompatible.");
        notes.Add("A root safetensors file is only an image artifact candidate; complete checkpoint contents, architecture, companion assets and ComfyUI compatibility remain unproven. No memory estimate was inferred from its size or filename.");
        notes.Add("Only public metadata was requested: no weight bytes, downloads, installations, model selection or engine execution. Results use a bounded source order, not recommendations or a complete catalog.");
        return Snapshot(items, notes);
    }

    private async Task<IReadOnlyList<DiscoveredItem>> ReadImageArtifactRepositoryAsync(string repository, int limit,
        List<string> notes, CancellationToken token)
    {
        var repo = EncodePath(repository);
        using var info = await GetJsonAsync(new Uri($"https://huggingface.co/api/models/{repo}"), token);
        var root = info.RootElement;
        if ((ReadString(root, "id") ?? ReadString(root, "modelId")) != repository)
            throw new InvalidDataException("Image metadata did not match the requested repository.");
        var revision = ReadString(root, "sha");
        if (revision is null || !RevisionRegex().IsMatch(revision))
            throw new InvalidDataException("The image repository did not provide a pinned revision.");
        if (ReadString(root, "pipeline_tag") != "text-to-image")
        {
            notes.Add($"{repository} does not declare the text-to-image task; no image checkpoint was inferred.");
            return [];
        }
        if (root.TryGetProperty("private", out var privateValue) && privateValue.ValueKind == JsonValueKind.True)
            throw new InvalidDataException("Private model metadata is outside public image discovery.");
        var gated = !root.TryGetProperty("gated", out var gate) || gate.ValueKind != JsonValueKind.False;
        var license = root.TryGetProperty("cardData", out var card) ? ReadString(card, "license") : null;
        if (license is null && root.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
            license = tags.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String)
                .Select(value => value.GetString()).FirstOrDefault(value => value?.StartsWith("license:", StringComparison.Ordinal) == true)?[8..];
        if (string.IsNullOrWhiteSpace(license) || license.Length > 256 || license.Any(char.IsControl)) license = null;
        using var tree = await GetJsonAsync(new Uri($"https://huggingface.co/api/models/{repo}/tree/{revision}?recursive=false&expand=false&limit=1000"), token);
        if (tree.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Image artifact metadata was not a file list.");
        var files = new Dictionary<string, (string Name, long? Bytes, string? Hash)>(StringComparer.OrdinalIgnoreCase);
        var conflicting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in tree.RootElement.EnumerateArray().Take(1000))
        {
            var path = ReadString(file, "path");
            if (ReadString(file, "type") != "file" || path is null || !path.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase)) continue;
            if (!SafeRootImageFile(path)) { notes.Add($"An unsafe or nested image artifact path in {repository} was skipped."); continue; }
            if (ImageShardRegex().IsMatch(path) || KnownImageComponent(path))
            { notes.Add($"An obvious component, adapter or shard in {repository} was skipped; one component is not a complete checkpoint."); continue; }
            var bytes = ReadPositiveBytes(file, "size");
            string? hash = null;
            if (file.TryGetProperty("lfs", out var lfs) && lfs.ValueKind == JsonValueKind.Object)
            {
                var lfsBytes = ReadPositiveBytes(lfs, "size");
                if (bytes is { } a && lfsBytes is { } b && a != b)
                {
                    files.Remove(path); conflicting.Add(path);
                    notes.Add($"An image artifact in {repository} had conflicting byte counts and was skipped.");
                    continue;
                }
                bytes ??= lfsBytes;
                var oid = ReadString(lfs, "oid");
                if (oid is not null && HashRegex().IsMatch(oid)) hash = oid.ToLowerInvariant();
            }
            if (conflicting.Contains(path)) continue;
            if (files.TryGetValue(path, out var prior))
            {
                if (prior.Bytes != bytes || !string.Equals(prior.Hash, hash, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(prior.Name, path, StringComparison.Ordinal))
                {
                    files.Remove(path); conflicting.Add(path);
                    notes.Add($"Conflicting duplicate image artifact metadata in {repository} was rejected; no first value was chosen.");
                }
                else notes.Add($"Duplicate image artifact metadata in {repository} was skipped.");
                continue;
            }
            files.Add(path, (path, bytes, hash));
        }
        if (tree.RootElement.GetArrayLength() >= 1000) notes.Add($"Only the first 1000 root entries in {repository} were checked; results may be partial.");
        if (gated) notes.Add($"{repository} has gated or unconfirmed public access; no sign-in or access grant was attempted.");
        if (files.Values.Any(file => file.Bytes is null || file.Hash is null) || license is null)
            notes.Add($"Some source size, SHA-256 or license evidence in {repository} is unknown; no missing fact was invented.");
        var readAt = DateTimeOffset.UtcNow;
        return files.Values.OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase).Take(limit).Select(file => new DiscoveredItem(
            $"huggingface:{repository}@{revision}:{file.Name}", repository + " / " + file.Name, repository.Split('/')[0],
            DiscoveryInterest.Images, "Model", license, new(repository, revision, file.Name, file.Bytes, file.Hash, "Safetensors", gated),
            [new("Pinned image model card", new Uri($"https://huggingface.co/{repo}/blob/{revision}/README.md"), readAt),
             new("Pinned image artifact metadata", new Uri($"https://huggingface.co/{repo}/blob/{revision}/{EncodePath(file.Name)}"), readAt)],
            "Public text-to-image repository root artifact candidate. Its safetensors extension does not prove a complete checkpoint, supported architecture or a working local engine recipe. Hardware fit and execution are unverified.",
            "text-to-image")).ToArray();
    }

    private static bool SafeRootImageFile(string path)
    {
        if (path.Length is < 1 or > 240 || path.Any(c => char.IsControl(c) || c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '#' or '*')
            || path.EndsWith(' ') || path.EndsWith('.') || path is "." or "..") return false;
        var stem = path.Split('.')[0].TrimEnd(' ');
        return !Regex.IsMatch(stem, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool KnownImageComponent(string name)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        return new[] { "lora", "pytorch_lora_weights", "adapter", "adapter_model", "diffusion_pytorch_model",
            "unet", "vae", "clip", "text_encoder", "text_encoder_2" }.Contains(stem, StringComparer.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"-\d+-of-\d+\.safetensors$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ImageShardRegex();
}
