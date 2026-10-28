using System.Text.Json;
using AINexus.App;

namespace AINexus.Checks;

internal static class ModelEvaluationLiveChecks
{
    internal static async Task RunAsync(string repository, Action<bool, string> check)
    {
        using var http = VerifiedOnlineDiscoveryProvider.CreateHttpClient();
        var result = await new ModelEvaluationClient(http).ReadAsync(repository);
        check(result.ModelRepository == repository && result.Results.Count <= ModelEvaluationParser.MaximumRows
            && result.Results.All(row => row.ModelRepository == repository && double.IsFinite(row.Value)),
            "Live model evidence retains exact repository identity and bounded finite benchmark values");
        check(result.Notes.Any(note => note.Contains("not transferred"))
            && result.Notes.Any(note => note.Contains("unversioned")),
            "Live evidence explicitly leaves evaluated weights and quantization equivalence unestablished");
        Console.WriteLine("Actual evaluation snapshot: " + JsonSerializer.Serialize(result));
    }
}
