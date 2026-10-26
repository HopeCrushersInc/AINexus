using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace AINexus.App;

public sealed record GpuInfo(string Name, double? VramGb, string VramSource);
public sealed record StorageInfo(string Name, double FreeGb, double TotalGb);
public sealed record HardwareSnapshot(
    string CpuName,
    double RamGb,
    IReadOnlyList<GpuInfo> Gpus,
    IReadOnlyList<StorageInfo> Storage,
    string? ScanNote = null);

public static class HardwareScanner
{
    public static async Task<HardwareSnapshot> ScanAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var systemTask = CaptureAsync(() => ReadSystemInfoAsync(cancellationToken),
            (Cpu: "Unavailable", RamGb: 0d), "CPU and memory");
        var gpuTask = CaptureAsync(() => ReadGpuInfoAsync(cancellationToken),
            (Gpus: (IReadOnlyList<GpuInfo>)Array.Empty<GpuInfo>(), Note: (string?)null), "Graphics");
        var storageTask = CaptureAsync(() => Task.Run(() => ReadStorageInfo(cancellationToken), cancellationToken),
            (Storage: (IReadOnlyList<StorageInfo>)Array.Empty<StorageInfo>(), Note: (string?)null), "Storage");

        await Task.WhenAll(systemTask, gpuTask, storageTask);
        cancellationToken.ThrowIfCancellationRequested();
        var system = await systemTask;
        var gpu = await gpuTask;
        var storage = await storageTask;
        var notes = new List<string?> { system.Note, gpu.Note, gpu.Value.Note, storage.Note, storage.Value.Note };
        if (system.Value.Cpu == "Unavailable") notes.Add("CPU details were unavailable.");
        if (system.Value.RamGb <= 0) notes.Add("Installed memory was unavailable.");
        if (gpu.Value.Gpus.Count == 0) notes.Add("No display adapters could be read.");
        var note = string.Join(" ", notes.Where(value => !string.IsNullOrWhiteSpace(value)));
        return new HardwareSnapshot(system.Value.Cpu, system.Value.RamGb, gpu.Value.Gpus,
            storage.Value.Storage, note.Length == 0 ? null : note);
    }

    private static async Task<(T Value, string? Note)> CaptureAsync<T>(Func<Task<T>> read, T fallback, string label)
    {
        try { return (await read(), null); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return (fallback, $"{label} scan failed: {ex.Message}");
        }
    }

    private static async Task<(string Cpu, double RamGb)> ReadSystemInfoAsync(CancellationToken token)
    {
        // Each property is independent so a failed CIM query does not hide the other.
        const string script = "$ErrorActionPreference='Stop'; $cpu=$null; $ram=$null; " +
                              "try {$cpu=Get-CimInstance Win32_Processor | Select-Object -First 1 -ExpandProperty Name} catch {}; " +
                              "try {$ram=[math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB, 1)} catch {}; " +
                              "[pscustomobject]@{Cpu=$cpu;RamGb=$ram} | ConvertTo-Json -Compress";
        using var json = JsonDocument.Parse(await RunPowerShellAsync(script, token));
        var root = json.RootElement;
        var cpu = root.TryGetProperty("Cpu", out var cpuValue) && cpuValue.ValueKind == JsonValueKind.String
            ? cpuValue.GetString() : null;
        var ram = root.TryGetProperty("RamGb", out var ramValue) && ramValue.ValueKind == JsonValueKind.Number
            && ramValue.TryGetDouble(out var value) && double.IsFinite(value) && value > 0 ? value : 0;
        return (string.IsNullOrWhiteSpace(cpu) ? "Unavailable" : cpu.Trim(), ram);
    }

    private static async Task<(IReadOnlyList<GpuInfo> Gpus, string? Note)> ReadGpuInfoAsync(CancellationToken token)
    {
        var notes = new List<string>();
        IReadOnlyList<GpuInfo> nvidia = Array.Empty<GpuInfo>();
        IReadOnlyList<GpuInfo> windows = Array.Empty<GpuInfo>();
        try
        {
            var output = await RunProcessAsync("nvidia-smi", new[]
            {
                "--query-gpu=name,memory.total", "--format=csv,noheader,nounits"
            }, token);
            if (!string.IsNullOrWhiteSpace(output)) nvidia = ParseNvidiaOutput(output);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            notes.Add($"NVIDIA VRAM query failed: {ex.Message}");
        }

        // Never use the often truncated 32-bit AdapterRAM value as actual VRAM.
        const string script = "$ErrorActionPreference='Stop'; Get-CimInstance Win32_VideoController | Select-Object Name | ConvertTo-Json -Compress";
        try
        {
            using var json = JsonDocument.Parse(await RunPowerShellAsync(script, token));
            var items = json.RootElement.ValueKind == JsonValueKind.Array
                ? json.RootElement.EnumerateArray().ToArray() : new[] { json.RootElement };
            windows = items.Where(item => item.ValueKind == JsonValueKind.Object)
                .Select(item => item.TryGetProperty("Name", out var name) && name.ValueKind == JsonValueKind.String
                    ? name.GetString() : null)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => new GpuInfo(name!.Trim(), null, "VRAM unavailable"))
                .ToArray();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            notes.Add($"Windows graphics inventory failed: {ex.Message}");
        }
        return (MergeGpuInfo(windows, nvidia), notes.Count == 0 ? null : string.Join(" ", notes));
    }

    internal static IReadOnlyList<GpuInfo> ParseNvidiaOutput(string output)
    {
        var result = new List<GpuInfo>();
        foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(',', 2);
            if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0])) continue;
            var hasMemory = double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var mb)
                && double.IsFinite(mb) && mb > 0;
            result.Add(new GpuInfo(parts[0].Trim(), hasMemory ? Math.Round(mb / 1024d, 1) : null,
                hasMemory ? "NVIDIA driver" : "VRAM unavailable"));
        }
        return result;
    }

    internal static IReadOnlyList<GpuInfo> MergeGpuInfo(IReadOnlyList<GpuInfo> windows, IReadOnlyList<GpuInfo> nvidia)
    {
        // Match one-to-one, retaining multiple physical cards with the same model name.
        var result = new List<GpuInfo>();
        var matched = new bool[nvidia.Count];
        foreach (var adapter in windows)
        {
            var index = -1;
            for (var i = 0; i < nvidia.Count; i++)
                if (!matched[i] && NormalizeGpuName(adapter.Name) == NormalizeGpuName(nvidia[i].Name))
                {
                    index = i;
                    break;
                }
            if (index >= 0)
            {
                matched[index] = true;
                result.Add(adapter with { VramGb = nvidia[index].VramGb, VramSource = nvidia[index].VramSource });
            }
            else result.Add(adapter);
        }
        for (var i = 0; i < nvidia.Count; i++)
            if (!matched[i]) result.Add(nvidia[i]);
        return result;
    }

    private static string NormalizeGpuName(string name)
    {
        var normalized = new string(name.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        return normalized.StartsWith("NVIDIA", StringComparison.Ordinal) ? normalized[6..] : normalized;
    }

    private static (IReadOnlyList<StorageInfo> Storage, string? Note) ReadStorageInfo(CancellationToken token)
    {
        var result = new List<StorageInfo>();
        var notes = new List<string>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                result.Add(new StorageInfo($"{drive.Name} ({drive.VolumeLabel})".Replace("()", "").Trim(),
                    Math.Round(drive.AvailableFreeSpace / 1073741824d, 1),
                    Math.Round(drive.TotalSize / 1073741824d, 1)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                notes.Add($"Storage information unavailable for {drive.Name}: {ex.Message}");
            }
        }
        return (result.OrderBy(drive => drive.Name).ToArray(), notes.Count == 0 ? null : string.Join(" ", notes));
    }

    private static async Task<string> RunPowerShellAsync(string script, CancellationToken token)
    {
        var output = await RunProcessAsync("powershell.exe", new[] { "-NoProfile", "-NonInteractive", "-Command", script }, token);
        return !string.IsNullOrWhiteSpace(output) ? output
            : throw new InvalidOperationException("PowerShell returned no hardware information.");
    }

    internal static async Task<string?> RunProcessAsync(string fileName, IEnumerable<string> arguments,
        CancellationToken token, TimeSpan? timeout = null)
    {
        token.ThrowIfCancellationRequested();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        try
        {
            if (!process.Start()) return null;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode is 2 or 3) { return null; }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(15));
        Task? completion = null;
        try
        {
            // Drain both pipes concurrently to avoid a full stderr pipe blocking exit.
            var outputTask = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var errorTask = process.StandardError.ReadToEndAsync(deadline.Token);
            completion = Task.WhenAll(process.WaitForExitAsync(deadline.Token), outputTask, errorTask);
            // The outer deadline also bounds streams whose underlying pipe cannot cancel immediately.
            await completion.WaitAsync(deadline.Token);
            if (process.ExitCode != 0) throw new InvalidOperationException($"{Path.GetFileName(fileName)} exited with code {process.ExitCode}.");
            return (await outputTask).Trim();
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException) { }
            if (completion is not null)
            {
                try { await completion.WaitAsync(TimeSpan.FromSeconds(2)); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { }
            }
            token.ThrowIfCancellationRequested();
            throw new TimeoutException($"{Path.GetFileName(fileName)} exceeded the hardware query time limit.");
        }
    }
}
