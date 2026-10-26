namespace AINexus.App;

public partial class MainWindow
{
    private async Task PersistLlamaCppPreferencesAsync()
    {
        if (!Uri.TryCreate(LlamaCppEndpointText.Text.Trim(), UriKind.Absolute, out var endpoint) ||
            !int.TryParse(LlamaCppContextText.Text, out var context) || context is < 512 or > 32768) return;
        try { OllamaClient.ValidateEndpoint(endpoint); }
        catch (ArgumentException) { return; }
        var executable = LlamaCppExecutableText.Text.Trim();
        var model = LlamaCppModelPathText.Text.Trim();
        if ((!string.IsNullOrEmpty(executable) && !System.IO.Path.IsPathFullyQualified(executable)) ||
            (!string.IsNullOrEmpty(model) && !System.IO.Path.IsPathFullyQualified(model))) return;
        _settings = _settings with
        {
            LlamaCppExecutablePath = executable,
            LlamaCppModelPath = model,
            LlamaCppEndpoint = endpoint.AbsoluteUri,
            LlamaCppContextSize = context
        };
        await SaveSettingsAsync();
        await ScanSoftwareAsync();
    }
}
