using System.Collections.Concurrent;
using System.Threading.Channels;

namespace AINexus.App;

public enum LocalGenerationRuntime { Ollama, LlamaCpp }

public sealed record AgentRequest(Guid Id, string Name, Uri Endpoint, string Model, string SystemPrompt, string Prompt,
    ResourcePolicy Policy, LocalGenerationRuntime Runtime = LocalGenerationRuntime.Ollama);
public sealed record AgentJobUpdate(Guid Id, string Status, string Detail, string? Response = null);

/// <summary>
/// Serial local generation queue. Resource policy controls admission only, not OS quotas.
/// No tool execution or file/system access is granted to model output.
/// </summary>
public sealed class AgentScheduler : IAsyncDisposable
{
    private readonly Channel<AgentRequest> _queue = Channel.CreateBounded<AgentRequest>(new BoundedChannelOptions(32)
    {
        SingleReader = true, FullMode = BoundedChannelFullMode.Wait
    });
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _jobs = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ILocalGenerationClient _client;
    private readonly ILocalGenerationClient? _llamaCppClient;
    private readonly Func<CancellationToken, Task<ResourceSnapshot>> _sample;
    private readonly Task _worker;
    private readonly object _stateGate = new();
    private int _disposed;

    public event Action<AgentJobUpdate>? Updated;

    public AgentScheduler(ILocalGenerationClient client, Func<CancellationToken, Task<ResourceSnapshot>> sample,
        ILocalGenerationClient? llamaCppClient = null)
    {
        _client = client;
        _llamaCppClient = llamaCppClient;
        _sample = sample;
        _worker = Task.Run(ProcessQueueAsync);
    }

    public bool TryEnqueue(AgentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_stateGate)
        {
        if (Volatile.Read(ref _disposed) != 0) return false;
        if (request.Runtime is not (LocalGenerationRuntime.Ollama or LocalGenerationRuntime.LlamaCpp)
            || request.Runtime == LocalGenerationRuntime.LlamaCpp && _llamaCppClient is null)
            throw new ArgumentException("The requested local runtime is not configured.", nameof(request));
        OllamaClient.ValidateEndpoint(request.Endpoint);
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Model) || string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Task name, actual model name, and prompt are required.");
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        if (!_jobs.TryAdd(request.Id, cancellation)) { cancellation.Dispose(); return false; }
        if (_queue.Writer.TryWrite(request)) return true;
        if (_jobs.TryRemove(request.Id, out var removed)) removed.Dispose();
        return false;
        }
    }

    public bool Cancel(Guid id)
    {
        if (!_jobs.TryGetValue(id, out var cancellation)) return false;
        try { cancellation.Cancel(); return true; }
        catch (ObjectDisposedException) { return false; }
    }

    private async Task ProcessQueueAsync()
    {
        await foreach (var request in _queue.Reader.ReadAllAsync())
        {
            if (!_jobs.TryGetValue(request.Id, out var cancellation)) continue;
            try
            {
                cancellation.Token.ThrowIfCancellationRequested();
                Publish(new(request.Id, "Checking resources", "Reading actual resource usage before admission."));
                // The first CPU sample establishes an interval; the second is used for admission.
                await _sample(cancellation.Token);
                await Task.Delay(250, cancellation.Token);
                var snapshot = await _sample(cancellation.Token);
                var admission = request.Policy.Evaluate(snapshot);
                if (!admission.Allowed)
                {
                    Publish(new(request.Id, "Blocked", admission.Reason));
                    continue;
                }
                Publish(new(request.Id, "Running", "Local generation request in progress. Resource policy is an admission check, not an OS quota."));
                var runtime = request.Runtime == LocalGenerationRuntime.LlamaCpp ? _llamaCppClient! : _client;
                var response = await runtime.GenerateAsync(request.Endpoint, request.Model, request.SystemPrompt, request.Prompt, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                Publish(new(request.Id, "Completed", "Local server reported completed generation.", response));
            }
            catch (OperationCanceledException)
            {
                Publish(new(request.Id, "Cancelled", "The request was cancelled. The server may continue processing until it observes disconnection."));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Publish(new(request.Id, "Failed", ex.Message));
            }
            finally
            {
                if (_jobs.TryRemove(request.Id, out var completed)) completed.Dispose();
            }
        }
    }

    private void Publish(AgentJobUpdate update)
    {
        var observers = Updated;
        if (observers is null) return;
        foreach (Action<AgentJobUpdate> observer in observers.GetInvocationList())
        {
            try { observer(update); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Presentation failures must not terminate the worker or strand queued requests.
                System.Diagnostics.Debug.WriteLine($"Agent status observer failed: {ex.GetType().Name}");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_stateGate)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _queue.Writer.TryComplete();
        }
        _lifetime.Cancel();
        await _worker;
        _lifetime.Dispose();
    }
}
