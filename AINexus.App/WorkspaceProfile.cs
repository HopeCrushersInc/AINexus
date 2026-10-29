using System.ComponentModel;

namespace AINexus.App;

public sealed record WorkspaceProfile(IReadOnlyList<DiscoveryInterest> Interests)
{
    public bool HasImages => Interests.Contains(DiscoveryInterest.Images);
    public bool HasAssistant => Interests.Any(i => i is DiscoveryInterest.LocalAssistants or DiscoveryInterest.Coding or DiscoveryInterest.Agents);
    public static WorkspaceProfile Create(MachineCapabilities? machine, IEnumerable<DiscoveryInterest> interests)
    {
        if (machine is null) throw new InvalidOperationException("Scan this computer before choosing workloads.");
        var selected = interests.Distinct().Order().ToArray();
        if (selected.Length == 0 || selected.Any(i => !Enum.IsDefined(i))) throw new ArgumentException("Choose at least one supported interest from the list.");
        return new(Array.AsReadOnly(selected));
    }
}

public sealed class WorkloadChoice(DiscoveryWorkflow workflow, bool selected = false) : INotifyPropertyChanged
{
    private bool _selected = selected;
    public DiscoveryInterest Interest => workflow.Interest;
    public IReadOnlyList<DiscoveryInterest> Interests => WorkloadPresentation.Members(Interest);
    public string Name => workflow.Name;
    public string Group => WorkloadPresentation.GroupFor(Interest);
    public string Description => Interest switch
    {
        DiscoveryInterest.Images => "Find image models and tools. Choose your tool, then review its available download, setup or local-use actions.",
        DiscoveryInterest.Video => "Find video models and tools. Complete pipeline setup and generation are still being built.",
        DiscoveryInterest.LocalAssistants => "Find local models for conversation and writing. Models and the tools that run them are separate choices.",
        DiscoveryInterest.Coding => "Find local text models to evaluate for coding. Source metadata does not prove coding quality.",
        DiscoveryInterest.Agents => "Find local text models to evaluate for automation. Tool use and unattended execution are not inferred.",
        DiscoveryInterest.Cad => "Find CAD design and parametric modeling tools. Select printing separately for slicing or printer software.",
        DiscoveryInterest.ThreeDPrinting => "Find slicing, print preparation and printer tools. This does not automatically search CAD design or image generation.",
        DiscoveryInterest.ThreeD => "Find tools for meshes, sculpting and animation. CAD and printing have their own task group.",
        _ => "Search online tool sources for this task. Review the results yourself; automated installation support varies by tool."
    };
    public bool Selected
    {
        get => _selected;
        set { if (_selected == value) return; _selected = value; PropertyChanged?.Invoke(this, new(nameof(Selected))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed record WorkloadWorkspaceCard(DiscoveryInterest Interest, string Name, string Description, string Action, string TargetPage, bool Available);
