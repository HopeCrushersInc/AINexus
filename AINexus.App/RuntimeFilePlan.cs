using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AINexus.App;

public sealed record RuntimeFilePlan(string PlanId, string RuntimeItemId, string Release, string Destination,
    string? DeclaredLicense, DateTimeOffset CreatedAt, DateTimeOffset HardwareScannedAt, MachineCapabilities Machine,
    bool Prerelease, bool Immutable,
    IReadOnlyList<RuntimeReleaseAsset> Files, long CompressedBytes, IReadOnlyList<string> Blockers,
    IReadOnlyList<DiscoverySource> Sources);

/// <summary>Exact-file review identity, not installation authorization or proof of dependency completeness.</summary>
public static class RuntimeFilePlanner
{
    public static RuntimeFilePlan Create(DiscoveryReport report, string itemId, string destination, string archiveName)
    {
        var review = RuntimeSetupPlanner.Create(report, itemId, destination);
        var selected = review.Archives.SingleOrDefault(a => a.Asset.Name == archiveName)
            ?? throw new ArgumentException("Select an exact archive from this runtime release.");
        if (selected.Role != "Runtime archive" || selected.Status != RuntimeArchiveStatus.PlatformMatch)
            throw new InvalidOperationException("Select a native-platform runtime archive with the required hardware evidence; support archives cannot be installed alone.");
        var files = new List<RuntimeReleaseAsset> { selected.Asset };
        var blockers = new List<string>
        {
            "This is an exact-file review, not approval. CPU archive downloading and inspected local extraction require their separate exact-plan approvals; broader runtime recipes remain unavailable.",
            "Expanded storage, archive contents, server executable, CPU requirements and dependency closure require inspection before extraction or execution."
        };
        if (selected.Backend.StartsWith("cuda-", StringComparison.Ordinal))
        {
            var companions = review.Archives.Where(a => a.Role == "CUDA support archive"
                && a.Architecture == selected.Architecture && a.Backend == selected.Backend).ToArray();
            if (companions.Length == 1)
            {
                files.Add(companions[0].Asset);
                blockers.Add("A same-release CUDA support archive was identified by its publisher label. Its contents, necessity and NVIDIA license terms still require review; the runtime's declared license does not cover every dependency.");
            }
            else blockers.Add(companions.Length == 0
                ? "No exact same-release CUDA support archive is published for this backend/architecture. Dependency completeness is unresolved; another release will not be substituted."
                : "Multiple same-release CUDA support archives match. Dependency selection is ambiguous; no support archive was chosen.");
            blockers.Add("The detected NVIDIA metadata does not verify this build's driver minimum or GPU targets.");
        }
        if (review.CachedMetadata) blockers.Add("Saved metadata must be refreshed online before acquisition can be authorized.");
        var now = DateTimeOffset.UtcNow;
        if (now - review.HardwareScannedAt > TimeSpan.FromMinutes(5) || review.HardwareScannedAt > now.AddMinutes(1))
            blockers.Add("Rescan stale or future-dated hardware before acquisition.");
        if (review.Sources.Count == 0 || review.Sources.Any(source => now - source.RetrievedAt > TimeSpan.FromHours(24)
            || source.RetrievedAt > now.AddMinutes(1))) blockers.Add("Source evidence is missing, stale or future-dated; refresh online.");
        if (string.IsNullOrWhiteSpace(review.License)) blockers.Add("The runtime's source license is unknown.");
        long bytes = 0;
        foreach (var file in files)
        {
            ValidateIdentity(file, review.Release);
            bytes = checked(bytes + file.SizeBytes);
            if (file.Sha256 is null) blockers.Add($"No usable upstream SHA-256 was supplied for {file.Name}; verified acquisition is blocked.");
        }
        var volume = report.Machine.Volumes.Single(v => string.Equals(v.Path, report.TargetVolume, StringComparison.OrdinalIgnoreCase));
        if (bytes > volume.AvailableBytes) blockers.Add("Selected compressed files exceed available space on the assessed volume. Expanded storage remains unknown.");
        var plan = new RuntimeFilePlan("", itemId, review.Release, review.Destination, review.License, now,
            review.HardwareScannedAt, report.Machine, report.Catalog.Items.Single(i => i.Id == itemId).RuntimeRelease!.Prerelease,
            report.Catalog.Items.Single(i => i.Id == itemId).RuntimeRelease!.Immutable,
            files.AsReadOnly(), bytes, blockers.AsReadOnly(), review.Sources);
        return plan with { PlanId = Fingerprint(plan) };
    }

    internal static string Fingerprint(RuntimeFilePlan plan) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(plan with { PlanId = "" })));

    internal static void ValidateIdentity(RuntimeReleaseAsset file, string tag)
    {
        if (file.SizeBytes <= 0 || string.IsNullOrWhiteSpace(file.Name) || file.Name.Length > 240
            || file.Name.Any(c => char.IsControl(c) || c is '/' or '\\' or ':') || file.Name.Contains("..", StringComparison.Ordinal)
            || file.DownloadUrl is null || !file.DownloadUrl.IsAbsoluteUri
            || file.DownloadUrl.AbsoluteUri != "https://github.com/ggml-org/llama.cpp/releases/download/"
                + Uri.EscapeDataString(tag) + "/" + Uri.EscapeDataString(file.Name)
            || file.Sha256 is not null && (file.Sha256.Length != 64 || file.Sha256.Any(c => !Uri.IsHexDigit(c))))
            throw new InvalidDataException("Selected runtime files do not have a valid same-release upstream identity.");
    }

    public static string Describe(RuntimeFilePlan plan)
    {
        var text = new StringBuilder("EXACT RUNTIME FILE REVIEW — NO INSTALLATION APPROVED\n");
        text.AppendLine($"Review identity: {plan.PlanId}\nRelease: {plan.Release}\nNew destination: {plan.Destination}\nDeclared runtime license: {plan.DeclaredLicense ?? "unknown"}\nSelected compressed bytes: {plan.CompressedBytes:N0}\nExpanded size: unknown\nPrepared: {plan.CreatedAt.ToLocalTime():g}");
        text.AppendLine($"Detected platform: {plan.Machine.OsDescription} / {plan.Machine.OsArchitecture}\nHardware scanned: {plan.HardwareScannedAt.ToLocalTime():g}\nPrerelease: {plan.Prerelease}\nUpstream immutable: {plan.Immutable}");
        foreach (var file in plan.Files)
            text.AppendLine($"\n{file.Name}\nBytes: {file.SizeBytes:N0}\nSHA-256: {file.Sha256 ?? "unknown"}\nSource: {file.DownloadUrl}");
        text.AppendLine("\nRequirements before acquisition/installation:");
        foreach (var blocker in plan.Blockers) text.AppendLine("• " + blocker);
        text.AppendLine("\nChanging files, destination or evidence changes this review identity. No approval is saved or inferred from creating this review.");
        return text.ToString();
    }
}
