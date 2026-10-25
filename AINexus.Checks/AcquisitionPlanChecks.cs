using System.IO;
using System.Security.Cryptography;
using System.Text;
using AINexus.App;

namespace AINexus.Checks;

internal static class AcquisitionPlanChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var bytes = Encoding.UTF8.GetBytes("GGUF deterministic artifact fixture");
            var report = FixtureReport(root, bytes);
            var destination = Path.Combine(root, "models not created by planning");
            var item = report.Catalog.Items.Single();
            var plan = ModelAcquisitionPlanner.Create(report, item.Id, destination);
            check(!Directory.Exists(destination) && plan.PlanId.Length == 64,
                "Preparing a fingerprinted acquisition plan creates no directories, downloads or installer processes");
            check(plan.DownloadUrl.Scheme == "https" && plan.DownloadUrl.Host == "huggingface.co"
                && plan.DownloadUrl.AbsolutePath.Contains("/resolve/" + item.Artifact!.Revision + "/"),
                "Acquisition URLs are constructed from a pinned public repository revision");
            check(plan.DeclaredLicense == item.License && plan.Artifact.Sha256 == item.Artifact!.Sha256
                && plan.Artifact.SizeBytes == bytes.Length && plan.DestinationPath == Path.Combine(destination, "fixture.gguf"),
                "The review plan captures the exact selected artifact, license, expected hash, bytes and destination");
            check(Rejects(() => ModelAcquisitionPlanner.Create(report with { Catalog = report.Catalog with { IsCached = true } }, item.Id, destination)),
                "Cached source metadata cannot silently authorize a new acquisition plan");
            check(Rejects(() => ModelAcquisitionPlanner.Create(report with { Catalog = report.Catalog with { RetrievedAt = DateTimeOffset.UtcNow.AddDays(-2) } }, item.Id, destination)),
                "Stale source evidence requires refresh before acquisition planning");
            check(Rejects(() => ModelAcquisitionPlanner.Create(report with { Machine = report.Machine with { ScannedAt = DateTimeOffset.UtcNow.AddMinutes(-10) } }, item.Id, destination)),
                "Stale hardware evidence requires rescan before acquisition planning");
            var wrongVolume = report with { TargetVolume = "Z:\\" };
            check(Rejects(() => ModelAcquisitionPlanner.Create(wrongVolume, item.Id, destination)),
                "A plan cannot redirect acquisition to an unassessed drive");
            foreach (var unsafePath in new[] { "../outside.gguf", "..\\outside.gguf", "/outside.gguf", "CON.gguf", "AUX.gguf", "file:stream.gguf", "bad./fixture.gguf" })
                check(Rejects(() => ModelAcquisitionPlanner.ResolveDestination(destination, unsafePath)),
                    "Artifact paths cannot escape folders, use device names or alternate data streams");
            check(Rejects(() => ModelAcquisitionPlanner.ResolveDestination("relative folder", "fixture.gguf")),
                "Acquisition destinations must be absolute model directories");

            var missing = await ModelArtifactVerifier.VerifyExistingAsync(plan);
            check(!missing.Exists && !missing.Matches && !Directory.Exists(destination),
                "Verifying a missing artifact never starts a download or creates its destination");
            Directory.CreateDirectory(destination);
            await File.WriteAllBytesAsync(plan.DestinationPath, bytes);
            var matches = await ModelArtifactVerifier.VerifyExistingAsync(plan);
            check(matches.Exists && matches.Matches && matches.ActualBytes == bytes.Length
                && matches.Description.Contains("Runtime execution has not been verified"),
                "Existing artifact verification uses real file bytes and distinguishes integrity from inference success");
            var changed = bytes.ToArray();
            changed[^1] ^= 1;
            await File.WriteAllBytesAsync(plan.DestinationPath, changed);
            var mismatch = await ModelArtifactVerifier.VerifyExistingAsync(plan);
            check(!mismatch.Matches && File.ReadAllBytes(plan.DestinationPath).SequenceEqual(changed),
                "A mismatching existing model file is preserved rather than overwritten or reported as verified");
            await File.WriteAllTextAsync(plan.DestinationPath, "Different sized fixture");
            var differentSize = await ModelArtifactVerifier.VerifyExistingAsync(plan);
            check(!differentSize.Matches && differentSize.ActualSha256 is null,
                "Size mismatches are reported without fabricating a matching checksum");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var cancelled = false;
            try { await ModelArtifactVerifier.VerifyExistingAsync(plan, cancellation.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            check(cancelled, "Existing artifact verification respects cancellation");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    internal static DiscoveryReport FixtureReport(string destination, byte[]? bytes = null)
    {
        bytes ??= "GGUF fixture"u8.ToArray();
        var now = DateTimeOffset.UtcNow;
        var original = DiscoveryPipelineChecks.FixtureCatalog();
        var item = original.Items.Single();
        item = item with
        {
            Artifact = item.Artifact! with { SizeBytes = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)) },
            Sources = item.Sources.Select(source => source with { RetrievedAt = now }).ToArray()
        };
        var root = Path.GetPathRoot(destination)!;
        var machine = DiscoveryPipelineChecks.FixtureMachine() with
        {
            ScannedAt = now, Volumes = [new(root, 30L * 1_073_741_824, 50L * 1_073_741_824)]
        };
        return DiscoveryPipeline.Evaluate(original with { Items = [item], RetrievedAt = now }, machine, root);
    }

    private static bool Rejects(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or ArgumentException) { return true; }
        return false;
    }
}
