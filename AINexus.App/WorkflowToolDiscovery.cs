using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

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
        var (terms, role, relevance) = request.Interest switch
        {
            DiscoveryInterest.Images => ("image generation", "image generation/editing", @"image[- ]generation|image[- ]editing|text[- ]to[- ]image|image[- ]to[- ]image|diffusion|inpainting"),
            DiscoveryInterest.MusicAudio => ("audio", "music/audio creation and processing", @"\b(music|audio|sound|synthesizer|daw)\b"),
            DiscoveryInterest.Video => ("video generation", "video generation", @"video[- ]generation|video[- ]synthesis|text[- ]to[- ]video|image[- ]to[- ]video|video.*diffusion|diffusion.*video"),
            DiscoveryInterest.Speech => ("speech", "speech recognition or synthesis", @"\b(speech|tts|stt|asr)\b|text[- ]to[- ]speech|speech[- ]to[- ]text|voice synthesis"),
            DiscoveryInterest.ThreeD => ("3d modeling", "3D modeling and meshes", @"3d.*(model|mesh|sculpt|design)|\b(modeling|modelling|sculpting)\b"),
            DiscoveryInterest.Cad => ("topic:cad", "CAD design and parametric modeling", @"\bcad\b|computer[- ]aided|parametric|solid[- ]model|drafting"),
            DiscoveryInterest.ThreeDPrinting => ("topic:3d-printing", "3D-print preparation, slicing and printer tools", @"3d[- ]?print|\bslicer\b|\bslicing\b|\bg[- ]?code\b|printer firmware"),
            DiscoveryInterest.GameDevelopment => ("game engine", "game development", @"\b(game|gamedev)\b|game[- ]engine|game[- ]development"),
            DiscoveryInterest.ReverseEngineering => ("\"reverse engineering\"", "reverse engineering and binary analysis", @"reverse[- ]engineering|decompil|disassembl|binary analysis|\bdebugger\b"),
            DiscoveryInterest.Streaming => ("topic:live-streaming", "live media streaming and broadcasting", @"\b(broadcast|broadcasting|obs)\b|live[- ]stream|screen[- ]record|video[- ]record"),
            _ => throw new ArgumentException("This workflow uses a different discovery adapter.", nameof(request))
        };
        if (_http.DefaultRequestHeaders.Authorization is not null || _http.DefaultRequestHeaders.Contains("Cookie"))
            throw new InvalidOperationException("Use a dedicated unauthenticated metadata client.");

        var query = request.Query.Trim();
        // Search words are literals, never caller-supplied qualifiers. Keep the workflow,
        // archive/fork restrictions and searched fields under application control.
        if (query.Contains('"') || query.Contains('\\'))
            throw new ArgumentException("Use plain search words without quotation marks or backslashes.", nameof(request));
        var userWords = string.Join(" ", query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(word => "\"" + word + "\""));
        var scopedQuery = (userWords.Length == 0 ? "" : userWords + " ") + terms + " in:name,description,topics archived:false fork:false";
        if (scopedQuery.Length > 256) throw new ArgumentException("Use a shorter tool search; the scoped source query must fit 256 characters.", nameof(request));
        var uri = new Uri("https://api.github.com/search/repositories?q="
            + Uri.EscapeDataString(scopedQuery)
            + "&per_page=" + Math.Min(request.Limit, 12));
        using var response = await GetJsonAsync(uri, token).ConfigureAwait(false);
        if (response.RootElement.ValueKind != JsonValueKind.Object
            || !response.RootElement.TryGetProperty("items", out var repositories)
            || repositories.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The workflow tool source did not return a repository list.");

        var at = DateTimeOffset.UtcNow;
        var results = new List<DiscoveredItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var excluded = 0;
        foreach (var repository in repositories.EnumerateArray().Take(request.Limit))
        {
            var fullName = ReadString(repository, "full_name");
            if (fullName is null || !IsRepositoryId(fullName) || !seen.Add(fullName)) continue;
            if (new[] { "archived", "disabled", "fork" }.Any(flag => repository.TryGetProperty(flag, out var value) && value.ValueKind == JsonValueKind.True))
            { excluded++; continue; }
            var rawDescription = ReadString(repository, "description");
            var topics = repository.TryGetProperty("topics", out var topicList) && topicList.ValueKind == JsonValueKind.Array
                ? topicList.EnumerateArray().Take(64).Where(topic => topic.ValueKind == JsonValueKind.String)
                    .Select(topic => topic.GetString()!).Where(topic => topic.Length <= 128 && !topic.Any(char.IsControl)).ToArray()
                : [];
            var evidence = fullName + " " + (rawDescription is { Length: > 1024 } ? rawDescription[..1024] : rawDescription) + " " + string.Join(" ", topics);
            if (!Regex.IsMatch(evidence, relevance, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
            { excluded++; continue; }
            var license = repository.TryGetProperty("license", out var licenseObject)
                ? ReadString(licenseObject, "spdx_id") : null;
            if (string.IsNullOrWhiteSpace(license) || license.Length > 64 || license.Any(char.IsControl)
                || license.Equals("NOASSERTION", StringComparison.OrdinalIgnoreCase)
                || license.Equals("unknown", StringComparison.OrdinalIgnoreCase)
                || license.Equals("other", StringComparison.OrdinalIgnoreCase)) license = null;
            var description = rawDescription ?? "No description was supplied by the repository.";
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
        notes.Add($"Search role: {role}. Source query: {scopedQuery}. Only repository names, descriptions and topics are searched; README keyword mentions do not qualify a result.");
        notes.Add("Listed tool candidates matched task words or topics in source metadata. These are not independently verified capabilities or proof of a complete application; missing hardware requirements remain unknown.");
        if (excluded > 0) notes.Add($"Excluded {excluded} returned repositories with no matching task metadata or a reported archived, disabled or fork status. No substitute category was searched.");
        if (response.RootElement.TryGetProperty("incomplete_results", out var incomplete) && incomplete.ValueKind == JsonValueKind.True)
            notes.Add("GitHub marked this search incomplete; the listed results are partial.");
        token.ThrowIfCancellationRequested();
        return new(results.AsReadOnly(), notes.AsReadOnly(), at);
    }
}
