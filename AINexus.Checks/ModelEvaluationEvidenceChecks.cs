using System.IO;
using System.Text.Json;
using AINexus.App;

namespace AINexus.Checks;

internal static class ModelEvaluationEvidenceChecks
{
    private const string Model = "Fixture/Model";
    private static readonly DateTimeOffset Retrieved = DateTimeOffset.Parse("2026-10-28T10:00:00Z");

    internal static void Run(Action<bool, string> check)
    {
        var trueRow = Row("true", ",\"date\":\"2026-10-20T12:30:00Z\",\"source\":{\"url\":\"https://example.com/results\",\"name\":\"Fixture results\"}", ",\"pullRequest\":46");
        var evidence = Parse(trueRow + "," + Row("false") + "," + Row(null));
        check(evidence.Results.Count == 3 && evidence.Results.Select(row => row.SourceVerified).SequenceEqual(new bool?[] { true, false, null }),
            "Evaluation parsing distinguishes reported true, false and unknown verification flags without upgrading them to Nexus verification");
        var first = evidence.Results[0];
        check(first.ModelRepository == Model && first.Dataset == "Fixture/Benchmark" && first.Task == "reasoning"
            && first.Value == 42.5 && first.EvaluatedAt == DateTimeOffset.Parse("2026-10-20T12:30:00Z")
            && first.SourceUrl?.AbsoluteUri == "https://example.com/results" && first.SourceName == "Fixture results"
            && first.FileName == ".eval_results/fixture.yaml" && first.RetrievedAt == Retrieved && first.PullRequest == 46,
            "Evaluation evidence retains exact model, benchmark, task, raw metric, source, date, filename, retrieval time and submission PR provenance");
        check(evidence.Results.Skip(1).All(row => row.EvaluatedAt is null && row.SourceUrl is null && row.PullRequest is null)
            && evidence.Notes.Any(note => note.Contains("not transferred")) && evidence.Notes.Any(note => note.Contains("unversioned")),
            "Missing evaluation metadata remains unknown and evidence cannot establish related quantization scores or an evaluated model revision");

        var invalidRows = "null,{\"error\":\"source rejected\"}," + Row("true").Replace("42.5", "\"NaN\"")
            + "," + Row("true").Replace("42.5", "1e999") + "," + Row("true").Replace("Fixture/Benchmark", "bad")
            + "," + Row("true").Replace(".eval_results/fixture.yaml", "../outside.yaml") + "," + Row("true");
        var partial = Parse(invalidRows);
        check(partial.Results.Count == 1 && partial.Notes.Count(note => note.Contains("was skipped")) == 6,
            "Malformed, nonfinite, source-rejected and unsafe-path evaluation rows are skipped while valid evidence is preserved");

        var links = new[] { "http://example.com/results", "https://user:password@example.com/results", "https://localhost/results", "https://example.com/results?token=secret", "javascript:alert(1)" };
        var linkEvidence = Parse(string.Join(',', links.Select(link => Row("false", ",\"source\":{\"url\":\"" + link + "\"}"))));
        check(linkEvidence.Results.Count == links.Length && linkEvidence.Results.All(row => row.SourceUrl is null)
            && linkEvidence.Notes.Count(note => note.Contains("no link was fetched")) == links.Length,
            "Unsafe citation URLs are omitted without fetching them or discarding otherwise valid benchmark scores");

        var unknownDate = Parse(Row("\"true\"", ",\"date\":\"not a date\""));
        check(unknownDate.Results.Single() is { SourceVerified: null, EvaluatedAt: null },
            "String verification claims and malformed dates do not become asserted verification or invented evaluation dates");
        check(Parse(Row(null, ",\"date\":\"13:30\"")).Results.Single().EvaluatedAt is null
            && Parse(Row(null, ",\"date\":\"2026-10-20\"")).Results.Single().EvaluatedAt == DateTimeOffset.Parse("2026-10-20T00:00:00Z"),
            "Evaluation dates accept explicit ISO dates while rejecting clock-only values that would invent today's date");
        var bounded = Parse(string.Join(',', Enumerable.Repeat(Row("false"), 40)));
        check(bounded.Results.Count == ModelEvaluationParser.MaximumRows && bounded.Notes.Any(note => note.Contains("first 32")),
            "Evaluation evidence parsing inspects at most thirty-two source rows and marks partial coverage");

        using (var absent = JsonDocument.Parse("{\"id\":\"" + Model + "\"}"))
        {
            var none = ModelEvaluationParser.Parse(Model, absent.RootElement, Retrieved);
            check(none.Results.Count == 0 && none.Notes.Any(note => note.Contains("no evaluation rows")),
                "An exact repository without evalResults remains unavailable rather than borrowing upstream scores");
        }
        var mismatchRejected = false;
        using (var wrong = JsonDocument.Parse("{\"id\":\"Other/Model\",\"evalResults\":[]}"))
        {
            try { ModelEvaluationParser.Parse(Model, wrong.RootElement, Retrieved); }
            catch (InvalidDataException) { mismatchRejected = true; }
        }
        check(mismatchRejected, "An evaluation response for another repository is rejected rather than attributed to the selected model");
        var oversized = Parse(Row("false").Replace("reasoning", new string('x', 257)) + "," + Row("false"));
        check(oversized.Results.Count == 1, "Oversized evaluation strings are bounded without discarding later valid source rows");
    }

    private static string Row(string? verified, string dataExtra = "", string rowExtra = "") =>
        "{\"filename\":\".eval_results/fixture.yaml\"" + (verified is null ? "" : ",\"verified\":" + verified)
        + ",\"data\":{\"dataset\":{\"id\":\"Fixture/Benchmark\",\"task_id\":\"reasoning\"},\"value\":42.5" + dataExtra + "}" + rowExtra + "}";

    private static ModelEvaluationSnapshot Parse(string rows)
    {
        using var doc = JsonDocument.Parse("{\"id\":\"" + Model + "\",\"evalResults\":[" + rows + "]}");
        return ModelEvaluationParser.Parse(Model, doc.RootElement, Retrieved);
    }
}
