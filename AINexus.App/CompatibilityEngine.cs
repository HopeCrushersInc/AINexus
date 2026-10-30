using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace AINexus.App;

/// <summary>
/// Conservative planning from actual discovery metadata and an actual machine snapshot.
/// A resource candidate is not an installation approval, benchmark, or verified working configuration.
/// CPU/CUDA build paths follow https://github.com/ggml-org/llama.cpp/blob/master/docs/build.md.
/// </summary>
public static partial class HardwareCompatibilityEngine
{
    private const double BytesPerGiB = 1_073_741_824d;

    public static CompatibilityRecommendation Evaluate(DiscoveredItem item, MachineCapabilities machine,
        string targetVolumePath, CompatibilityPolicy? policy = null, bool artifactAlreadyPresent = false)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(machine);
        policy ??= new CompatibilityPolicy();
        var reasons = new List<string>();
        var limitations = new List<string>
        {
            "This is an unverified resource estimate, not a guarantee that the model loads, runs correctly, or meets a performance target.",
            "Model architecture, KV-cache metadata, context length, runtime build, and other workloads can change actual memory use. No benchmark or tokens-per-second estimate is available.",
            "Storage and available memory are point-in-time readings; this recommendation reserves no resources and authorizes no installation."
        };
        double? memory = null;
        double? storage = null;

        CompatibilityRecommendation Result(RecommendationStatus status, string path, string reason)
        {
            reasons.Add(reason);
            return new(item, status, path, memory, storage, reasons.AsReadOnly(), limitations.AsReadOnly());
        }

        if (!ValidPolicy(policy))
            return Result(RecommendationStatus.NeedsDetails, "Not assessed",
                "Invalid estimate policy: the weight multiplier must be finite and at least 1; memory/storage reserves must be finite and nonnegative.");

        if (item.Interest is not (DiscoveryInterest.LocalAssistants or DiscoveryInterest.Coding or DiscoveryInterest.Agents))
            return Result(RecommendationStatus.NeedsDetails, "Workflow requirements not modeled",
                "This workflow needs a dedicated runtime and requirements recipe. The GGUF text-generation memory rule cannot establish compatibility for it.");
        if (item.Kind == "Runtime" && item.RuntimeRelease is { } runtime)
        {
            foreach (var assessment in runtime.Archives.Select(asset => RuntimeSetupPlanner.Assess(asset, runtime.Tag, machine)))
                reasons.Add($"{assessment.Asset.Name}: {assessment.Status}; {assessment.Role}; {assessment.Backend} / {assessment.Architecture}.");
            return Result(RecommendationStatus.NeedsDetails, "Review runtime setup",
                runtime.Archives.Count == 0
                    ? "This release has no supported published archives. No nightly download or source build was inferred."
                    : "Review runtime setup for native platform and backend evidence. Archive contents, dependencies and driver minima require verification; a platform match does not establish a working installation.");
        }
        if (item.Artifact is not { } artifact)
            return Result(RecommendationStatus.NeedsDetails, "Artifact requirements unavailable",
                "No model artifact metadata is attached. Runtime/software release requirements need a separate compatibility recipe.");
        if (string.IsNullOrWhiteSpace(artifact.FileName) || !string.Equals(artifact.Format, "GGUF", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetExtension(artifact.FileName), ".gguf", StringComparison.OrdinalIgnoreCase))
            return Result(RecommendationStatus.NeedsDetails, "Runtime recipe required",
                "This recipe handles GGUF text-generation artifacts with llama.cpp. Other formats need their own runtime and memory requirements.");
        try { ModelFileSet.Validate(artifact); }
        catch (InvalidDataException)
        {
            return Result(RecommendationStatus.NeedsDetails, "Complete model inventory required",
                "A split GGUF needs every consecutive part with exact bytes and SHA-256, in the same pinned inventory. A single shard is not a whole model.");
        }
        if (artifact.FileName.Contains("mmproj", StringComparison.OrdinalIgnoreCase))
            return Result(RecommendationStatus.NeedsDetails, "Companion artifact requirements required",
                "A multimodal projector is a companion artifact, not a complete text-generation model.");
        if (!string.Equals(item.PipelineTag, "text-generation", StringComparison.OrdinalIgnoreCase))
            return Result(RecommendationStatus.NeedsDetails, "Model task unconfirmed",
                "Source metadata does not identify this artifact as text-generation. A filename or workflow label alone cannot establish its task or runtime compatibility.");
        if (artifact.SizeBytes is not > 0)
            return Result(RecommendationStatus.NeedsDetails, "Artifact size unavailable",
                "The source did not report a positive artifact size. Model memory and storage requirements cannot be estimated honestly.");

        var totalBytes = ModelFileSet.TotalBytes(artifact);
        var weights = totalBytes / BytesPerGiB;
        memory = weights * policy.WeightMemoryMultiplier + policy.RuntimeReserveGiB + policy.ContextReserveGiB;
        storage = (artifactAlreadyPresent ? 0 : weights) + policy.StorageReserveGiB;
        if (!double.IsFinite(memory.Value) || !double.IsFinite(storage.Value))
            return Result(RecommendationStatus.NeedsDetails, "Not assessed", "The configured estimate policy produces an invalid memory or storage bound.");
        reasons.Add($"Source-reported complete model size: {weights:N2} GiB ({totalBytes:N0} bytes across {1 + (artifact.AdditionalFiles?.Count ?? 0)} file(s)).");
        reasons.Add($"Planning estimate: weights × {policy.WeightMemoryMultiplier.ToString("0.###", CultureInfo.InvariantCulture)} + {policy.RuntimeReserveGiB:N2} GiB runtime allowance + {policy.ContextReserveGiB:N2} GiB context allowance = {memory:N2} GiB.");
        reasons.Add(artifactAlreadyPresent
            ? $"The artifact is already present. Additional storage allowance: {policy.StorageReserveGiB:N3} GiB; no duplicate model download is budgeted."
            : $"Model storage budget: {weights:N2} GiB artifact + {policy.StorageReserveGiB:N2} GiB allowance = {storage:N2} GiB.");
        limitations.Add("Memory allowances are configurable heuristics, not measured KV-cache requirements. Storage allowance excludes unknown compiler/toolchain, dependency, cache, and source-build sizes.");

        var volume = FindVolume(machine.Volumes, targetVolumePath);
        if (volume is null)
            return Result(RecommendationStatus.NeedsDetails, "Select storage volume",
                "Choose an actual scanned storage volume. Free space from unrelated drives is not combined or substituted.");
        if (volume.TotalBytes <= 0 || volume.AvailableBytes < 0 || volume.AvailableBytes > volume.TotalBytes)
            return Result(RecommendationStatus.NeedsDetails, "Storage reading unavailable",
                "The selected volume's free/total byte counters are missing or inconsistent.");
        var availableDisk = volume.AvailableBytes / BytesPerGiB;
        reasons.Add($"Selected volume {volume.Path}: {availableDisk:N2} GiB currently available.");
        if (availableDisk < storage.Value)
            return Result(RecommendationStatus.InsufficientResources, "More storage required",
                "The selected volume has less free space than the model's storage planning budget. Another drive's free space does not satisfy this selection.");

        if (string.IsNullOrWhiteSpace(machine.OsDescription))
            return Result(RecommendationStatus.NeedsDetails, "OS details required", "The operating system was not identified.");
        if (!machine.OsDescription.Contains("Windows", StringComparison.OrdinalIgnoreCase)
            || !(machine.OsArchitecture.Equals("X64", StringComparison.OrdinalIgnoreCase)
                || machine.OsArchitecture.Equals("Arm64", StringComparison.OrdinalIgnoreCase)))
            return Result(RecommendationStatus.Unsupported, "Platform recipe required",
                "This initial recipe covers Windows x64/ARM64 llama.cpp planning only. Other hosts need an explicit platform/build recipe; they are not declared incompatible with llama.cpp itself.");
        reasons.Add($"Detected platform: {machine.OsDescription}; {machine.OsArchitecture}; {machine.LogicalProcessors} logical processors.");
        reasons.Add($"Current-process instruction support: AVX={machine.AvxSupported}, AVX2={machine.Avx2Supported}. These are detected capabilities, not assumed CPU model specifications.");

        if (string.IsNullOrWhiteSpace(item.License) || item.License.Equals("unknown", StringComparison.OrdinalIgnoreCase))
            return Result(RecommendationStatus.NeedsDetails, "License review required",
                "The source did not provide an identified license. Resource estimates do not establish permission to acquire or use this model.");
        reasons.Add($"Source-reported license: {item.License}. License terms still require review; discovery does not accept them.");
        if (artifact.Gated)
            return Result(RecommendationStatus.NeedsDetails, "Access approval required",
                "The source reports gated access. Access/license requirements must be resolved explicitly; the portal will not supply credentials or bypass a gate.");
        if (!IsHex(artifact.Revision, 40) || !IsHex(artifact.Sha256, 64)
            || item.Sources is null || item.Sources.Count == 0 || !item.Sources.All(source => source is not null && IsPublicHttpsSource(source.Url)))
            return Result(RecommendationStatus.NeedsDetails, "Source verification details required",
                "A pinned source revision, artifact SHA-256, and credential-free HTTPS source provenance are required before this item becomes a resource candidate.");
        reasons.Add($"Artifact identity is pinned to source revision {artifact.Revision}; SHA-256 is supplied by the source.");

        if (!PositiveFinite(machine.Hardware.RamGb))
            return Result(RecommendationStatus.NeedsDetails, "RAM inventory required", "Installed/system-visible RAM was unavailable in the hardware scan.");
        var installedRam = machine.Hardware.RamGb;
        var resources = machine.Resources;
        if (resources.RamTotalGb is not { } visibleRam || !PositiveFinite(visibleRam)
            || resources.RamUsedGb is not { } usedRam || !double.IsFinite(usedRam) || usedRam < 0 || usedRam > visibleRam)
            return Result(RecommendationStatus.NeedsDetails, "Available RAM reading required",
                "Current RAM availability is unknown or inconsistent. Installed capacity is not a substitute for available memory.");
        var freeRam = visibleRam - usedRam;
        reasons.Add($"Hardware RAM inventory: {installedRam:N2} GiB; current physical-memory total: {visibleRam:N2} GiB; current available RAM: {freeRam:N2} GiB.");

        var hostAllowance = policy.RuntimeReserveGiB + policy.ContextReserveGiB;
        var readableGpuCount = 0;
        foreach (var gpu in resources.Gpus)
        {
            if (!HasDriverEvidence(gpu.Name, machine.DriverEvidence)
                || gpu.VramTotalGb is not { } total || !PositiveFinite(total)
                || gpu.VramUsedGb is not { } used || !double.IsFinite(used) || used < 0 || used > total) continue;
            readableGpuCount++;
            var free = total - used;
            if (free < memory.Value) continue;
            reasons.Add($"One driver-reported NVIDIA adapter ({gpu.Name}) has {free:N2} GiB available VRAM, meeting the {memory:N2} GiB device-memory estimate. Adapters are assessed separately.");
            if (installedRam < hostAllowance || visibleRam < hostAllowance)
                return Result(RecommendationStatus.InsufficientResources, "More host RAM required",
                    $"GPU capacity does not replace host memory. The detected physical RAM total is below the {hostAllowance:N2} GiB host allowance.");
            if (freeRam < hostAllowance)
                return Result(RecommendationStatus.InsufficientResources, "Free host RAM required",
                    $"The GPU resource estimate fits, but currently available host RAM is below the {hostAllowance:N2} GiB allowance. Close workloads or rescan; no resources were reserved.");
            limitations.Add("CUDA driver evidence is not a CUDA runtime or binary-architecture test. Verify the chosen release's supported compute architectures, driver minimum, dependencies, and an actual model load.");
            limitations.Add("GPU loading still needs host memory for mapping, transfers, metadata, and runtime allocations; the host allowance is only a planning heuristic. Partial offload and multi-GPU splitting are not modeled.");
            return Result(RecommendationStatus.ResourceCandidate, $"NVIDIA GPU candidate: {gpu.Name}",
                "Current per-adapter VRAM, host-RAM allowance, selected-volume space, and source metadata pass the planning checks. Install approval and real verification remain required.");
        }

        reasons.Add(readableGpuCount > 0
            ? "No single driver-identified NVIDIA adapter has enough currently available VRAM for this full-offload estimate. VRAM is never pooled across adapters."
            : "Usable NVIDIA driver/compute-capability evidence and per-adapter available VRAM were not both reported. This does not declare other accelerators unsupported by their own runtimes.");
        if (installedRam < memory.Value || visibleRam < memory.Value)
            return Result(RecommendationStatus.InsufficientResources, "Larger memory budget required",
                "The conservative full-resident CPU estimate exceeds detected physical RAM and no assessed single-GPU path meets the estimate. Partial offload and disk-backed operation require a separate recipe and may perform differently.");
        if (freeRam < memory.Value)
            return Result(RecommendationStatus.InsufficientResources, "Free RAM required",
                "Detected physical capacity meets the CPU estimate, but currently available RAM does not. Close other workloads or rescan; this is current pressure, not a claim that the machine can never run the model.");
        if (machine.OsArchitecture.Equals("X64", StringComparison.OrdinalIgnoreCase) && !machine.AvxSupported && !machine.Avx2Supported)
            return Result(RecommendationStatus.NeedsDetails, "Baseline CPU build selection required",
                "RAM and storage meet the estimate, but no usable AVX/AVX2 support was detected for this process. A matching baseline instruction-set build must be selected and tested rather than assuming a generic binary works.");
        limitations.Add("CPU execution needs a matching architecture/instruction-set runtime build. Generation speed is unmeasured and may be unsuitable for the intended workflow. No mandatory preinstalled model or runtime is assumed.");
        return Result(RecommendationStatus.ResourceCandidate, "CPU candidate (llama.cpp)",
            "Detected available RAM and selected-volume space meet the CPU planning estimate. GPU acceleration was not established; a matching CPU runtime and actual load/inference test remain required.");
    }

    private static bool ValidPolicy(CompatibilityPolicy policy) =>
        double.IsFinite(policy.WeightMemoryMultiplier) && policy.WeightMemoryMultiplier >= 1
        && NonnegativeFinite(policy.RuntimeReserveGiB) && NonnegativeFinite(policy.ContextReserveGiB)
        && NonnegativeFinite(policy.StorageReserveGiB);

    private static bool NonnegativeFinite(double value) => double.IsFinite(value) && value >= 0;
    private static bool PositiveFinite(double value) => double.IsFinite(value) && value > 0;
    private static bool IsHex(string? value, int length) => value?.Length == length && value.All(Uri.IsHexDigit);
    private static bool IsPublicHttpsSource(Uri? uri) => uri is { IsAbsoluteUri: true } && uri.Scheme == "https" && uri.UserInfo.Length == 0;

    private static StorageVolume? FindVolume(IReadOnlyList<StorageVolume> volumes, string target)
    {
        if (string.IsNullOrWhiteSpace(target) || !Path.IsPathFullyQualified(target)) return null;
        try
        {
            var normalized = NormalizePath(target);
            return volumes.FirstOrDefault(volume => Path.IsPathFullyQualified(volume.Path)
                && string.Equals(NormalizePath(volume.Path), normalized, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { return null; }
    }

    private static string NormalizePath(string value) => Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool HasDriverEvidence(string name, IReadOnlyList<DriverCapabilityEvidence> drivers) => drivers.Any(driver =>
        NormalizeGpuName(driver.AdapterName) == NormalizeGpuName(name)
        && driver.Source.Contains("nvidia-smi", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(driver.DriverVersion) && DriverVersionPattern().IsMatch(driver.DriverVersion)
        && double.TryParse(driver.ComputeCapability, NumberStyles.Float, CultureInfo.InvariantCulture, out var compute)
        && double.IsFinite(compute) && compute > 0);

    private static string NormalizeGpuName(string value)
    {
        var name = new string(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        return name.StartsWith("NVIDIA", StringComparison.Ordinal) ? name[6..] : name;
    }

    [GeneratedRegex(@"-\d{5}-of-\d{5}\.gguf$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SplitGgufName();
    [GeneratedRegex(@"^\d+(?:\.\d+){1,4}$", RegexOptions.CultureInvariant)]
    private static partial Regex DriverVersionPattern();
}
