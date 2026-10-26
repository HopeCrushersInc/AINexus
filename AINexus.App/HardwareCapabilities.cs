using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Text;
using System.Text.Json;

namespace AINexus.App;

public sealed record StorageVolume(string Path, long AvailableBytes, long TotalBytes);

public sealed record DriverCapabilityEvidence(
    string AdapterName,
    string? DriverVersion,
    string? ComputeCapability,
    string Source,
    string Status,
    string? AdapterId = null);

public sealed record MachineCapabilities(
    HardwareSnapshot Hardware,
    ResourceSnapshot Resources,
    string OsDescription,
    string OsArchitecture,
    int LogicalProcessors,
    bool AvxSupported,
    bool Avx2Supported,
    IReadOnlyList<DriverCapabilityEvidence> DriverEvidence,
    IReadOnlyList<StorageVolume> Volumes,
    DateTimeOffset ScannedAt,
    IReadOnlyList<string> Notes);

/// <summary>
/// A read-only capability snapshot for recommendation decisions. Driver metadata is
/// evidence of reported capabilities, never proof that a downloaded runtime will execute.
/// No inference, GPU benchmark, driver installation, or external network request is performed.
/// </summary>
public static class HardwareCapabilityScanner
{
    public static async Task<MachineCapabilities> ScanAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var hardwareTask = HardwareScanner.ScanAsync(cancellationToken);
        var resourceTask = new ResourceMonitor().SampleAsync(cancellationToken);
        var driverTask = ReadDriversAsync(cancellationToken);
        var volumeTask = Task.Run(() => ReadVolumes(cancellationToken), cancellationToken);
        await Task.WhenAll(hardwareTask, resourceTask, driverTask, volumeTask);
        cancellationToken.ThrowIfCancellationRequested();
        var hardware = await hardwareTask;
        var resources = await resourceTask;
        var drivers = await driverTask;
        var volumes = await volumeTask;
        var notes = new List<string>(drivers.Notes);
        notes.AddRange(volumes.Notes);
        if (hardware.ScanNote is not null) notes.Add(hardware.ScanNote);
        notes.AddRange(resources.Notes);
        notes.Add("CPU instruction-set flags describe features usable by this process and operating system. Driver metadata does not verify application acceleration or benchmark performance.");
        notes.Add("Vulkan, DirectML, ROCm, and application-specific backend compatibility have not been probed by this scan.");
        return new MachineCapabilities(hardware, resources, RuntimeInformation.OSDescription,
            RuntimeInformation.OSArchitecture.ToString(), Environment.ProcessorCount,
            Avx.IsSupported, Avx2.IsSupported, drivers.Evidence, volumes.Volumes,
            DateTimeOffset.Now, notes.Distinct(StringComparer.Ordinal).ToList().AsReadOnly());
    }

    private static async Task<(IReadOnlyList<DriverCapabilityEvidence> Evidence, IReadOnlyList<string> Notes)> ReadDriversAsync(CancellationToken token)
    {
        var evidence = new List<DriverCapabilityEvidence>();
        var notes = new List<string>();
        // DriverVersion is read-only Win32_VideoController metadata. Do not use its
        // frequently truncated AdapterRAM field as a memory capacity measurement.
        // https://learn.microsoft.com/windows/win32/cimwin32prov/win32-videocontroller
        const string script = "$ErrorActionPreference='Stop'; Get-CimInstance Win32_VideoController | " +
            "Select-Object Name,DriverVersion,ConfigManagerErrorCode,PNPDeviceID | ConvertTo-Json -Compress";
        try
        {
            var output = await HardwareScanner.RunProcessAsync("powershell.exe",
                ["-NoProfile", "-NonInteractive", "-Command", script], token, TimeSpan.FromSeconds(10));
            if (!string.IsNullOrWhiteSpace(output)) evidence.AddRange(ParseWindowsDrivers(output));
            else notes.Add("Windows did not return graphics-driver metadata; acceleration remains unverified.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            notes.Add($"Windows graphics-driver metadata could not be read ({ex.GetType().Name}); acceleration remains unverified.");
        }

        // Only reporting fields are queried. No nvidia-smi modification option is used.
        // https://docs.nvidia.com/deploy/nvidia-smi/index.html
        try
        {
            var output = await HardwareScanner.RunProcessAsync("nvidia-smi",
                ["--query-gpu=name,driver_version,compute_cap,uuid", "--format=csv,noheader,nounits"], token, TimeSpan.FromSeconds(3));
            var nvidia = string.IsNullOrWhiteSpace(output) ? [] : ParseNvidiaDrivers(output);
            evidence.AddRange(nvidia);
            if (nvidia.Count == 0)
                notes.Add("No readable NVIDIA compute-capability evidence was returned. CUDA acceleration has not been established; other vendors are not classified by this query.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            notes.Add($"The NVIDIA compute-capability query was unavailable ({ex.GetType().Name}); CUDA compatibility remains unknown.");
            // Older drivers can reject an optional field. Preserve driver/name evidence
            // without inventing a compute capability or installing another dependency.
            try
            {
                var output = await HardwareScanner.RunProcessAsync("nvidia-smi",
                    ["--query-gpu=name,driver_version", "--format=csv,noheader,nounits"], token, TimeSpan.FromSeconds(3));
                if (!string.IsNullOrWhiteSpace(output)) evidence.AddRange(ParseNvidiaDrivers(output));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception fallback) when (fallback is not OutOfMemoryException)
            {
                notes.Add($"NVIDIA driver-version metadata was also unavailable ({fallback.GetType().Name}).");
            }
        }
        return (evidence.AsReadOnly(), notes.AsReadOnly());
    }

    internal static IReadOnlyList<DriverCapabilityEvidence> ParseWindowsDrivers(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Null) return Array.Empty<DriverCapabilityEvidence>();
        if (root.ValueKind is not (JsonValueKind.Array or JsonValueKind.Object))
            throw new InvalidDataException("Windows graphics-driver metadata must be an object or array.");
        var items = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToArray() : [root];
        var result = new List<DriverCapabilityEvidence>();
        foreach (var item in items)
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var name = ReadText(item, "Name");
            if (name is null) continue;
            var status = "Windows driver metadata reported; acceleration runtime execution is unverified.";
            if (item.TryGetProperty("ConfigManagerErrorCode", out var error) && error.ValueKind == JsonValueKind.Number && error.TryGetInt32(out var code))
                status = code == 0
                    ? "Windows reports the adapter configured without a device-manager error; acceleration runtime execution is unverified."
                    : $"Windows reports device-manager error {code}; review the driver before using this adapter.";
            result.Add(new DriverCapabilityEvidence(name, ReadText(item, "DriverVersion"), null,
                "Windows CIM Win32_VideoController", status, ReadText(item, "PNPDeviceID")));
        }
        return result.AsReadOnly();
    }

    internal static IReadOnlyList<DriverCapabilityEvidence> ParseNvidiaDrivers(string output)
    {
        var result = new List<DriverCapabilityEvidence>();
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var columns = ParseCsvLine(line);
            if (columns is null || columns.Count is < 2 or > 4 || Clean(columns[0]) is not { } name) continue;
            var compute = columns.Count >= 3 ? ReadComputeCapability(columns[2]) : null;
            result.Add(new DriverCapabilityEvidence(name, Clean(columns[1]), compute, "NVIDIA driver (nvidia-smi)",
                compute is not null
                    ? "The NVIDIA driver reports this CUDA compute capability; the CUDA runtime, toolkit, and application execution are not verified."
                    : "NVIDIA driver metadata reported; CUDA compute capability is unavailable and runtime execution is unverified.",
                columns.Count == 4 ? Clean(columns[3]) : null));
        }
        return result.AsReadOnly();
    }

    internal static string? ReadComputeCapability(string value)
    {
        var text = Clean(value);
        var parts = text?.Split('.');
        return parts is { Length: 2 }
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major) && major > 0
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor) && minor >= 0
                ? $"{major}.{minor}" : null;
    }

    private static IReadOnlyList<string>? ParseCsvLine(string line)
    {
        var columns = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < line.Length; index++)
        {
            var value = line[index];
            if (value == '"')
            {
                if (quoted && index + 1 < line.Length && line[index + 1] == '"') { field.Append('"'); index++; }
                else quoted = !quoted;
            }
            else if (value == ',' && !quoted) { columns.Add(field.ToString().Trim()); field.Clear(); }
            else field.Append(value);
        }
        if (quoted) return null;
        columns.Add(field.ToString().Trim());
        return columns;
    }

    private static (IReadOnlyList<StorageVolume> Volumes, IReadOnlyList<string> Notes) ReadVolumes(CancellationToken token)
    {
        var volumes = new List<StorageVolume>();
        var notes = new List<string>();
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if (!drive.IsReady || drive.DriveType != DriveType.Fixed) continue;
                    var free = drive.AvailableFreeSpace;
                    var total = drive.TotalSize;
                    if (free < 0 || total <= 0 || free > total)
                    {
                        notes.Add($"The available-space reading for {drive.Name} was inconsistent and was excluded.");
                        continue;
                    }
                    volumes.Add(new StorageVolume(drive.RootDirectory.FullName, free, total));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    notes.Add($"Storage capacity could not be read for {drive.Name} ({ex.GetType().Name}).");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            notes.Add($"Storage volumes could not be enumerated ({ex.GetType().Name}).");
        }
        return (volumes.OrderBy(volume => volume.Path, StringComparer.OrdinalIgnoreCase).ToList().AsReadOnly(), notes.AsReadOnly());
    }

    private static string? ReadText(JsonElement root, string property) => root.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String ? Clean(value.GetString()) : null;

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value)
        || value.Trim().Equals("N/A", StringComparison.OrdinalIgnoreCase)
        || value.Trim().Equals("[Not Supported]", StringComparison.OrdinalIgnoreCase) ? null : value.Trim();
}
