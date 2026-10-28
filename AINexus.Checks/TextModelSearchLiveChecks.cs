using AINexus.App;

namespace AINexus.Checks;

internal static class TextModelSearchLiveChecks
{
    internal static async Task RunAsync(string query, Action<bool, string> check)
    {
        using var http = VerifiedOnlineDiscoveryProvider.CreateHttpClient();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(75));
        var snapshot = await new VerifiedOnlineDiscoveryProvider(http).SearchAsync(
            new(DiscoveryInterest.LocalAssistants, query), deadline.Token);
        var models = snapshot.Items.Where(item => item.Artifact is not null).ToArray();
        check(models.Length > 0 && models.All(item => item.Artifact is { SizeBytes: > 0, Revision.Length: 40, Sha256.Length: 64 }),
            "Explicit live text-model search returns real pinned candidate file sizes and hashes without downloading weights");
        check(models.All(item => item.Sources.Count > 0 && item.Sources.All(source => source.Url.Host == "huggingface.co"))
            && snapshot.Items.Count <= 12 && snapshot.Notes.Any(note => note.Contains("not a quality rating")),
            "Live text search retains source provenance, bounded coverage and popularity-versus-quality disclosures");
        foreach (var item in models)
            Console.WriteLine($"Live text source: {item.Artifact!.Repository} | {item.Artifact.FileName} | {item.Artifact.SizeBytes} bytes | revision {item.Artifact.Revision}");
        foreach (var note in snapshot.Notes) Console.WriteLine("Live search note: " + note);
    }
}
