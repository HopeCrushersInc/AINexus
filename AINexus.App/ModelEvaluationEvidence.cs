using System.Globalization;
using System.IO;
using System.Text.Json;

namespace AINexus.App;

/// <summary>Source-reported evidence for exactly one repository, never an inherited score for a quantization.</summary>
public sealed record ModelEvaluationEvidence(string ModelRepository, string Dataset, string Task, double Value,
    bool? SourceVerified, DateTimeOffset? EvaluatedAt, Uri? SourceUrl, string? SourceName, string FileName,
    DateTimeOffset RetrievedAt, int? PullRequest = null);

public sealed record ModelEvaluationSnapshot(string ModelRepository, IReadOnlyList<ModelEvaluationEvidence> Results,
    DateTimeOffset RetrievedAt, IReadOnlyList<string> Notes, bool IsCached = false);

/// <summary>
/// Pure bounded parsing of the model-centric Hub evalResults API. Source documents and URLs are data only.
/// Format: https://huggingface.co/docs/hub/leaderboard-data-guide and
/// https://github.com/huggingface/huggingface_hub/blob/main/src/huggingface_hub/_eval_results.py.
/// </summary>
public static class ModelEvaluationParser
{
    public const int MaximumRows = 32;

    public static ModelEvaluationSnapshot Parse(string expectedRepository, JsonElement root, DateTimeOffset retrievedAt)
    {
        if (!Repository(expectedRepository)) throw new ArgumentException("Choose an exact owner/repository identity.", nameof(expectedRepository));
        if (retrievedAt == DateTimeOffset.MinValue) throw new ArgumentException("An actual retrieval timestamp is required.", nameof(retrievedAt));
        if (root.ValueKind != JsonValueKind.Object
            || (root.TryGetProperty("id", out _) ? String(root, "id", 257) : String(root, "modelId", 257)) != expectedRepository)
            throw new InvalidDataException("Evaluation metadata does not match the exact requested model repository.");

        var results = new List<ModelEvaluationEvidence>();
        var notes = new List<string>
        {
            "Scores are source-reported benchmark/task values, not a universal best-model ranking or a Nexus evaluation.",
            "Evidence applies only to the named repository. It is not transferred to related models or quantized artifacts, and does not establish local hardware fit or generation quality.",
            "The source verification flag is retained as reported; it is not independent verification by Nexus. A PR number does not establish whether a submission was accepted or remains pending.",
            "This model-centric response is an unversioned source snapshot; no evaluated model revision or exact artifact hash is established. Missing dates and verification flags remain unknown."
        };
        if (!root.TryGetProperty("evalResults", out var rows) || rows.ValueKind == JsonValueKind.Null)
        {
            notes.Add("The source supplied no evaluation rows for this exact repository. No upstream or alternative model's scores were substituted.");
            return Snapshot();
        }
        if (rows.ValueKind != JsonValueKind.Array) throw new InvalidDataException("The source evaluation field is not a list.");
        if (rows.GetArrayLength() > MaximumRows) notes.Add($"Only the first {MaximumRows} source rows were inspected; evaluation results are partial.");
        var index = 0;
        foreach (var row in rows.EnumerateArray().Take(MaximumRows))
        {
            index++;
            if (row.ValueKind != JsonValueKind.Object || row.TryGetProperty("error", out _)) { Skip(); continue; }
            var data = row.TryGetProperty("data", out var nested) ? nested : row;
            if (data.ValueKind != JsonValueKind.Object
                || !data.TryGetProperty("dataset", out var dataset) || dataset.ValueKind != JsonValueKind.Object
                || String(dataset, "id", 257) is not { } datasetId || !Repository(datasetId)
                || String(dataset, "task_id", 256) is not { } task
                || !data.TryGetProperty("value", out var metric) || metric.ValueKind != JsonValueKind.Number
                || !metric.TryGetDouble(out var value) || !double.IsFinite(value)) { Skip(); continue; }

            var filename = String(row, "filename", 512) ?? "";
            if (row.TryGetProperty("filename", out var suppliedFilename) && suppliedFilename.ValueKind != JsonValueKind.Null
                && (filename.Length == 0 || !SafeEvidencePath(filename))) { Skip(); continue; }
            bool? verified = row.TryGetProperty("verified", out var flag) ? flag.ValueKind switch
            { JsonValueKind.True => true, JsonValueKind.False => false, _ => null } : null;
            var rawDate = String(data, "date", 64);
            DateTimeOffset? evaluatedAt = rawDate is not null
                && DateTimeOffset.TryParseExact(rawDate,
                    ["yyyy-MM-dd", "yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
                        "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz"],
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
                ? date : null;
            if (data.TryGetProperty("date", out var dateValue) && dateValue.ValueKind != JsonValueKind.Null && evaluatedAt is null)
                notes.Add($"Evaluation row {index}: an invalid or oversized date was left unknown.");

            Uri? sourceUrl = null;
            string? sourceName = null;
            if (data.TryGetProperty("source", out var source) && source.ValueKind != JsonValueKind.Null)
            {
                if (source.ValueKind != JsonValueKind.Object) { Skip(); continue; }
                sourceName = String(source, "name", 256);
                var rawUrl = String(source, "url", 2048);
                if (rawUrl is not null && Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri)
                    && uri.Scheme == "https" && uri.UserInfo.Length == 0 && !uri.IsLoopback
                    && uri.Query.Length == 0 && uri.Fragment.Length == 0) sourceUrl = uri;
                else notes.Add($"Evaluation row {index}: an invalid, credential-bearing, loopback or query-bearing source link was omitted; no link was fetched.");
            }
            int? pullRequest = row.TryGetProperty("pullRequest", out var pr) && pr.ValueKind == JsonValueKind.Number
                && pr.TryGetInt32(out var number) && number > 0 ? number : null;
            results.Add(new(expectedRepository, datasetId, task, value, verified, evaluatedAt, sourceUrl, sourceName,
                filename, retrievedAt, pullRequest));
        }
        if (results.Count == 0) notes.Add("No usable source evaluation rows were available. This is not a quality verdict.");
        return Snapshot();

        void Skip() => notes.Add($"Evaluation row {index} was invalid or source-rejected and was skipped; no score was guessed.");
        ModelEvaluationSnapshot Snapshot() => new(expectedRepository, results.AsReadOnly(), retrievedAt, notes.AsReadOnly());
    }

    private static string? String(JsonElement value, string property, int maximum) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var item)
        && item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text
        && text.Length <= maximum && !string.IsNullOrWhiteSpace(text) && !text.Any(char.IsControl) ? text : null;

    private static bool Repository(string? value) => value is { Length: <= 257 }
        && value.Split('/') is [var owner, var name] && Segment(owner) && Segment(name);
    private static bool Segment(string value) => value.Length is > 0 and <= 128 && value is not ("." or "..")
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
    private static bool SafeEvidencePath(string value) => !value.Contains('\\') && !value.Contains(':')
        && value.Split('/').All(segment => segment.Length > 0 && segment is not ("." or ".."));
}
