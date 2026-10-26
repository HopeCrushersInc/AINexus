using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using AINexus.App;

namespace AINexus.Checks;

internal static class RuntimeArchiveChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var name = "llama-bin-win-cpu-x64.zip";
        var path = Path.Combine(root, name);
        var destination = Path.Combine(root, "uncreated-runtime");
        async Task<RuntimeFilePlan> WriteAsync((string Name, int Attributes)[] entries, Action<byte[]>? mutate = null)
        {
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
                foreach (var (entryName, attributes) in entries)
                {
                    var entry = zip.CreateEntry(entryName);
                    if (attributes != 0) entry.ExternalAttributes = attributes;
                    if (!entryName.EndsWith('/'))
                    { using var writer = new StreamWriter(entry.Open()); writer.Write("Archive fixture, never execute."); }
                }
            var bytes = await File.ReadAllBytesAsync(path);
            if (mutate is not null) { mutate(bytes); await File.WriteAllBytesAsync(path, bytes); }
            var asset = new RuntimeReleaseAsset(name, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)),
                new Uri("https://github.com/ggml-org/llama.cpp/releases/download/fixture/" + name));
            return RuntimeFilePlanner.Create(RuntimeSetupChecks.Report(RuntimeSetupChecks.Machine(), asset), "fixture/runtime", destination, name);
        }
        async Task<bool> RejectAsync((string, int)[] entries, Action<byte[]>? mutate = null)
        {
            File.Delete(path); // Only this GUID fixture's generated ZIP.
            var plan = await WriteAsync(entries, mutate);
            try { await RuntimeArchiveInspector.InspectAsync(plan, root); } catch (InvalidDataException) { return true; }
            return false;
        }
        try
        {
            var plan = await WriteAsync([("llama-server.exe", 0), ("licenses/", 0), ("licenses/LICENSE", 0)]);
            var valid = await RuntimeArchiveInspector.InspectAsync(plan, root);
            check(valid.HasServer && valid.Entries.Count == 3 && valid.DeclaredExpandedBytes > 0
                && !Directory.Exists(destination) && !File.Exists(Path.Combine(root, "llama-server.exe")),
                "Runtime ZIP inspection reads verified identities and declared contents without extracting or executing fixtures");
            var tamperedRejected = false;
            var changedBytes = await File.ReadAllBytesAsync(path);
            changedBytes[0] ^= 1;
            await File.WriteAllBytesAsync(path, changedBytes);
            try { await RuntimeArchiveInspector.InspectAsync(plan, root); } catch (InvalidDataException) { tamperedRejected = true; }
            check(tamperedRejected, "Same-size changed archive bytes fail SHA-256 before ZIP parsing");
            check(await RejectAsync([("../outside.exe", 0)]), "ZIP traversal paths are rejected before extraction exists");
            check(await RejectAsync([("C:/outside.exe", 0)]) && await RejectAsync([("file:stream", 0)])
                && await RejectAsync([("NUL.txt", 0)]), "ZIP drive paths, alternate data streams and reserved devices are rejected");
            check(await RejectAsync([("same.dll", 0), ("SAME.dll", 0)]), "Case-colliding ZIP destinations cannot overwrite each other");
            check(await RejectAsync([("parent", 0), ("parent/child.dll", 0)]), "ZIP file-versus-parent-directory collisions are rejected");
            check(await RejectAsync([("link", unchecked((int)0xA1FF0000))]), "Unix symbolic links in ZIP metadata are rejected");
            check(await RejectAsync([("link", 0x400)]), "Windows reparse entries in ZIP metadata are rejected");
            check(await RejectAsync([("safe.dll", 0)], bytes => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(bytes.Length - 10), 17 * 1024 * 1024)),
                "Excessive ZIP directory metadata is rejected before ZipArchive allocates its entry index");
            check(await RejectAsync([("safe.dll", 0)], bytes => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(bytes.Length - 12), ushort.MaxValue)),
                "ZIP64/oversized entry declarations cannot bypass the preflight entry limit");
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            var canceledSafely = false;
            try { await RuntimeArchiveInspector.InspectAsync(plan, @"\\unapproved-server\share", canceled.Token); }
            catch (OperationCanceledException) { canceledSafely = true; }
            check(canceledSafely, "Canceled archive inspection stops before opening any selected path");
        }
        finally { Directory.Delete(root, true); }
    }

    public static async Task RunLiveAsync(string reportPath, string directory, string archiveName, Action<bool, string> check)
    {
        var report = JsonSerializer.Deserialize<DiscoveryReport>(await File.ReadAllTextAsync(reportPath))
            ?? throw new InvalidDataException("No saved release report was supplied.");
        var item = report.Catalog.Items.Single(i => i.RuntimeRelease is not null);
        var destination = Path.Combine(AppContext.BaseDirectory, "Reports", "ArchiveInspection", "UncreatedRuntime-" + Guid.NewGuid().ToString("N"));
        var plan = RuntimeFilePlanner.Create(report, item.Id, destination, archiveName);
        var inspection = await RuntimeArchiveInspector.InspectAsync(plan, directory);
        check(inspection.HasServer && inspection.Entries.Count > 0 && !Directory.Exists(destination),
            "Actual approved local runtime ZIP identities and paths pass read-only inspection with no extraction or process launch");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var evidence = Path.Combine(Path.GetDirectoryName(destination)!, "last-inspection.json");
        await File.WriteAllTextAsync(evidence, JsonSerializer.Serialize(inspection, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Actual archive inspection: {plan.Files.Count} files; {inspection.Entries.Count} entries; {inspection.DeclaredExpandedBytes:N0} declared expanded bytes. Evidence: {evidence}");
    }
}
