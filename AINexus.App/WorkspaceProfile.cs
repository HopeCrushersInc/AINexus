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
    public string Name => workflow.Name;
    public string Description => Interest switch
    {
        DiscoveryInterest.Images => "Choose an image engine and model. An existing ComfyUI installation can be configured; image downloads are not integrated yet.",
        DiscoveryInterest.LocalAssistants or DiscoveryInterest.Coding or DiscoveryInterest.Agents => "Search compatible local text models, review the exact download, then configure your chosen runtime.",
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
