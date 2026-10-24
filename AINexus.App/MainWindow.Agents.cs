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

    private void InitializeAgents()
    {
        var monitor = new ResourceMonitor();
        _agents = new AgentScheduler(new OllamaGenerationClient(_agentHttp), monitor.SampleAsync);
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
    }

    private void UpdateAgentModels(OllamaSnapshot snapshot, Uri endpoint)
    {
        _agentEndpoint = snapshot.AvailableModelsSucceeded ? endpoint : null;
        AgentModelSelector.ItemsSource = snapshot.AvailableModelsSucceeded
            ? snapshot.AvailableModels.Select(model => model.Name).Distinct().Order().ToArray() : Array.Empty<string>();
        AgentModelSelector.SelectedIndex = -1;
        AgentStatusText.Text = snapshot.AvailableModelsSucceeded
            ? $"{snapshot.AvailableModels.Count} models reported by {endpoint.GetLeftPart(UriPartial.Authority)}. Select one explicitly. Admission checks apply to each task; no OS quotas are imposed."
            : "Model discovery is unavailable. An existing local Ollama server and an installed model are required for generation.";
    }

    private void AgentModelSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AgentEnqueueButton is not null) AgentEnqueueButton.IsEnabled = _agentEndpoint is not null && AgentModelSelector.SelectedItem is string;
    }

    private void AgentRefreshModelsButton_Click(object sender, RoutedEventArgs e) => OllamaRefreshButton_Click(sender, e);

    private void AgentEnqueueButton_Click(object sender, RoutedEventArgs e)
    {
        if (_agents is null || _agentEndpoint is null || AgentModelSelector.SelectedItem is not string model) return;
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
            AgentSystemText.Text, AgentPromptText.Text, new ResourcePolicy(ram, cpu, vram));
        var row = new AgentRow(request.Id, request.Name, model);
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

    private void AgentJobGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AgentJobGrid.SelectedItem is AgentRow row) DisplayAgentOutput(row);
    }

    private void DisplayAgentOutput(AgentRow row) => AgentOutputText.Text = row.Response ?? $"{row.Status}: {row.Detail}";

    private sealed class AgentRow(Guid id, string name, string model) : INotifyPropertyChanged
    {
        public Guid Id { get; } = id;
        public string Name { get; } = name;
        public string Model { get; } = model;
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
