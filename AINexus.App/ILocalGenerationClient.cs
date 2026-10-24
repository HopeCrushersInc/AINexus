namespace AINexus.App;

/// <summary>A local runtime adapter. Implementations must never launch external tools from model output.</summary>
public interface ILocalGenerationClient
{
    Task<string> GenerateAsync(Uri endpoint, string model, string systemPrompt,
        string prompt, CancellationToken cancellationToken = default);
}
