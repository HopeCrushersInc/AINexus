using AINexus.App;

namespace AINexus.Checks;

internal static class CompatibilityChecks
{
    private const long GiB = 1_073_741_824;

    public static void Run(Action<bool, string> check)
    {
        var model = Model();
        var machine = Machine();
        var cpu = Evaluate(model, machine);
        check(cpu.Status == RecommendationStatus.ResourceCandidate && cpu.ExecutionPath.StartsWith("CPU", StringComparison.Ordinal),
            "GGUF CPU planning uses actual sufficient RAM and selected storage without requiring a preinstalled runtime");
        check(cpu.EstimatedMemoryGiB == 7 && cpu.RequiredStorageGiB == 5,
            "Model byte size drives explicit configurable weight/runtime/context/storage estimates");
        check(cpu.Limitations.Any(value => value.Contains("unverified", StringComparison.OrdinalIgnoreCase))
            && cpu.Limitations.Any(value => value.Contains("benchmark", StringComparison.OrdinalIgnoreCase)),
            "Resource candidates explicitly remain unverified and performance is not fabricated");

        var gpuMachine = machine with
        {
            DriverEvidence = [Driver("Fixture adapter")],
            Resources = machine.Resources with { Gpus = [new("NVIDIA Fixture adapter", 0, 1, 9)] }
        };
        var gpu = Evaluate(model, gpuMachine);
        check(gpu.Status == RecommendationStatus.ResourceCandidate && gpu.ExecutionPath.StartsWith("NVIDIA GPU", StringComparison.Ordinal),
            "One actual GPU's available VRAM plus matching NVIDIA driver/compute evidence yields a GPU resource candidate");
        check(gpu.Limitations.Any(value => value.Contains("driver minimum", StringComparison.OrdinalIgnoreCase)),
            "NVIDIA driver evidence does not pretend a matching CUDA binary was verified");
        var noCompute = gpuMachine with { DriverEvidence = [Driver("Fixture adapter") with { ComputeCapability = null }] };
        check(Evaluate(model, noCompute).ExecutionPath.StartsWith("CPU", StringComparison.Ordinal),
            "Unknown CUDA compute evidence falls back to assessed CPU resources instead of assuming acceleration");
        var wrongAdapter = gpuMachine with { DriverEvidence = [Driver("Different fixture adapter")] };
        check(Evaluate(model, wrongAdapter).ExecutionPath.StartsWith("CPU", StringComparison.Ordinal),
            "Driver evidence for a different adapter cannot justify a GPU recommendation");
        var noDriver = gpuMachine with { DriverEvidence = [Driver("Fixture adapter") with { DriverVersion = "Unavailable" }] };
        check(Evaluate(model, noDriver).ExecutionPath.StartsWith("CPU", StringComparison.Ordinal),
            "Unreported driver versions do not become verified NVIDIA acceleration");

        var pooled = gpuMachine with
        {
            Hardware = machine.Hardware with { RamGb = 4 },
            Resources = machine.Resources with { RamTotalGb = 4, RamUsedGb = 1,
                Gpus = [new("Fixture adapter", 0, 1, 5), new("Fixture adapter", 0, 1, 5)] }
        };
        check(Evaluate(model, pooled).Status == RecommendationStatus.InsufficientResources,
            "VRAM from two adapters is never added to satisfy one model's full-offload estimate");
        var gpuLowHost = gpuMachine with { Resources = gpuMachine.Resources with { RamUsedGb = 7 } };
        check(Evaluate(model, gpuLowHost).Status == RecommendationStatus.InsufficientResources
            && Evaluate(model, gpuLowHost).ExecutionPath == "Free host RAM required",
            "GPU recommendations still require actual available host RAM");
        var gpuWeightsLargerThanHost = gpuMachine with
        {
            Hardware = machine.Hardware with { RamGb = 4 },
            Resources = gpuMachine.Resources with { RamTotalGb = 4, RamUsedGb = 1 }
        };
        check(Evaluate(model, gpuWeightsLargerThanHost).Status == RecommendationStatus.ResourceCandidate,
            "A GPU candidate uses an explicit host allowance rather than requiring all GPU weights to reside in host RAM");

        var installedTooSmall = machine with { Hardware = machine.Hardware with { RamGb = 4 },
            Resources = machine.Resources with { RamTotalGb = 4, RamUsedGb = 1 } };
        check(Evaluate(model, installedTooSmall).ExecutionPath == "Larger memory budget required",
            "Physical capacity below the CPU estimate is distinguished from temporary memory pressure");
        var pressure = machine with { Resources = machine.Resources with { RamUsedGb = 5 } };
        check(Evaluate(model, pressure).ExecutionPath == "Free RAM required",
            "Sufficient installed capacity with insufficient free RAM is reported as current pressure");
        var unknownRam = machine with { Resources = machine.Resources with { RamUsedGb = null } };
        check(Evaluate(model, unknownRam).Status == RecommendationStatus.NeedsDetails,
            "Installed RAM cannot substitute for missing current availability");
        check(Evaluate(model, machine with { Resources = machine.Resources with { RamUsedGb = double.NaN } }).Status == RecommendationStatus.NeedsDetails,
            "Nonfinite telemetry remains unknown rather than falsely satisfying memory checks");
        check(Evaluate(model, machine with { Hardware = machine.Hardware with { RamGb = 0 } }).Status == RecommendationStatus.NeedsDetails,
            "Missing hardware RAM inventory is explicitly unknown");

        var twoVolumes = machine with { Volumes = [new("C:\\", 2 * GiB, 100 * GiB), new("D:\\", 50 * GiB, 100 * GiB)] };
        check(Evaluate(model, twoVolumes, "C:\\").Status == RecommendationStatus.InsufficientResources,
            "An unrelated drive's free space cannot satisfy the selected volume");
        check(Evaluate(model, twoVolumes, "D:\\").Status == RecommendationStatus.ResourceCandidate,
            "Changing the explicit selected volume reevaluates its actual free space");
        check(Evaluate(model, twoVolumes, "Z:\\").Status == RecommendationStatus.NeedsDetails
            && Evaluate(model, twoVolumes, "relative").Status == RecommendationStatus.NeedsDetails,
            "Unknown or relative storage targets never silently choose another drive");
        check(Evaluate(model, machine with { Volumes = [new("C:\\", -1, 100 * GiB)] }).Status == RecommendationStatus.NeedsDetails,
            "Invalid selected-volume counters stay unknown");

        check(Evaluate(model with { Artifact = model.Artifact! with { SizeBytes = null } }, machine).Status == RecommendationStatus.NeedsDetails,
            "Missing source artifact size cannot fabricate an estimate");
        var split = model with { Artifact = model.Artifact! with { FileName = "fixture-00001-of-00002.gguf" } };
        check(Evaluate(split, machine).Status == RecommendationStatus.NeedsDetails && Evaluate(split, machine).EstimatedMemoryGiB is null,
            "One split GGUF shard cannot be mistaken for a complete model's memory requirement");
        check(Evaluate(model with { Artifact = model.Artifact! with { FileName = "mmproj-fixture.gguf" } }, machine).Status == RecommendationStatus.NeedsDetails,
            "A multimodal projector cannot be recommended as a standalone text model");
        check(Evaluate(model with { PipelineTag = null }, machine).Status == RecommendationStatus.NeedsDetails,
            "Missing model-task metadata is not inferred from a filename");
        check(Evaluate(model with { License = null }, machine).Status == RecommendationStatus.NeedsDetails,
            "Missing licensing information remains a required review item");
        check(Evaluate(model with { Artifact = model.Artifact! with { Gated = true } }, machine).Status == RecommendationStatus.NeedsDetails,
            "Gated access requires explicit source approval without credentials or bypasses");
        check(Evaluate(model with { Artifact = model.Artifact! with { Sha256 = null } }, machine).Status == RecommendationStatus.NeedsDetails,
            "Missing source checksum cannot pass the source identity planning gate");
        check(Evaluate(model with { Artifact = model.Artifact! with { Revision = "main" } }, machine).Status == RecommendationStatus.NeedsDetails,
            "Mutable branch names cannot replace a pinned artifact revision");
        check(Evaluate(model with { Sources = [] }, machine).Status == RecommendationStatus.NeedsDetails,
            "Missing source provenance is never treated as verified");
        check(Evaluate(model with { Artifact = model.Artifact! with { Format = null! } }, machine).Status == RecommendationStatus.NeedsDetails
            && Evaluate(model with { Sources = null! }, machine).Status == RecommendationStatus.NeedsDetails,
            "Incomplete deserialized artifact/provenance metadata cannot crash compatibility evaluation");
        check(Evaluate(model with { Sources = [new("Fixture", new Uri("https://user:fixture@example.com/source"), DateTimeOffset.Now)] }, machine).Status == RecommendationStatus.NeedsDetails,
            "Credential-bearing source references cannot pass provenance planning");

        foreach (var invalid in new[] { new CompatibilityPolicy(double.NaN), new CompatibilityPolicy(.5),
            new CompatibilityPolicy(RuntimeReserveGiB: -1), new CompatibilityPolicy(ContextReserveGiB: double.PositiveInfinity) })
            check(HardwareCompatibilityEngine.Evaluate(model, machine, "C:\\", invalid).Status == RecommendationStatus.NeedsDetails,
                "Invalid policy values cannot understate weights or produce a compatibility claim");
        check(Evaluate(model with { Interest = DiscoveryInterest.Images }, machine).Status == RecommendationStatus.NeedsDetails,
            "Image workflows do not inherit unsupported text-generation memory assumptions");
        check(Evaluate(model with { Artifact = model.Artifact! with { Format = "SafeTensors", FileName = "fixture.safetensors" } }, machine).Status == RecommendationStatus.NeedsDetails,
            "Formats without a supported recipe remain unassessed");
        check(Evaluate(model, machine with { OsDescription = "Fixture Linux", OsArchitecture = "X64" }).Status == RecommendationStatus.Unsupported,
            "An unimplemented platform recipe is distinguished from established runtime compatibility");
        check(Evaluate(model, machine with { AvxSupported = false, Avx2Supported = false }).Status == RecommendationStatus.NeedsDetails,
            "Unknown baseline CPU build suitability is not replaced by a hardcoded CPU model assumption");
        check(Evaluate(model, machine with { OsArchitecture = "Arm64", AvxSupported = false, Avx2Supported = false }).Status == RecommendationStatus.ResourceCandidate,
            "ARM64 planning does not incorrectly require x86-only AVX instruction flags");
    }

    private static CompatibilityRecommendation Evaluate(DiscoveredItem item, MachineCapabilities machine, string volume = "C:\\") =>
        HardwareCompatibilityEngine.Evaluate(item, machine, volume);

    private static DiscoveredItem Model() => new("fixture", "Synthetic test model", "Fixture publisher", DiscoveryInterest.LocalAssistants,
        "Model", "fixture-license", new("fixture/model", new string('a', 40), "fixture.gguf", 4 * GiB, new string('b', 64), "GGUF", false),
        [new("Fixture model metadata", new("https://example.com/fixture"), DateTimeOffset.UtcNow)], "Synthetic regression fixture, never a discovered result.", "text-generation");

    private static DriverCapabilityEvidence Driver(string name) => new(name, "123.45", "8.0", "NVIDIA driver (nvidia-smi)", "Synthetic driver fixture");

    private static MachineCapabilities Machine() => new(
        new HardwareSnapshot("Synthetic CPU fixture", 8, [], []),
        new ResourceSnapshot(DateTimeOffset.UtcNow, 10, 1, 8, [], [], "All processors"),
        "Microsoft Windows fixture", "X64", 4, true, true, [], [new("C:\\", 50 * GiB, 100 * GiB)], DateTimeOffset.UtcNow, []);
}
