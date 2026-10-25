using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace AINexus.App;

public sealed record RuntimeArchiveEntry(string Archive, string RelativePath, long DeclaredBytes, bool Directory);
public sealed record RuntimeArchiveInspection(string PlanId, RuntimeFilePlan Plan, DateTimeOffset InspectedAt,
    long DeclaredExpandedBytes, IReadOnlyList<RuntimeArchiveEntry> Entries, bool HasServer,
    IReadOnlyList<string> Limitations);

/// <summary>Reads and hashes reviewed-file identities, then inspects ZIP metadata. Never extracts or executes.</summary>
public static partial class RuntimeArchiveInspector
{
    private const int MaximumEntries = 10000;
    private const long MaximumExpandedBytes = 8L * 1024 * 1024 * 1024;

    public static async Task<RuntimeArchiveInspection> InspectAsync(RuntimeFilePlan plan, string localArchiveDirectory,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (plan.PlanId != RuntimeFilePlanner.Fingerprint(plan) || plan.Files.Count is < 1 or > 8)
            throw new InvalidDataException("The exact runtime review identity or file count is invalid.");
        var entries = new List<RuntimeArchiveEntry>();
        var paths = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var leases = new List<FileStream>();
        long expanded = 0;
        try
        {
            foreach (var file in plan.Files)
            {
                RuntimeFilePlanner.ValidateIdentity(file, plan.Release);
                if (!file.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || file.Sha256 is null)
                    throw new InvalidDataException("Inspection requires a ZIP archive with a recorded upstream SHA-256.");
                var path = Path.Combine(localArchiveDirectory, file.Name);
                OfflineModelVerifier.CheckNoReparsePath(path);
                var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous);
                leases.Add(stream);
                if (stream.Length != file.SizeBytes) throw new InvalidDataException("The local archive size differs from the exact review: " + file.Name);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
                if (!hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The local archive hash differs from the exact review: " + file.Name);
                await CheckCentralDirectoryAsync(stream, token).ConfigureAwait(false);
                stream.Position = 0;
                using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
                if (entries.Count + zip.Entries.Count > MaximumEntries) throw new InvalidDataException("ZIP entry-count policy exceeded.");
                foreach (var entry in zip.Entries)
                {
                    token.ThrowIfCancellationRequested();
                    var name = SafePath(entry.FullName);
                    var directory = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
                    var attributes = unchecked((uint)entry.ExternalAttributes);
                    var unixType = (attributes >> 16) & 0xf000;
                    if ((attributes & 0x400) != 0 || unixType is not (0 or 0x8000 or 0x4000)
                        || unixType == 0x4000 && !directory || directory && entry.Length != 0)
                        throw new InvalidDataException("Archive links, special nodes or inconsistent directory entries are not supported: " + name);
                    if (!paths.TryAdd(name, directory)) throw new InvalidDataException("Duplicate or case-colliding archive destination: " + name);
                    if (entry.Length > MaximumExpandedBytes || entry.Length > Math.Max(1, entry.CompressedLength) * 1000d)
                        throw new InvalidDataException("ZIP declared-size or compression-ratio policy exceeded.");
                    expanded = checked(expanded + entry.Length);
                    if (expanded > MaximumExpandedBytes) throw new InvalidDataException("Total declared expanded-size policy exceeded.");
                    entries.Add(new(file.Name, name, entry.Length, directory));
                }
            }
            foreach (var entry in entries)
            {
                var parent = entry.RelativePath;
                while (parent.LastIndexOf('/') is var slash && slash >= 0)
                {
                    parent = parent[..slash];
                    if (paths.TryGetValue(parent, out var isDirectory) && !isDirectory)
                        throw new InvalidDataException("An archive file conflicts with another entry's parent directory: " + parent);
                }
            }
            return new(plan.PlanId, plan, DateTimeOffset.UtcNow, expanded, entries.AsReadOnly(),
                entries.Any(e => !e.Directory && e.RelativePath.Equals("llama-server.exe", StringComparison.OrdinalIgnoreCase)),
                ["ZIP bytes matched the selected review's recorded hashes. This is not publisher authentication or installation approval.",
                 "Expanded sizes are central-directory declarations. Payload decompression, executable authenticity, dependency closure, licenses and runtime execution were not verified.",
                 "No file was extracted, installed or executed. All archive identities and destination safety must be checked again before any future extraction.",
                 "ZIP64, split archives, links, duplicate destinations and excessive metadata/size/ratio are unsupported. Limits are safety policy, not hardware specifications."]);
        }
        finally { foreach (var stream in leases) await stream.DisposeAsync().ConfigureAwait(false); }
    }

    private static string SafePath(string original)
    {
        var path = original.Replace('\\', '/').TrimEnd('/');
        if (path.Length is < 1 or > 240 || original.StartsWith('/') || original.StartsWith('\\'))
            throw new InvalidDataException("The archive contains an unsafe or overlong path.");
        foreach (var part in path.Split('/'))
            if (part is "" or "." or ".." || part.EndsWith('.') || part.EndsWith(' ')
                || part.Any(c => char.IsControl(c) || "<>:\"|?*".Contains(c)) || ReservedName().IsMatch(part))
                throw new InvalidDataException("The archive contains a traversal, device or invalid Windows path.");
        return path;
    }

    private static async Task CheckCentralDirectoryAsync(FileStream stream, CancellationToken token)
    {
        var tail = new byte[(int)Math.Min(stream.Length, 65557)];
        var start = stream.Length - tail.Length;
        stream.Position = start;
        await stream.ReadExactlyAsync(tail, token).ConfigureAwait(false);
        for (var i = tail.Length - 22; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i)) != 0x06054b50
                || BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(i + 20)) != tail.Length - i - 22) continue;
            var count = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(i + 10));
            var size = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i + 12));
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i + 16));
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i + 4)) != 0
                || BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(i + 8)) != count
                || count > MaximumEntries || size > 16 * 1024 * 1024 || offset == uint.MaxValue
                || (long)offset + size > start + i)
                throw new InvalidDataException("Split/ZIP64 or excessive ZIP directory metadata is unsupported.");
            return;
        }
        throw new InvalidDataException("No supported ZIP directory footer was found.");
    }

    [GeneratedRegex(@"^(?:CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])(?:\..*)?\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReservedName();
}
