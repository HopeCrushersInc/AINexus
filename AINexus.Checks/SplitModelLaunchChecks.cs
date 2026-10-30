using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using AINexus.App;

namespace AINexus.Checks;

internal static class SplitModelLaunchChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", "split-launch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            byte[] first = "GGUF first inert part"u8.ToArray();
            byte[] second = "GGUF other inert part"u8.ToArray();
            var firstName = "model-00001-of-00002.gguf";
            var secondName = "model-00002-of-00002.gguf";
            var firstPath = Path.Combine(root, firstName);
            var secondPath = Path.Combine(root, secondName);
            var firstHash = Convert.ToHexString(SHA256.HashData(first));
            var secondHash = Convert.ToHexString(SHA256.HashData(second));
            var artifact = new ModelArtifact("Fixture/Model", new string('a', 40), firstName, first.Length, firstHash, "GGUF", false,
                [new(secondName, second.Length, secondHash)]);
            var now = DateTimeOffset.UtcNow;
            var plan = new ModelAcquisitionPlan("", "fixture", "Split launch fixture", artifact,
                new Uri($"https://huggingface.co/Fixture/Model/resolve/{artifact.Revision}/{firstName}"), firstPath,
                Path.GetPathRoot(root)!, "MIT", new Uri($"https://huggingface.co/Fixture/Model/blob/{artifact.Revision}/README.md"), now, now, now, [], []);
            plan = plan with { PlanId = ModelAcquisitionPlanner.ComputeFingerprint(plan) };
            await File.WriteAllBytesAsync(firstPath, first);
            await File.WriteAllBytesAsync(secondPath, second);
            var evidence = new ModelIntegrityEvidence(firstPath, true, true, first.Length, firstHash, now, "inert first",
                [new(secondPath, true, true, second.Length, secondHash, now, "inert second")]);
            var receipt = new ArtifactAcquisitionReceipt(1, plan, new(evidence, true, now), now);
            OfflineModelVerifier.ValidateReceipt(receipt);
            var roundTrip = JsonSerializer.Deserialize<ArtifactAcquisitionReceipt>(JsonSerializer.Serialize(receipt))!;
            OfflineModelVerifier.ValidateReceipt(roundTrip);
            check(ModelFileSet.SameIdentity(roundTrip.Plan.Artifact, plan.Artifact)
                && roundTrip.Plan.PlanId == ModelAcquisitionPlanner.ComputeFingerprint(roundTrip.Plan),
                "A split receipt preserves complete inventory identity and its approval fingerprint across JSON roundtrip");
            check(Rejects(() => OfflineModelVerifier.ValidateReceipt(receipt with
            {
                Acquisition = receipt.Acquisition with { Evidence = evidence with { AdditionalFiles = null } }
            })), "A split receipt cannot claim complete integrity using only the first file's evidence");
            check(Rejects(() => OfflineModelVerifier.ValidateReceipt(receipt with
            {
                Acquisition = receipt.Acquisition with { Evidence = evidence with
                {
                    AdditionalFiles = [evidence.AdditionalFiles![0] with { ActualSha256 = new string('b', 64) }]
                } }
            })), "A split receipt rejects a mismatching later-file hash despite valid first-file evidence");

            using (var lease = await OfflineModelVerifier.ModelLease.CaptureAsync(plan))
            {
                check(lease.FirstHash == firstHash && CannotWrite(firstPath) && CannotWrite(secondPath),
                    "Before runtime launch every split file is hashed and held against concurrent writes and deletion");
            }
            using (File.Open(secondPath, FileMode.Open, FileAccess.Write, FileShare.None)) { }
            check(true, "Disposing a complete model lease releases its later-file handle without running an engine");

            await File.WriteAllBytesAsync(secondPath, second.Select(value => (byte)(value ^ 1)).ToArray());
            check(await RejectsCaptureAsync(plan), "A changed later part blocks prelaunch verification even when the first file is intact");
            using (File.Open(firstPath, FileMode.Open, FileAccess.Write, FileShare.None)) { }
            File.Delete(secondPath);
            check(await RejectsCaptureAsync(plan), "A missing later part blocks prelaunch verification and releases previously opened handles");
            using (File.Open(firstPath, FileMode.Open, FileAccess.Write, FileShare.None)) { }
            await File.WriteAllBytesAsync(secondPath, second);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            var observed = false;
            try { using var lease = await OfflineModelVerifier.ModelLease.CaptureAsync(plan, cancelled.Token); }
            catch (OperationCanceledException) { observed = true; }
            check(observed && !CannotWrite(firstPath) && !CannotWrite(secondPath),
                "Cancellation before split model hashing opens no persistent file handles or runtime process");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static bool CannotWrite(string path)
    {
        try { using var file = File.Open(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete); return false; }
        catch (IOException) { return true; }
    }

    private static bool Rejects(Action action)
    {
        try { action(); return false; }
        catch (InvalidDataException) { return true; }
    }

    private static async Task<bool> RejectsCaptureAsync(ModelAcquisitionPlan plan)
    {
        try { using var lease = await OfflineModelVerifier.ModelLease.CaptureAsync(plan); return false; }
        catch (InvalidDataException) { return true; }
        catch (FileNotFoundException) { return true; }
    }
}
