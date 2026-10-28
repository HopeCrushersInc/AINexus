using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace AINexus.App;

public partial class MainWindow
{
    private CancellationTokenSource? _modelEvidenceOperation;
    private long _modelEvidenceGeneration;
    internal Func<string, CancellationToken, Task<ModelEvaluationSnapshot>>? ModelEvidenceReadOverride { get; set; }
    internal ModelEvaluationCache? ModelEvidenceCacheOverride { get; set; }
    private ModelEvaluationCache? CurrentModelEvidenceCache => ModelEvidenceCacheOverride
        ?? (_catalogDatabase is { } database
            ? new ModelEvaluationCache(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(database.DatabasePath)!, "Evaluations")) : null);

    private void InvalidateModelEvidenceForSelection()
    {
        _modelEvidenceGeneration++;
        _modelEvidenceOperation?.Cancel();
        if (ModelEvidenceRepositoryText is null) return;
        ModelEvidenceRepositoryText.Text = "";
        ModelEvidenceExpander.IsExpanded = false;
        ModelEvidenceDetailsText.Text = "Model selection or source results changed. Inspect the chosen repository explicitly; no quality score is carried over.";
    }

    private void DiscoveryEvidenceButton_Click(object sender, RoutedEventArgs e) => OpenSelectedModelEvidence();

    internal void OpenSelectedModelEvidence()
    {
        if (_discoveryBusy || _modelEvidenceOperation is not null
            || DiscoveryResultsGrid.SelectedItem is not DiscoveryRow { Recommendation.Item.Artifact: { Format: "GGUF" } artifact }) return;
        ModelEvidenceRepositoryText.Text = artifact.Repository;
        ModelEvidenceExpander.IsExpanded = true;
        ModelEvidenceDetailsText.Text = "The selected repository is ready for an explicit public evidence lookup. No request has been sent. Results for another repository must not be treated as measurements of this exact file.";
    }

    private void ModelEvidenceRepositoryText_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (ModelEvidenceDetailsText is null) return;
        _modelEvidenceGeneration++;
        _modelEvidenceOperation?.Cancel();
        ModelEvidenceDetailsText.Text = "Repository changed. Read fresh evidence for these exact inputs. No model or benchmark has been selected or verified by Nexus.";
    }

    private async void ModelEvidenceReadButton_Click(object sender, RoutedEventArgs e) => await ReadModelEvidenceAsync();
    private async void ModelEvidenceSavedButton_Click(object sender, RoutedEventArgs e) => await ReadModelEvidenceAsync(saved: true);
    private void ModelEvidenceCancelButton_Click(object sender, RoutedEventArgs e) => _modelEvidenceOperation?.Cancel();

    internal async Task ReadModelEvidenceAsync(bool saved = false)
    {
        if (_modelEvidenceOperation is not null || _lifetime.IsCancellationRequested) return;
        var repository = ModelEvidenceRepositoryText.Text.Trim();
        try { _ = ModelEvaluationClient.RequestUri(repository); }
        catch (ArgumentException ex) { ModelEvidenceDetailsText.Text = ex.Message; return; }
        var generation = ++_modelEvidenceGeneration;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _modelEvidenceOperation = cancellation;
        DiscoveryEvidenceButton.IsEnabled = false;
        ModelEvidenceReadButton.IsEnabled = false;
        ModelEvidenceSavedButton.IsEnabled = false;
        ModelEvidenceCancelButton.IsEnabled = true;
        ModelEvidenceDetailsText.Text = saved
            ? "Reading saved benchmark evidence for " + repository + ". No online request will be made."
            : "Reading public benchmark metadata for " + repository + ". No weights, inference requests or external source pages are fetched.";
        try
        {
            var cache = CurrentModelEvidenceCache;
            ModelEvaluationSnapshot? snapshot;
            if (saved)
            {
                if (cache is null) throw new InvalidOperationException("No catalog folder is configured for saved evidence");
                snapshot = await cache.LoadAsync(repository, cancellation.Token);
            }
            else
            {
                snapshot = ModelEvidenceReadOverride is { } read
                    ? await read(repository, cancellation.Token)
                    : await new ModelEvaluationClient(_discoveryHttp, cache).ReadAsync(repository, cancellation.Token);
            }
            cancellation.Token.ThrowIfCancellationRequested();
            if (generation != _modelEvidenceGeneration || ModelEvidenceRepositoryText.Text.Trim() != repository) return;
            if (snapshot is null)
            {
                ModelEvidenceDetailsText.Text = "No saved evidence exists for this exact repository. No online request was made; quality remains unknown.";
                return;
            }
            var text = new StringBuilder();
            text.AppendLine($"Repository: {snapshot.ModelRepository}\nRetrieved: {snapshot.RetrievedAt.ToLocalTime():g}");
            text.AppendLine(snapshot.IsCached ? "SAVED evidence as of the original retrieval time. No online request was made; the source may have changed."
                : cache is null ? "Live source snapshot; no catalog folder was available for saving."
                : "Live source snapshot. Saved-evidence status and any save failure are reported below.");
            text.AppendLine("Reported benchmark evidence, not a Nexus quality certification. Raw scores from different tasks are not directly comparable. Exact evaluated weights/quantization are not established.");
            if (snapshot.Results.Count == 0) text.AppendLine("No evaluation rows were returned for this repository. Quality remains unknown; an upstream model's scores are not substituted.");
            foreach (var result in snapshot.Results)
            {
                text.AppendLine($"\nBenchmark: {result.Dataset}\nTask: {result.Task}\nReported value: {result.Value:G} (scale not inferred)");
                text.AppendLine($"Source verification flag: {result.SourceVerified?.ToString() ?? "unknown"}; evaluation date: {result.EvaluatedAt?.ToString("u") ?? "unknown"}");
                text.AppendLine($"Source: {result.SourceName ?? "unknown"}\n{result.SourceUrl?.AbsoluteUri ?? "No valid source link reported"}\nEvidence filename: {(string.IsNullOrWhiteSpace(result.FileName) ? "unknown" : result.FileName)}");
                if (result.PullRequest is { } number) text.AppendLine($"Reported pull request: #{number}; acceptance status is not established by this response.");
            }
            foreach (var note in snapshot.Notes) text.AppendLine("\n" + note);
            ModelEvidenceDetailsText.Text = text.ToString();
        }
        catch (OperationCanceledException)
        {
            if (!_lifetime.IsCancellationRequested && generation == _modelEvidenceGeneration)
                ModelEvidenceDetailsText.Text = "Evidence lookup cancelled or timed out. No model was installed or started.";
        }
        catch (Exception ex)
        {
            if (!_lifetime.IsCancellationRequested && generation == _modelEvidenceGeneration)
                ModelEvidenceDetailsText.Text = (saved ? "Saved evidence unavailable: " : "Evidence unavailable: ") + ex.Message
                    + (saved ? ". No online request was made; quality remains unknown." : ". Quality remains unknown.");
        }
        finally
        {
            _modelEvidenceOperation = null;
            if (!_lifetime.IsCancellationRequested)
            {
                ModelEvidenceReadButton.IsEnabled = true;
                ModelEvidenceSavedButton.IsEnabled = true;
                ModelEvidenceCancelButton.IsEnabled = false;
                RefreshDiscoveryControls();
            }
        }
    }
}
