using System.IO;
using System.Text.Json;

namespace AINexus.App;

public sealed partial class VerifiedOnlineDiscoveryProvider
{
    /// <summary>Live public repository metadata for the user's chosen workflow. No executable
    /// artifacts, installation plans, local hardware details or credentials are transferred.</summary>
    internal async Task<OnlineDiscoverySnapshot> SearchWorkflowToolsAsync(OnlineDiscoveryRequest request,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        token.ThrowIfCancellationRequested();
        if (request.Limit is < 1 or > 12 || request.Query is null || request.Query.Length > 160 || request.Query.Any(char.IsControl))
            throw new ArgumentException("Choose 1–12 results and search text of at most 160 characters without controls.", nameof(request));
        if (request.RuntimeReleaseTag is not null)
            throw new ArgumentException("Use the explicit runtime release lookup for a named runtime tag; workflow search cannot substitute it.", nameof(request));
        var terms = request.Interest switch
        {
            DiscoveryInterest.Images => "AI image generation",
            DiscoveryInterest.MusicAudio => "AI music generation",
            DiscoveryInterest.Video => "AI video generation",
            DiscoveryInterest.Speech => "speech recognition synthesis",
            DiscoveryInterest.ThreeD => "3D modeling",
            DiscoveryInterest.Cad => "CAD",
            DiscoveryInterest.ThreeDPrinting => "3D printing slicer",
            DiscoveryInterest.GameDevelopment => "game engine",
            DiscoveryInterest.ReverseEngineering => "reverse engineering decompiler",
            DiscoveryInterest.Streaming => "streaming OBS",
            _ => throw new ArgumentException("This workflow uses a different discovery adapter.", nameof(request))
        };
        if (_http.DefaultRequestHeaders.Authorization is not null)
            throw new InvalidOperationException("Use a dedicated unauthenticated metadata client.");

        var query = request.Query.Trim();
        var uri = new Uri("https://api.github.com/search/repositories?q="
            + Uri.EscapeDataString((query.Length == 0 ? "" : query + " ") + terms)
            + "&per_page=" + Math.Min(request.Limit, 12));
        using var response = await GetJsonAsync(uri, token).ConfigureAwait(false);
        if (response.RootElement.ValueKind != JsonValueKind.Object
            || !response.RootElement.TryGetProperty("items", out var repositories)
            || repositories.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The workflow tool source did not return a repository list.");

        var at = DateTimeOffset.UtcNow;
        var results = new List<DiscoveredItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var repository in repositories.EnumerateArray().Take(request.Limit))
        {
            var fullName = ReadString(repository, "full_name");
            if (fullName is null || !IsRepositoryId(fullName) || !seen.Add(fullName)) continue;
            var license = repository.TryGetProperty("license", out var licenseObject)
                ? ReadString(licenseObject, "spdx_id") : null;
            if (string.IsNullOrWhiteSpace(license) || license.Length > 64 || license.Any(char.IsControl)
                || license.Equals("NOASSERTION", StringComparison.OrdinalIgnoreCase)
                || license.Equals("unknown", StringComparison.OrdinalIgnoreCase)
                || license.Equals("other", StringComparison.OrdinalIgnoreCase)) license = null;
            var description = ReadString(repository, "description") ?? "No description was supplied by the repository.";
            if (description.Length > 1024) description = description[..1024];
            var source = new Uri("https://github.com/" + EncodePath(fullName));
            results.Add(new("github-tool:" + fullName, fullName, fullName.Split('/')[0], request.Interest,
                "Tool", license, null, Array.AsReadOnly(new[] { new DiscoverySource("Live GitHub repository metadata", source, at) }),
                description));
        }
        var notes = new List<string>
        {
            "Live repository metadata only. Tool presence or star ordering is not compatibility evidence or installation approval; the installation adapter may be unavailable.",
            "Only the chosen workflow terms and search text were sent. No hardware profile, local paths, files, model weights or installers were transferred.",
            "Results are a bounded single-page source search, not a complete catalog or a claim that a tool is the best choice. Unknown licenses remain unknown."
        };
        if (response.RootElement.TryGetProperty("incomplete_results", out var incomplete) && incomplete.ValueKind == JsonValueKind.True)
            notes.Add("GitHub marked this search incomplete; the listed results are partial.");
        token.ThrowIfCancellationRequested();
        return new(results.AsReadOnly(), notes.AsReadOnly(), at);
    }
}
