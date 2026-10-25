using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace AINexus.App;

public partial class MainWindow
{
    private void DiscoveryDestinationText_TextChanged(object sender, TextChangedEventArgs e)
    {
        _reviewedAcquisitionPlan = null;
        if (DiscoveryVerifyButton is not null) RefreshDiscoveryControls();
    }

    private void DiscoveryChooseFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_discoveryBusy) return;
        var dialog = new OpenFolderDialog { Title = "Choose a model folder on the assessed storage volume", Multiselect = false };
        if (dialog.ShowDialog(this) == true) DiscoveryDestinationText.Text = dialog.FolderName;
    }

    private void DiscoveryPlanButton_Click(object sender, RoutedEventArgs e)
    {
        if (_discoveryBusy || _lastDiscoveryReport is null || DiscoveryResultsGrid.SelectedItem is not DiscoveryRow row) return;
        try
        {
            var plan = ModelAcquisitionPlanner.Create(_lastDiscoveryReport, row.Recommendation.Item.Id, DiscoveryDestinationText.Text.Trim());
            _reviewedAcquisitionPlan = plan;
            DiscoveryDetailsText.Text = DescribeAcquisitionPlan(plan);
            DiscoveryStatusText.Text = "Download plan prepared for review. No download or Windows change has occurred. Verify an existing file for reuse if available.";
        }
        catch (Exception ex)
        {
            _reviewedAcquisitionPlan = null;
            DiscoveryStatusText.Text = $"Download plan requires attention: {ex.Message}";
        }
        RefreshDiscoveryControls();
    }

    private async void DiscoveryVerifyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_reviewedAcquisitionPlan is not { } plan) return;
        await RunDiscoveryOperationAsync(async token =>
        {
            DiscoveryStatusText.Text = "Reading the existing artifact and computing SHA-256. No inference or download is running...";
            var evidence = await ModelArtifactVerifier.VerifyExistingAsync(plan, token);
            token.ThrowIfCancellationRequested();
            DiscoveryDetailsText.Text = DescribeAcquisitionPlan(plan) +
                $"\n\nEXISTING FILE EVIDENCE\nChecked: {evidence.CheckedAt.ToLocalTime():g}\nExists: {evidence.Exists}\n" +
                $"Size/hash match: {evidence.Matches}\nActual bytes: {evidence.ActualBytes?.ToString() ?? "not available"}\n" +
                $"Actual SHA-256: {evidence.ActualSha256 ?? "not computed"}\n{evidence.Description}";
            DiscoveryStatusText.Text = evidence.Description;
        });
    }

    private static string DescribeAcquisitionPlan(ModelAcquisitionPlan plan)
    {
        var text = new StringBuilder();
        text.AppendLine("MODEL DATA ACQUISITION — REVIEW ONLY");
        text.AppendLine($"{plan.Name}\nPlan ID: {plan.PlanId}\nSource: {plan.DownloadUrl}\nDestination: {plan.DestinationPath}");
        text.AppendLine($"Expected bytes: {plan.Artifact.SizeBytes:N0}\nExpected SHA-256: {plan.Artifact.Sha256}\nPinned revision: {plan.Artifact.Revision}");
        text.AppendLine($"Declared license: {plan.DeclaredLicense}\nTerms reference: {plan.LicenseReference}");
        text.AppendLine($"Source metadata: {plan.MetadataRetrievedAt.ToLocalTime():g}\nHardware evidence: {plan.HardwareScannedAt.ToLocalTime():g}");
        text.AppendLine("\nProposed actions:");
        foreach (var action in plan.Actions) text.AppendLine("• " + action);
        text.AppendLine("\nScope and limitations:");
        foreach (var limitation in plan.Limitations) text.AppendLine("• " + limitation);
        return text.ToString();
    }
}
