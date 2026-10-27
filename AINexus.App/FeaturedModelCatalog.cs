namespace AINexus.App;

public sealed record FeaturedModel(string Id, string Name, string Repository, string SearchQuery,
    string PreferredFile, string Description, bool Nsfw, string LicenseNote)
{
    public Uri SourceUrl => new("https://huggingface.co/" + Repository);
    public string NsfwLabel => Nsfw ? "NSFW / adult-oriented" : "General assistant";
}

/// <summary>
/// Curated source preferences, not installed models or compatibility evidence. Accessing the
/// catalog makes no network request; exact current identities must be retrieved before review.
/// Adult/less-filtered entries are visible only through an explicit session opt-in.
/// </summary>
public static class FeaturedModelCatalog
{
    public const string NsfwWarning = "NSFW: optional adult-oriented or less-filtered models may produce explicit content. Publisher descriptions are not a guarantee of behavior or quality.";

    private static readonly IReadOnlyList<FeaturedModel> Models = Array.AsReadOnly(new[]
    {
        new FeaturedModel("qwen-7b", "Qwen 2.5 7B", "bartowski/Qwen2.5-7B-Instruct-GGUF",
            "Qwen2.5-7B-Instruct-GGUF", "Qwen2.5-7B-Instruct-Q4_K_M.gguf",
            "A smaller general assistant to get started. Community GGUF quantization of Qwen's model; hardware fit is checked before download.",
            false, "Source declares Apache-2.0. Review the current pinned model card and license before acquisition."),
        new FeaturedModel("qwen-coder-7b", "Qwen 2.5 Coder 7B", "Qwen/Qwen2.5-Coder-7B-Instruct-GGUF",
            "Qwen2.5-Coder-7B-Instruct-GGUF", "qwen2.5-coder-7b-instruct-q4_k_m.gguf",
            "Qwen's official coding-focused GGUF. Useful for local code discussion and development tasks; code quality and hardware fit require separate verification.",
            false, "Source declares Apache-2.0. Review the current pinned official model card and license before acquisition."),
        new FeaturedModel("qwen-32b", "Qwen 2.5 32B", "bartowski/Qwen2.5-32B-Instruct-GGUF",
            "Qwen2.5-32B-Instruct-GGUF", "Qwen2.5-32B-Instruct-Q4_K_M.gguf",
            "A larger general assistant with a substantial download and memory requirement. The portal checks current storage/RAM/VRAM; actual runtime verification is still required.",
            false, "Source declares Apache-2.0. Review the current pinned model card and license before acquisition."),
        new FeaturedModel("stheno-8b", "Stheno 3.2 8B (NSFW)", "bartowski/L3-8B-Stheno-v3.2-GGUF",
            "L3-8B-Stheno-v3.2-GGUF", "L3-8B-Stheno-v3.2-Q4_K_M.gguf",
            "Roleplay and creative-writing model. The upstream Sao10K model card describes mixed SFW/NSFW storywriting data; responses are not guaranteed.",
            true, "Source declares CC-BY-NC-4.0 (noncommercial). Review that license and applicable base-model terms before acquisition."),
        new FeaturedModel("lexi-8b", "Lexi Llama 3 8B (NSFW)", "bartowski/Lexi-Llama-3-8B-Uncensored-GGUF",
            "Lexi-Llama-3-8B-Uncensored-GGUF", "Lexi-Llama-3-8B-Uncensored-Q4_K_M.gguf",
            "Less-filtered assistant described as uncensored by its upstream Orenguteng publisher. May produce NSFW content; this does not guarantee compliance, accuracy or quality.",
            true, "Source declares the Llama 3 license, not Apache-2.0. Review its current terms and acceptable-use requirements before acquisition.")
    });

    public static IReadOnlyList<FeaturedModel> Visible(bool includeNsfw) =>
        Array.AsReadOnly(Models.Where(model => includeNsfw || !model.Nsfw).ToArray());
}
