using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Readers;

namespace AINexus.App;

public sealed record ToolArchiveEntry(string RelativePath, long DeclaredBytes, bool Directory, bool TermsCandidate);
public sealed record ToolArchiveInspection(string ArchivePath, long ArchiveBytes, string Sha256, DateTimeOffset InspectedAt,
    long DeclaredExpandedBytes, IReadOnlyList<ToolArchiveEntry> Entries, bool HasEmbeddedPython, bool HasComfyMain,
    IReadOnlyList<string> Limitations);

internal sealed record SevenZipEntryMetadata(string? Path, long Size, bool Directory, bool Encrypted = false,
    bool Anti = false, bool Split = false, int? Attributes = null, int? ExtendedAttributes = null, string? LinkTarget = null);

/// <summary>
/// Metadata-only 7z inspection. The caller must run this parser in the bounded isolated inspection worker;
/// parser allocations occur before entry enumeration and are not bounded by the result-entry policy alone.
/// No extraction, payload stream, installation, network or execution capability is used here.
/// </summary>
internal static partial class SevenZipInspection
{
    internal const int MaximumEntries = 100_000;
    internal const long MaximumArchiveBytes = 32L * 1024 * 1024 * 1024;
    internal const long MaximumExpandedBytes = 128L * 1024 * 1024 * 1024;
    internal const int MaximumPathCharacters = 512;
    internal const long MaximumTotalPathCharacters = 8_000_000;

    internal static async Task<ToolArchiveInspection> InspectAsync(string archivePath, long expectedBytes, string expectedSha,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (expectedBytes is < 32 or > MaximumArchiveBytes || expectedSha is null || expectedSha.Length != 64
            || expectedSha.Any(character => !char.IsAsciiHexDigit(character)))
            throw new InvalidDataException("Inspection requires the selected archive's exact bounded size and SHA-256.");
        OfflineModelVerifier.RequireLocalDrivePath(archivePath);
        var full = Path.GetFullPath(archivePath);
        if (!Path.GetExtension(full).Equals(".7z", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("This inspection recipe supports a single 7z archive only.");
        OfflineModelVerifier.CheckNoReparsePath(full);
        await using var source = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 131072,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (source.Length != expectedBytes) throw new InvalidDataException("Archive bytes differ from the selected download size.");
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(source, token).ConfigureAwait(false));
        if (!hash.Equals(expectedSha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Archive SHA-256 differs from the selected download. No metadata was trusted.");
        source.Position = 0;
        var header = new byte[32];
        await source.ReadExactlyAsync(header, token).ConfigureAwait(false);
        if (!header.AsSpan(0, 6).SequenceEqual(new byte[] { 0x37, 0x7a, 0xbc, 0xaf, 0x27, 0x1c }) || header[6] != 0)
            throw new InvalidDataException("The reviewed file is not a supported 7z archive at offset zero.");
        source.Position = 0;
        try
        {
            using var archive = SevenZipArchive.OpenArchive(source, new ReaderOptions
            {
                LeaveStreamOpen = true, LookForHeader = false, Password = null
            });
            IEnumerable<SevenZipEntryMetadata> ReadEntries()
            {
                foreach (var item in archive.Entries)
                {
                    token.ThrowIfCancellationRequested();
                    if (item is not SevenZipArchiveEntry entry)
                        throw new InvalidDataException("The archive parser returned an unexpected entry type.");
                    // 7z LinkTarget is always null and Attrib discards high POSIX bits in the pinned library.
                    // ExtendedAttrib retains the original attributes; CompressedSize is always zero for 7z.
                    yield return new(entry.Key, entry.Size, entry.IsDirectory, entry.IsEncrypted, entry.IsAnti,
                        entry.IsSplitAfter, entry.Attrib, entry.ExtendedAttrib, entry.LinkTarget);
                }
            }
            var (entries, expanded) = ValidateEntries(ReadEntries(), token);
            if (entries.Count == 0) throw new InvalidDataException("The archive has no inspectable entries.");
            return new(full, expectedBytes, hash, DateTimeOffset.UtcNow, expanded, entries,
                entries.Any(entry => !entry.Directory && MatchesSuffix(entry.RelativePath, "python_embeded/python.exe")),
                entries.Any(entry => !entry.Directory && MatchesSuffix(entry.RelativePath, "ComfyUI/main.py")),
                ["Archive bytes matched the exact selected size and SHA-256. This is not publisher authentication or extraction approval.",
                 "Entry sizes and engine markers are archive metadata only. Payload decompression, integrity, executable authenticity and readiness were not verified.",
                 "Terms candidates identify filenames only. License contents, bundled dependency terms, dependencies and hardware compatibility were not reviewed.",
                 "No payload file was extracted, installed, configured or executed. A separate exact extraction plan, terms review and approval are required.",
                 "Entry/size/path limits are inspection policy, not hardware specifications. Parsing must remain inside the isolated memory/time-bounded worker."]);
        }
        catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException and not InvalidDataException)
        {
            throw new InvalidDataException("The archive metadata could not be inspected safely; encrypted, corrupt or unsupported archives are not accepted.", error);
        }
    }

    internal static (IReadOnlyList<ToolArchiveEntry> Entries, long ExpandedBytes) ValidateEntries(
        IEnumerable<SevenZipEntryMetadata> metadata, CancellationToken token = default)
    {
        var entries = new List<ToolArchiveEntry>();
        var paths = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        long pathCharacters = 0;
        foreach (var entry in metadata)
        {
            token.ThrowIfCancellationRequested();
            if (entries.Count >= MaximumEntries) throw new InvalidDataException("Archive entry-count policy exceeded.");
            var path = SafePath(entry.Path, entry.Directory);
            pathCharacters += path.Length;
            if (pathCharacters > MaximumTotalPathCharacters) throw new InvalidDataException("Archive path metadata exceeds inspection policy.");
            if (entry.Encrypted || entry.Anti || entry.Split || entry.LinkTarget is not null)
                throw new InvalidDataException("Encrypted, anti, split or link archive entries are unsupported.");
            var attributes = unchecked((uint)(entry.ExtendedAttributes ?? entry.Attributes ?? 0));
            var windows = unchecked((uint)(entry.Attributes ?? 0)) | (attributes & 0xffff);
            var unixType = (attributes >> 16) & 0xf000;
            if ((windows & 0x400) != 0 || unixType is not (0 or 0x8000 or 0x4000)
                || unixType == 0x4000 && !entry.Directory || unixType == 0x8000 && entry.Directory
                || (windows & 0x10) != 0 && !entry.Directory)
                throw new InvalidDataException("Archive links, special nodes or conflicting file attributes are unsupported.");
            if (entry.Size < 0 || entry.Directory && entry.Size != 0 || entry.Size > MaximumExpandedBytes - expanded)
                throw new InvalidDataException("Archive declared expanded-size policy exceeded or invalid directory/file size.");
            expanded += entry.Size;
            if (!paths.TryAdd(path, entry.Directory)) throw new InvalidDataException("Duplicate or case-colliding archive destination: " + path);
            entries.Add(new(path, entry.Size, entry.Directory, !entry.Directory && IsTermsCandidate(path)));
        }
        foreach (var entry in entries)
        {
            var parent = entry.RelativePath;
            while (parent.LastIndexOf('/') is var slash && slash >= 0)
            {
                parent = parent[..slash];
                if (paths.TryGetValue(parent, out var directory) && !directory)
                    throw new InvalidDataException("An archive file conflicts with another entry's parent directory: " + parent);
            }
        }
        return (entries.AsReadOnly(), expanded);
    }

    private static string SafePath(string? original, bool directory)
    {
        if (string.IsNullOrWhiteSpace(original) || original.Length > MaximumPathCharacters
            || original.StartsWith('/') || original.StartsWith('\\')
            || !directory && (original.EndsWith('/') || original.EndsWith('\\')))
            throw new InvalidDataException("The archive contains an unsafe or excessive path.");
        var path = original.Replace('\\', '/').TrimEnd('/');
        if (path.Length == 0) throw new InvalidDataException("The archive contains an empty destination.");
        foreach (var part in path.Split('/'))
            if (part is "" or "." or ".." || part.EndsWith('.') || part.EndsWith(' ')
                || part.Any(character => char.IsControl(character) || "<>:\"|?*".Contains(character)) || ReservedName().IsMatch(part))
                throw new InvalidDataException("The archive contains a traversal, alternate stream, device or invalid Windows path.");
        return path;
    }

    private static bool IsTermsCandidate(string path)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        return name.Equals("LICENSE", StringComparison.OrdinalIgnoreCase) || name.StartsWith("LICENSE.", StringComparison.OrdinalIgnoreCase)
            || name.Equals("LICENCE", StringComparison.OrdinalIgnoreCase) || name.StartsWith("LICENCE.", StringComparison.OrdinalIgnoreCase)
            || name.Equals("COPYING", StringComparison.OrdinalIgnoreCase) || name.StartsWith("COPYING.", StringComparison.OrdinalIgnoreCase)
            || name.Equals("NOTICE", StringComparison.OrdinalIgnoreCase) || name.StartsWith("NOTICE.", StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesSuffix(string path, string suffix) => path.Equals(suffix, StringComparison.OrdinalIgnoreCase)
        || path.EndsWith("/" + suffix, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"\A(?:CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|CLOCK\$|COM[1-9¹²³]|LPT[1-9¹²³]) *(?:\..*)?\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReservedName();
}
