using System.Windows;

namespace AINexus.App;

public partial class MainWindow
{
    private void InitializeEmbeddedApps()
    {
        EmbeddedHost.StatusChanged += status => EmbeddedStatusText.Text = status;
        Closed += (_, _) => EmbeddedHost.Dispose();
    }

    private async void EmbeddedOpenButton_Click(object sender, RoutedEventArgs e)
    {
        if (!Uri.TryCreate(EmbeddedEndpointText.Text.Trim(), UriKind.Absolute, out var endpoint))
        {
            EmbeddedStatusText.Text = "Enter the HTTP or HTTPS loopback URL of your local AI web interface.";
            return;
        }
        EmbeddedOpenButton.IsEnabled = false;
        try { await EmbeddedHost.NavigateAsync(endpoint); }
        catch (Exception ex) { EmbeddedStatusText.Text = $"Unable to open local interface: {ex.Message}"; }
        finally { if (!_lifetime.IsCancellationRequested) EmbeddedOpenButton.IsEnabled = true; }
    }
}
