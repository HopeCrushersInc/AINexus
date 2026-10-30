using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AINexus.App;

/// <summary>A split GGUF is one pinned model identity, with a hash for every file.</summary>
public static partial class ModelFileSet
{
    public const int MaximumFiles = 512;

    public static void Validate(ModelArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        var match = SplitName().Match(artifact.FileName ?? "");
        if (!match.Success)
        {
            if (artifact.AdditionalFiles is not null)
                throw new InvalidDataException("Additional model files require a canonical split GGUF first-file identity.");
            if (LooseSplitName().IsMatch(artifact.FileName ?? ""))
                throw new InvalidDataException("Split GGUF filenames must use the canonical five-digit part inventory.");
            return;
        }
        var count = int.Parse(match.Groups["count"].Value, System.Globalization.CultureInfo.InvariantCulture);
        if (match.Groups["index"].Value != "00001" || count is < 2 or > MaximumFiles
            || artifact.AdditionalFiles is not { } other || other.Count != count - 1)
            throw new InvalidDataException("A split model requires the first part and its complete bounded inventory, in order.");
        var first = new ModelArtifactPart(artifact.FileName!, artifact.SizeBytes ?? 0, artifact.Sha256 ?? "");
        ValidatePart(first);
        long total = first.SizeBytes;
        for (var index = 0; index < other.Count; index++)
        {
            var part = other[index] ?? throw new InvalidDataException("A model part is missing.");
            var expected = $"{match.Groups["prefix"].Value}-{index + 2:00000}-of-{count:00000}.gguf";
            if (!string.Equals(part.FileName, expected, StringComparison.Ordinal))
                throw new InvalidDataException("Model parts must share the exact folder/base/count and consecutive canonical filenames.");
            ValidatePart(part);
            try { total = checked(total + part.SizeBytes); }
            catch (OverflowException) { throw new InvalidDataException("The aggregate model byte count exceeds the supported integer range."); }
        }
    }

    public static IReadOnlyList<ModelArtifactPart> GetFiles(ModelArtifact artifact)
    {
        Validate(artifact);
        if (artifact.SizeBytes is not > 0 || artifact.Sha256 is null)
            throw new InvalidDataException("Exact positive file bytes and hashes are required for the model inventory.");
        return new[] { new ModelArtifactPart(artifact.FileName, artifact.SizeBytes.Value, artifact.Sha256) }
            .Concat(artifact.AdditionalFiles ?? []).ToArray();
    }

    public static long TotalBytes(ModelArtifact artifact)
    {
        if (artifact.AdditionalFiles is null)
        {
            Validate(artifact);
            return artifact.SizeBytes is > 0 ? artifact.SizeBytes.Value
                : throw new InvalidDataException("The model's byte count is unavailable.");
        }
        var files = GetFiles(artifact);
        try { return files.Aggregate(0L, (sum, file) => checked(sum + file.SizeBytes)); }
        catch (OverflowException) { throw new InvalidDataException("The aggregate model byte count exceeds the supported integer range."); }
    }

    public static bool SameIdentity(ModelArtifact? left, ModelArtifact? right) =>
        left is null || right is null ? left is null && right is null
        : JsonSerializer.Serialize(left) == JsonSerializer.Serialize(right);

    /// <summary>Internal projections carry no new user approval or standalone acquisition eligibility.</summary>
    internal static IReadOnlyList<ModelAcquisitionPlan> FilePlans(ModelAcquisitionPlan plan)
    {
        if (plan.Artifact.AdditionalFiles is null) return [plan];
        var directory = Path.GetDirectoryName(plan.DestinationPath)!;
        return GetFiles(plan.Artifact).Select(file =>
        {
            var destination = ModelAcquisitionPlanner.ResolveDestination(directory, Path.GetFileName(file.FileName));
            var artifact = plan.Artifact with { FileName = file.FileName, SizeBytes = file.SizeBytes, Sha256 = file.Sha256, AdditionalFiles = null };
            var path = string.Join('/', file.FileName.Split('/').Select(Uri.EscapeDataString));
            var repository = string.Join('/', artifact.Repository.Split('/').Select(Uri.EscapeDataString));
            var projected = plan with { Artifact = artifact, DestinationPath = destination,
                DownloadUrl = new Uri($"https://huggingface.co/{repository}/resolve/{artifact.Revision}/{path}") };
            return projected with { PlanId = ModelAcquisitionPlanner.ComputeFingerprint(projected) };
        }).ToArray();
    }

    internal static void ValidateEvidence(ModelAcquisitionPlan plan, ModelIntegrityEvidence evidence)
    {
        var files = FilePlans(plan);
        var evidenceFiles = new[] { evidence }.Concat(evidence.AdditionalFiles ?? []).ToArray();
        if (files.Count != evidenceFiles.Length || evidence.AdditionalFiles is not null && files.Count == 1)
            throw new InvalidDataException("Acquisition evidence does not cover the complete model inventory.");
        for (var index = 0; index < files.Count; index++)
        {
            var actual = evidenceFiles[index];
            var expected = files[index];
            if (actual is null || !actual.Exists || !actual.Matches || actual.ActualBytes != expected.Artifact.SizeBytes
                || !string.Equals(actual.Path, expected.DestinationPath, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(actual.ActualSha256, expected.Artifact.Sha256, StringComparison.OrdinalIgnoreCase)
                || index > 0 && actual.AdditionalFiles is not null)
                throw new InvalidDataException("Acquisition evidence lacks matching integrity for every reviewed model file.");
        }
    }

    private static void ValidatePart(ModelArtifactPart part)
    {
        if (part.SizeBytes <= 0 || part.Sha256 is not { Length: 64 } || !part.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("Every model part needs exact positive bytes and its source SHA-256.");
    }

    [GeneratedRegex(@"^(?<prefix>.+)-(?<index>[0-9]{5})-of-(?<count>[0-9]{5})\.gguf$", RegexOptions.CultureInvariant)]
    private static partial Regex SplitName();
    [GeneratedRegex(@"-\d+-of-\d+\.gguf$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LooseSplitName();
}
