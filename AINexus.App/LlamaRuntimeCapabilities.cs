using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace AINexus.App;

/// <summary>Bounded capability preflight for an explicitly selected local runtime; never loads a model.</summary>
internal static partial class LlamaRuntimeCapabilities
{
    internal static bool AdvertisesOffline(string help) => OfflineOption().IsMatch(help);
    [GeneratedRegex(@"(?m)^\s*--offline(?:\s|$)", RegexOptions.CultureInvariant)] private static partial Regex OfflineOption();

    internal static async Task RequireOfflineSupportAsync(string executable, string model, int context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ValidateOfflinePaths(executable, model);
        await LlamaServerController.ValidateConfigurationAsync(executable, model, 1024, context, token).ConfigureAwait(false);
        using var process = new Process { StartInfo = LlamaServerController.CreateStartInfo(executable, model, 1024, context) };
        process.StartInfo.ArgumentList.Clear();
        process.StartInfo.ArgumentList.Add("--help");
        token.ThrowIfCancellationRequested();
        if (!process.Start()) throw new InvalidOperationException("The selected runtime help query could not start.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var stdout = ReadBoundedHelpAsync(process.StandardOutput, timeout.Token);
        var stderr = ReadBoundedHelpAsync(process.StandardError, timeout.Token);
        var all = Task.WhenAll(process.WaitForExitAsync(timeout.Token), stdout, stderr);
        try
        {
            await all.WaitAsync(timeout.Token).ConfigureAwait(false);
            if (process.ExitCode != 0) throw new InvalidOperationException("The selected runtime help query failed.");
            RequireAdvertisedOffline(await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            try { await all.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { }
            if (!all.IsCompleted) _ = all.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
            token.ThrowIfCancellationRequested();
            throw new TimeoutException("The selected runtime help query exceeded its time limit.");
        }
    }

    internal static void RequireAdvertisedOffline(string stdout, string stderr)
    {
        if (!AdvertisesOffline(stdout) && !AdvertisesOffline(stderr))
            throw new NotSupportedException("The selected runtime does not advertise --offline. Select a compatible runtime; no model was launched. Online fallback is disabled.");
    }

    internal static void ValidateOfflinePaths(string executable, string model)
    {
        OfflineModelVerifier.CheckNoReparsePath(executable);
        OfflineModelVerifier.CheckNoReparsePath(model);
    }

    private static async Task<string> ReadBoundedHelpAsync(StreamReader reader, CancellationToken token)
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            if (text.Length < 262144) text.Append(buffer, 0, Math.Min(read, 262144 - text.Length));
        }
        return text.ToString();
    }

}
