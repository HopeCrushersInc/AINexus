using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace AINexus.App;

public sealed record GpuPowerEvidence(bool Supported, bool Verified, int? LimitWatts,
    string Message, DateTimeOffset CheckedAt);
public sealed record GpuPowerRequest(string Uuid, int Watts);
public sealed record GpuPowerAdapter(string Uuid, string Name, decimal? ConfiguredWatts,
    decimal? EnforcedWatts, decimal? MinimumWatts, decimal? MaximumWatts);
public sealed record GpuPowerSnapshot(IReadOnlyList<GpuPowerAdapter> Adapters, string Message, DateTimeOffset CheckedAt);

/// <summary>Optional NVIDIA power controls. Inspection and verification are read-only;
/// only ApplyAsync changes explicitly requested UUIDs. There is no default wattage,
/// launch gate, automatic restoration, elevation or persistent-startup mutation.</summary>
public sealed class GpuPowerPolicy
{
    private const string Query = "--query-gpu=index,uuid,name,power.limit,enforced.power.limit,power.min_limit,power.max_limit";
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<string>> _run;

    public GpuPowerPolicy(Func<IReadOnlyList<string>, CancellationToken, Task<string>>? runner = null)
        => _run = runner ?? RunNvidiaSmiAsync;

    public async Task<GpuPowerSnapshot> InspectAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            var adapters = await ReadAsync(token).ConfigureAwait(false);
            return new(adapters, adapters.Count == 0
                ? "No NVIDIA adapter power information was returned. Existing settings were not changed."
                : "Current NVIDIA power readings. Unavailable values remain unknown. No setting was changed.", DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { return new([], "NVIDIA power information is unavailable. Existing settings were not changed. " + Failure(ex), DateTimeOffset.UtcNow); }
    }

    public Task<GpuPowerEvidence> VerifyAsync(IReadOnlyList<GpuPowerRequest> requests, CancellationToken token = default)
        => EvaluateAsync(requests, apply: false, token);

    public Task<GpuPowerEvidence> ApplyAsync(IReadOnlyList<GpuPowerRequest> requests, CancellationToken token = default)
        => EvaluateAsync(requests, apply: true, token);

    private async Task<GpuPowerEvidence> EvaluateAsync(IReadOnlyList<GpuPowerRequest> requests, bool apply, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var selected = ValidateRequests(requests);
        if (selected.Length == 0)
            return Evidence(false, false, selected, "No GPU power limits were requested. Existing hardware settings were left unchanged; no driver command was run.");
        IReadOnlyList<GpuPowerAdapter> before;
        try { before = await ReadAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { return Evidence(false, false, selected, "Selected GPU power information could not be read. No setting was changed. " + Failure(ex)); }
        foreach (var request in selected)
        {
            var adapter = Find(before, request.Uuid);
            if (adapter is null)
                return Evidence(false, false, selected, "A requested GPU UUID is not present in the current driver readings. No setting was changed.");
            if (!HasControlReadings(adapter) || request.Watts < adapter.MinimumWatts || request.Watts > adapter.MaximumWatts)
                return Evidence(false, false, selected, "A selected adapter has unavailable/inconsistent power readings or does not support the requested watts. No setting was changed.");
        }
        if (selected.All(request => Satisfies(Find(before, request.Uuid)!, request)))
            return Evidence(true, true, selected, "Verified the requested limits on the selected GPU UUIDs. No setting change was needed.");
        if (!apply)
            return Evidence(true, false, selected, "The selected GPUs do not currently satisfy the requested limits. Verification is read-only; no setting was changed.");

        try
        {
            foreach (var request in selected)
            {
                token.ThrowIfCancellationRequested();
                if (Satisfies(Find(before, request.Uuid)!, request)) continue;
                await _run(["--id=" + request.Uuid, "--power-limit=" + request.Watts.ToString(CultureInfo.InvariantCulture)], token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            var after = await ReadAsync(token).ConfigureAwait(false);
            if (!selected.All(request => Find(after, request.Uuid) is { } adapter && HasControlReadings(adapter)
                && request.Watts >= adapter.MinimumWatts && request.Watts <= adapter.MaximumWatts && Satisfies(adapter, request)))
                return Evidence(true, false, selected, "A selected GPU disappeared or its requested power limit could not be confirmed. Some selected settings may already have changed; inspect the current readings.");
            return Evidence(true, true, selected, "Applied and verified the requested limits on selected GPU UUIDs. Unselected adapters were not changed. Reinspect after a reboot or driver reset.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Evidence(true, false, selected, "A requested GPU setting could not be applied and verified. Some selected settings may already have changed; inspect the current readings. "
                + Failure(ex) + " Administrator permission may be required; this operation never elevates or changes accounts.");
        }
    }

    private async Task<IReadOnlyList<GpuPowerAdapter>> ReadAsync(CancellationToken token)
    {
        var output = await _run([Query, "--format=csv,noheader,nounits"], token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return Parse(output);
    }

    private static GpuPowerRequest[] ValidateRequests(IReadOnlyList<GpuPowerRequest> requests)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count > 32) throw new ArgumentException("Choose at most 32 GPU UUIDs.", nameof(requests));
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return requests.Select(request =>
        {
            if (request is null || request.Watts <= 0 || !TryUuid(request.Uuid, out var uuid) || !identities.Add(uuid))
                throw new ArgumentException("GPU requests need unique observed UUIDs and a positive whole-number wattage.", nameof(requests));
            return request with { Uuid = uuid };
        }).ToArray();
    }

    private static GpuPowerAdapter? Find(IReadOnlyList<GpuPowerAdapter> adapters, string uuid)
        => adapters.FirstOrDefault(adapter => adapter.Uuid.Equals(uuid, StringComparison.OrdinalIgnoreCase));
    private static bool HasControlReadings(GpuPowerAdapter adapter) => adapter.MinimumWatts is >= 0
        && adapter.MaximumWatts is > 0 && adapter.MinimumWatts <= adapter.MaximumWatts
        && adapter.ConfiguredWatts is > 0 && adapter.EnforcedWatts is > 0;
    private static bool Satisfies(GpuPowerAdapter adapter, GpuPowerRequest request)
        => adapter.ConfiguredWatts == request.Watts && adapter.EnforcedWatts is > 0 && adapter.EnforcedWatts <= request.Watts;
    private static GpuPowerEvidence Evidence(bool supported, bool verified, IReadOnlyList<GpuPowerRequest> requests, string message)
        => new(supported, verified, verified && requests.Select(request => request.Watts).Distinct().Count() == 1 ? requests[0].Watts : null,
            message, DateTimeOffset.UtcNow);
    private static bool TryUuid(string? value, out string uuid)
    {
        uuid = "";
        if (value is null || !value.StartsWith("GPU-", StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(value[4..], "D", out var id)) return false;
        uuid = "GPU-" + id.ToString("D");
        return true;
    }

    private static IReadOnlyList<GpuPowerAdapter> Parse(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (output.Length > 65536) throw new InvalidDataException("Power-query output exceeds its bound.");
        var rows = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (rows.Length > 32) throw new InvalidDataException("Power-query adapter count exceeds its bound.");
        var result = new List<GpuPowerAdapter>();
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var fields = row.Split(',').Select(field => field.Trim()).ToArray();
            if (fields.Length != 7 || !int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out _)
                || !TryUuid(fields[1], out var uuid) || string.IsNullOrWhiteSpace(fields[2]) || !identities.Add(uuid))
                throw new InvalidDataException("Power-query adapter identity is missing, duplicated or malformed.");
            var power = fields.Skip(3).Select(value => decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var watts)
                ? (decimal?)watts : null).ToArray();
            result.Add(new(uuid, fields[2], power[0], power[1], power[2], power[3]));
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
