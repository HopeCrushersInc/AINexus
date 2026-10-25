using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace AINexus.App;

/// <summary>
/// Owns one explicitly started portable llama.cpp server. It never searches for or terminates
/// another process. Diagnostics are bounded in memory and are not automatically persisted.
/// </summary>
public sealed class LlamaServerController : IDisposable, IAsyncDisposable
{
    private const int OutputLimit = 64 * 1024;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly object _stateGate = new();
    private readonly object _outputGate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly byte[] _output = new byte[OutputLimit];
    private int _outputHead;
    private int _outputCount;
    private Process? _owned;
    private Task? _stdout;
    private Task? _stderr;
    private CancellationTokenSource? _starting;
    private Uri? _endpoint;
    private string? _lastError;
    private bool _stopping;
    private bool _disposed;
    private long _stopEpoch;
    private Task? _disposeTask;
    private readonly Func<string, string, int, CancellationToken, Task> _requireOfflineSupport;

    public LlamaServerController() : this(LlamaRuntimeCapabilities.RequireOfflineSupportAsync) { }

    internal LlamaServerController(Func<string, string, int, CancellationToken, Task> requireOfflineSupport)
    {
        _requireOfflineSupport = requireOfflineSupport ?? throw new ArgumentNullException(nameof(requireOfflineSupport));
    }

    public event Action<string>? StatusChanged;

    public bool IsRunning { get { lock (_stateGate) return IsAlive(_owned); } }
    public int? Pid { get { lock (_stateGate) return IsAlive(_owned) ? _owned!.Id : null; } }
    public Uri? Endpoint { get { lock (_stateGate) return IsAlive(_owned) ? _endpoint : null; } }
    public string? LastError { get { lock (_stateGate) return _lastError; } }

    /// <summary>Latest 64 KiB of UTF-8 diagnostic bytes; never displayed or saved automatically.</summary>
    public string RecentOutput
    {
        get
        {
            lock (_outputGate)
            {
                var bytes = new byte[_outputCount];
                for (var i = 0; i < bytes.Length; i++) bytes[i] = _output[(_outputHead + i) % OutputLimit];
                return Encoding.UTF8.GetString(bytes);
            }
        }
    }

    public async Task StartAsync(string executablePath, string modelPath, int port, int contextSize = 4096,
        CancellationToken cancellationToken = default, bool offlineMode = false)
    {
        long startEpoch;
        lock (_stateGate) startEpoch = _stopEpoch;
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        CancellationTokenSource? starting = null;
        try
        {
            lock (_stateGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (IsAlive(_owned)) throw new InvalidOperationException("A server owned by this controller is already running.");
                starting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
                _starting = starting;
                if (startEpoch != _stopEpoch) starting.Cancel();
                _lastError = null;
            }
            if (_owned is not null) await StopOwnedAsync().ConfigureAwait(false);
            if (offlineMode) LlamaRuntimeCapabilities.ValidateOfflinePaths(executablePath, modelPath);
            await ValidateConfigurationAsync(executablePath, modelPath, port, contextSize, starting.Token).ConfigureAwait(false);
            lock (_outputGate) { _outputHead = 0; _outputCount = 0; }

            // Reserve briefly to detect an existing listener without inspecting its process or PID.
            // The reservation must be released before the owned child can bind this same address.
            using (var reservation = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                reservation.ExclusiveAddressUse = true;
                try { reservation.Bind(new IPEndPoint(IPAddress.Loopback, port)); }
                catch (SocketException ex)
                {
                    throw new InvalidOperationException("The selected loopback port is unavailable. Existing servers were left untouched.", ex);
                }
            }
            if (offlineMode)
                await _requireOfflineSupport(executablePath, modelPath, contextSize, starting.Token).ConfigureAwait(false);
            starting.Token.ThrowIfCancellationRequested();
            var endpoint = new Uri($"http://127.0.0.1:{port}");
            var process = new Process { StartInfo = CreateStartInfo(executablePath, modelPath, port, contextSize, offlineMode), EnableRaisingEvents = true };
            process.Exited += OwnedProcessExited;
            try
            {
                if (!process.Start()) throw new InvalidOperationException("Windows did not start the selected portable server.");
            }
            catch { process.Dispose(); throw; }
            lock (_stateGate)
            {
                _owned = process;
                _stopping = false;
                _stdout = DrainAsync(process.StandardOutput, "stdout");
                _stderr = DrainAsync(process.StandardError, "stderr");
            }
            Publish("Starting the owned portable server; waiting for its local health endpoint.");
            await WaitForReadyAsync(process, endpoint, starting.Token).ConfigureAwait(false);
            starting.Token.ThrowIfCancellationRequested();
            lock (_stateGate)
            {
                if (!IsAlive(process)) throw new InvalidOperationException("The owned portable server exited before startup completed.");
                _endpoint = endpoint;
            }
            Publish("Owned portable server is ready on its loopback endpoint.");
        }
        catch (Exception ex)
        {
            lock (_stateGate) _lastError = ex is OperationCanceledException ? "Portable server startup was cancelled." : ex.Message;
            // A rejected duplicate Start call must never terminate the already running server.
            if (starting is not null && _owned is not null)
            {
                try { await StopOwnedAsync().ConfigureAwait(false); }
                catch (Exception cleanup) when (cleanup is not OutOfMemoryException)
                {
                    lock (_stateGate) _lastError = "Startup failed and the owned server could not be stopped. Ownership is retained for another stop attempt.";
                    throw new InvalidOperationException(_lastError, new AggregateException(ex, cleanup));
                }
            }
            Publish(ex is OperationCanceledException ? "Portable server startup cancelled." : "Portable server startup failed; inspect bounded diagnostics if needed.");
            throw;
        }
        finally
        {
            lock (_stateGate) { if (ReferenceEquals(_starting, starting)) _starting = null; }
            starting?.Dispose();
            _lifecycle.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_stateGate) _stopEpoch++;
        CancelStarting();
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await StopOwnedAsync().ConfigureAwait(false); }
        finally { _lifecycle.Release(); }
    }

    internal static async Task ValidateConfigurationAsync(string executablePath, string modelPath, int port,
        int contextSize, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        if (!Path.IsPathFullyQualified(executablePath) || !Path.IsPathFullyQualified(modelPath))
            throw new ArgumentException("Select absolute paths for the portable executable and GGUF model.");
        if (!string.Equals(Path.GetFileName(executablePath), "llama-server.exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Select the portable llama-server.exe executable.");
        if (!File.Exists(executablePath)) throw new FileNotFoundException("The selected portable server executable does not exist.");
        if (!string.Equals(Path.GetExtension(modelPath), ".gguf", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Select a GGUF model file.");
        if (port is < 1024 or > 65535) throw new ArgumentOutOfRangeException(nameof(port), "The loopback port must be between 1024 and 65535.");
        if (contextSize is < 512 or > 32768) throw new ArgumentOutOfRangeException(nameof(contextSize), "Context policy must be between 512 and 32768 tokens.");
        await using var model = new FileStream(modelPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        var header = new byte[4];
        try { await model.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false); }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException("The selected model is too short to contain a GGUF header.", ex);
        }
        if (!header.AsSpan().SequenceEqual("GGUF"u8))
            throw new InvalidDataException("The selected model does not have a GGUF header. Only its first four bytes were inspected.");
    }

    internal static ProcessStartInfo CreateStartInfo(string executablePath, string modelPath, int port, int contextSize, bool offlineMode = false)
    {
        var info = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(executablePath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in new[]
        {
            "--model", modelPath, "--host", "127.0.0.1", "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--ctx-size", contextSize.ToString(System.Globalization.CultureInfo.InvariantCulture), "--parallel", "1",
            "--n-gpu-layers", "auto", "--no-ui-mcp-proxy", "--cors-origins", $"http://127.0.0.1:{port}"
        }) info.ArgumentList.Add(argument);
        if (offlineMode) info.ArgumentList.Add("--offline");

        // Do not forward API keys, HF tokens, tool settings, or LLAMA_ARG overrides to a child.
        // Keep only Windows/runtime essentials and existing device visibility restrictions.
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "PATH", "SystemRoot", "WINDIR", "SystemDrive", "ComSpec", "TEMP", "TMP", "USERPROFILE", "APPDATA", "LOCALAPPDATA",
            "ProgramData", "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432", "HOMEDRIVE", "HOMEPATH",
            "PROCESSOR_ARCHITECTURE", "NUMBER_OF_PROCESSORS", "CUDA_VISIBLE_DEVICES", "HIP_VISIBLE_DEVICES", "ROCR_VISIBLE_DEVICES"
        };
        foreach (var name in info.Environment.Keys.Where(name => !allowed.Contains(name)).ToArray()) info.Environment.Remove(name);
        return info;
    }

    private static async Task WaitForReadyAsync(Process process, Uri endpoint, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var http = OllamaClient.CreateLocalHttpClient();
        try
        {
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (!IsAlive(process)) throw new InvalidOperationException($"The owned portable server exited during startup (code {process.ExitCode}).");
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                attempt.CancelAfter(TimeSpan.FromSeconds(1));
                try
                {
                    using var response = await http.GetAsync(new Uri(endpoint, "/health"), HttpCompletionOption.ResponseHeadersRead, attempt.Token).ConfigureAwait(false);
                    if ((int)response.StatusCode is >= 300 and < 400)
                        throw new InvalidOperationException("The owned server health endpoint returned a redirect.");
                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        if (response.Content.Headers.ContentLength > 16384) throw new InvalidDataException("The server health response exceeds its size limit.");
                        await using var stream = await response.Content.ReadAsStreamAsync(attempt.Token).ConfigureAwait(false);
                        using var data = new MemoryStream();
                        var buffer = new byte[1024];
                        int read;
                        while ((read = await stream.ReadAsync(buffer, attempt.Token).ConfigureAwait(false)) > 0)
                        {
                            if (data.Length + read > 16384) throw new InvalidDataException("The server health response exceeds its size limit.");
                            data.Write(buffer, 0, read);
                        }
                        using var json = JsonDocument.Parse(data.GetBuffer().AsMemory(0, (int)data.Length));
                        if (json.RootElement.ValueKind == JsonValueKind.Object
                            && json.RootElement.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String
                            && status.GetString() == "ok") return;
                        throw new InvalidDataException("The owned server did not report healthy status.");
                    }
                    if (response.StatusCode != HttpStatusCode.ServiceUnavailable)
                        throw new InvalidOperationException($"The server health endpoint returned HTTP {(int)response.StatusCode}.");
                }
                catch (HttpRequestException) { /* The owned process may still be loading its model. */ }
                catch (OperationCanceledException) when (!deadline.IsCancellationRequested) { }
                await Task.Delay(150, deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The owned portable server did not become healthy within 30 seconds.");
        }
    }

    private async Task StopOwnedAsync()
    {
        Process? process;
        Task[] drains;
        lock (_stateGate)
        {
            process = _owned;
            if (process is null) return;
            _stopping = true;
            _endpoint = null;
            drains = new[] { _stdout, _stderr }.OfType<Task>().ToArray();
        }
        try
        {
            if (IsAlive(process)) process.Kill(entireProcessTree: true);
            using var exitDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(exitDeadline.Token).ConfigureAwait(false);
        }
        catch (InvalidOperationException) when (!IsAlive(process)) { }
        catch (Exception ex) when (ex is Win32Exception or OperationCanceledException or InvalidOperationException or NotSupportedException)
        {
            lock (_stateGate) { _stopping = false; _lastError = "The owned portable server could not be stopped; its process remains tracked."; }
            throw new InvalidOperationException(LastError, ex);
        }
        try { await Task.WhenAll(drains).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            process.StandardOutput.Dispose();
            process.StandardError.Dispose();
        }
        lock (_stateGate)
        {
            _owned = null;
            _stdout = null;
            _stderr = null;
            _stopping = false;
        }
        process.Dispose();
        Publish("Owned portable server stopped. External runtimes were left untouched.");
    }

    private async Task DrainAsync(StreamReader reader, string source)
    {
        try
        {
            var buffer = new char[2048];
            int read;
            while ((read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
                AppendOutput($"[{source}] " + new string(buffer, 0, read));
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    internal void AppendOutput(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        lock (_outputGate)
        {
            foreach (var value in bytes)
            {
                if (_outputCount == OutputLimit) { _output[_outputHead] = value; _outputHead = (_outputHead + 1) % OutputLimit; }
                else { _output[(_outputHead + _outputCount) % OutputLimit] = value; _outputCount++; }
            }
        }
    }

    private void OwnedProcessExited(object? sender, EventArgs args)
    {
        lock (_stateGate)
        {
            if (!ReferenceEquals(sender, _owned) || _stopping) return;
            _endpoint = null;
            _lastError = "The owned portable server exited. Inspect bounded diagnostics if needed.";
        }
        Publish("Owned portable server exited.");
    }

    private static bool IsAlive(Process? process)
    {
        if (process is null) return false;
        try { return !process.HasExited; }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { return false; }
    }

    private void CancelStarting()
    {
        CancellationTokenSource? starting;
        lock (_stateGate) starting = _starting;
        try { starting?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private void Publish(string status)
    {
        var observers = StatusChanged;
        if (observers is null) return;
        foreach (Action<string> observer in observers.GetInvocationList())
        {
            try { observer(status); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { }
        }
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        lock (_stateGate)
        {
            if (_disposeTask is not null) return new ValueTask(_disposeTask);
            _disposed = true;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _disposeTask = completion.Task;
        }
        _ = CompleteDisposeAsync(completion);
        return new ValueTask(completion.Task);
    }

    private async Task CompleteDisposeAsync(TaskCompletionSource completion)
    {
        try
        {
            _lifetime.Cancel();
            CancelStarting();
            await _lifecycle.WaitAsync().ConfigureAwait(false);
            try { await StopOwnedAsync().ConfigureAwait(false); }
            finally { _lifecycle.Release(); }
            _lifetime.Dispose();
            completion.TrySetResult();
        }
        catch (Exception ex) { completion.TrySetException(ex); }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
