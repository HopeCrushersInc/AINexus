using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;

namespace AINexus.App;

public partial class MainWindow
{
    private readonly ObservableCollection<AgentRow> _agentRows = [];
    private readonly HttpClient _agentHttp = OllamaClient.CreateLocalHttpClient();
    private AgentScheduler? _agents;
    private Uri? _agentEndpoint;
    private readonly Dictionary<LocalGenerationRuntime, (Uri Endpoint, string[] Models)> _agentModelCatalogs = [];
    private const int LlamaCppTaskOutputTokens = 128;

    private sealed record AgentRuntimeChoice(LocalGenerationRuntime Runtime, string Name);
    private LocalGenerationRuntime? SelectedAgentRuntime =>
        (AgentRuntimeSelector.SelectedItem as AgentRuntimeChoice)?.Runtime;

    private void InitializeAgents()
    {
        var monitor = new ResourceMonitor();
        _agents = new AgentScheduler(new OllamaGenerationClient(_agentHttp), monitor.SampleAsync,
            new LlamaCppClient(_agentHttp, LlamaCppTaskOutputTokens));
        AgentRuntimeSelector.ItemsSource = Array.Empty<AgentRuntimeChoice>();
        AgentRuntimeSelector.SelectedIndex = -1;
        AgentRefreshModelsButton.IsEnabled = false;
        AgentJobGrid.ItemsSource = _agentRows;
        _agents.Updated += update =>
        {
            if (_lifetime.IsCancellationRequested || Dispatcher.HasShutdownStarted) return;
            Dispatcher.BeginInvoke(() =>
            {
                var row = _agentRows.FirstOrDefault(item => item.Id == update.Id);
                if (row is null) return;
                row.Update(update);
                if (AgentJobGrid.SelectedItem == row) DisplayAgentOutput(row);
            });
        };
        Closed += async (_, _) =>
        {
            await _agents.DisposeAsync();
            _agentHttp.Dispose();
        };
        RenderAgentModelCatalog();
    }

    private void UpdateAgentModels(OllamaSnapshot snapshot, Uri endpoint)
    {
        SetAgentModelCatalog(LocalGenerationRuntime.Ollama, endpoint, snapshot.AvailableModelsSucceeded
            ? snapshot.AvailableModels.Select(model => model.Name).Distinct().Order().ToArray() : null);
    }

    internal void SetAgentModelCatalog(LocalGenerationRuntime runtime, Uri endpoint, string[]? models)
    {
        var selected = SelectedAgentRuntime;
        if (models is null) _agentModelCatalogs.Remove(runtime);
        else _agentModelCatalogs[runtime] = (endpoint, models);
        AgentRuntimeSelector.ItemsSource = _agentModelCatalogs.Keys.Order().Select(key =>
            new AgentRuntimeChoice(key, (key == LocalGenerationRuntime.LlamaCpp ? "llama.cpp" : "Ollama") + " — queried connection")).ToArray();
        AgentRuntimeSelector.SelectedItem = AgentRuntimeSelector.Items.Cast<AgentRuntimeChoice>().FirstOrDefault(choice => choice.Runtime == selected);
        if (selected is null) AgentRuntimeSelector.SelectedIndex = -1;
        RenderAgentModelCatalog();
    }

    private void AgentRuntimeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AgentModelSelector is not null && AgentStatusText is not null) RenderAgentModelCatalog();
    }

    private void RenderAgentModelCatalog()
    {
        _agentEndpoint = null;
        AgentModelSelector.SelectedIndex = -1;
        AgentRefreshModelsButton.IsEnabled = SelectedAgentRuntime is not null;
        if (SelectedAgentRuntime is not { } runtime)
        {
            AgentModelSelector.ItemsSource = Array.Empty<string>();
            AgentStatusText.Text = "No assistant model or connection selected. Find a model in Discover. Connections appear here only after you explicitly query a chosen local server; engine names are not assistants.";
            AgentEnqueueButton.IsEnabled = false;
            return;
        }
        var name = runtime == LocalGenerationRuntime.LlamaCpp ? "llama.cpp" : "Ollama";
        if (_agentModelCatalogs.TryGetValue(runtime, out var catalog))
        {
            _agentEndpoint = catalog.Endpoint;
            AgentModelSelector.ItemsSource = catalog.Models;
            AgentStatusText.Text = $"{catalog.Models.Length} model IDs last reported by {name} at {catalog.Endpoint.GetLeftPart(UriPartial.Authority)}. Select one explicitly. Queued tasks retain their original runtime and endpoint.";
            if (SelectedAgentRuntime == LocalGenerationRuntime.LlamaCpp)
                AgentStatusText.Text += $" Output is limited to {LlamaCppTaskOutputTokens} tokens; truncation is reported as a task failure.";
        }
        else
        {
            AgentModelSelector.ItemsSource = Array.Empty<string>();
            AgentStatusText.Text = $"Query your existing local {name} server to select an actual model ID. The portal does not start a server or load a model.";
        }
        AgentEnqueueButton.IsEnabled = false;
    }

    private void AgentModelSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AgentEnqueueButton is not null) AgentEnqueueButton.IsEnabled = SelectedAgentRuntime is not null && _agentEndpoint is not null && AgentModelSelector.SelectedItem is string;
    }

    private async void AgentFindModels_Click(object sender, RoutedEventArgs e)
    {
        if (!_workspaceConfigured || _discoveryBusy || _workspaceProfile?.HasAssistant != true) return;
        DiscoveryInterestSelector.SelectedItem = ((IEnumerable<DiscoveryWorkflow>)DiscoveryInterestSelector.ItemsSource)
            .FirstOrDefault(workflow => workflow.Interest is DiscoveryInterest.LocalAssistants or DiscoveryInterest.Coding or DiscoveryInterest.Agents);
        DiscoveryTab.IsSelected = true;
        await RunDiscoverySearchAsync(online: true);
    }

    private async void AgentRefreshModelsButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedAgentRuntime is null) return;
        if (SelectedAgentRuntime == LocalGenerationRuntime.LlamaCpp) await QueryLlamaCppAsync();
        else OllamaRefreshButton_Click(sender, e);
    }

    private void AgentEnqueueButton_Click(object sender, RoutedEventArgs e)
    {
        if (_agents is null || _agentEndpoint is null || SelectedAgentRuntime is not { } runtime || AgentModelSelector.SelectedItem is not string model) return;
        if (_agentRows.Count >= 100)
        {
            AgentStatusText.Text = "This session has reached its 100-task history limit. Start a new session for additional tasks.";
            return;
        }
        if (!double.TryParse(AgentMinRamText.Text, out var ram) || !double.TryParse(AgentMaxCpuText.Text, out var cpu) ||
            !double.TryParse(AgentMinVramText.Text, out var vram))
        {
            AgentStatusText.Text = "Enter numeric resource policy thresholds.";
            return;
        }
        var request = new AgentRequest(Guid.NewGuid(), AgentNameText.Text.Trim(), _agentEndpoint, model,
            AgentSystemText.Text, AgentPromptText.Text, new ResourcePolicy(ram, cpu, vram), runtime);
        var row = new AgentRow(request.Id, request.Name, model, request.Runtime == LocalGenerationRuntime.LlamaCpp ? "llama.cpp" : "Ollama");
        _agentRows.Add(row);
        try
        {
            if (!_agents.TryEnqueue(request))
            {
                _agentRows.Remove(row);
                AgentStatusText.Text = "The task queue is full or shutting down. Wait before submitting another task.";
                return;
            }
            AgentJobGrid.SelectedItem = row;
            AgentStatusText.Text = "Task queued. Requests run one at a time after actual resource readings pass the selected policy. Model loading can use additional resources beyond this admission check.";
            if (request.Runtime == LocalGenerationRuntime.LlamaCpp)
                AgentStatusText.Text += $" llama.cpp output limit: {LlamaCppTaskOutputTokens} tokens; truncated replies are reported as failures.";
        }
        catch (ArgumentException ex)
        {
            _agentRows.Remove(row);
            AgentStatusText.Text = ex.Message;
        }
    }

    private void AgentCancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (AgentJobGrid.SelectedItem is AgentRow row && _agents?.Cancel(row.Id) == true)
            AgentStatusText.Text = "Cancellation requested. A local server may continue processing until it observes disconnection.";
    }

    private void CancelLlamaCppAgentTasks()
    {
        foreach (var row in _agentRows.Where(row => row.Runtime == "llama.cpp"))
            _agents?.Cancel(row.Id);
    }

    private void AgentJobGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AgentJobGrid.SelectedItem is AgentRow row) DisplayAgentOutput(row);
    }

    private void DisplayAgentOutput(AgentRow row) => AgentOutputText.Text = row.Response ?? $"{row.Status}: {row.Detail}";

    private sealed class AgentRow(Guid id, string name, string model, string runtime) : INotifyPropertyChanged
    {
        public Guid Id { get; } = id;
        public string Name { get; } = name;
        public string Model { get; } = model;
        public string Runtime { get; } = runtime;
        public string Status { get; private set; } = "Queued";
        public string Detail { get; private set; } = "Waiting for the preceding task.";
        public string? Response { get; private set; }
        public event PropertyChangedEventHandler? PropertyChanged;
        public void Update(AgentJobUpdate update)
        {
            Status = update.Status;
            Detail = update.Detail;
            Response = update.Response;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        }
    }
}
