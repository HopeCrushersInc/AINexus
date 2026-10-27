using System.IO;
using AINexus.App;

namespace AINexus.Checks;

internal static class GpuPowerPolicyChecks
{
    private const string First = "GPU-26ba1570-b097-3d0b-4b6f-90b1d7f1900a";
    private const string Second = "GPU-11111111-2222-3333-4444-555555555555";
    private static string Row(string uuid, int configured, int enforced, int minimum = 100, int maximum = 600)
        => $"0, {uuid}, NVIDIA fixture, {configured}, {enforced}, {minimum}, {maximum}";
    private static string Unavailable(string uuid) => $"1, {uuid}, NVIDIA unsupported fixture, [N/A], [N/A], [N/A], [N/A]";
    private static bool IsSetting(IReadOnlyList<string> arguments) => arguments[0].StartsWith("--id=", StringComparison.Ordinal);

    internal static async Task RunAsync(Action<bool, string> check)
    {
        var calls = new List<string[]>();
        var policy = new GpuPowerPolicy((arguments, _) => { calls.Add(arguments.ToArray()); return Task.FromResult(""); });
        var emptyApply = await policy.ApplyAsync([]);
        var emptyVerify = await policy.VerifyAsync([]);
        check(calls.Count == 0 && !emptyApply.Verified && !emptyVerify.Verified && emptyApply.LimitWatts is null,
            "No optional GPU policy runs no driver query or setter and invents no verified cap");

        policy = new GpuPowerPolicy((arguments, _) =>
        {
            calls.Add(arguments.ToArray());
            return Task.FromResult(Row(First, 180, 175, 100, 225) + "\n" + Unavailable(Second));
        });
        calls.Clear();
        var snapshot = await policy.InspectAsync();
        check(snapshot.Adapters.Count == 2 && snapshot.Adapters[0].ConfiguredWatts == 180
            && snapshot.Adapters[1].ConfiguredWatts is null && snapshot.Adapters[1].MinimumWatts is null
            && calls.Count == 1 && !IsSetting(calls[0]),
            "Read-only power inspection preserves per-adapter unknown fields without discarding a different GPU's readings");
        calls.Clear();
        var evidence = await policy.VerifyAsync([new(First, 180)]);
        check(evidence.Supported && evidence.Verified && evidence.LimitWatts == 180 && calls.Count == 1,
            "Selected-GPU verification accepts a stricter positive enforced limit and ignores an unselected unsupported adapter");
        calls.Clear();
        evidence = await policy.VerifyAsync([new(First, 190)]);
        check(!evidence.Verified && evidence.LimitWatts is null && calls.Count == 1 && calls.All(a => !IsSetting(a)),
            "A failed optional verification never applies a limit or runs a setter");

        calls.Clear();
        var queries = 0;
        policy = new GpuPowerPolicy((arguments, _) =>
        {
            calls.Add(arguments.ToArray());
            if (IsSetting(arguments)) return Task.FromResult("Set");
            return Task.FromResult((++queries == 1 ? Row(First, 225, 225, 100, 225) : Row(First, 180, 175, 100, 225)) + "\n" + Unavailable(Second));
        });
        evidence = await policy.ApplyAsync([new(First, 180)]);
        check(evidence.Verified && calls.Count == 3 && calls[1].SequenceEqual(new[] { "--id=" + First, "--power-limit=180" }),
            "An explicitly selected lower-power GPU receives its own supported wattage, never a universal cap or a change to another adapter");

        calls.Clear(); queries = 0;
        policy = new GpuPowerPolicy((arguments, _) =>
        {
            calls.Add(arguments.ToArray());
            if (IsSetting(arguments)) return Task.FromResult("Set");
            return Task.FromResult(++queries == 1
                ? Row(First, 225, 225, 100, 225) + "\n" + Row(Second, 450, 450, 100, 600)
                : Row(Second, 420, 410, 100, 600) + "\n" + Row(First, 180, 180, 100, 225));
        });
        evidence = await policy.ApplyAsync([new(First, 180), new(Second, 420)]);
        check(evidence.Verified && evidence.LimitWatts is null && calls.Count == 4
            && calls[1].SequenceEqual(new[] { "--id=" + First, "--power-limit=180" })
            && calls[2].SequenceEqual(new[] { "--id=" + Second, "--power-limit=420" }),
            "Different selected GPUs retain different requested limits and UUID readback works independently of query order");

        calls.Clear();
        policy = new GpuPowerPolicy((arguments, _) => { calls.Add(arguments.ToArray()); return Task.FromResult(Row(First, 180, 170)); });
        evidence = await policy.ApplyAsync([new(First, 180)]);
        check(evidence.Verified && calls.Count == 1 && !IsSetting(calls[0]),
            "An already-satisfied explicit request does not write hardware settings again");

        foreach (var invalid in new GpuPowerRequest[][]
        {
            [new(First, 0)], [new(First, -5)], [new("GPU-not-a-guid --power-limit=999", 180)],
            [new(First, 180), new(First.ToUpperInvariant(), 200)]
        })
        {
            calls.Clear(); var rejected = false;
            try { await policy.ApplyAsync(invalid); } catch (ArgumentException) { rejected = true; }
            check(rejected && calls.Count == 0,
                "Invalid or duplicate power requests are rejected before driver commands or argument interpolation");
        }

        calls.Clear();
        policy = new GpuPowerPolicy((arguments, _) =>
        { calls.Add(arguments.ToArray()); return Task.FromResult(Row(First, 225, 225, 100, 225) + "\n" + Row(Second, 450, 450)); });
        evidence = await policy.ApplyAsync([new(First, 180), new(Second, 650)]);
        check(!evidence.Supported && !evidence.Verified && calls.Count == 1,
            "Every selected adapter's supported range is validated before the first hardware write");
        calls.Clear();
        evidence = await policy.ApplyAsync([new(First, 90)]);
        check(!evidence.Verified && calls.Count == 1, "A selected wattage below the driver-reported minimum is rejected without mutation");

        calls.Clear();
        policy = new GpuPowerPolicy((arguments, _) => { calls.Add(arguments.ToArray()); return Task.FromResult(Row(Second, 450, 450)); });
        evidence = await policy.ApplyAsync([new(First, 180)]);
        check(!evidence.Verified && calls.Count == 1, "An absent selected UUID cannot redirect a setting to another GPU");
        calls.Clear();
        policy = new GpuPowerPolicy((arguments, _) => { calls.Add(arguments.ToArray()); return Task.FromResult(Unavailable(First) + "\n" + Row(Second, 450, 450)); });
        evidence = await policy.ApplyAsync([new(First, 180)]);
        check(!evidence.Supported && calls.Count == 1, "Unavailable power controls on the selected GPU do not authorize a speculative setting");

        foreach (var invalid in new[] { "malformed", Row(First, 180, 180) + "\n" + Row(First, 180, 180), Row(First, 180, 180, 300, 200), "" })
        {
            calls.Clear();
            policy = new GpuPowerPolicy((arguments, _) => { calls.Add(arguments.ToArray()); return Task.FromResult(invalid); });
            evidence = await policy.ApplyAsync([new(First, 180)]);
            check(!evidence.Verified && calls.Count == 1 && calls.All(a => !IsSetting(a)),
                "Missing, duplicate, malformed or inconsistent selected power evidence prevents all setting changes");
        }

        foreach (var after in new[] { Row(First, 180, 200), Row(First, 175, 175), Row(First, 180, 0), Row(Second, 180, 180) })
        {
            calls.Clear(); queries = 0;
            policy = new GpuPowerPolicy((arguments, _) =>
            {
                calls.Add(arguments.ToArray());
                return Task.FromResult(IsSetting(arguments) ? "Set" : ++queries == 1 ? Row(First, 225, 225) : after);
            });
            evidence = await policy.ApplyAsync([new(First, 180)]);
            check(!evidence.Verified && evidence.LimitWatts is null && calls.Count == 3,
                "A setter response alone cannot verify a changed UUID, wrong configured limit, zero reading or enforced limit above the request");
        }

        calls.Clear();
        policy = new GpuPowerPolicy((arguments, _) =>
        {
            calls.Add(arguments.ToArray());
            if (IsSetting(arguments)) throw new InvalidOperationException("Insufficient Permissions");
            return Task.FromResult(Row(First, 225, 225));
        });
        evidence = await policy.ApplyAsync([new(First, 180)]);
        check(evidence.Supported && !evidence.Verified && evidence.Message.Contains("Administrator")
            && evidence.Message.Contains("never elevates") && calls.Count == 2,
            "Denied explicit changes remain unverified and do not trigger elevation, account changes or a retry loop");

        calls.Clear();
        policy = new GpuPowerPolicy((arguments, _) =>
        {
            calls.Add(arguments.ToArray());
            if (IsSetting(arguments))
            {
                if (arguments[0] == "--id=" + Second) throw new InvalidOperationException("Second adapter failed");
                return Task.FromResult("Set");
            }
            return Task.FromResult(Row(First, 225, 225) + "\n" + Row(Second, 450, 450));
        });
        evidence = await policy.ApplyAsync([new(First, 180), new(Second, 420)]);
        check(!evidence.Verified && calls.Count == 3 && evidence.Message.Contains("Some selected settings may already have changed"),
            "Partial multi-GPU apply failure is reported honestly without claiming rollback or issuing extra hardware writes");

        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        calls.Clear(); var cancellationObserved = false;
        try { await policy.ApplyAsync([new(First, 180)], cancelled.Token); } catch (OperationCanceledException) { cancellationObserved = true; }
        check(cancellationObserved && calls.Count == 0, "Precancelled optional power control runs no driver command");
        using var during = new CancellationTokenSource(); calls.Clear();
        policy = new GpuPowerPolicy((arguments, token) =>
        {
            calls.Add(arguments.ToArray());
            if (!IsSetting(arguments)) return Task.FromResult(Row(First, 225, 225) + "\n" + Row(Second, 450, 450));
            during.Cancel(); return Task.FromCanceled<string>(token);
        });
        cancellationObserved = false;
        try { await policy.ApplyAsync([new(First, 180), new(Second, 420)], during.Token); } catch (OperationCanceledException) { cancellationObserved = true; }
        check(cancellationObserved && calls.Count == 2, "Cancellation during an explicit setting stops remaining selected writes and does not invent confirming readback");

        policy = new GpuPowerPolicy((_, _) => throw new FileNotFoundException("nvidia-smi unavailable"));
        snapshot = await policy.InspectAsync();
        check(snapshot.Adapters.Count == 0 && snapshot.Message.Contains("unavailable"),
            "Missing NVIDIA utility is an optional-controls limitation, not a fabricated adapter or cap");
    }
}
