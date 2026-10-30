using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using AINexus.App;

namespace AINexus.Checks;

/// <summary>Opt-in local proof using an explicitly supplied, already available derived split model.</summary>
internal static class SplitModelLiveChecks
{
    internal static async Task RunAsync(string runtime, string explicitExistingSplitDirectory, Action<bool, string> check)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        var token = deadline.Token;
        if (!Path.IsPathFullyQualified(runtime) || !File.Exists(runtime)
            || !Path.IsPathFullyQualified(explicitExistingSplitDirectory) || !Directory.Exists(explicitExistingSplitDirectory))
            throw new ArgumentException("The split-model live check requires an explicit existing local runtime and split-model directory.");
        var directory = Path.GetFullPath(explicitExistingSplitDirectory);
        OfflineModelVerifier.CheckNoReparsePath(Path.GetFullPath(runtime));
        OfflineModelVerifier.CheckNoReparsePath(directory);
        var paths = Directory.EnumerateFiles(directory, "*.gguf", SearchOption.TopDirectoryOnly)
            .Take(ModelFileSet.MaximumFiles + 1).Order(StringComparer.Ordinal).ToArray();
        if (paths.Length is < 2 or > ModelFileSet.MaximumFiles)
            throw new InvalidDataException("Choose a directory containing one complete bounded split GGUF inventory, with at least two parts.");

        var parts = new List<ModelArtifactPart>();
        foreach (var path in paths)
        {
            token.ThrowIfCancellationRequested();
            OfflineModelVerifier.CheckNoReparsePath(path);
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var size = file.Length;
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, token));
            parts.Add(new(Path.GetFileName(path), size, hash));
        }

        // These identities are deliberately synthetic fixture data. They were not
        // fetched from a publisher and make no claim about upstream hashes or terms.
        const string fixtureRepository = "AINexus-LocalTests/Derived-Split-Model";
        var fixtureRevision = new string('a', 40);
        var first = parts[0];
        var artifact = new ModelArtifact(fixtureRepository, fixtureRevision, first.FileName, first.SizeBytes, first.Sha256,
            "GGUF", false, parts.Skip(1).ToArray());
        ModelFileSet.Validate(artifact);
        var now = DateTimeOffset.UtcNow;
        var item = new DiscoveredItem("local-derived-split-fixture", "Locally derived split-model development fixture",
            "Local development test", DiscoveryInterest.LocalAssistants, "Model",
            "Local test fixture identity; original model terms must be reviewed separately", artifact,
            [new("Synthetic test provenance, not retrieved publisher metadata",
                new Uri($"https://huggingface.co/{fixtureRepository}/blob/{fixtureRevision}/README.md"), now)],
            "Explicit existing local files hashed for a development test. Not a real source discovery or publisher-verification result.", "text-generation");
        var machine = await HardwareCapabilityScanner.ScanAsync(token);
        var snapshot = new OnlineDiscoverySnapshot([item],
            ["Synthetic local development fixture only; no model metadata was requested from the Internet."], now);
        var report = DiscoveryPipeline.Evaluate(snapshot, machine, Path.GetPathRoot(directory)!);
        var assessment = report.Recommendations.Single();
        check(assessment.Status == RecommendationStatus.ResourceCandidate
            && assessment.EstimatedMemoryGiB > ModelFileSet.TotalBytes(artifact) / 1_073_741_824d,
            "Actual hardware planning accounts for every existing split file plus disclosed memory allowances");
        var plan = ModelAcquisitionPlanner.Create(report, item.Id, directory);
        var network = new NoNetworkHandler();
        using var http = new HttpClient(network) { Timeout = Timeout.InfiniteTimeSpan };
        var acquisition = await new ModelAcquisitionService(http).AcquireAsync(plan, new(plan.PlanId, true, true), cancellationToken: token);
        check(network.Calls == 0 && acquisition.ReusedExisting && acquisition.Evidence.Matches
            && acquisition.Evidence.AdditionalFiles?.Count == parts.Count - 1,
            "Explicitly approved local split-model acquisition reuses and verifies all existing files without making HTTP requests");
        var receiptPath = await ArtifactReceiptStore.SaveAsync(plan, acquisition, token);
        var audit = await OfflineModelVerifier.AuditReceiptAsync(receiptPath, token);
        check(audit.Integrity.Matches && ModelFileSet.SameIdentity(audit.Receipt.Plan.Artifact, artifact)
            && audit.Integrity.AdditionalFiles?.Count == parts.Count - 1,
            "The saved split-model receipt roundtrips its complete file inventory and reaudits every local part");

        var verification = await OfflineModelVerifier.VerifyAsync(receiptPath, runtime, contextSize: 2048, cancellationToken: token);
        check(!string.IsNullOrWhiteSpace(verification.Response) && verification.ContextSize == 2048
            && verification.AcquisitionPlanId == plan.PlanId && verification.ModelPath == plan.DestinationPath,
            "The actual selected runtime loads the first split file in offline mode and completes generation from the approved complete inventory");
        var portClosed = false;
        using (var client = new TcpClient())
        {
            try { await client.ConnectAsync(verification.Endpoint.Host, verification.Endpoint.Port, token); }
            catch (SocketException) { portClosed = true; }
        }
        check(portClosed, "The split-model verification stops its owned server and closes its loopback listening port");

        var reports = Path.Combine(AppContext.BaseDirectory, "Reports", "SplitModelLive");
        Directory.CreateDirectory(reports);
        var destination = Path.Combine(reports, Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(destination, JsonSerializer.Serialize(new
        {
            CompletedAt = DateTimeOffset.UtcNow,
            Runtime = Path.GetFullPath(runtime), ModelDirectory = directory,
            Files = parts, TotalModelBytes = ModelFileSet.TotalBytes(artifact),
            Receipt = receiptPath, VerificationEvidence = verification.SavedEvidencePath,
            ContextSize = verification.ContextSize, Response = verification.Response,
            AcquisitionReusedExisting = acquisition.ReusedExisting,
            NetworkRequestsDuringAcquisition = network.Calls,
            OwnedServerStopped = portClosed,
            Provenance = "Locally derived test split files; SHA-256 values measured locally. Synthetic fixture repository/revision/license strings, not publisher metadata or license certification.",
            Limitations = "One actual local model/runtime/hardware smoke test. No model was downloaded. Does not establish general compatibility, response quality, upstream authenticity or operating-system network isolation."
        }, new JsonSerializerOptions { WriteIndented = true }), token);
        Console.WriteLine("Split-model live response: " + verification.Response);
        Console.WriteLine("Split-model live report: " + destination);
    }

    private sealed class NoNetworkHandler : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("This existing-file development check must not request a network download.");
        }
    }
}
