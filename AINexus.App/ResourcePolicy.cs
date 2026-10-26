namespace AINexus.App;

public sealed record ResourceAdmission(bool Allowed, string Reason);

/// <summary>
/// User-configured thresholds for admitting a new Portal task. These defaults are policy
/// choices, not detected hardware specifications or guarantees that a model will fit.
/// This policy does not impose operating-system resource limits or stop other applications.
/// </summary>
public sealed record ResourcePolicy(
    double MinimumAvailableRamGb = 1d,
    double MaximumCpuPercent = 95d,
    double MinimumAvailableVramGb = 0d)
{
    public ResourceAdmission Evaluate(ResourceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!double.IsFinite(MinimumAvailableRamGb) || MinimumAvailableRamGb < 0
            || !double.IsFinite(MaximumCpuPercent) || MaximumCpuPercent is < 0 or > 100
            || !double.IsFinite(MinimumAvailableVramGb) || MinimumAvailableVramGb < 0)
            return new ResourceAdmission(false, "Resource thresholds are invalid. RAM and VRAM minimums must be nonnegative; the CPU ceiling must be between 0 and 100 percent.");

        if (snapshot.CpuUsagePercent is not { } cpu || !double.IsFinite(cpu) || cpu is < 0 or > 100)
            return new ResourceAdmission(false, "CPU usage is unknown. Wait for a valid monitoring sample before starting a task.");
        if (snapshot.CpuScope.StartsWith("Processor group", StringComparison.OrdinalIgnoreCase))
            return new ResourceAdmission(false, "CPU monitoring is limited to one processor group and cannot establish the configured whole-system CPU ceiling.");
        if (cpu > MaximumCpuPercent)
            return new ResourceAdmission(false, $"CPU usage is {cpu:N1}%, above the configured {MaximumCpuPercent:N1}% ceiling.");

        if (snapshot.RamUsedGb is not { } usedRam || snapshot.RamTotalGb is not { } totalRam
            || !double.IsFinite(usedRam) || !double.IsFinite(totalRam)
            || totalRam <= 0 || usedRam < 0 || usedRam > totalRam)
            return new ResourceAdmission(false, "Physical RAM availability is unknown. A valid monitoring sample is required before starting a task.");
        var availableRam = totalRam - usedRam;
        if (availableRam < MinimumAvailableRamGb)
            return new ResourceAdmission(false, $"Available RAM is {availableRam:N2} GiB, below the configured {MinimumAvailableRamGb:N2} GiB minimum.");

        if (MinimumAvailableVramGb > 0)
        {
            var readableAdapters = snapshot.Gpus.Where(gpu =>
                gpu.VramUsedGb is { } used && gpu.VramTotalGb is { } total
                && double.IsFinite(used) && double.IsFinite(total)
                && total > 0 && used >= 0 && used <= total).ToArray();
            if (readableAdapters.Length == 0)
                return new ResourceAdmission(false, "The configured VRAM minimum requires valid NVIDIA VRAM telemetry, which is unavailable.");
            if (!readableAdapters.Any(gpu => gpu.VramTotalGb!.Value - gpu.VramUsedGb!.Value >= MinimumAvailableVramGb))
                return new ResourceAdmission(false, $"No monitored NVIDIA adapter has the configured {MinimumAvailableVramGb:N2} GiB of available VRAM. Memory from separate adapters is not combined.");
        }

        return new ResourceAdmission(true, "Current telemetry meets the configured admission thresholds. Model fit and future resource availability are not guaranteed.");
    }
}
