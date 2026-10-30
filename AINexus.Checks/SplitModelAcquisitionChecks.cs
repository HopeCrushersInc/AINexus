using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using AINexus.App;

namespace AINexus.Checks;

internal static class SplitModelAcquisitionChecks
{
    private static readonly byte[] First = "GGUF inert first split-model fixture"u8.ToArray();
    private static readonly byte[] Second = "GGUF inert second split-model fixture with different bytes"u8.ToArray();

    internal static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await CheckApprovalAndLegacyAsync(root, check);
            await CheckCompleteDownloadAndReuseAsync(root, check);
            await CheckIncompleteAndDifferentFilesAsync(root, check);
            await CheckCancellationAndConcurrencyAsync(root, check);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task CheckApprovalAndLegacyAsync(string root, Action<bool, string> check)
    {
        var plan = CreatePlan(root, "approval");
        var calls = 0;
        using var http = new HttpClient(new Handler((_, _) => { calls++; return Task.FromResult(Bytes(First)); }));
        var service = new ModelAcquisitionService(http);
        var changed = plan with
        {
            Artifact = plan.Artifact with
            {
                AdditionalFiles = [plan.Artifact.AdditionalFiles![0] with { Sha256 = new string('a', 64) }]
            }
        };
        check(await RejectsAsync<InvalidOperationException>(() => service.AcquireAsync(changed, Approve(plan)))
            && calls == 0 && !Directory.Exists(Path.GetDirectoryName(plan.DestinationPath)),
            "Changing a later split-model file invalidates the original review approval before HTTP or destination writes");
        var restored = JsonSerializer.Deserialize<ModelAcquisitionPlan>(JsonSerializer.Serialize(plan))!;
        ModelAcquisitionPlanner.ValidateForAcquisition(restored, plan.PlanId);
        check(ModelFileSet.SameIdentity(plan.Artifact, restored.Artifact)
            && ModelFileSet.TotalBytes(restored.Artifact) == First.Length + Second.Length,
            "A serialized split-model review preserves the complete identity, approval fingerprint and aggregate byte count");

        var legacyArtifact = plan.Artifact with { FileName = "legacy.gguf", AdditionalFiles = null };
        var legacyJson = JsonSerializer.Serialize(new
        {
            legacyArtifact.Repository, legacyArtifact.Revision, legacyArtifact.FileName,
            legacyArtifact.SizeBytes, legacyArtifact.Sha256, legacyArtifact.Format, legacyArtifact.Gated
        });
        var legacyEvidence = new ModelIntegrityEvidence("fixture.gguf", true, true, First.Length, Hash(First), DateTimeOffset.UtcNow, "Inert legacy fixture.");
        var legacyEvidenceJson = JsonSerializer.Serialize(new
        {
            legacyEvidence.Path, legacyEvidence.Exists, legacyEvidence.Matches, legacyEvidence.ActualBytes,
            legacyEvidence.ActualSha256, legacyEvidence.CheckedAt, legacyEvidence.Description
        });
        check(JsonSerializer.Serialize(legacyArtifact) == legacyJson
            && JsonSerializer.Serialize(legacyEvidence) == legacyEvidenceJson,
            "Absent additional-file fields preserve the exact legacy artifact and integrity JSON used by existing receipt fingerprints");
    }

    private static async Task CheckCompleteDownloadAndReuseAsync(string root, Action<bool, string> check)
    {
        var plan = CreatePlan(root, "complete");
        var files = ModelFileSet.FilePlans(plan);
        var requests = new List<Uri>();
        var progress = new List<AcquisitionProgress>();
        using var http = new HttpClient(new Handler((request, _) =>
        {
            requests.Add(request.RequestUri!);
            return Task.FromResult(Bytes(request.RequestUri == files[0].DownloadUrl ? First : Second));
        }));
        var service = new ModelAcquisitionService(http);
        var result = await service.AcquireAsync(plan, Approve(plan), new InlineProgress(progress.Add));
        ModelFileSet.ValidateEvidence(plan, result.Evidence);
        check(!result.ReusedExisting && result.Evidence.Matches && result.Evidence.AdditionalFiles is [{ Matches: true }]
            && requests.SequenceEqual(files.Select(file => file.DownloadUrl))
            && File.ReadAllBytes(files[0].DestinationPath).SequenceEqual(First)
            && File.ReadAllBytes(files[1].DestinationPath).SequenceEqual(Second),
            "One explicit split-model approval downloads every pinned file and returns complete per-file integrity evidence");
        check(progress.Count > 0 && progress.All(value => value.ExpectedBytes == First.Length + Second.Length)
            && progress.Select(value => value.CompletedBytes).SequenceEqual(progress.Select(value => value.CompletedBytes).Order())
            && progress[^1].CompletedBytes == First.Length + Second.Length && !service.IsBusy,
            "Split-model progress accounts for the aggregate inventory, remains monotonic and releases the single acquisition gate");
        var reused = await service.AcquireAsync(plan, Approve(plan));
        ModelFileSet.ValidateEvidence(plan, reused.Evidence);
        check(reused.ReusedExisting && reused.Evidence.Matches && requests.Count == 2,
            "Reusing a complete split model rehashes every existing file without downloading it again");
        var receiptPath = await ArtifactReceiptStore.SaveAsync(plan, result);
        var receipt = await OfflineModelVerifier.ReadReceiptAsync(receiptPath);
        check(ModelFileSet.SameIdentity(plan.Artifact, receipt.Plan.Artifact)
            && receipt.Acquisition.Evidence.AdditionalFiles is [{ Matches: true, ActualBytes: > 0 }],
            "Saving and reopening an acquisition receipt preserves the approved split inventory and every file's integrity proof");
        var firstOnly = result with { Evidence = result.Evidence with { AdditionalFiles = null } };
        var receiptCount = Directory.EnumerateFiles(Path.GetDirectoryName(receiptPath)!, "*.json").Count();
        check(await RejectsAsync<InvalidDataException>(() => ArtifactReceiptStore.SaveAsync(plan, firstOnly))
            && Directory.EnumerateFiles(Path.GetDirectoryName(receiptPath)!, "*.json").Count() == receiptCount,
            "A matching first-file proof alone cannot save a successful complete-model acquisition receipt");
    }

    private static async Task CheckIncompleteAndDifferentFilesAsync(string root, Action<bool, string> check)
    {
        var plan = CreatePlan(root, "incomplete");
        var files = ModelFileSet.FilePlans(plan);
        Directory.CreateDirectory(Path.GetDirectoryName(files[0].DestinationPath)!);
        await File.WriteAllBytesAsync(files[0].DestinationPath, First);
        var missing = await ModelArtifactVerifier.VerifyExistingAsync(plan);
        check(!missing.Matches && missing.AdditionalFiles is [{ Exists: false, Matches: false }]
            && File.ReadAllBytes(files[0].DestinationPath).SequenceEqual(First),
            "A correct first part with a missing later part never passes complete-model verification");

        var wrong = Second.ToArray();
        wrong[^1] ^= 1;
        using (var http = new HttpClient(new Handler((_, _) => Task.FromResult(Bytes(wrong)))))
        {
            var service = new ModelAcquisitionService(http);
            check(await RejectsAsync<InvalidDataException>(() => service.AcquireAsync(plan, Approve(plan)))
                && File.ReadAllBytes(files[0].DestinationPath).SequenceEqual(First)
                && !File.Exists(files[1].DestinationPath)
                && !Directory.EnumerateFiles(Path.GetDirectoryName(plan.DestinationPath)!, "*.partial").Any()
                && !service.IsBusy,
                "A later-part download hash mismatch preserves the verified earlier file, removes its own partial and produces no complete-model result");
        }
        await File.WriteAllBytesAsync(files[1].DestinationPath, wrong);
        var different = await ModelArtifactVerifier.VerifyExistingAsync(plan);
        check(!different.Matches && different.AdditionalFiles is [{ Exists: true, Matches: false }],
            "A later part with the right byte count and wrong SHA-256 makes the entire existing model unverified");
        var calls = 0;
        using (var http = new HttpClient(new Handler((_, _) => { calls++; return Task.FromResult(Bytes(Second)); })))
        {
            var service = new ModelAcquisitionService(http);
            check(await RejectsAsync<InvalidDataException>(() => service.AcquireAsync(plan, Approve(plan)))
                && calls == 0 && File.ReadAllBytes(files[1].DestinationPath).SequenceEqual(wrong),
                "A conflicting existing later part is preserved without overwrite or a replacement HTTP request");
        }
    }

    private static async Task CheckCancellationAndConcurrencyAsync(string root, Action<bool, string> check)
    {
        var plan = CreatePlan(root, "cancel");
        var files = ModelFileSet.FilePlans(plan);
        var enteredSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Handler((request, _) => Task.FromResult(request.RequestUri == files[0].DownloadUrl
            ? Bytes(First)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new PauseAfterChunkStream(Second)) })));
        using var cancellation = new CancellationTokenSource();
        var service = new ModelAcquisitionService(http);
        var progress = new InlineProgress(value =>
        {
            if (value.CompletedBytes > First.Length && value.CompletedBytes < First.Length + Second.Length)
                enteredSecond.TrySetResult();
        });
        var pending = service.AcquireAsync(plan, Approve(plan), progress, cancellation.Token);
        var cancelled = false;
        try
        {
            await enteredSecond.Task.WaitAsync(TimeSpan.FromSeconds(5));
            check(service.IsBusy && await RejectsAsync<InvalidOperationException>(() => service.AcquireAsync(plan, Approve(plan))),
                "The split-model acquisition gate remains held across files and rejects an overlapping approval");
        }
        finally
        {
            cancellation.Cancel();
            cancelled = await RejectsAsync<OperationCanceledException>(() => pending);
        }
        check(cancelled && !service.IsBusy && File.ReadAllBytes(files[0].DestinationPath).SequenceEqual(First)
            && !File.Exists(files[1].DestinationPath)
            && !Directory.EnumerateFiles(Path.GetDirectoryName(plan.DestinationPath)!, "*.partial").Any(),
            "Cancelling a later part retains the completed verified part, cleans the owned partial and releases the acquisition gate");
    }

    private static ModelAcquisitionPlan CreatePlan(string root, string name)
    {
        var folder = Path.Combine(root, name);
        var report = AcquisitionPlanChecks.FixtureReport(folder, First);
        var template = ModelAcquisitionPlanner.Create(report, report.Catalog.Items.Single().Id, folder);
        var artifact = template.Artifact with
        {
            FileName = "fixture-00001-of-00002.gguf", SizeBytes = First.Length, Sha256 = Hash(First),
            AdditionalFiles = [new("fixture-00002-of-00002.gguf", Second.Length, Hash(Second))]
        };
        var repository = string.Join('/', artifact.Repository.Split('/').Select(Uri.EscapeDataString));
        var plan = template with
        {
            Artifact = artifact, DestinationPath = Path.Combine(folder, artifact.FileName),
            DownloadUrl = new Uri($"https://huggingface.co/{repository}/resolve/{artifact.Revision}/{artifact.FileName}")
        };
        return plan with { PlanId = ModelAcquisitionPlanner.ComputeFingerprint(plan) };
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static AcquisitionApproval Approve(ModelAcquisitionPlan plan) => new(plan.PlanId, true, true);
    private static HttpResponseMessage Bytes(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    private static async Task<bool> RejectsAsync<T>(Func<Task> operation) where T : Exception
    {
        try { await operation(); }
        catch (T) { return true; }
        return false;
    }
    private sealed class InlineProgress(Action<AcquisitionProgress> report) : IProgress<AcquisitionProgress>
    {
        public void Report(AcquisitionProgress value) => report(value);
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
    private sealed class PauseAfterChunkStream(byte[] bytes) : MemoryStream(bytes)
    {
        private bool _read;
        public override bool CanSeek => false;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (_read) return new ValueTask<int>(WaitForCancellationAsync(token));
            _read = true;
            return base.ReadAsync(buffer[..Math.Min(5, buffer.Length)], token);
        }
        private static async Task<int> WaitForCancellationAsync(CancellationToken token)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return 0;
        }
    }
}
