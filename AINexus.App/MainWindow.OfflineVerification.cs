using System.Text;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace AINexus.App;

public partial class MainWindow
{
    private CancellationTokenSource? _offlineOperation;
    private bool _offlineBusy;
    private bool _closeAfterOffline;

    private void InitializeOfflineVerification()
    {
        SetOfflineControls(true);
        Closing += (_, args) =>
        {
            if (args.Cancel || !_offlineBusy) return;
            args.Cancel = true;
            _closeAfterOffline = true;
            OfflineStatusText.Text = "Cancelling verification and stopping its owned server before closing...";
            _offlineOperation?.Cancel();
        };
    }

    private void OfflineReceiptBrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Select a saved model acquisition receipt", Filter = "Acquisition receipt (*.json)|*.json", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) OfflineReceiptText.Text = dialog.FileName;
    }
    private void OfflineRuntimeBrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Select an existing portable llama.cpp runtime", Filter = "llama.cpp server|llama-server.exe", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) OfflineRuntimeText.Text = dialog.FileName;
    }

    private async void OfflineAuditButton_Click(object sender, RoutedEventArgs e) => await RunOfflineOperationAsync(async token =>
    {
        OfflineStatusText.Text = "Auditing the saved receipt and hashing actual local model bytes. No runtime will start...";
        var audit = await OfflineModelVerifier.AuditReceiptAsync(OfflineReceiptText.Text.Trim(), token);
        var plan = audit.Receipt.Plan;
        OfflineEvidenceText.Text = $"LOCAL ARTIFACT AUDIT\nModel: {plan.Name}\nPath: {plan.DestinationPath}\n" +
            $"Recorded revision: {plan.Artifact.Revision}\nExpected SHA-256: {plan.Artifact.Sha256}\nActual SHA-256: {audit.Integrity.ActualSha256}\n" +
            $"Actual bytes: {audit.Integrity.ActualBytes:N0}\nMatch: {audit.Integrity.Matches}\nAudited: {audit.AuditedAt.ToLocalTime():g}\n\n" +
            audit.Integrity.Description + "\n\nThis audit does not run inference or refresh the publisher's online metadata.";
        OfflineStatusText.Text = audit.Integrity.Matches ? "Local file integrity matches the saved source identity. Runtime execution is not established by this audit."
            : "Local file integrity did not match the saved identity. No runtime was started.";
    });

    private async void OfflineRunButton_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(OfflineContextText.Text, out var context) || context is < 512 or > 32768)
        {
            OfflineStatusText.Text = "Choose a context policy between 512 and 32768 tokens.";
            return;
        }
        var receipt = OfflineReceiptText.Text.Trim();
        var runtime = OfflineRuntimeText.Text.Trim();
        await RunOfflineOperationAsync(async token =>
        {
            OfflineStatusText.Text = "Checking local artifact/runtime identities and current resources. If admitted, one hidden offline-mode server will load this model for a short smoke test...";
            var evidence = await OfflineModelVerifier.VerifyAsync(receipt, runtime, contextSize: context, cancellationToken: token);
            RenderOfflineVerification(evidence);
        });
    }

    internal async Task RunOfflineOperationAsync(Func<CancellationToken, Task> operation)
    {
        if (_offlineBusy || _lifetime.IsCancellationRequested) return;
        _offlineBusy = true;
        OfflineEvidenceText.Clear();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _offlineOperation = cancellation;
        SetOfflineControls(false);
        try { await operation(cancellation.Token); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            OfflineEvidenceText.Clear();
            if (!_lifetime.IsCancellationRequested) OfflineStatusText.Text = "Verification cancelled. Its owned server was stopped; external servers and model files were preserved.";
        }
        catch (Exception ex)
        {
            OfflineEvidenceText.Clear();
            if (!_lifetime.IsCancellationRequested) OfflineStatusText.Text = $"Verification could not complete: {ex.Message}";
        }
        finally
        {
            _offlineBusy = false;
            _offlineOperation = null;
            if (!_lifetime.IsCancellationRequested) SetOfflineControls(true);
            if (_closeAfterOffline && !_lifetime.IsCancellationRequested)
            {
                _closeAfterOffline = false;
                Close();
            }
        }
    }

    private void SetOfflineControls(bool enabled)
    {
        OfflineReceiptText.IsEnabled = OfflineRuntimeText.IsEnabled = OfflineContextText.IsEnabled = enabled;
        OfflineReceiptBrowseButton.IsEnabled = OfflineRuntimeBrowseButton.IsEnabled = enabled;
        OfflineAuditButton.IsEnabled = enabled && LocalFileExists(OfflineReceiptText.Text);
        OfflineRunButton.IsEnabled = enabled && LocalFileExists(OfflineReceiptText.Text) && LocalFileExists(OfflineRuntimeText.Text)
            && int.TryParse(OfflineContextText.Text, out var context) && context is >= 512 and <= 32768;
        OfflineCancelButton.IsEnabled = !enabled;
    }
    private void OfflineInputChanged(object sender, TextChangedEventArgs e)
    {
        // TextChanged also fires during XAML initialization, before later controls exist.
        if (OfflineEvidenceText is not null) OfflineEvidenceText.Clear();
        if (OfflineStatusText is not null && !_offlineBusy)
            OfflineStatusText.Text = "Inputs changed. Audit or verify the current selection before relying on its evidence. Saved evidence files are preserved.";
        if (OfflineCancelButton is not null) SetOfflineControls(!_offlineBusy);
    }
    private static bool LocalFileExists(string value)
    {
        var path = value.Trim();
        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] is not ('\\' or '/')) return false;
        try
        {
            OfflineModelVerifier.CheckNoReparsePath(path);
            return File.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or InvalidOperationException) { return false; }
    }
    private void OfflineCancelButton_Click(object sender, RoutedEventArgs e) => _offlineOperation?.Cancel();

    internal void RenderOfflineVerification(OfflineVerificationEvidence evidence)
    {
        var text = new StringBuilder();
        text.AppendLine("LOCAL OFFLINE-MODE SMOKE TEST");
        text.AppendLine($"Model: {evidence.ModelPath}\nModel SHA-256: {evidence.ModelSha256}\nRuntime: {evidence.RuntimeExecutable}");
        text.AppendLine($"Context: {evidence.ContextSize} tokens\nModel ID reported by server: {evidence.ActualModelId}\nActual response: {evidence.Response}");
        text.AppendLine($"Started: {evidence.StartedAt.ToLocalTime():g}\nCompleted: {evidence.CompletedAt.ToLocalTime():g}");
        text.AppendLine($"Runtime offline flag used: {evidence.OfflineModeRequested}\nOS network isolation tested: {evidence.OSNetworkIsolationVerified}\nSaved evidence: {evidence.SavedEvidencePath}");
        text.AppendLine("\nRuntime file identities:");
        foreach (var file in evidence.RuntimeFiles) text.AppendLine($"{file.RelativePath} | {file.SizeBytes:N0} bytes | {file.Sha256}");
        text.AppendLine("\nLimits of this verification:");
        foreach (var limitation in evidence.Limitations) text.AppendLine("• " + limitation);
        OfflineEvidenceText.Text = text.ToString();
        OfflineStatusText.Text = "Local offline-mode inference completed and evidence was saved. The test server was stopped. Windows network policy was not changed or used as proof of isolation.";
    }
}
