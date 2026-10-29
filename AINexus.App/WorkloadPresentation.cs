namespace AINexus.App;

/// <summary>Presentation groups never select or broaden an independently chosen task.</summary>
public static class WorkloadPresentation
{
    public static string GroupFor(DiscoveryInterest interest) => interest switch
    {
        DiscoveryInterest.LocalAssistants or DiscoveryInterest.Agents => "Assistants",
        DiscoveryInterest.Coding or DiscoveryInterest.GameDevelopment or DiscoveryInterest.ReverseEngineering => "Coding & software",
        DiscoveryInterest.Images or DiscoveryInterest.Video or DiscoveryInterest.MusicAudio or DiscoveryInterest.Speech
            or DiscoveryInterest.ThreeD or DiscoveryInterest.Streaming => "Creative media",
        DiscoveryInterest.Cad or DiscoveryInterest.ThreeDPrinting => "CAD & 3D printing",
        _ => throw new ArgumentOutOfRangeException(nameof(interest))
    };
    private static int GroupOrder(DiscoveryInterest interest) => GroupFor(interest) switch
    {
        "Assistants" => 0, "Coding & software" => 1, "Creative media" => 2, _ => 3
    };
    public static IReadOnlyList<DiscoveryInterest> Members(DiscoveryInterest interest) => new[] { interest };
    public static IReadOnlyList<DiscoveryWorkflow> Choices { get; } = Array.AsReadOnly(VerifiedOnlineDiscoveryProvider.Workflows
        .OrderBy(workflow => GroupOrder(workflow.Interest)).ToArray());
    public static IReadOnlyList<DiscoveryWorkflow> ForProfile(WorkspaceProfile profile) => Choices
        .Where(workflow => profile.Interests.Contains(workflow.Interest)).ToArray();
}
