using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;

namespace AINexus.App;

public sealed record GpuResourceSample(
    string Name,
    double? UtilizationPercent,
    double? VramUsedGb,
    double? VramTotalGb);

public sealed record ResourceSnapshot(
    DateTimeOffset ScannedAt,
    double? CpuUsagePercent,
    double? RamUsedGb,
    double? RamTotalGb,
    IReadOnlyList<GpuResourceSample> Gpus,
    IReadOnlyList<string> Notes,
    string CpuScope);

/// <summary>
/// Reads actual Windows counters and NVIDIA driver telemetry without changing resource settings.
/// Keep one instance across samples: CPU usage requires two counter readings.
/// </summary>
public sealed class ResourceMonitor
{
    private readonly object _cpuGate = new();
    private CpuTimes? _previousCpu;

    public async Task<ResourceSnapshot> SampleAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sampledAt = DateTimeOffset.Now;
        var notes = new List<string>();
        double? cpu = null;
        double? usedRam = null;
        double? totalRam = null;
        var scope = "All processors";
        try
        {
            lock (_cpuGate)
            {
                var group = (ushort)0;
                if (GetActiveProcessorGroupCount() > 1)
                {
                    if (!GetThreadGroupAffinity(GetCurrentThread(), out var affinity))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    group = affinity.Group;
                    scope = $"Processor group {group}; not a whole-system CPU total";
                    notes.Add("This system has multiple processor groups. CPU usage covers the sampling thread's primary group only.");
                }
                if (!GetSystemTimes(out var idle, out var kernel, out var user))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                var current = new CpuTimes(idle.Value, kernel.Value, user.Value, group);
                cpu = CalculateCpuUsage(_previousCpu, current);
                _previousCpu = current;
                if (cpu is null) notes.Add("CPU usage needs two valid samples from the same processor group.");
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            lock (_cpuGate) _previousCpu = null;
            notes.Add($"CPU telemetry unavailable: {ex.Message}");
        }

        try
        {
            var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
            if (!GlobalMemoryStatusEx(ref memory))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (memory.TotalPhysical == 0 || memory.AvailablePhysical > memory.TotalPhysical)
                throw new InvalidOperationException("Windows returned inconsistent physical memory counters.");
            totalRam = memory.TotalPhysical / 1073741824d;
            usedRam = (memory.TotalPhysical - memory.AvailablePhysical) / 1073741824d;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            notes.Add($"RAM telemetry unavailable: {ex.Message}");
        }

        IReadOnlyList<GpuResourceSample> gpus = Array.Empty<GpuResourceSample>();
        try
        {
            var output = await HardwareScanner.RunProcessAsync("nvidia-smi", new[]
            {
                "--query-gpu=name,utilization.gpu,memory.used,memory.total",
                "--format=csv,noheader,nounits"
            }, cancellationToken, TimeSpan.FromSeconds(3));
            if (!string.IsNullOrWhiteSpace(output)) gpus = ParseNvidiaResources(output);
            if (gpus.Count == 0)
                notes.Add("GPU utilization and VRAM telemetry unavailable: no readable NVIDIA driver data. Other GPU vendors are not supported by this monitor yet.");
            else if (gpus.Any(gpu => gpu.UtilizationPercent is null || gpu.VramUsedGb is null || gpu.VramTotalGb is null))
                notes.Add("The NVIDIA driver did not report all GPU utilization or VRAM counters.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            notes.Add($"GPU telemetry unavailable: {ex.Message}");
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new ResourceSnapshot(sampledAt, cpu, usedRam, totalRam, gpus, notes.AsReadOnly(), scope);
    }

    internal readonly record struct CpuTimes(ulong Idle, ulong Kernel, ulong User, ushort GroupId = 0);

    internal static double? CalculateCpuUsage(CpuTimes? previous, CpuTimes current)
    {
        if (previous is not { } last || last.GroupId != current.GroupId
            || current.Idle < last.Idle || current.Kernel < last.Kernel || current.User < last.User)
            return null;

        // Kernel time already includes idle time. Adding idle again would underreport usage.
        var total = (decimal)(current.Kernel - last.Kernel) + (current.User - last.User);
        var idle = (decimal)(current.Idle - last.Idle);
        if (total <= 0 || idle > total) return null;
        return (double)((total - idle) * 100m / total);
    }

    internal static IReadOnlyList<GpuResourceSample> ParseNvidiaResources(string output)
    {
        var result = new List<GpuResourceSample>();
        foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var columns = line.Split(',');
            if (columns.Length != 4 || string.IsNullOrWhiteSpace(columns[0])) continue;
            var utilization = ReadNumber(columns[1]);
            if (utilization > 100) utilization = null;
            var used = ReadNumber(columns[2]);
            var total = ReadNumber(columns[3]);
            if (total <= 0) total = null;
            if (used > total) used = null;
            result.Add(new GpuResourceSample(columns[0].Trim(), utilization, used / 1024d, total / 1024d));
        }
        return result.AsReadOnly();
    }

    private static double? ReadNumber(string value) =>
        double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
        && double.IsFinite(number) && number >= 0 ? number : null;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        public uint Low;
        public uint High;
        public readonly ulong Value => ((ulong)High << 32) | Low;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GroupAffinity
    {
        public UIntPtr Mask;
        public ushort Group;
        public ushort Reserved0;
        public ushort Reserved1;
        public ushort Reserved2;
    }

    // https://learn.microsoft.com/windows/win32/api/processthreadsapi/nf-processthreadsapi-getsystemtimes
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out NativeFileTime idle, out NativeFileTime kernel, out NativeFileTime user);

    // https://learn.microsoft.com/windows/win32/api/sysinfoapi/nf-sysinfoapi-globalmemorystatusex
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    [DllImport("kernel32.dll")]
    private static extern ushort GetActiveProcessorGroupCount();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetThreadGroupAffinity(IntPtr thread, out GroupAffinity affinity);
}
