using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using AINexus.App;

namespace AINexus.Checks;

internal static class HardwareCapabilityChecks
{
    public static void Run(Action<bool, string> check)
    {
        var windows = HardwareCapabilityScanner.ParseWindowsDrivers("""
            [{"Name":"Fixture adapter","DriverVersion":"1.2.3","ConfigManagerErrorCode":0,"PNPDeviceID":"Fixture-ID"},
             {"Name":"Fixture unavailable adapter","DriverVersion":null,"ConfigManagerErrorCode":43},
             {"Name":null,"DriverVersion":"ignored"}]
            """);
        check(windows.Count == 2 && windows[0].DriverVersion == "1.2.3" && windows[0].AdapterId == "Fixture-ID",
            "Windows driver metadata preserves actual names, versions and reported device IDs");
        check(windows[0].ComputeCapability is null && windows[1].DriverVersion is null && windows[1].Status.Contains("43"),
            "Unknown driver capabilities remain unknown and device errors remain visible");
        check(HardwareCapabilityScanner.ParseWindowsDrivers("null").Count == 0,
            "An empty Windows graphics inventory does not fabricate adapters");
        check(HardwareCapabilityScanner.ParseWindowsDrivers("{\"Name\":\"Fixture\",\"DriverVersion\":\"N/A\"}")[0].DriverVersion is null,
            "Unavailable Windows driver metadata is not presented as a version");
        check(HardwareCapabilityScanner.ParseWindowsDrivers("{\"Name\":\"Fixture\",\"ConfigManagerErrorCode\":\"unknown\"}")[0].Status.Contains("unverified"),
            "Malformed device-manager status remains unverified instead of discarding other driver evidence");
        var invalid = false;
        try { HardwareCapabilityScanner.ParseWindowsDrivers("123"); }
        catch (InvalidDataException) { invalid = true; }
        check(invalid, "Malformed Windows driver metadata cannot silently classify acceleration");

        var nvidia = HardwareCapabilityScanner.ParseNvidiaDrivers("Fixture GPU, 500.1, 8.6, GPU-fixture\n" +
            "\"Fixture, quoted GPU\", 500.1, N/A, GPU-other\nInvalid capability, N/A, NaN\nmalformed\n\"unterminated,500.1,8.6");
        check(nvidia.Count == 3 && nvidia[0].DriverVersion == "500.1" && nvidia[0].ComputeCapability == "8.6"
            && nvidia[0].AdapterId == "GPU-fixture", "NVIDIA driver evidence preserves actual reported compute capability and adapter identity");
        check(nvidia[1].AdapterName == "Fixture, quoted GPU" && nvidia[1].ComputeCapability is null
            && nvidia[2].ComputeCapability is null && nvidia[2].DriverVersion is null,
            "Quoted GPU names and missing or invalid NVIDIA capabilities remain truthful");
        check(nvidia.All(item => item.Status.Contains("unverified") || item.Status.Contains("not verified")),
            "Driver capability evidence never claims runtime execution was verified");
        check(HardwareCapabilityScanner.ParseNvidiaDrivers("Fixture fallback, 500.1")[0].ComputeCapability is null,
            "Older-driver version-only fallback cannot invent compute capability");
        check(new[] { "N/A", "-1.0", "8,6", "NaN", "8", "0.0", "8.-1" }
                .All(value => HardwareCapabilityScanner.ReadComputeCapability(value) is null),
            "Invalid compute-capability syntax cannot establish CUDA support");
    }

    public static async Task RunLiveAsync(Action<bool, string> check)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var snapshot = await HardwareCapabilityScanner.ScanAsync(deadline.Token);
        check(snapshot.OsArchitecture == RuntimeInformation.OSArchitecture.ToString()
            && snapshot.OsDescription == RuntimeInformation.OSDescription && snapshot.LogicalProcessors == Environment.ProcessorCount,
            "Live capability scan uses the actual OS, architecture and process-visible logical processor count");
        check(snapshot.AvxSupported == Avx.IsSupported && snapshot.Avx2Supported == Avx2.IsSupported,
            "Live capability scan reports instruction sets actually usable by this process");
        check(snapshot.Volumes.All(volume => Path.IsPathFullyQualified(volume.Path) && Directory.Exists(volume.Path)
            && volume.AvailableBytes >= 0 && volume.AvailableBytes <= volume.TotalBytes),
            "Live selectable volumes are actual absolute roots with valid storage readings");
        check(snapshot.DriverEvidence.All(evidence => !string.IsNullOrWhiteSpace(evidence.Source)
            && !string.IsNullOrWhiteSpace(evidence.Status)), "Live driver evidence includes source and verification limits");
        Console.WriteLine($"Live capability evidence: {snapshot.OsArchitecture}, {snapshot.LogicalProcessors} logical processors, " +
            $"{snapshot.DriverEvidence.Count} driver reports, {snapshot.Volumes.Count} selectable volumes.");
    }
}
