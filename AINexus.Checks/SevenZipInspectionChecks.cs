using System.IO;
using System.Security.Cryptography;
using AINexus.App;

namespace AINexus.Checks;

internal static class SevenZipInspectionChecks
{
    // Real inert 7z fixtures prepared once with 7-Zip. The checks need no installed 7-Zip tool.
    private const string SafeArchive = "N3q8ryccAAR1uV0ybgAAAAAAAABmAgAAAAAAALFoi6lmaXh0dXJlIHRlcm1zIG1hcmtlcjsgbm8gbGVnYWwgdGVybXNpbmVydCBzY3JpcHQgbWFya2VyOyBuZXZlciBleGVjdXRlZGluZXJ0IGV4ZWN1dGFibGUgbWFya2VyOyBuZXZlciBleGVjdXRlZAEEBgADCSQjJwAHCwMAAQEAAQEAAQEADCQjJwAICgGEkqyFxJYO564h4VYAAAUGDgHgGQgAAAAAAAAAABGBzwBDAG8AbQBmAHkAVQBJAF8AdwBpAG4AZABvAHcAcwBfAHAAbwByAHQAYQBiAGwAZQAAAEMAbwBtAGYAeQBVAEkAXwB3AGkAbgBkAG8AdwBzAF8AcABvAHIAdABhAGIAbABlAC8AQwBvAG0AZgB5AFUASQAAAEMAbwBtAGYAeQBVAEkAXwB3AGkAbgBkAG8AdwBzAF8AcABvAHIAdABhAGIAbABlAC8AcAB5AHQAaABvAG4AXwBlAG0AYgBlAGQAZQBkAAAAQwBvAG0AZgB5AFUASQBfAHcAaQBuAGQAbwB3AHMAXwBwAG8AcgB0AGEAYgBsAGUALwBDAG8AbQBmAHkAVQBJAC8ATABJAEMARQBOAFMARQAAAEMAbwBtAGYAeQBVAEkAXwB3AGkAbgBkAG8AdwBzAF8AcABvAHIAdABhAGIAbABlAC8AQwBvAG0AZgB5AFUASQAvAG0AYQBpAG4ALgBwAHkAAABDAG8AbQBmAHkAVQBJAF8AdwBpAG4AZABvAHcAcwBfAHAAbwByAHQAYQBiAGwAZQAvAHAAeQB0AGgAbwBuAF8AZQBtAGIAZQBkAGUAZAAvAHAAeQB0AGgAbwBuAC4AZQB4AGUAAAAZBAAAAAAUMgEABGjrFZNn3QEve+wVk2fdAQRo6xWTZ90Bv47sFZNn3QEve+wVk2fdAWRn7BWTZ90BFRoBABAAAAAQAAAAEAAAACAAAAAgAAAAIAAAAAAA";
    private const string EncryptedArchive = "N3q8ryccAARHGNEqLAEAAAAAAAAiAAAAAAAAAOuOxBuyh4cNZJ1pXFK83Jc1iud285pEpJrDf8/7U0Hjgmc1w0bh+FrS1qX0X/BNAkkce0DK8HMZOWBIf4xq2k+Jb1qk2wa2ACMRMl71IsTkrQutigAAgTMHrg/SwW/9QLuUZDs5pHrnaiJtPZjEH0Yzwsby/aolI/6iWksYQ/+OVnrYYSUMa1MMNmlQ6mdkyGM4b4TZBCsQ/+XJNfbDVVar0eYZkKjgDMf80C4++QVBTYAOl88Y54+HElgOF1PCQlyVop0k7vwEOgK+EWai1cez0vdHhkQsDQ4vU4UuR57BfSZy73+0L61XpBi7oPplHVdwVE8j/5KpPk/GGc21CFOTg77Bi9Lv+wL3YDlM+/o9WoMu8G2DbIyQ6LCDrnRxHhiLSaOMsRv0C2VYUJ8X7UAXBlABCYDcAAcLAQABIwMBAQVdABAAAAyCdgoBOrtoAQAA";

    internal static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", "sevenzip-inspection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var bytes = Convert.FromBase64String(SafeArchive);
            var path = Path.Combine(root, "chosen.7z");
            await File.WriteAllBytesAsync(path, bytes);
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            var result = await SevenZipInspection.InspectAsync(path, bytes.Length, hash);
            check(result.ArchivePath == path && result.ArchiveBytes == bytes.Length && result.Sha256 == hash
                && result.Entries.Count == 6 && result.DeclaredExpandedBytes == 110,
                "Typed 7z inspection verifies exact reviewed bytes and reports real fixture entry metadata");
            check(result.HasEmbeddedPython && result.HasComfyMain
                && result.Entries.Single(entry => entry.TermsCandidate).RelativePath.EndsWith("/LICENSE", StringComparison.Ordinal)
                && result.Limitations.Any(note => note.Contains("No payload file was extracted")),
                "7z inspection reports layout/terms filename candidates without claiming engine readiness or reviewed license contents");
            check(File.ReadAllBytes(path).SequenceEqual(bytes) && Directory.GetFileSystemEntries(root).Length == 1,
                "Inspecting a real 7z fixture changes no archive bytes and writes or extracts no files");
            check(await RejectsAsync<InvalidDataException>(() => SevenZipInspection.InspectAsync(path, bytes.Length, new string('0', 64))),
                "7z inspection refuses a changed hash before trusting archive metadata");
            check(await RejectsAsync<InvalidDataException>(() => SevenZipInspection.InspectAsync(path, bytes.Length + 1, hash)),
                "7z inspection refuses size evidence that does not identify the chosen file");
            var fake = new byte[40]; var fakePath = Path.Combine(root, "not-sevenzip.7z");
            await File.WriteAllBytesAsync(fakePath, fake);
            check(await RejectsAsync<InvalidDataException>(() => SevenZipInspection.InspectAsync(fakePath, fake.Length, Convert.ToHexString(SHA256.HashData(fake)))),
                "A matching hash cannot turn non-7z bytes into a valid metadata inspection");
            var truncated = bytes[..^1]; var truncatedPath = Path.Combine(root, "truncated.7z");
            await File.WriteAllBytesAsync(truncatedPath, truncated);
            check(await RejectsAsync<InvalidDataException>(() => SevenZipInspection.InspectAsync(truncatedPath, truncated.Length, Convert.ToHexString(SHA256.HashData(truncated)))),
                "Corrupt 7z metadata is rejected even when its selected byte identity matches");
            var encrypted = Convert.FromBase64String(EncryptedArchive); var encryptedPath = Path.Combine(root, "encrypted.7z");
            await File.WriteAllBytesAsync(encryptedPath, encrypted);
            check(await RejectsAsync<InvalidDataException>(() => SevenZipInspection.InspectAsync(encryptedPath, encrypted.Length, Convert.ToHexString(SHA256.HashData(encrypted)))),
                "A real encrypted 7z fixture cannot authorize metadata inspection or installation");
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            check(await RejectsAsync<OperationCanceledException>(() => SevenZipInspection.InspectAsync(path, bytes.Length, hash, canceled.Token)),
                "Canceled 7z inspection does not open or parse the chosen file");
            CheckMetadataPolicies(check);
        }
        finally
        {
            OfflineModelVerifier.CheckNoReparsePath(root);
            Directory.Delete(root, recursive: true); // Only this run's newly created GUID fixture tree.
        }
    }

    internal static async Task RunWorkerAsync(Action<bool, string> check)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", "sevenzip-worker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var bytes = Convert.FromBase64String(SafeArchive);
            var path = Path.Combine(root, "chosen.7z");
            await File.WriteAllBytesAsync(path, bytes);
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            var request = new ToolArchiveInspectionRequest(path, bytes.Length, hash);
            var result = await ToolArchiveInspector.InspectAsync(request);
            check(result.ArchivePath == path && result.Sha256 == hash && result.ArchiveBytes == bytes.Length
                && result.Entries.Count == 6 && result.HasEmbeddedPython && result.HasComfyMain,
                "An isolated bounded portal worker returns typed metadata for the exact real 7z fixture");
            check(await RejectsAsync<InvalidDataException>(() => ToolArchiveInspector.InspectAsync(request with { ExpectedSha256 = new string('0', 64) })),
                "The isolated worker rejects changed archive identity rather than reporting installation readiness");
            var fake = new byte[40]; var fakePath = Path.Combine(root, "not-sevenzip.7z");
            await File.WriteAllBytesAsync(fakePath, fake);
            check(await RejectsAsync<InvalidDataException>(() => ToolArchiveInspector.InspectAsync(new(fakePath, fake.Length, Convert.ToHexString(SHA256.HashData(fake))))),
                "The isolated worker rejects matching-identity non-7z bytes");
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            check(await RejectsAsync<OperationCanceledException>(() => ToolArchiveInspector.InspectAsync(request, canceled.Token)),
                "Canceled isolated inspection starts no new archive worker");
            using var activeCancellation = new CancellationTokenSource();
            var active = ToolArchiveInspector.InspectAsync(request, activeCancellation.Token);
            activeCancellation.Cancel();
            check(await RejectsAsync<OperationCanceledException>(() => active),
                "Canceling an active archive inspection waits for its owned worker to stop without extracting files");
            check(File.ReadAllBytes(path).SequenceEqual(bytes) && Directory.GetFileSystemEntries(root).Length == 2
                && !Directory.GetFileSystemEntries(root).Any(Directory.Exists),
                "Isolated metadata inspection creates no extracted or report files and preserves all chosen archive bytes");
        }
        finally
        {
            OfflineModelVerifier.CheckNoReparsePath(root);
            Directory.Delete(root, recursive: true); // Only this run's newly created GUID fixture tree.
        }
    }

    private static void CheckMetadataPolicies(Action<bool, string> check)
    {
        foreach (var path in new[] { "../escape", "/absolute", @"\\server\share", "C:/drive", "root/../escape", "root//empty", "root/stream:payload",
            "root/NUL.txt", "root/COM1", "root/COM¹.txt", "root/NUL .txt", "root/CONIN$", "root/CONOUT$", "root/trailing.", "root/trailing ", "root/tab\tfile", "root/file/" })
            check(RejectMetadata([new(path, 1, false)]), "7z destination validation rejects unsafe Windows path: " + path.Replace("\t", "<tab>"));
        check(RejectMetadata([new(new string('a', SevenZipInspection.MaximumPathCharacters + 1), 1, false)]),
            "7z inspection rejects excessive per-entry path metadata");
        check(RejectMetadata([new("dir/File", 1, false), new("DIR/file", 1, false)]),
            "7z inspection rejects duplicate and case-colliding destinations");
        check(RejectMetadata([new("parent", 1, false), new("parent/child", 1, false)])
            && RejectMetadata([new("parent/child", 1, false), new("parent", 1, false)]),
            "7z inspection rejects parent-file conflicts independently of archive ordering");
        foreach (var entry in new[]
        {
            new SevenZipEntryMetadata("link", 1, false, Attributes: 0x400),
            new SevenZipEntryMetadata("unix-link", 1, false, Attributes: 0x20, ExtendedAttributes: unchecked((int)0xA1FF0020u)),
            new SevenZipEntryMetadata("unix-pipe", 1, false, Attributes: 0x20, ExtendedAttributes: 0x11A40020),
            new SevenZipEntryMetadata("anti", 0, false, Anti: true),
            new SevenZipEntryMetadata("encrypted", 1, false, Encrypted: true),
            new SevenZipEntryMetadata("split", 1, false, Split: true),
            new SevenZipEntryMetadata("link-target", 1, false, LinkTarget: "elsewhere"),
            new SevenZipEntryMetadata("negative", -1, false),
            new SevenZipEntryMetadata("directory", 1, true),
            new SevenZipEntryMetadata("directory-as-file", 0, false, Attributes: 0x10),
            new SevenZipEntryMetadata("unix-directory-as-file", 0, false, ExtendedAttributes: 0x41ED0010),
            new SevenZipEntryMetadata("unix-file-as-directory", 0, true, ExtendedAttributes: unchecked((int)0x81A40020u))
        }) check(RejectMetadata([entry]), "7z metadata rejects links, special types, encryption and inconsistent entries: " + entry.Path);
        var (safe, expanded) = SevenZipInspection.ValidateEntries([
            new("folder", 0, true, Attributes: 0x10, ExtendedAttributes: 0x41ED0010),
            new("folder/file.txt", 3, false, Attributes: 0x20, ExtendedAttributes: unchecked((int)0x81A40020u))]);
        check(safe.Count == 2 && expanded == 3, "7z original POSIX attributes permit only consistent regular files and directories");
        check(RejectMetadata([new("huge", SevenZipInspection.MaximumExpandedBytes, false), new("extra", 1, false)]),
            "7z declared aggregate expansion is bounded without relying on unavailable per-entry compressed sizes");
        check(RejectMetadata(Enumerable.Range(0, SevenZipInspection.MaximumEntries + 1).Select(index => new SevenZipEntryMetadata("file-" + index, 0, false))),
            "7z result metadata enforces its entry-count policy");
        check(RejectMetadata(Enumerable.Range(0, 20_000).Select(index => new SevenZipEntryMetadata(index + "/" + new string('a', 500), 0, false))),
            "7z result metadata enforces cumulative path-character policy before unbounded report growth");
    }

    private static bool RejectMetadata(IEnumerable<SevenZipEntryMetadata> entries)
    { try { SevenZipInspection.ValidateEntries(entries); return false; } catch (InvalidDataException) { return true; } }
    private static async Task<bool> RejectsAsync<T>(Func<Task> action) where T : Exception
    { try { await action(); return false; } catch (T) { return true; } }
}
