using System.IO;
using System.Security;
using System.Text.Json;

namespace AINexus.App;

public sealed record CatalogInstallation(string CatalogId, string ModelPath, string ReceiptPath,
    bool FilePresent, long? ActualBytes, DateTimeOffset ReceiptCompletedAt, string State)
{
    public ModelAcquisitionPlan? Plan { get; init; }
}

public sealed record CatalogInstallationSnapshot(IReadOnlyList<CatalogInstallation> Installations,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Private, read-only links between explicitly tracked model folders and existing acquisition
/// receipts. Receipt evidence is historical; indexing does not hash weights or run inference.
/// </summary>
public static class CatalogLibraryIndex
{
    internal const int MaximumDepth = 8;
    internal const int MaximumDirectories = 2000;
    internal const int MaximumReceipts = 1000;

    public static Task<CatalogInstallationSnapshot> ScanAsync(IEnumerable<string> modelRoots,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(modelRoots);
        var roots = modelRoots.Take(65).ToArray();
        return Task.Run(() => ScanCoreAsync(roots, token), token);
    }

    private static async Task<CatalogInstallationSnapshot> ScanCoreAsync(string[] roots, CancellationToken token)
    {
        var warnings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new Dictionary<string, CatalogInstallation>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<(string Path, string Root, int Depth)>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (roots.Length > 64) warnings.Add("Receipt indexing is limited to the first 64 tracked folders.");
        foreach (var root in roots.Take(64))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(root);
                OfflineModelVerifier.CheckNoReparsePath(root);
                var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
                if (string.Equals(full, Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full)!), StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Select a model folder, not an entire drive.");
                if (!File.GetAttributes(full).HasFlag(FileAttributes.Directory))
                    throw new ArgumentException("The tracked model location is not a folder.");
                if (visited.Add(full)) pending.Enqueue((full, full, 0));
            }
            catch (Exception ex) when (IsLocationError(ex))
            { warnings.Add("Skipped tracked receipt folder: " + root + " (" + ex.GetType().Name + ")."); }
        }

        var receiptCount = 0;
        var exhausted = false;
        while (pending.TryDequeue(out var folder) && !exhausted)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                OfflineModelVerifier.CheckNoReparsePath(folder.Path);
                var receiptFolder = Path.Combine(folder.Path, ".ainexus", "receipts");
                OfflineModelVerifier.CheckNoReparsePath(receiptFolder);
                if (Directory.Exists(receiptFolder))
                {
                    foreach (var path in Directory.EnumerateFiles(receiptFolder, "*.json", SearchOption.TopDirectoryOnly))
                    {
                        token.ThrowIfCancellationRequested();
                        if (++receiptCount > MaximumReceipts)
                        {
                            warnings.Add($"Receipt index reached its {MaximumReceipts}-receipt limit; results are partial.");
                            exhausted = true;
                            break;
                        }
                        try
                        {
                            var receipt = await OfflineModelVerifier.ReadReceiptAsync(path, token).ConfigureAwait(false);
                            var plan = receipt.Plan;
                            var modelPath = Path.GetFullPath(plan.DestinationPath);
                            if (!IsWithin(modelPath, folder.Root))
                                throw new InvalidDataException("Receipt destination is outside its tracked model folder.");
                            if (receipt.Acquisition.CompletedAt == DateTimeOffset.MinValue
                                || receipt.Acquisition.CompletedAt > DateTimeOffset.UtcNow.AddMinutes(1))
                                throw new InvalidDataException("Receipt acquisition timestamp is invalid.");
                            var item = new DiscoveredItem(plan.ItemId, plan.Name, plan.Artifact.Repository.Split('/')[0],
                                DiscoveryInterest.LocalAssistants, "Model", plan.DeclaredLicense, plan.Artifact,
                                [new("Recorded acquisition source", plan.LicenseReference, plan.MetadataRetrievedAt)],
                                "Historical acquisition receipt; not current integrity or runtime evidence.", "text-generation");
                            var id = CatalogDatabase.CanonicalId(item);
                            bool present;
                            long? actualBytes = null;
                            try
                            {
                                OfflineModelVerifier.CheckNoReparsePath(modelPath);
                                if (File.GetAttributes(modelPath).HasFlag(FileAttributes.Directory))
                                    throw new InvalidDataException("The recorded model destination is now a folder.");
                                actualBytes = new FileInfo(modelPath).Length;
                                present = true;
                            }
                            catch (FileNotFoundException) { present = false; }
                            catch (DirectoryNotFoundException) { present = false; }
                            var state = !present ? "Receipt recorded; model file missing"
                                : actualBytes != plan.Artifact.SizeBytes ? "Receipt recorded; file size mismatch"
                                : "Receipt recorded; file present; integrity not rechecked";
                            var installation = new CatalogInstallation(id, modelPath, Path.GetFullPath(path), present,
                                actualBytes, receipt.Acquisition.CompletedAt, state) { Plan = plan };
                            if (!results.TryGetValue(modelPath, out var prior) || prior.ReceiptCompletedAt < installation.ReceiptCompletedAt)
                                results[modelPath] = installation;
                        }
                        catch (Exception ex) when (IsLocationError(ex) || ex is JsonException)
                        { warnings.Add("Receipt could not be indexed: " + path + " (" + ex.GetType().Name + ")."); }
                    }
                }
                if (exhausted) break;
                foreach (var child in Directory.EnumerateDirectories(folder.Path))
                {
                    token.ThrowIfCancellationRequested();
                    if (string.Equals(Path.GetFileName(child), ".ainexus", StringComparison.OrdinalIgnoreCase)) continue;
                    if (folder.Depth >= MaximumDepth)
                    {
                        warnings.Add($"Receipt folder depth limit ({MaximumDepth}) reached; results are partial.");
                        break;
                    }
                    if (visited.Count >= MaximumDirectories)
                    {
                        warnings.Add($"Receipt folder limit ({MaximumDirectories}) reached; results are partial.");
                        break;
                    }
                    try
                    {
                        OfflineModelVerifier.CheckNoReparsePath(child);
                        if (visited.Add(child)) pending.Enqueue((child, folder.Root, folder.Depth + 1));
                    }
                    catch (Exception ex) when (IsLocationError(ex))
                    { warnings.Add("Skipped receipt search folder: " + child + " (" + ex.GetType().Name + ")."); }
                }
            }
            catch (Exception ex) when (IsLocationError(ex))
            { warnings.Add("Could not inspect receipt folder: " + folder.Path + " (" + ex.GetType().Name + ")."); }
        }
        token.ThrowIfCancellationRequested();
        return new(Array.AsReadOnly(results.Values.OrderBy(row => row.ModelPath, StringComparer.OrdinalIgnoreCase).ToArray()),
            Array.AsReadOnly(warnings.Order(StringComparer.OrdinalIgnoreCase).ToArray()));
    }

    private static bool IsWithin(string path, string root) => path.StartsWith(
        Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static bool IsLocationError(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException
        or SecurityException or ArgumentException or InvalidOperationException or NotSupportedException;
}
