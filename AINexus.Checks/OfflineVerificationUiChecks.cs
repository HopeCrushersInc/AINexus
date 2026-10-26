using System.Windows;
using System.Windows.Controls;
using AINexus.App;

namespace AINexus.Checks;

internal static class OfflineVerificationUiChecks
{
    public static void RunNative(MainWindow window, Action<bool, string> check)
    {
        var evidence = (TextBox)window.FindName("OfflineEvidenceText");
        var status = (TextBlock)window.FindName("OfflineStatusText");
        foreach (var name in new[] { "OfflineReceiptText", "OfflineRuntimeText", "OfflineContextText" })
        {
            var input = (TextBox)window.FindName(name);
            var previous = input.Text;
            evidence.Text = "Successful evidence for the previous selection";
            input.Text = previous + "changed";
            check(evidence.Text.Length == 0 && status.Text.Contains("Inputs changed"),
                $"Changing {name} invalidates displayed verification evidence");
            input.Text = previous;
        }

        evidence.Text = "Previous successful evidence";
        window.RunOfflineOperationAsync(_ =>
        {
            check(evidence.Text.Length == 0, "A new offline operation clears previous success before starting work");
            evidence.Text = "Incomplete operation evidence";
            return Task.FromException(new InvalidOperationException("Fixture failure"));
        }).GetAwaiter().GetResult();
        check(evidence.Text.Length == 0 && status.Text.Contains("Fixture failure"),
            "Failed offline operations cannot leave success or incomplete evidence displayed");

        evidence.Text = "Previous successful evidence";
        window.RunOfflineOperationAsync(token =>
        {
            evidence.Text = "Incomplete operation evidence";
            ((Button)window.FindName("OfflineCancelButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            return Task.FromCanceled(token);
        }).GetAwaiter().GetResult();
        check(evidence.Text.Length == 0 && status.Text.Contains("cancelled"),
            "Cancelled offline operations clear displayed evidence without launching a runtime");
        check(!((Button)window.FindName("OfflineCancelButton")).IsEnabled
            && ((TextBox)window.FindName("OfflineReceiptText")).IsEnabled,
            "Offline controls recover after failure and cancellation");
    }
}
