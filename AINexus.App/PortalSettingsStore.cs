using System.IO;
using System.Text.Json;

namespace AINexus.App;

public sealed record PortalSettings(IReadOnlyList<string> ModelRoots, string OllamaEndpoint)
{
    public string LlamaCppExecutablePath { get; init; } = "";
    public string LlamaCppModelPath { get; init; } = "";
    public string LlamaCppEndpoint { get; init; } = "http://127.0.0.1:8080";
    public int LlamaCppContextSize { get; init; } = 4096;
    public static PortalSettings Default => new(Array.Empty<string>(), "http://127.0.0.1:11434");
}

public sealed record SettingsLoadResult(PortalSettings Settings, string? Warning);

public sealed class PortalSettingsStore(string path)
{
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    public static PortalSettingsStore ForCurrentUser() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AINexus", "settings.json"));

    public async Task<SettingsLoadResult> LoadAsync(CancellationToken token = default)
    {
        try
        {
            if (!File.Exists(path)) return new(PortalSettings.Default, null);
            if (new FileInfo(path).Length > 65_536) throw new InvalidDataException("Settings file exceeds the supported size.");
            await using var stream = File.OpenRead(path);
            var settings = await JsonSerializer.DeserializeAsync<PortalSettings>(stream, cancellationToken: token);
            if (settings is null || settings.ModelRoots is null || settings.OllamaEndpoint is null || settings.ModelRoots.Count > 64 ||
                settings.ModelRoots.Any(root => string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root)))
                throw new InvalidDataException("Settings contain invalid model folders or endpoint.");
            OllamaClient.ValidateEndpoint(new Uri(settings.LlamaCppEndpoint));
            if (settings.LlamaCppContextSize is < 512 or > 32768 ||
                (!string.IsNullOrEmpty(settings.LlamaCppExecutablePath) && !Path.IsPathFullyQualified(settings.LlamaCppExecutablePath)) ||
                (!string.IsNullOrEmpty(settings.LlamaCppModelPath) && !Path.IsPathFullyQualified(settings.LlamaCppModelPath)))
                throw new InvalidDataException("Portable runtime settings are invalid.");
            return new(settings with { ModelRoots = settings.ModelRoots.Distinct(StringComparer.OrdinalIgnoreCase).ToArray() }, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or UriFormatException)
        {
            return new(PortalSettings.Default, $"Settings could not be loaded ({ex.GetType().Name}). Existing settings have been preserved.");
        }
    }

    public async Task SaveAsync(PortalSettings settings, CancellationToken token = default)
    {
        await _saveGate.WaitAsync(token);
        try
        {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(stream, settings, new JsonSerializerOptions { WriteIndented = true }, token);
            await stream.FlushAsync(token);
        }
        // Replacement applies only to this application's settings file, never model files.
        File.Move(temporary, fullPath, overwrite: true);
        }
        finally { _saveGate.Release(); }
    }
}
