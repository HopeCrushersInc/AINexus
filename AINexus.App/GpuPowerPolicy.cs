using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace AINexus.App;

public sealed record GpuPowerEvidence(bool Supported, bool Verified, int? LimitWatts,
    string Message, DateTimeOffset CheckedAt);

/// <summary>Explicit NVIDIA power-cap enforcement. Queries and, when necessary, requests 250 W
/// by the observed GPU UUID. No elevation, account, service or persistent-startup changes occur.</summary>
public sealed class GpuPowerPolicy
{
    public const int RequiredWatts = 250;
    private const string Query = "--query-gpu=index,uuid,name,power.limit,enforced.power.limit,power.min_limit,power.max_limit";
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<string>> _run;

    public GpuPowerPolicy(Func<IReadOnlyList<string>, CancellationToken, Task<string>>? runner = null)
        => _run = runner ?? RunNvidiaSmiAsync;

    public async Task<GpuPowerEvidence> EnsureAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        IReadOnlyList<Adapter> before;
        try { before = Parse(await _run([Query, "--format=csv,noheader,nounits"], token).ConfigureAwait(false)); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { return Evidence(false, false, "NVIDIA power limits could not be read or validated. GPU launch is not approved by this check. " + Failure(ex)); }
        if (before.Count == 0)
            return Evidence(false, false, "No NVIDIA adapter power evidence was returned. No power setting was changed.");
        if (before.Any(adapter => adapter.Minimum > RequiredWatts || adapter.Maximum < RequiredWatts))
            return Evidence(false, false, "At least one NVIDIA adapter does not support the requested 250 W setting. No power setting was changed.");
        if (before.All(IsCapped))
            return Evidence(true, true, $"Verified 250 W configured and enforced on all {before.Count} NVIDIA adapter(s). No setting change was needed.");

        try
        {
            foreach (var adapter in before.Where(adapter => !IsCapped(adapter)))
            {
                token.ThrowIfCancellationRequested();
                await _run(["--id=" + adapter.Uuid, "--power-limit=250"], token).ConfigureAwait(false);
            }
            var after = Parse(await _run([Query, "--format=csv,noheader,nounits"], token).ConfigureAwait(false));
            if (after.Count != before.Count || after.Any(adapter => !before.Any(original => original.Uuid == adapter.Uuid))
                || after.Any(adapter => adapter.Minimum > RequiredWatts || adapter.Maximum < RequiredWatts || !IsCapped(adapter)))
                return Evidence(true, false, "The NVIDIA adapter identities or verified power limits changed after the request. The 250 W cap is not established for every adapter; do not start GPU work.");
            return Evidence(true, true, $"Verified 250 W configured and enforced on all {after.Count} NVIDIA adapter(s) after the explicit request. Recheck after reboot or driver reset.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Evidence(true, false, "The 250 W setting could not be applied and verified on every NVIDIA adapter. Some settings may already have changed; GPU work must wait. "
                + Failure(ex) + " If administrator permission is required, Windows can remain a local account; this check never elevates or changes accounts.");
        }
    }

    private static bool IsCapped(Adapter adapter) => adapter.Configured == RequiredWatts && adapter.Enforced == RequiredWatts;
    private static GpuPowerEvidence Evidence(bool supported, bool verified, string message)
        => new(supported, verified, verified ? RequiredWatts : null, message, DateTimeOffset.UtcNow);

    private sealed record Adapter(string Uuid, decimal Configured, decimal Enforced, decimal Minimum, decimal Maximum);

    private static IReadOnlyList<Adapter> Parse(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (output.Length > 65536) throw new InvalidDataException("Power-query output exceeds its bound.");
        var rows = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (rows.Length > 32) throw new InvalidDataException("Power-query adapter count exceeds its bound.");
        var result = new List<Adapter>();
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var fields = row.Split(',').Select(field => field.Trim()).ToArray();
            if (fields.Length != 7 || !int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out _)
                || !fields[1].StartsWith("GPU-", StringComparison.Ordinal) || !Guid.TryParseExact(fields[1][4..], "D", out var id)
                || string.IsNullOrWhiteSpace(fields[2]) || !identities.Add(id.ToString("D")))
                throw new InvalidDataException("Power-query adapter identity is missing, duplicated or malformed.");
            var power = fields.Skip(3).Select(value => decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var watts)
                ? watts : throw new InvalidDataException("Power-query limits are unavailable or malformed.")).ToArray();
            if (power[0] <= 0 || power[1] <= 0 || power[2] < 0 || power[3] <= 0 || power[2] > power[3])
                throw new InvalidDataException("Power-query limits are invalid.");
            result.Add(new("GPU-" + id.ToString("D"), power[0], power[1], power[2], power[3]));
        }
        return result.AsReadOnly();
    }

    private static string Failure(Exception exception)
    {
        var message = exception.Message.Replace('\r', ' ').Replace('\n', ' ');
        return message.Length <= 300 ? message : message[..300];
    }

    private static async Task<string> RunNvidiaSmiAsync(IReadOnlyList<string> arguments, CancellationToken token)
    {
        var executable = ResolveNvidiaSmi();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            }
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        token.ThrowIfCancellationRequested();
        if (!process.Start()) throw new InvalidOperationException("nvidia-smi did not start.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        Task? completion = null;
        try
        {
            var stdout = ReadBoundedAsync(process.StandardOutput, deadline.Token);
            var stderr = ReadBoundedAsync(process.StandardError, deadline.Token);
            completion = Task.WhenAll(process.WaitForExitAsync(deadline.Token), stdout, stderr);
            await completion.WaitAsync(deadline.Token).ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"nvidia-smi exited with code {process.ExitCode}. " + (await stderr.ConfigureAwait(false)).Trim());
            return (await stdout.ConfigureAwait(false)).Trim();
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException) { }
            if (completion is not null)
            {
                try { await completion.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { }
            }
            token.ThrowIfCancellationRequested();
            if (deadline.IsCancellationRequested) throw new TimeoutException("nvidia-smi exceeded its ten-second limit.");
            throw;
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken token)
    {
        var text = new StringBuilder();
        var buffer = new char[1024];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
        {
            if (text.Length + count > 65536) throw new InvalidDataException("nvidia-smi output exceeds its 64-KiB text bound.");
            text.Append(buffer, 0, count);
        }
        return text.ToString();
    }

    private static string ResolveNvidiaSmi()
    {
        var directories = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI")
        }.Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator));
        foreach (var directory in directories)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory)) continue;
            var path = Path.Combine(directory, "nvidia-smi.exe");
            if (File.Exists(path)) return Path.GetFullPath(path);
        }
        throw new FileNotFoundException("The NVIDIA driver utility nvidia-smi.exe is unavailable.");
    }
}
