using AINexus.App;

namespace AINexus.Checks;

internal static class GpuPowerPolicyChecks
{
    private const string First = "GPU-26ba1570-b097-3d0b-4b6f-90b1d7f1900a";
    private const string Second = "GPU-11111111-2222-3333-4444-555555555555";
    private static string Row(string uuid, int configured, int enforced, int minimum = 100, int maximum = 350)
        => $"0, {uuid}, NVIDIA fixture, {configured}, {enforced}, {minimum}, {maximum}";

    internal static async Task RunAsync(Action<bool, string> check)
    {
        var calls = new List<string[]>();
        var policy = new GpuPowerPolicy((arguments, _) =>
        {
            calls.Add(arguments.ToArray());
            return Task.FromResult(Row(First, 250, 250));
        });
        var evidence = await policy.EnsureAsync();
        check(evidence.Supported && evidence.Verified && evidence.LimitWatts == 250 && calls.Count == 1,
            "An already-enforced 250 W cap is verified without changing GPU settings");

        calls.Clear();
        policy = new GpuPowerPolicy((arguments, _) =>
        {
            calls.Add(arguments.ToArray());
            return Task.FromResult(calls.Count == 1 ? Row(First, 350, 350)
                : arguments[0].StartsWith("--id=", StringComparison.Ordinal) ? "Power limit set" : Row(First, 250, 250));
        });
        evidence = await policy.EnsureAsync();
        check(evidence.Verified && calls.Count == 3 && calls[1].SequenceEqual(new[] { "--id=" + First, "--power-limit=250" }),
            "GPU power changes target the observed stable UUID and require a confirming query");

        var queried = false;
        policy = new GpuPowerPolicy((arguments, _) =>
        {
            if (arguments[0].StartsWith("--id=", StringComparison.Ordinal)) return Task.FromResult("Set");
            var answer = queried ? Row(Second, 250, 250) : Row(First, 350, 350);
            queried = true;
            return Task.FromResult(answer);
        });
        evidence = await policy.EnsureAsync();
        check(evidence.Supported && !evidence.Verified && evidence.LimitWatts is null,
            "A changed GPU identity cannot satisfy post-change power verification");

        var queryCount = 0;
        policy = new GpuPowerPolicy((arguments, _) =>
        {
            if (arguments[0].StartsWith("--id=", StringComparison.Ordinal)) return Task.FromResult("Set");
            queryCount++;
            return Task.FromResult(Row(First, queryCount == 1 ? 350 : 250, 350));
        });
        check(!(await policy.EnsureAsync()).Verified,
            "A setter success without an enforced 250 W reading cannot approve GPU work");

        foreach (var invalid in new[] { "malformed", Row(First, 250, 250).Replace("250", "[N/A]"), Row(First, 250, 250) + "\n" + Row(First, 250, 250) })
        {
            var count = 0;
            policy = new GpuPowerPolicy((_, _) => { count++; return Task.FromResult(invalid); });
            evidence = await policy.EnsureAsync();
            check(!evidence.Supported && !evidence.Verified && count == 1,
                "Malformed, unavailable or duplicate GPU evidence prevents all setting changes");
        }

        var settingCalls = 0;
        policy = new GpuPowerPolicy((_, _) =>
        {
            settingCalls++;
            return Task.FromResult(Row(First, 250, 250) + "\n" + Row(Second, 200, 200, 100, 200));
        });
        evidence = await policy.EnsureAsync();
        check(!evidence.Supported && !evidence.Verified && settingCalls == 1,
            "Every adapter must support 250 W before any multi-GPU mutation is attempted");

        policy = new GpuPowerPolicy((arguments, _) => arguments[0].StartsWith("--id=", StringComparison.Ordinal)
            ? throw new InvalidOperationException("Insufficient Permissions") : Task.FromResult(Row(First, 350, 350)));
        evidence = await policy.EnsureAsync();
        check(evidence.Supported && !evidence.Verified && evidence.Message.Contains("administrator")
            && evidence.Message.Contains("local account"),
            "Permission failure blocks verified status without requesting Microsoft sign-in or silently elevating");

        calls.Clear();
        var initial = true;
        policy = new GpuPowerPolicy((arguments, _) =>
        {
            calls.Add(arguments.ToArray());
            if (arguments[0].StartsWith("--id=", StringComparison.Ordinal)) return Task.FromResult("Set");
            var output = initial ? Row(First, 250, 250) + "\n" + Row(Second, 350, 350)
                : Row(Second, 250, 250) + "\n" + Row(First, 250, 250);
            initial = false;
            return Task.FromResult(output);
        });
        evidence = await policy.EnsureAsync();
        check(evidence.Verified && calls.Count == 3 && calls[1][0] == "--id=" + Second,
            "Multi-GPU enforcement changes only uncapped UUIDs and verifies the full set regardless of query order");

        policy = new GpuPowerPolicy((_, _) => Task.FromResult(""));
        evidence = await policy.EnsureAsync();
        check(!evidence.Supported && !evidence.Verified && evidence.LimitWatts is null,
            "No GPU evidence yields no fictitious power limit or verification");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var attempted = false;
        policy = new GpuPowerPolicy((_, _) => { attempted = true; return Task.FromResult(""); });
        var cancellationObserved = false;
        try { await policy.EnsureAsync(cancelled.Token); }
        catch (OperationCanceledException) { cancellationObserved = true; }
        check(cancellationObserved && !attempted,
            "Precancelled power policy runs no query or setting command");

        using var duringSetting = new CancellationTokenSource();
        var inFlightCalls = 0;
        policy = new GpuPowerPolicy((arguments, token) =>
        {
            inFlightCalls++;
            if (!arguments[0].StartsWith("--id=", StringComparison.Ordinal)) return Task.FromResult(Row(First, 350, 350));
            duringSetting.Cancel();
            return Task.FromCanceled<string>(token);
        });
        cancellationObserved = false;
        try { await policy.EnsureAsync(duringSetting.Token); }
        catch (OperationCanceledException) { cancellationObserved = true; }
        check(cancellationObserved && inFlightCalls == 2,
            "Cancellation during a setting request propagates without reporting verification or issuing a follow-up query");
    }
}
