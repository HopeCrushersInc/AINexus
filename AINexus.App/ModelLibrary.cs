using System.IO;
using System.Security;

namespace AINexus.App;

public sealed record ModelFile(string Name, string FullPath, string Format, long SizeBytes, DateTimeOffset LastWriteTime)
{
    public string SizeDisplay => SizeBytes >= 1_073_741_824
        ? $"{SizeBytes / 1_073_741_824d:N2} GiB" : $"{SizeBytes / 1_048_576d:N1} MiB";
}

public sealed record ModelLibrarySnapshot(
    IReadOnlyList<ModelFile> Files,
    IReadOnlyList<string> Warnings,
    DateTimeOffset ScannedAt,
    bool Truncated);

/// <summary>
/// Catalogs model file metadata beneath explicitly selected folders. It does not
/// open model contents or infer model compatibility from a filename extension.
/// </summary>
public static class ModelLibrary
{
    public const int MaximumEntries = 20_000;
    public const int MaximumDepth = 16;
    private static readonly IReadOnlyDictionary<string, string> Formats = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".gguf"] = "GGUF",
        [".safetensors"] = "Safetensors",
        [".onnx"] = "ONNX"
    };

    public static Task<ModelLibrarySnapshot> ScanAsync(IEnumerable<string> roots, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roots);
        // Capture the UI's selection before doing work on the background thread.
        var selectedRoots = roots.ToArray();
        return Task.Run(() => ScanLocations(selectedRoots, cancellationToken: cancellationToken), cancellationToken);
    }

    internal static ModelLibrarySnapshot ScanLocations(IEnumerable<string> roots, int maxEntries = MaximumEntries,
        int maxDepth = MaximumDepth, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roots);
        if (maxEntries <= 0) throw new ArgumentOutOfRangeException(nameof(maxEntries));
        if (maxDepth < 0) throw new ArgumentOutOfRangeException(nameof(maxDepth));
        var warnings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new Dictionary<string, ModelFile>(StringComparer.OrdinalIgnoreCase);
        var queued = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var folders = new Queue<(string Path, int Depth)>();
        var truncated = false;
        var entriesInspected = 0;

        foreach (var root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
                {
                    warnings.Add("A selected model folder was skipped because it is not an absolute path.");
                    continue;
                }
                var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
                var driveRoot = Path.TrimEndingDirectorySeparator(Path.GetPathRoot(path)!);
                if (path.Equals(driveRoot, StringComparison.OrdinalIgnoreCase))
                {
                    warnings.Add($"Whole-drive or share-root scans are not allowed: {path}");
                    continue;
                }
                if (HasReparseAncestor(path))
                {
                    warnings.Add($"Skipped a selected folder reached through a symbolic link or junction: {path}");
                    continue;
                }
                if (!File.GetAttributes(path).HasFlag(FileAttributes.Directory))
                {
                    warnings.Add($"The selected location is not a folder: {path}");
                    continue;
                }
                if (queued.Add(path)) folders.Enqueue((path, 0));
            }
            catch (Exception ex) when (IsLocationError(ex))
            {
                warnings.Add($"Could not inspect selected model folder: {root} ({ex.GetType().Name}).");
            }
        }

        // Breadth-first traversal ensures separately selected nested roots are
        // visited at depth zero before any overlapping parent traversal reaches them.
        while (folders.TryDequeue(out var folder))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // Check again in case the folder changed after it was queued.
                if (File.GetAttributes(folder.Path).HasFlag(FileAttributes.ReparsePoint))
                {
                    warnings.Add($"Skipped a symbolic link or junction: {folder.Path}");
                    continue;
                }
                using var entries = Directory.EnumerateFileSystemEntries(folder.Path).GetEnumerator();
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!entries.MoveNext()) break;
                    if (entriesInspected >= maxEntries)
                    {
                        truncated = true;
                        warnings.Add($"Scan stopped at the {maxEntries:N0}-entry safety limit. Select a more specific model folder to inspect additional files.");
                        folders.Clear();
                        break;
                    }
                    entriesInspected++;
                    var path = entries.Current;
                    try
                    {
                        var attributes = File.GetAttributes(path);
                        if (attributes.HasFlag(FileAttributes.ReparsePoint))
                        {
                            warnings.Add($"Skipped a symbolic link or junction: {path}");
                            continue;
                        }
                        if (attributes.HasFlag(FileAttributes.Directory))
                        {
                            if (queued.Contains(path)) continue;
                            if (folder.Depth >= maxDepth)
                            {
                                truncated = true;
                                warnings.Add($"Skipped a folder beyond the depth limit ({maxDepth}): {path}");
                            }
                            else if (queued.Add(path)) folders.Enqueue((path, folder.Depth + 1));
                            continue;
                        }
                        if (!Formats.TryGetValue(Path.GetExtension(path), out var format) || files.ContainsKey(path)) continue;
                        var info = new FileInfo(path);
                        files.Add(path, new ModelFile(info.Name, info.FullName, format, info.Length,
                            new DateTimeOffset(DateTime.SpecifyKind(info.LastWriteTimeUtc, DateTimeKind.Utc))));
                    }
                    catch (Exception ex) when (IsLocationError(ex))
                    {
                        warnings.Add($"Could not inspect a model library entry: {path} ({ex.GetType().Name}).");
                    }
                }
            }
            catch (Exception ex) when (IsLocationError(ex))
            {
                warnings.Add($"Could not read model folder: {folder.Path} ({ex.GetType().Name}).");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new ModelLibrarySnapshot(files.Values.OrderBy(file => file.FullPath, StringComparer.OrdinalIgnoreCase).ToList().AsReadOnly(),
            warnings.Order(StringComparer.OrdinalIgnoreCase).ToList().AsReadOnly(), DateTimeOffset.Now, truncated);
    }

    private static bool HasReparseAncestor(string path)
    {
        for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
            if (File.GetAttributes(directory.FullName).HasFlag(FileAttributes.ReparsePoint)) return true;
        return false;
    }

    private static bool IsLocationError(Exception exception) => exception is IOException or UnauthorizedAccessException
        or SecurityException or ArgumentException or NotSupportedException;
}
