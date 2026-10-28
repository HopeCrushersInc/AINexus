using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Windows.Controls;
using AINexus.App;

namespace AINexus.Checks;

internal static class ModelEvaluationUiChecks
{
    private const string Repository = "fixture/model";

    internal static void RunNative(Action<bool, string> check)
    {
        var window = new MainWindow { GuidedPersistPreferences = false };
        try
        {
            WorkloadDiscoveryChecks.Pump(async () =>
            {
                var repository = Find<TextBox>(window, "ModelEvidenceRepositoryText");
                var details = Find<TextBox>(window, "ModelEvidenceDetailsText");
                var open = Find<Button>(window, "DiscoveryEvidenceButton");
                var read = Find<Button>(window, "ModelEvidenceReadButton");
                var cancel = Find<Button>(window, "ModelEvidenceCancelButton");
                var expander = Find<Expander>(window, "ModelEvidenceExpander");
                var grid = Find<DataGrid>(window, "DiscoveryResultsGrid");
                var reads = 0;
                window.ModelEvidenceReadOverride = (requested, _) =>
                {
                    reads++;
                    return Task.FromResult(Snapshot(requested));
                };
                check(repository.Text == "" && !open.IsEnabled && !expander.IsExpanded && !cancel.IsEnabled,
                    "Benchmark evidence begins blank and closed with no chosen model or active lookup");

                var catalog = DiscoveryPipelineChecks.FixtureCatalog();
                window.RenderDiscoveryReport(DiscoveryPipeline.Evaluate(catalog, DiscoveryPipelineChecks.FixtureMachine(), "C:\\"));
                grid.SelectedIndex = 0;
                check(open.IsEnabled && repository.Text == "" && reads == 0,
                    "Selecting a GGUF result enables explicit evidence inspection without fetching scores or selecting a benchmark source");
                window.OpenSelectedModelEvidence();
                check(repository.Text == Repository && expander.IsExpanded && reads == 0
                    && details.Text.Contains("No request has been sent"),
                    "Opening selected-model evidence copies only its exact repository and sends no metadata request");
                await window.ReadModelEvidenceAsync();
                check(reads == 1 && details.Text.Contains("Benchmark: Fixture/Benchmark")
                    && details.Text.Contains("Task: open_question") && details.Text.Contains("Reported value: 40")
                    && details.Text.Contains("Source verification flag: False") && details.Text.Contains("#46")
                    && details.Text.Contains("acceptance status is not established")
                    && details.Text.Contains("Exact evaluated weights/quantization are not established"),
                    "Explicit evidence lookup preserves exact task, raw score, reported false verification and PR attribution without certifying quality or quantization");

                var pendingInput = new TaskCompletionSource<ModelEvaluationSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
                window.ModelEvidenceReadOverride = (_, _) => pendingInput.Task; // Deliberately ignores cancellation.
                var inputRead = window.ReadModelEvidenceAsync();
                check(!read.IsEnabled && cancel.IsEnabled,
                    "An active benchmark read disables duplicate requests and exposes bounded cancellation");
                var waitingDetails = details.Text;
                window.OpenSelectedModelEvidence();
                check(!open.IsEnabled && details.Text == waitingDetails,
                    "Reopening evidence during an active read cannot replace its pending status with a misleading no-request message");
                repository.Text = "Fixture/Other";
                pendingInput.SetResult(Snapshot(Repository));
                await inputRead;
                check(repository.Text == "Fixture/Other" && !details.Text.Contains("Benchmark: Fixture/Benchmark")
                    && details.Text.Contains("Repository changed") && read.IsEnabled && !cancel.IsEnabled,
                    "Changing the repository during an awaited lookup prevents cancellation-ignoring stale evidence from reappearing");

                repository.Text = Repository;
                var pendingSelection = new TaskCompletionSource<ModelEvaluationSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
                window.ModelEvidenceReadOverride = (_, _) => pendingSelection.Task;
                var selectionRead = window.ReadModelEvidenceAsync();
                grid.SelectedIndex = -1;
                pendingSelection.SetResult(Snapshot(Repository));
                await selectionRead;
                check(repository.Text == "" && !expander.IsExpanded && !open.IsEnabled
                    && !details.Text.Contains("Benchmark: Fixture/Benchmark") && read.IsEnabled && !cancel.IsEnabled,
                    "Changing the chosen model revokes its evidence display and prevents a late response from restoring old scores");

                repository.Text = "Fixture/WithoutRows";
                window.ModelEvidenceReadOverride = (requested, _) => Task.FromResult(new ModelEvaluationSnapshot(
                    requested, [], DateTimeOffset.UtcNow, []));
                await window.ReadModelEvidenceAsync();
                check(details.Text.Contains("No evaluation rows") && details.Text.Contains("Quality remains unknown")
                    && details.Text.Contains("upstream model's scores are not substituted"),
                    "An exact repository without reported evaluations remains unknown rather than inheriting upstream quality scores");
                check(!window.OwnedLlamaServerRunning && window.GuidedModelPath is null
                    && Find<CheckBox>(window, "AcquisitionApprovalCheck").IsChecked != true
                    && Find<CheckBox>(window, "GuidedModelApprovalCheck").IsChecked != true,
                    "Benchmark browsing and cancellation grant no download approval, choose no runtime model and start no engine");
            });
        }
        finally { window.Close(); }
    }

    internal static async Task RunAsync(Action<bool, string> check)
    {
        var requests = new List<(HttpMethod Method, Uri? Uri, bool HasAuthorization, bool HasCookie)>();
        using (var http = new HttpClient(new Handler((request, _) =>
        {
            requests.Add((request.Method, request.RequestUri, request.Headers.Authorization is not null, request.Headers.Contains("Cookie")));
            return Task.FromResult(Json(Body()));
        })))
        {
            var result = await new ModelEvaluationClient(http).ReadAsync(Repository);
            var request = requests.Single();
            check(request.Method == HttpMethod.Get && request.Uri?.AbsoluteUri ==
                    "https://huggingface.co/api/models/fixture/model?expand=evalResults"
                && !request.HasAuthorization && !request.HasCookie
                && result.ModelRepository == Repository && result.Results.Count == 1,
                "Evaluation client makes one exact unauthenticated public model-metadata GET and retains reported evidence");
            check(requests.Count == 1 && result.Results[0].SourceUrl?.Host == "example.com",
                "Reported external benchmark attribution links are retained as data and are never followed by the metadata client");
            var invalid = await RejectsAsync<ArgumentException>(() => new ModelEvaluationClient(http).ReadAsync("https://example.com/model"));
            check(invalid && requests.Count == 1,
                "Invalid repository input is rejected before an evaluation HTTP request can be issued");
        }

        using (var http = new HttpClient(new Handler((_, _) => Task.FromResult(Json(Body("Fixture/Other"))))))
            check(await RejectsAsync<InvalidDataException>(() => new ModelEvaluationClient(http).ReadAsync(Repository)),
                "Evaluation client rejects source responses belonging to a different repository");
        using (var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect)
            { Headers = { Location = new Uri("https://example.com/alternate") } }))))
            check(await RejectsAsync<InvalidDataException>(() => new ModelEvaluationClient(http).ReadAsync(Repository)),
                "Evaluation redirects are rejected instead of substituting another evidence origin");
        foreach (var declared in new[] { true, false })
        {
            using var http = new HttpClient(new Handler((_, _) =>
            {
                // A custom stream without a content-length exercises the actual read bound.
                HttpContent body = declared ? new ByteArrayContent(new byte[1024 * 1024 + 1])
                    : new StreamContent(new NonSeekableStream(new byte[1024 * 1024 + 1]));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = body });
            }));
            check(await RejectsAsync<InvalidDataException>(() => new ModelEvaluationClient(http).ReadAsync(Repository)),
                "Evaluation metadata is bounded to one MiB with or without a declared content length");
        }
        var credentialRequests = 0;
        using (var http = new HttpClient(new Handler((_, _) =>
        {
            credentialRequests++;
            return Task.FromResult(Json(Body()));
        })))
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "synthetic-fixture-only");
            check(await RejectsAsync<InvalidOperationException>(() => new ModelEvaluationClient(http).ReadAsync(Repository))
                && credentialRequests == 0,
                "Evaluation lookup refuses a credential-bearing shared client before making any request");
        }
        using (var http = new HttpClient(new Handler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Json(Body());
        })))
        using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(30)))
            check(await RejectsAsync<OperationCanceledException>(() => new ModelEvaluationClient(http).ReadAsync(Repository, cancellation.Token)),
                "Caller cancellation interrupts evaluation metadata work without fabricating a successful evidence snapshot");
    }

    private static ModelEvaluationSnapshot Snapshot(string repository) => new(repository,
        [new(repository, "Fixture/Benchmark", "open_question", 40, false,
            DateTimeOffset.Parse("2026-06-02T09:57:40Z"), new Uri("https://example.com/results"), "Fixture source",
            ".eval_results/fixture.yaml", DateTimeOffset.UtcNow, 46)], DateTimeOffset.UtcNow, []);

    private static string Body(string repository = Repository) => "{\"id\":\"" + repository
        + "\",\"evalResults\":[{\"filename\":\".eval_results/fixture.yaml\",\"verified\":false,\"pullRequest\":46,"
        + "\"data\":{\"dataset\":{\"id\":\"Fixture/Benchmark\",\"task_id\":\"open_question\"},\"value\":40,"
        + "\"source\":{\"url\":\"https://example.com/results\",\"name\":\"Fixture source\"}}}]}";
    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
        { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private static T Find<T>(MainWindow window, string name) where T : class => (T)window.FindName(name);
    private static async Task<bool> RejectsAsync<T>(Func<Task<ModelEvaluationSnapshot>> action) where T : Exception
    { try { await action(); return false; } catch (T) { return true; } }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => action(request, cancellationToken);
    }
    private sealed class NonSeekableStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _inner.ReadAsync(buffer, cancellationToken);
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    }
}
