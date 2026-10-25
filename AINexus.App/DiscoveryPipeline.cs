using System.IO;
using System.Text.Json;

namespace AINexus.App;

public sealed record DiscoveryReport(OnlineDiscoverySnapshot Catalog, MachineCapabilities Machine,
    string TargetVolume, IReadOnlyList<CompatibilityRecommendation> Recommendations);

public sealed class DiscoveryPipeline(IOnlineDiscoveryProvider provider, DiscoveryCacheStore cache)
{
    public async Task<DiscoveryReport> SearchOnlineAsync(OnlineDiscoveryRequest request, MachineCapabilities machine,
        string targetVolume, CompatibilityPolicy? policy = null, CancellationToken token = default)
    {
        var snapshot = await provider.SearchAsync(request, token);
        if (snapshot.Items.Count > 0)
        {
            try { await cache.SaveAsync(request, snapshot, token); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
            {
                snapshot = snapshot with { Notes = snapshot.Notes.Append("Search results could not be saved; they are available in this session only.").ToArray() };
            }
        }
        return Evaluate(snapshot, machine, targetVolume, policy);
    }

    public async Task<DiscoveryReport> LoadSavedAsync(OnlineDiscoveryRequest request, MachineCapabilities machine,
        string targetVolume, CompatibilityPolicy? policy = null, CancellationToken token = default)
    {
        var snapshot = await cache.LoadAsync(request, token)
            ?? new OnlineDiscoverySnapshot([], ["No saved search exists for this exact workflow, query and result limit. No online request was made."], DateTimeOffset.MinValue, true);
        return Evaluate(snapshot, machine, targetVolume, policy);
    }

    public static DiscoveryReport Evaluate(OnlineDiscoverySnapshot snapshot, MachineCapabilities machine,
        string targetVolume, CompatibilityPolicy? policy = null)
    {
        var recommendations = snapshot.Items.Select(item => HardwareCompatibilityEngine.Evaluate(item, machine, targetVolume, policy))
            .OrderBy(result => result.Status switch
            {
                RecommendationStatus.ResourceCandidate => 0,
                RecommendationStatus.NeedsDetails => 1,
                RecommendationStatus.Unsupported => 2,
                _ => 3
            }).ThenBy(result => result.EstimatedMemoryGiB ?? double.MaxValue).ToArray();
        return new(snapshot, machine, targetVolume, recommendations);
    }
}
