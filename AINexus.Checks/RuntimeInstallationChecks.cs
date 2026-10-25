using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using AINexus.App;

namespace AINexus.Checks;

internal static class RuntimeInstallationChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var native = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
        var archiveName = $"llama-bin-win-cpu-{native}.zip";
        var archivePath = Path.Combine(root, archiveName);
        var fixtureServer = "This is an inert extraction fixture, never a real executable."u8.ToArray();
        var personal = Path.Combine(root, "personal.txt");
        await File.WriteAllTextAsync(personal, "Preserve this unrelated file.");
        async Task<RuntimeArchiveInspection> FixtureAsync(string destination, string serverName = "llama-server.exe", bool includeLicense = true, bool expandedMismatch = false, byte[]? licenseContents = null, string? extraName = null)
        {
            File.Delete(archivePath); // Only this run's inert fixture ZIP.
            using (var stream = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                using (var file = zip.CreateEntry(serverName, CompressionLevel.NoCompression).Open()) file.Write(fixtureServer);
                if (includeLicense)
                {
                    using var file = zip.CreateEntry("licenses/LICENSE.txt", CompressionLevel.NoCompression).Open();
                    file.Write(licenseContents ?? "Fixture terms, not an actual runtime license."u8.ToArray());
                }
                if (extraName is not null)
                {
                    using var file = zip.CreateEntry(extraName, CompressionLevel.NoCompression).Open();
                    file.Write("Reserved fixture entry"u8);
                }
            }
            var bytes = await File.ReadAllBytesAsync(archivePath);
            if (expandedMismatch)
            {
                // The inspected first entry declares one fewer byte than its stored payload.
                // Its recorded archive hash describes those exact deliberately malformed bytes.
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(22), (uint)(fixtureServer.Length - 1));
                for (var index = 0; index < bytes.Length - 46; index++)
                    if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(index)) == 0x02014b50)
                    { BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index + 24), (uint)(fixtureServer.Length - 1)); break; }
                await File.WriteAllBytesAsync(archivePath, bytes);
            }
            var asset = new RuntimeReleaseAsset(archiveName, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)),
                new Uri("https://github.com/ggml-org/llama.cpp/releases/download/fixture/" + archiveName));
            var machine = RuntimeSetupChecks.Machine() with { OsArchitecture = native };
            var filePlan = RuntimeFilePlanner.Create(RuntimeSetupChecks.Report(machine, asset), "fixture/runtime", destination, archiveName);
            return await RuntimeArchiveInspector.InspectAsync(filePlan, root);
        }
        static RuntimeInstallationApproval Approve(RuntimeInstallationPlan plan) => new(plan.PlanId, true, true, true);
        async Task<bool> RejectsAsync<T>(Func<Task> action) where T : Exception
        {
            try { await action(); return false; }
            catch (T) { return true; }
        }
        try
        {
            var destination = Path.Combine(root, "installed-fixture");
            var inspection = await FixtureAsync(destination);
            var plan = await RuntimeInstallationPlanner.CreateAsync(inspection, root);
            var terms = await RuntimeInstallationPlanner.ReadLicenseTextsAsync(plan);
            check(terms.Contains("licenses/LICENSE.txt") && terms.Contains("Fixture terms, not an actual runtime license.") && !Directory.Exists(destination),
                "Bundled term viewer reads bounded verified local UTF-8 license files without extracting or accepting them");
            check(!Directory.Exists(destination) && Directory.GetDirectories(root, ".ainexus-stage-*").Length == 0
                && RuntimeInstallationPlanner.Describe(plan).Contains(plan.PlanId),
                "CPU extraction review rechecks inert local ZIP identities without writing a destination or granting approval");
            check(await RejectsAsync<InvalidOperationException>(() => RuntimeInstallationService.InstallAsync(plan, new(plan.PlanId, false, true, true)))
                && await RejectsAsync<InvalidOperationException>(() => RuntimeInstallationService.InstallAsync(plan, new(plan.PlanId, true, false, true)))
                && await RejectsAsync<InvalidOperationException>(() => RuntimeInstallationService.InstallAsync(plan, new(plan.PlanId, true, true, false)))
                && await RejectsAsync<InvalidOperationException>(() => RuntimeInstallationService.InstallAsync(plan, new("different-plan", true, true, true)))
                && !Directory.Exists(destination),
                "Runtime extraction requires separate exact-plan license, bundled-terms and destination-write approval");
            check(await RejectsAsync<InvalidDataException>(() => RuntimeInstallationService.InstallAsync(plan with { LocalArchiveDirectory = root + "-changed" }, Approve(plan)))
                && await RejectsAsync<InvalidDataException>(() => RuntimeInstallationService.InstallAsync(plan with { Inspection = inspection with { DeclaredExpandedBytes = inspection.DeclaredExpandedBytes + 1 } }, Approve(plan))),
                "Changed archive directory or expanded inspection evidence cannot reuse installation approval");
            var forged = inspection with { Entries = inspection.Entries.Select(e => e.RelativePath == "llama-server.exe" ? e with { RelativePath = "../outside.exe" } : e).ToArray() };
            check(await RejectsAsync<InvalidDataException>(() => RuntimeInstallationPlanner.CreateAsync(forged, root)),
                "Forged inspected traversal entries fail fresh inspection before extraction");
            check(await RejectsAsync<ArgumentException>(() => RuntimeInstallationPlanner.CreateAsync(inspection, @"\\unapproved-server\share")),
                "Runtime extraction review rejects network archive paths before contacting them");
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                check(await RejectsAsync<OperationCanceledException>(() => RuntimeInstallationService.InstallAsync(plan, Approve(plan), cancelled.Token))
                    && !Directory.Exists(destination) && Directory.GetDirectories(root, ".ainexus-stage-*").Length == 0,
                    "Precancelled runtime extraction writes no destination or staging files");
            }
            var originalBytes = await File.ReadAllBytesAsync(archivePath);
            var changed = originalBytes.ToArray(); changed[0] ^= 1;
            await File.WriteAllBytesAsync(archivePath, changed);
            check(await RejectsAsync<InvalidDataException>(() => RuntimeInstallationService.InstallAsync(plan, Approve(plan)))
                && !Directory.Exists(destination), "Changed same-size ZIP bytes fail the approved hash before staging exists");
            await File.WriteAllBytesAsync(archivePath, originalBytes);
            Directory.CreateDirectory(destination);
            await File.WriteAllTextAsync(Path.Combine(destination, "preserve.txt"), "Existing installation.");
            check(await RejectsAsync<IOException>(() => RuntimeInstallationService.InstallAsync(plan, Approve(plan)))
                && await File.ReadAllTextAsync(Path.Combine(destination, "preserve.txt")) == "Existing installation.",
                "Existing runtime destinations and their personal files cannot be overwritten");
            var newDestination = Path.Combine(root, "new-installed-fixture");
            inspection = await FixtureAsync(newDestination);
            plan = await RuntimeInstallationPlanner.CreateAsync(inspection, root);
            var publishedArchiveBytes = await File.ReadAllBytesAsync(archivePath);
            var result = await RuntimeInstallationService.InstallAsync(plan, Approve(plan));
            check(result.Destination == newDestination && File.Exists(result.ReceiptPath)
                && (await File.ReadAllBytesAsync(result.ExecutablePath)).SequenceEqual(fixtureServer)
                && File.Exists(Path.Combine(newDestination, "licenses", "LICENSE.txt"))
                && (await File.ReadAllBytesAsync(archivePath)).SequenceEqual(publishedArchiveBytes)
                && Directory.GetDirectories(root, ".ainexus-stage-*").Length == 0,
                "Approved local CPU ZIP extraction publishes exact inert bytes, preserves its archive and moves away its staging folder without executing a payload");
            var receipt = await File.ReadAllTextAsync(result.ReceiptPath);
            check(receipt.Contains("\"RuntimeExecutionVerified\":false") && receipt.Contains("\"PublisherAuthenticityVerified\":false")
                && await RejectsAsync<IOException>(() => RuntimeInstallationService.InstallAsync(plan, Approve(plan))),
                "Extraction receipt makes no execution/authenticity claim and successful approval cannot overwrite its published destination");
            var injectedInspection = await FixtureAsync(Path.Combine(root, "unexpected-stage-file"));
            var injectedPlan = await RuntimeInstallationPlanner.CreateAsync(injectedInspection, root);
            var injectionRejected = false;
            try
            {
                await RuntimeInstallationService.InstallAsync(injectedPlan, Approve(injectedPlan), progress:
                    new InlineProgress(_ =>
                    {
                        var stage = Directory.GetDirectories(root, ".ainexus-stage-*").Single();
                        File.WriteAllText(Path.Combine(stage, "unexpected.txt"), "Unexpected inert fixture file.");
                    }));
            }
            catch (IOException ex)
            {
                injectionRejected = ex.InnerException is InvalidDataException && !Directory.Exists(injectedPlan.Destination);
            }
            check(injectionRejected && Directory.GetDirectories(root, ".ainexus-stage-*").Length == 1,
                "Unexpected staging files prevent publication and remain identified for inspection without recursive cleanup");
            var taggedName = $"llama-b11669-bin-win-cpu-{native}.zip";
            var taggedPath = Path.Combine(root, taggedName);
            File.Copy(archivePath, taggedPath, overwrite: false);
            var taggedAsset = injectedInspection.Plan.Files[0] with
            {
                Name = taggedName,
                DownloadUrl = new Uri("https://github.com/ggml-org/llama.cpp/releases/download/b11669/" + taggedName)
            };
            var taggedFilePlan = injectedInspection.Plan with
            {
                Release = "b11669", Destination = Path.Combine(root, "tagged-runtime"), Files = [taggedAsset]
            };
            taggedFilePlan = taggedFilePlan with { PlanId = RuntimeFilePlanner.Fingerprint(taggedFilePlan) };
            var taggedInspection = await RuntimeArchiveInspector.InspectAsync(taggedFilePlan, root);
            var taggedPlan = await RuntimeInstallationPlanner.CreateAsync(taggedInspection, root);
            var taggedResult = await RuntimeInstallationService.InstallAsync(taggedPlan, Approve(taggedPlan));
            check((await File.ReadAllBytesAsync(taggedResult.ExecutablePath)).SequenceEqual(fixtureServer)
                && File.Exists(taggedPath) && taggedPlan.Inspection.Plan.Files[0].Sha256 == injectedInspection.Plan.Files[0].Sha256,
                "Official tag-prefixed CPU archive naming preserves exact release source/hash and supports approved inert extraction");
            var wrongName = $"llama-b11670-bin-win-cpu-{native}.zip";
            var wrongTagPlan = taggedFilePlan with
            {
                Destination = Path.Combine(root, "wrong-tag-runtime"),
                Files = [taggedAsset with { Name = wrongName,
                    DownloadUrl = new Uri("https://github.com/ggml-org/llama.cpp/releases/download/b11669/" + wrongName) }]
            };
            wrongTagPlan = wrongTagPlan with { PlanId = RuntimeFilePlanner.Fingerprint(wrongTagPlan) };
            check(await RejectsAsync<NotSupportedException>(() => RuntimeInstallationPlanner.CreateAsync(
                taggedInspection with { Plan = wrongTagPlan, PlanId = wrongTagPlan.PlanId }, root)),
                "A different release tag in the runtime archive filename cannot be normalized into the reviewed release");
            var foreignReleasePlan = taggedFilePlan with
            {
                Destination = Path.Combine(root, "wrong-source-tag-runtime"),
                Files = [taggedAsset with { DownloadUrl = new Uri("https://github.com/ggml-org/llama.cpp/releases/download/b11670/" + taggedName) }]
            };
            foreignReleasePlan = foreignReleasePlan with { PlanId = RuntimeFilePlanner.Fingerprint(foreignReleasePlan) };
            check(await RejectsAsync<InvalidDataException>(() => RuntimeInstallationPlanner.CreateAsync(
                taggedInspection with { Plan = foreignReleasePlan, PlanId = foreignReleasePlan.PlanId }, root)),
                "Tag-prefixed CPU extraction still rejects a download URL from another release");
            var missingServer = await FixtureAsync(Path.Combine(root, "missing-server"), "not-a-server.txt");
            check(await RejectsAsync<InvalidDataException>(() => RuntimeInstallationPlanner.CreateAsync(missingServer, root)),
                "Runtime extraction requires an inspected nonempty root server filename");
            var missingLicense = await FixtureAsync(Path.Combine(root, "missing-license"), includeLicense: false);
            check(await RejectsAsync<InvalidDataException>(() => RuntimeInstallationPlanner.CreateAsync(missingLicense, root)),
                "Runtime extraction rejects archives without recognizable bundled license files");
            var unlicensed = await FixtureAsync(Path.Combine(root, "unknown-license"));
            var unknownFilePlan = unlicensed.Plan with { DeclaredLicense = null };
            unknownFilePlan = unknownFilePlan with { PlanId = RuntimeFilePlanner.Fingerprint(unknownFilePlan) };
            check(await RejectsAsync<InvalidDataException>(() => RuntimeInstallationPlanner.CreateAsync(unlicensed with { Plan = unknownFilePlan, PlanId = unknownFilePlan.PlanId }, root)),
                "Unknown declared runtime licenses cannot be converted into approved extraction plans");
            var multi = unlicensed.Plan with { Files = [unlicensed.Plan.Files[0], unlicensed.Plan.Files[0]] };
            multi = multi with { PlanId = RuntimeFilePlanner.Fingerprint(multi) };
            check(await RejectsAsync<InvalidDataException>(() => RuntimeInstallationPlanner.CreateAsync(unlicensed with { Plan = multi, PlanId = multi.PlanId }, root)),
                "Multiple runtime/support archives remain outside the initial extraction recipe");
            var reserved = await FixtureAsync(Path.Combine(root, "reserved-receipt"), extraName: RuntimeInstallationPlanner.ReceiptName);
            check(await RejectsAsync<InvalidDataException>(() => RuntimeInstallationPlanner.CreateAsync(reserved, root)),
                "An archive cannot supply or shadow the reserved installation receipt path");
            var unreadable = await FixtureAsync(Path.Combine(root, "binary-license"), licenseContents: [0xff, 0xfe, 0x00]);
            var unreadablePlan = await RuntimeInstallationPlanner.CreateAsync(unreadable, root);
            check(await RejectsAsync<InvalidDataException>(() => RuntimeInstallationPlanner.ReadLicenseTextsAsync(unreadablePlan)),
                "Bundled license viewer refuses invalid UTF-8 rather than silently replacing unsupported bytes");
            var oversized = await FixtureAsync(Path.Combine(root, "oversized-license"), licenseContents: new byte[65537]);
            var oversizedPlan = await RuntimeInstallationPlanner.CreateAsync(oversized, root);
            check(await RejectsAsync<InvalidDataException>(() => RuntimeInstallationPlanner.ReadLicenseTextsAsync(oversizedPlan)),
                "Bundled license viewer enforces its per-file byte limit before displaying incomplete terms");
            var cancellationInspection = await FixtureAsync(Path.Combine(root, "cancel-after-staging"));
            var cancellationPlan = await RuntimeInstallationPlanner.CreateAsync(cancellationInspection, root);
            using (var cancellation = new CancellationTokenSource())
            {
                var cancelledAfterStaging = false;
                try
                {
                    await RuntimeInstallationService.InstallAsync(cancellationPlan, Approve(cancellationPlan), cancellation.Token,
                        new InlineProgress(_ => cancellation.Cancel()));
                }
                catch (OperationCanceledException ex)
                {
                    cancelledAfterStaging = ex.CancellationToken == cancellation.Token && ex.Message.Contains(".ainexus-stage-")
                        && !Directory.Exists(cancellationPlan.Destination);
                }
                check(cancelledAfterStaging && Directory.GetDirectories(root, ".ainexus-stage-*").Length == 2,
                    "Cancellation after staging creation preserves its token and identifies retained staging without publishing the destination");
            }
            var malformed = await FixtureAsync(Path.Combine(root, "bad-expanded-payload"), expandedMismatch: true);
            var malformedPlan = await RuntimeInstallationPlanner.CreateAsync(malformed, root);
            var failedSafely = false;
            try { await RuntimeInstallationService.InstallAsync(malformedPlan, Approve(malformedPlan)); }
            catch (IOException ex) { failedSafely = ex.Message.Contains("retained staging") && !Directory.Exists(malformedPlan.Destination); }
            check(failedSafely && Directory.GetDirectories(root, ".ainexus-stage-*").Length == 3
                && !File.Exists(Path.Combine(root, "outside.exe"))
                && await File.ReadAllTextAsync(personal) == "Preserve this unrelated file.",
                "Declared-size decompression failure publishes nothing and identifies retained staging without recursive deletion or external-file changes");
        }
        finally
        {
            // Only this run's GUID fixture tree, containing no links and inert payloads, is removed.
            OfflineModelVerifier.CheckNoReparsePath(root);
            Directory.Delete(root, recursive: true);
        }
    }
    private sealed class InlineProgress(Action<RuntimeInstallationProgress> report) : IProgress<RuntimeInstallationProgress>
    {
        public void Report(RuntimeInstallationProgress value) => report(value);
    }
}
