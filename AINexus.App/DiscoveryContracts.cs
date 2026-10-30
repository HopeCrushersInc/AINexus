namespace AINexus.App;

public enum DiscoveryInterest
{
    LocalAssistants, Coding, Images, MusicAudio, Video, Speech, ThreeD, Cad,
    ThreeDPrinting, GameDevelopment, ReverseEngineering, Streaming, Agents
}

public sealed record DiscoveryWorkflow(DiscoveryInterest Interest, string Name, string Scope);

public sealed record DiscoverySource(string Name, Uri Url, DateTimeOffset RetrievedAt);

public sealed record DiscoverySignals(long? Downloads, long? Likes, DateTimeOffset? UpdatedAt, long? ParameterCount = null);

public sealed record ModelArtifactPart(string FileName, long SizeBytes, string Sha256);

public sealed record ModelArtifact(
    string Repository, string Revision, string FileName, long? SizeBytes, string? Sha256,
    string Format, bool Gated,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<ModelArtifactPart>? AdditionalFiles = null);

public sealed record DiscoveredItem(
    string Id, string Name, string Publisher, DiscoveryInterest Interest, string Kind,
    string? License, ModelArtifact? Artifact, IReadOnlyList<DiscoverySource> Sources,
    string Description, string? PipelineTag = null, RuntimeReleaseMetadata? RuntimeRelease = null,
    DiscoverySignals? Signals = null);

public sealed record RuntimeReleaseAsset(string Name, long SizeBytes, string? Sha256, Uri DownloadUrl);
public sealed record RuntimeReleaseMetadata(string Tag, bool Prerelease, bool Immutable,
    IReadOnlyList<RuntimeReleaseAsset> Archives, IReadOnlyList<string> Notes);

public enum TextSearchOrder { Broad, Popular, Trending, RecentlyUpdated }

public sealed record OnlineDiscoveryRequest(DiscoveryInterest Interest, string Query, int Limit = 12,
    string? RuntimeReleaseTag = null, TextSearchOrder SearchOrder = TextSearchOrder.Broad);

public sealed record OnlineDiscoverySnapshot(
    IReadOnlyList<DiscoveredItem> Items, IReadOnlyList<string> Notes,
    DateTimeOffset RetrievedAt, bool IsCached = false);

public enum RecommendationStatus { ResourceCandidate, InsufficientResources, NeedsDetails, Unsupported }

public sealed record CompatibilityPolicy(
    double WeightMemoryMultiplier = 1.25, double RuntimeReserveGiB = 1,
    double ContextReserveGiB = 1, double StorageReserveGiB = 1);

public sealed record CompatibilityRecommendation(
    DiscoveredItem Item, RecommendationStatus Status, string ExecutionPath,
    double? EstimatedMemoryGiB, double? RequiredStorageGiB,
    IReadOnlyList<string> Reasons, IReadOnlyList<string> Limitations);

/// <summary>Read-only discovery contract. Discovery never installs, builds or executes candidates.</summary>
public interface IOnlineDiscoveryProvider
{
    Task<OnlineDiscoverySnapshot> SearchAsync(OnlineDiscoveryRequest request, CancellationToken cancellationToken = default);
}
