using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using AINexus.App;

namespace AINexus.Checks;

internal static class ImageArtifactAcquisitionChecks
{
    private static readonly byte[] Bytes = "image artifact fixture data; no executable checkpoint claim"u8.ToArray();

    internal static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            PlannerChecks(root, check);
            await ApprovalDownloadReuseAsync(root, check);
            await FailureAndCancellationAsync(root, check);
        }
        finally
        {
            if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "fixtures"))
                + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe image acquisition fixture cleanup.");
            Directory.Delete(root, recursive: true);
        }
    }

    private static void PlannerChecks(string root, Action<bool, string> check)
    {
        var folder = NewFolder(root, "planning");
        var report = FixtureReport(folder);
        var selected = report.Catalog.Items.Single();
        var plan = ImageArtifactAcquisitionPlanner.Create(report, selected.Id, folder);
        check(report.Recommendations.Single().Status == RecommendationStatus.NeedsDetails
            && plan.Artifact.Repository == "Fixture/UnlistedImage" && !Directory.EnumerateFileSystemEntries(folder).Any(),
            "A generic user-selected image data plan admits unassessed source artifacts without a featured-model whitelist, writes or GGUF fit inference");
        var description = ImageArtifactAcquisitionPlanner.Describe(plan);
        check(description.Contains("not proven to be a complete checkpoint", StringComparison.Ordinal)
            && description.Contains("hardware fit", StringComparison.Ordinal) && description.Contains("No engine", StringComparison.Ordinal)
            && plan.RequiredAdditionalBytes == Bytes.Length + ImageArtifactAcquisitionPlanner.ReceiptReserveBytes,
            "Image data review exposes the exact file/storage budget and separately disclaims checkpoint completeness, hardware fit and engine changes");
        check(Rejects(() => ImageArtifactAcquisitionPlanner.Create(report with { Catalog = report.Catalog with { IsCached = true } }, selected.Id, folder))
            && Rejects(() => ImageArtifactAcquisitionPlanner.Create(report with { Catalog = report.Catalog with { RetrievedAt = DateTimeOffset.UtcNow.AddDays(-2) } }, selected.Id, folder))
            && Rejects(() => ImageArtifactAcquisitionPlanner.Create(report with { Machine = report.Machine with { ScannedAt = DateTimeOffset.UtcNow.AddMinutes(-6) } }, selected.Id, folder)),
            "Image acquisition requires online source evidence within24hours and a hardware/storage scan within5minutes");
        check(Rejects(() => Create(selected with { Sources = selected.Sources.Select(source => source with { RetrievedAt = DateTimeOffset.UtcNow.AddDays(-2) }).ToArray() }))
            && Rejects(() => Create(selected with { Sources = [] }))
            && Rejects(() => Create(selected with { PipelineTag = "text-generation" }))
            && Rejects(() => Create(selected with { Interest = DiscoveryInterest.Video })),
            "Fresh report timestamps cannot hide stale or missing pinned sources, the wrong task or an unrelated workflow");
        foreach (var invalid in new[]
        {
            selected with { License = "unknown" }, selected with { License = null },
            selected with { Artifact = selected.Artifact! with { Gated = true } },
            selected with { Artifact = selected.Artifact! with { Sha256 = null } },
            selected with { Artifact = selected.Artifact! with { SizeBytes = 0 } },
            selected with { Artifact = selected.Artifact! with { Format = "GGUF" } }
        }) check(Rejects(() => Create(invalid)), "Missing public image identity, positive size/hash or reviewable license prevents data planning");
        var controlArtifact = selected.Artifact! with { Repository = "Fixture/UnlistedImage\n" };
        var encodedRepository = string.Join('/', controlArtifact.Repository.Split('/').Select(Uri.EscapeDataString));
        var controlSource = new Uri($"https://huggingface.co/{encodedRepository}/blob/{controlArtifact.Revision}/");
        check(Rejects(() => Create(selected with { Artifact = controlArtifact, Sources =
            [new("Matching encoded card", new Uri(controlSource, "README.md"), DateTimeOffset.UtcNow),
             new("Matching encoded file", new Uri(controlSource, controlArtifact.FileName), DateTimeOffset.UtcNow)] })),
            "A terminal repository control character is rejected even when encoded pinned source references otherwise match it");
        foreach (var name in new[] { "../escape.safetensors", "nested/model.safetensors", "CON.safetensors", "bad:stream.safetensors", "fixture.gguf" })
            check(Rejects(() => ImageArtifactAcquisitionPlanner.ResolveDestination(folder, name))
                && Rejects(() => Create(selected with { Artifact = selected.Artifact! with { FileName = name } })),
                "Image acquisition cannot use traversal, nested components, Windows devices/streams or a different data format");
        check(Rejects(() => ImageArtifactAcquisitionPlanner.Create(report, selected.Id, Path.Combine(folder, "not-created")))
            && !Directory.Exists(Path.Combine(folder, "not-created"))
            && Rejects(() => ImageArtifactAcquisitionPlanner.Create(report, selected.Id, Path.GetPathRoot(folder)!))
            && Rejects(() => ImageArtifactAcquisitionPlanner.Create(report, selected.Id, Environment.GetFolderPath(Environment.SpecialFolder.Windows))),
            "Image data plans require an existing private local folder and cannot create parents or target drive/Windows roots");
        var lowSpace = report with { Machine = report.Machine with { Volumes = report.Machine.Volumes.Select(volume => volume with { AvailableBytes = 0 }).ToArray() } };
        check(Rejects(() => ImageArtifactAcquisitionPlanner.Create(lowSpace, selected.Id, folder))
            && Rejects(() => ImageArtifactAcquisitionPlanner.Create(report with { TargetVolume = "Z:\\" }, selected.Id, folder)),
            "Image acquisition enforces the selected scanned volume and actual image/receipt storage budget without substituting another drive");
        var foreign = Rebind(plan with { DownloadUrl = new("https://example.com/foreign.safetensors") });
        check(Rejects(() => ImageArtifactAcquisitionPlanner.ValidateForAcquisition(foreign, foreign.PlanId))
            && Rejects(() => ImageArtifactAcquisitionPlanner.ValidateForAcquisition(plan with { Name = "changed review" }, plan.PlanId)),
            "Recomputed fingerprints cannot authorize foreign image origins, and changed review contents revoke prior approval");
        check(!Directory.EnumerateFileSystemEntries(folder).Any(), "Rejected image plans leave the destination unchanged");

        ImageArtifactAcquisitionPlan Create(DiscoveredItem item) => ImageArtifactAcquisitionPlanner.Create(
            DiscoveryPipeline.Evaluate(report.Catalog with { Items = [item] }, report.Machine, report.TargetVolume), item.Id, folder);
    }

    private static async Task ApprovalDownloadReuseAsync(string root, Action<bool, string> check)
    {
        var folder = NewFolder(root, "acquire");
        var report = FixtureReport(folder);
        var plan = ImageArtifactAcquisitionPlanner.Create(report, report.Catalog.Items.Single().Id, folder);
        var calls = 0;
        using var http = new HttpClient(new Handler((request, _) =>
        {
            calls++;
            check(request.Method == HttpMethod.Get && request.Headers.Authorization is null && request.RequestUri == plan.DownloadUrl,
                "Approved image acquisition reads only the explicitly pinned public artifact source");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Bytes) });
        }));
        var service = new ImageArtifactAcquisitionService(http);
        check(await RejectsAsync(() => service.AcquireAsync(plan, new(plan.PlanId, false, true)))
            && await RejectsAsync(() => service.AcquireAsync(plan, new(plan.PlanId, true, false)))
            && await RejectsAsync(() => service.AcquireAsync(plan, new("another-plan", true, true)))
            && calls == 0 && !Directory.EnumerateFileSystemEntries(folder).Any(),
            "Image download and private receipt writes require both exact-plan license and destination approvals before HTTP or files");
        var result = await service.AcquireAsync(plan, Approve(plan), new CallbackProgress(_ => throw new InvalidOperationException("fixture UI error")));
        check(!result.ReusedExisting && result.Path == plan.DestinationPath && File.ReadAllBytes(result.Path).SequenceEqual(Bytes)
            && result.ActualBytes == Bytes.Length && result.ActualSha256.Equals(plan.Artifact.Sha256, StringComparison.OrdinalIgnoreCase)
            && calls == 1 && !service.IsBusy && !Directory.GetFiles(folder, "*.partial").Any(),
            "Approved generic image data is fully size/hash verified before publication and progress-presentation failures cannot corrupt completion");
        using (var receipt = JsonDocument.Parse(await File.ReadAllTextAsync(result.ReceiptPath)))
        {
            check(receipt.RootElement.GetProperty("ArtifactKind").GetString() == "ImageArtifactCandidate"
                && receipt.RootElement.GetProperty("RuntimeExecutionVerified").ValueKind == JsonValueKind.False
                && receipt.RootElement.GetProperty("CompleteCheckpointVerified").ValueKind == JsonValueKind.False
                && receipt.RootElement.GetProperty("HardwareFitVerified").ValueKind == JsonValueKind.False
                && Path.GetFileName(Path.GetDirectoryName(result.ReceiptPath)) == ImageArtifactAcquisitionService.ReceiptDirectoryName
                && !Directory.Exists(Path.Combine(folder, ".ainexus", "receipts")),
                "Image-specific private receipts record integrity without claiming a complete checkpoint, hardware fit, engine execution or GGUF launch approval");
        }
        check(await RejectsAsync(() => OfflineModelVerifier.ReadReceiptAsync(result.ReceiptPath)),
            "An image data receipt cannot be accepted by the GGUF offline-launch receipt reader");
        var reuse = ImageArtifactAcquisitionPlanner.Create(FixtureReport(folder), plan.ItemId, folder);
        var reused = await service.AcquireAsync(reuse, Approve(reuse));
        check(reuse.ExistingFileAtReview && reuse.RequiredAdditionalBytes == ImageArtifactAcquisitionPlanner.ReceiptReserveBytes
            && reused.ReusedExisting && calls == 1 && reused.ReceiptPath != result.ReceiptPath,
            "Existing image data is rehashed under a read lease and records reuse without another transfer or duplicate weight-space budget");
        var changed = Bytes.ToArray(); changed[^1] ^= 1;
        await File.WriteAllBytesAsync(plan.DestinationPath, changed);
        check(await RejectsAsync(() => service.AcquireAsync(reuse, Approve(reuse))) && calls == 1
            && File.ReadAllBytes(plan.DestinationPath).SequenceEqual(changed),
            "A same-size existing image hash conflict is preserved without network repair or overwrite");
        File.Delete(plan.DestinationPath);
        check(await RejectsAsync(() => service.AcquireAsync(reuse, Approve(reuse))) && calls == 1 && !File.Exists(plan.DestinationPath),
            "A disappeared reuse-only image file requires a new review rather than silently downloading a replacement");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "fixture-only");
        check(await RejectsAsync(() => service.AcquireAsync(plan, Approve(plan))) && calls == 1,
            "Image acquisition rejects inherited authentication headers instead of forwarding them to public sources");
    }

    private static async Task FailureAndCancellationAsync(string root, Action<bool, string> check)
    {
        foreach (var failure in new[] { "short", "oversize", "hash", "foreign-redirect", "source-secret", "stream-secret", "race", "receipt" })
        {
            var folder = NewFolder(root, failure);
            var report = FixtureReport(folder);
            var plan = ImageArtifactAcquisitionPlanner.Create(report, report.Catalog.Items.Single().Id, folder);
            using var http = new HttpClient(new Handler((_, _) =>
            {
                if (failure == "source-secret") throw new HttpRequestException("fixture-signed-secret");
                if (failure == "foreign-redirect")
                {
                    var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                    redirect.Headers.Location = new("https://example.com/foreign?secret=fixture-signed-secret");
                    return Task.FromResult(redirect);
                }
                if (failure == "race") File.WriteAllText(plan.DestinationPath, "preserve competing file");
                if (failure == "receipt") File.WriteAllText(Path.Combine(folder, ImageArtifactAcquisitionService.ReceiptDirectoryName), "preserve receipt blocker");
                var payload = failure switch { "short" => Bytes[..^1], "oversize" => Bytes.Concat(new byte[] { 1 }).ToArray(), "hash" => new byte[Bytes.Length], _ => Bytes };
                Stream stream = failure == "stream-secret" ? new FaultingStream() : new UnknownLengthStream(payload);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) });
            }));
            Exception? observed = null;
            try { await new ImageArtifactAcquisitionService(http).AcquireAsync(plan, Approve(plan)); }
            catch (Exception ex) { observed = ex; }
            check(observed is not null && !observed.ToString().Contains("fixture-signed-secret", StringComparison.Ordinal)
                && !Directory.GetFiles(folder, "*.partial").Any(),
                $"Image acquisition failure ({failure}) reports no unchecked completion, redacts source secrets and cleans only its partial");
            if (failure == "race") check(File.ReadAllText(plan.DestinationPath) == "preserve competing file", "An image destination created during transfer is never overwritten");
            else if (failure == "receipt") check(File.ReadAllBytes(plan.DestinationPath).SequenceEqual(Bytes)
                && observed!.Message.Contains("receipt", StringComparison.OrdinalIgnoreCase),
                "Receipt failure preserves the verified image bytes while explicitly reporting incomplete receipt completion");
            else check(!File.Exists(plan.DestinationPath), "Unverified image transfer bytes are never published as the final artifact");
        }
        var cancelFolder = NewFolder(root, "cancel");
        var cancelReport = FixtureReport(cancelFolder);
        var cancelPlan = ImageArtifactAcquisitionPlanner.Create(cancelReport, cancelReport.Catalog.Items.Single().Id, cancelFolder);
        var readEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancelHttp = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StreamContent(new NonCooperativeReadStream(readEntered)) })));
        using var cancel = new CancellationTokenSource();
        var service = new ImageArtifactAcquisitionService(cancelHttp);
        var pending = service.AcquireAsync(cancelPlan, Approve(cancelPlan), cancellationToken: cancel.Token);
        await readEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        check(service.IsBusy && await RejectsAsync(() => service.AcquireAsync(cancelPlan, Approve(cancelPlan))),
            "Overlapping image acquisitions cannot share a service operation or destination transfer");
        cancel.Cancel();
        check(await RejectsAsync(() => pending.WaitAsync(TimeSpan.FromSeconds(3))) && pending.IsCanceled && !service.IsBusy
            && !Directory.GetFiles(cancelFolder, "*.partial").Any() && !File.Exists(cancelPlan.DestinationPath),
            "Cancellation bounds a noncooperative source read, removes only the owned image partial and releases the operation gate");
    }

    internal static DiscoveryReport FixtureReport(string folder)
    {
        var report = AcquisitionPlanChecks.FixtureReport(folder, Bytes);
        var now = DateTimeOffset.UtcNow;
        var artifact = report.Catalog.Items.Single().Artifact! with { Repository = "Fixture/UnlistedImage", FileName = "fixture-image.safetensors", Format = "Safetensors" };
        var card = new Uri($"https://huggingface.co/{artifact.Repository}/blob/{artifact.Revision}/README.md");
        var file = new Uri($"https://huggingface.co/{artifact.Repository}/blob/{artifact.Revision}/{artifact.FileName}");
        var item = report.Catalog.Items.Single() with { Interest = DiscoveryInterest.Images, PipelineTag = "text-to-image", Artifact = artifact,
            Sources = [new("Pinned card", card, now), new("Pinned image file", file, now)] };
        return DiscoveryPipeline.Evaluate(report.Catalog with { Items = [item], RetrievedAt = now }, report.Machine, report.TargetVolume);
    }
    private static string NewFolder(string root, string name) { var folder = Path.Combine(root, name); Directory.CreateDirectory(folder); return folder; }
    private static ImageArtifactAcquisitionPlan Rebind(ImageArtifactAcquisitionPlan plan) => plan with { PlanId = ImageArtifactAcquisitionPlanner.Fingerprint(plan) };
    private static AcquisitionApproval Approve(ImageArtifactAcquisitionPlan plan) => new(plan.PlanId, true, true);
    private static bool Rejects(Action action) { try { action(); return false; } catch (Exception ex) when (Expected(ex)) { return true; } }
    private static async Task<bool> RejectsAsync(Func<Task> action) { try { await action(); return false; } catch (Exception ex) when (Expected(ex)) { return true; } }
    private static bool Expected(Exception ex) => ex is InvalidDataException or InvalidOperationException or ArgumentException or IOException or OperationCanceledException;
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token); }
    private sealed class CallbackProgress(Action<AcquisitionProgress> action) : IProgress<AcquisitionProgress> { public void Report(AcquisitionProgress value) => action(value); }
    private class UnknownLengthStream(byte[] bytes) : MemoryStream(bytes) { public override bool CanSeek => false; }
    private sealed class FaultingStream() : UnknownLengthStream([])
    { public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromException<int>(new IOException("fixture-signed-secret")); }
    private sealed class NonCooperativeReadStream(TaskCompletionSource entered) : UnknownLengthStream([])
    {
        private readonly TaskCompletionSource<int> _never = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { entered.TrySetResult(); return new(_never.Task); }
    }
}
