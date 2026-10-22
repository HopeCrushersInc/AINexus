using System.Diagnostics;
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
        var systemTask = Task.Run(() => ReadSystemInfo(cancellationToken), cancellationToken);
        var gpuTask = Task.Run(() => ReadGpuInfo(cancellationToken), cancellationToken);
        var storageTask = Task.Run(ReadStorageInfo, cancellationToken);

        await Task.WhenAll(systemTask, gpuTask, storageTask);
        var (cpu, ram) = await systemTask;
        var notes = new List<string>();
        if (gpuTask.Result.Count == 0)
            notes.Add("Windows did not report a display adapter.");
        if (string.IsNullOrWhiteSpace(cpu))
            notes.Add("CPU details were unavailable.");

        return new HardwareSnapshot(
            string.IsNullOrWhiteSpace(cpu) ? "Unavailable" : cpu,
            ram,
            gpuTask.Result,
            storageTask.Result,
            notes.Count == 0 ? null : string.Join(" ", notes));
    }

    private static (string Cpu, double RamGb) ReadSystemInfo(CancellationToken token)
    {
        const string script = "$cpu=(Get-CimInstance Win32_Processor | Select-Object -First 1 -ExpandProperty Name); " +
                              "$ram=[math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB, 1); " +
                              "[pscustomobject]@{Cpu=$cpu;RamGb=$ram} | ConvertTo-Json -Compress";
        var output = RunPowerShell(script, token);
        using var json = JsonDocument.Parse(output);
        var root = json.RootElement;
        return (
            root.TryGetProperty("Cpu", out var cpu) ? cpu.GetString() ?? "Unavailable" : "Unavailable",
            root.TryGetProperty("RamGb", out var ram) ? ram.GetDouble() : 0);
    }

    private static IReadOnlyList<GpuInfo> ReadGpuInfo(CancellationToken token)
    {
        // nvidia-smi provides the actual NVIDIA VRAM value when available.
        var nvidiaOutput = TryRun("nvidia-smi", new[]
        {
            "--query-gpu=name,memory.total", "--format=csv,noheader,nounits"
        }, token);

        if (!string.IsNullOrWhiteSpace(nvidiaOutput))
        {
            var detected = nvidiaOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Split(',', 2))
                .Where(parts => parts.Length == 2)
                .Select(parts =>
                {
                    var name = parts[0].Trim();
                    var hasMemory = double.TryParse(parts[1].Trim(), out var mb);
                    return new GpuInfo(name, hasMemory ? Math.Round(mb / 1024d, 1) : null,
                        hasMemory ? "NVIDIA driver" : "VRAM unavailable");
                }).ToList();
            if (detected.Count > 0) return detected;
        }

        // WMI's AdapterRAM field is only 32-bit on many Windows versions, so don't
        // present it as authoritative VRAM. Names are still useful as a fallback.
        const string script = "Get-CimInstance Win32_VideoController | Select-Object Name | ConvertTo-Json -Compress";
        try
        {
            var output = RunPowerShell(script, token);
            using var json = JsonDocument.Parse(output);
            var items = json.RootElement.ValueKind == JsonValueKind.Array
                ? json.RootElement.EnumerateArray().ToArray()
                : new[] { json.RootElement };
            return items.Select(item => new GpuInfo(
                item.TryGetProperty("Name", out var name) ? name.GetString() ?? "Unknown GPU" : "Unknown GPU",
                null, "VRAM unavailable")).ToList();
        }
        catch
        {
            return Array.Empty<GpuInfo>();
        }
    }

    private static IReadOnlyList<StorageInfo> ReadStorageInfo()
    {
        return DriveInfo.GetDrives()
            .Where(drive => drive.IsReady && drive.DriveType == DriveType.Fixed)
            .Select(drive => new StorageInfo(
                $"{drive.Name} ({drive.VolumeLabel})".Replace("()", "").Trim(),
                Math.Round(drive.AvailableFreeSpace / 1073741824d, 1),
                Math.Round(drive.TotalSize / 1073741824d, 1)))
            .OrderBy(drive => drive.Name)
            .ToList();
    }

    private static string RunPowerShell(string script, CancellationToken token)
    {
        var output = TryRun("powershell.exe", new[] { "-NoProfile", "-NonInteractive", "-Command", script }, token);
        if (string.IsNullOrWhiteSpace(output))
            throw new InvalidOperationException("PowerShell returned no hardware information.");
        return output;
    }

    private static string? TryRun(string fileName, IEnumerable<string> arguments, CancellationToken token)
    {
        try
        {
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
            if (!process.Start()) return null;
            var outputTask = process.StandardOutput.ReadToEndAsync(token);
            var errorTask = process.StandardError.ReadToEndAsync(token);
            process.WaitForExitAsync(token).GetAwaiter().GetResult();
            var output = outputTask.GetAwaiter().GetResult();
            _ = errorTask.GetAwaiter().GetResult();
            return process.ExitCode == 0 ? output.Trim() : null;
        }
        catch
        {
            return null;
        }
    }
}
