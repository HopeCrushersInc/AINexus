using System.IO;
using System.Text.Json;

namespace AINexus.App;

public sealed partial class VerifiedOnlineDiscoveryProvider
{
    /// <summary>Two bounded metadata requests for one curated file; no weights, authentication or fallback search.</summary>
    public async Task<OnlineDiscoverySnapshot> LookupFeaturedModelAsync(FeaturedModel model, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        token.ThrowIfCancellationRequested();
        if (!FeaturedModelCatalog.Visible(true).Contains(model))
            throw new ArgumentException("Choose a model from the featured catalog.", nameof(model));
        if (_http.DefaultRequestHeaders.Authorization is not null)
            throw new InvalidOperationException("Use a dedicated unauthenticated metadata client.");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(40));
        try
        {
            var repo = EncodePath(model.Repository);
            using var info = await GetJsonAsync(new Uri($"https://huggingface.co/api/models/{repo}"), deadline.Token);
            var root = info.RootElement;
            if ((ReadString(root, "id") ?? ReadString(root, "modelId")) != model.Repository)
                throw new InvalidDataException("Featured model metadata did not match the requested repository.");
            var revision = ReadString(root, "sha");
            if (revision is null || !RevisionRegex().IsMatch(revision))
                throw new InvalidDataException("The featured source did not report an immutable revision.");
            if (!root.TryGetProperty("gated", out var gated) || gated.ValueKind != JsonValueKind.False)
                throw new InvalidDataException("The featured source is gated or has unknown public-access status; no anonymous acquisition was prepared.");
            if (ReadString(root, "pipeline_tag") != "text-generation")
                throw new InvalidDataException("The featured source no longer declares the supported text-generation workflow.");
            var license = root.TryGetProperty("cardData", out var card) ? ReadString(card, "license") : null;
            if (string.IsNullOrWhiteSpace(license) || license.Equals("unknown", StringComparison.OrdinalIgnoreCase)
                || license.Equals("NOASSERTION", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The featured source's current license is unknown. Review its terms before acquisition.");

            var slash = model.PreferredFile.LastIndexOf('/');
            var parent = slash >= 0 ? "/" + EncodePath(model.PreferredFile[..slash]) : "";
            using var tree = await GetJsonAsync(new Uri($"https://huggingface.co/api/models/{repo}/tree/{revision}{parent}?recursive=false&expand=false&limit=1000"), deadline.Token);
            if (tree.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("The featured source's artifact metadata was not a list.");
            var matches = tree.RootElement.EnumerateArray().Take(1000)
                .Where(entry => ReadString(entry, "type") == "file" && ReadString(entry, "path") == model.PreferredFile).ToArray();
            if (matches.Length != 1)
                throw new InvalidDataException("The exact featured file was not uniquely present in the bounded source listing. No substitute file or pagination was selected.");
            var file = matches[0];
            if (!file.TryGetProperty("lfs", out var lfs) || lfs.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("The exact featured file lacks source-reported SHA-256 evidence.");
            var hash = ReadString(lfs, "oid");
            var bytes = ReadPositiveBytes(file, "size");
            var lfsBytes = ReadPositiveBytes(lfs, "size");
            if (hash is null || !HashRegex().IsMatch(hash) || bytes is null || lfsBytes is null || bytes != lfsBytes)
                throw new InvalidDataException("The featured file's byte count or SHA-256 is missing or inconsistent.");
            token.ThrowIfCancellationRequested();
            var at = DateTimeOffset.UtcNow;
            var item = new DiscoveredItem($"huggingface:{model.Repository}@{revision}:{model.PreferredFile}", model.Name,
                model.Repository.Split('/')[0], DiscoveryInterest.LocalAssistants, "Model", license,
                new(model.Repository, revision, model.PreferredFile, bytes, hash.ToLowerInvariant(), "GGUF", false),
                Array.AsReadOnly(new[]
                {
                    new DiscoverySource("Pinned model card and licensing", new Uri($"https://huggingface.co/{repo}/blob/{revision}/README.md"), at),
                    new DiscoverySource("Pinned artifact metadata", new Uri($"https://huggingface.co/{repo}/blob/{revision}/{EncodePath(model.PreferredFile)}"), at)
                }), model.Description + (model.Nsfw ? " " + FeaturedModelCatalog.NsfwWarning : ""), "text-generation");
            return new(Array.AsReadOnly(new[] { item }), Array.AsReadOnly(new[]
            {
                "Exact featured file lookup; no model bytes, runtime, installation or automatic alternate selection occurred.",
                "Source-declared licensing and model behavior require review. Current hardware assessment and explicit acquisition approval are separate."
            }), at);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("The featured model metadata lookup timed out. No substitute or download was selected.");
        }
    }
}
