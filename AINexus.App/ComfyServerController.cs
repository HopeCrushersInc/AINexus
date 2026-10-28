using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.Http;

namespace AINexus.App;

public sealed class ComfyServerController : IDisposable
{
    private Process? _process;
    private bool _started;
    private string _diagnostics = "";
    private readonly object _gate = new();
    public bool IsRunning => _started && _process is { HasExited: false };
    public string Diagnostics { get { lock (_gate) return _diagnostics; } }

    public async Task StartAsync(string directory, int port, CancellationToken token)
    {
        if (IsRunning) throw new InvalidOperationException("This window already owns an image engine.");
        if (_process is not null) Stop();
        if (port is < 1024 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        var root = Path.GetFullPath(directory);
        if (!File.Exists(Path.Combine(root, "python_embeded", "python.exe"))) root = Path.Combine(root, "ComfyUI_windows_portable");
        var python = Path.Combine(root, "python_embeded", "python.exe");
        var main = Path.Combine(root, "ComfyUI", "main.py");
        OfflineModelVerifier.CheckNoReparsePath(python);
        OfflineModelVerifier.CheckNoReparsePath(main);
        if (!File.Exists(python) || !File.Exists(main)) throw new FileNotFoundException("Choose an existing ComfyUI portable folder containing its embedded Python and ComfyUI directory. Nothing was installed.");
        var probe = new TcpListener(IPAddress.Loopback, port);
        try { probe.Start(); } finally { probe.Stop(); }
        var info = new ProcessStartInfo(python) { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var key in info.Environment.Keys.Where(k => k.StartsWith("PYTHON", StringComparison.OrdinalIgnoreCase)
            || k.Contains("TOKEN", StringComparison.OrdinalIgnoreCase) || k.Contains("API_KEY", StringComparison.OrdinalIgnoreCase)).ToArray()) info.Environment.Remove(key);
        foreach (var arg in new[] { "-s", main, "--windows-standalone-build", "--listen", "127.0.0.1", "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture), "--disable-auto-launch", "--disable-all-custom-nodes" }) info.ArgumentList.Add(arg);
        var config = Path.Combine(root, "ainexus-models.yaml");
        if (File.Exists(config)) { OfflineModelVerifier.CheckNoReparsePath(config); info.ArgumentList.Add("--extra-model-paths-config"); info.ArgumentList.Add(config); }
        ComfyCheckpointFolderPlanner.AppendStartupConfiguration(info, root);
        info.Environment["HF_HUB_OFFLINE"] = "1";
        info.Environment["TRANSFORMERS_OFFLINE"] = "1";
        info.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        var cache = Path.Combine(root, ".ainexus-cache");
        OfflineModelVerifier.CheckNoReparsePath(Path.Combine(cache, "temp"));
        Directory.CreateDirectory(Path.Combine(cache, "temp"));
        foreach (var key in new[] { "HF_HOME", "HUGGINGFACE_HUB_CACHE", "TORCH_HOME", "TORCHINDUCTOR_CACHE_DIR", "XDG_CACHE_HOME" }) info.Environment[key] = cache;
        info.Environment["TEMP"] = info.Environment["TMP"] = Path.Combine(cache, "temp");
        lock (_gate) _diagnostics = "";
        _process = new Process { StartInfo = info, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, e) => Append(e.Data);
        _process.ErrorDataReceived += (_, e) => Append(e.Data);
        try
        {
            if (!_process.Start()) throw new InvalidOperationException("The image engine could not start.");
            _started = true;
            _process.BeginOutputReadLine(); _process.BeginErrorReadLine();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromMinutes(2));
            using var http = OllamaClient.CreateLocalHttpClient();
            var client = new ComfyImageClient(http);
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (_process.HasExited) throw new InvalidOperationException("The image engine exited during startup. " + Diagnostics);
                try { await client.InspectAsync(new Uri($"http://127.0.0.1:{port}"), deadline.Token); break; }
                catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException) && !deadline.IsCancellationRequested) { }
                await Task.Delay(500, deadline.Token);
            }
        }
        catch { Stop(); throw; }
    }
    private void Append(string? line)
    {
        if (line is null) return;
        lock (_gate) { _diagnostics += line + "\n"; if (_diagnostics.Length > 16384) _diagnostics = _diagnostics[^16384..]; }
    }
    public void Stop()
    {
        if (_process is not { } process) return;
        if (_started && !process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            if (!process.WaitForExit(5000)) throw new InvalidOperationException("The owned image engine has not exited yet. Ownership is retained; retry Stop.");
        }
        process.Dispose(); _process = null; _started = false;
    }
    public void Dispose() => Stop();
}
