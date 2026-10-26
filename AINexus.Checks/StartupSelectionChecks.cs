using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AINexus.App;

namespace AINexus.Checks;

internal static class StartupSelectionChecks
{
    internal static void RunNative(MainWindow _, Action<bool, string> check)
    {
        // Keep the caller's initial setup state intact. Never Show: no real Loaded probes run.
        var window = new MainWindow();
        try
        {
            var setup = (TabItem)window.FindName("WorkloadSetupTab");
            var proceed = (Button)window.FindName("WorkloadContinueButton");
            var choicesControl = (ItemsControl)window.FindName("WorkloadChoices");
            check(setup.IsSelected && !proceed.IsEnabled,
                "A separate native startup window opens setup without an approved workload");

            window.CompleteWorkloadScan(DiscoveryPipelineChecks.FixtureMachine());
            Layout(window);
            var choices = ((IEnumerable<WorkloadChoice>)choicesControl.ItemsSource).ToArray();
            var boxes = Descendants<CheckBox>(choicesControl).ToArray();
            check(boxes.Length == choices.Length && boxes.Length > 0
                && boxes.All(box => box.IsChecked == false && box.DataContext is WorkloadChoice { Selected: false })
                && choices.All(choice => !choice.Selected) && !proceed.IsEnabled,
                "Realized first-page workload checkboxes are all visibly unchecked, matching their backing choices");
            SavePreview(window, "unchecked");

            var selectedBox = boxes.First();
            selectedBox.SetCurrentValue(CheckBox.IsCheckedProperty, true);
            Layout(window);
            check(selectedBox.IsChecked == true && selectedBox.DataContext is WorkloadChoice { Selected: true }
                && boxes.Count(box => box.IsChecked == true) == 1 && proceed.IsEnabled,
                "An explicit workload checkbox change checks only that choice and enables confirmation");
            SavePreview(window, "explicit-selection");

            window.CompleteWorkloadScan(DiscoveryPipelineChecks.FixtureMachine());
            Layout(window);
            var rebuiltBoxes = Descendants<CheckBox>(choicesControl).ToArray();
            check(rebuiltBoxes.Length == choices.Length
                && rebuiltBoxes.All(box => box.IsChecked == false && box.DataContext is WorkloadChoice { Selected: false })
                && !proceed.IsEnabled,
                "A rebuilt native startup chooser resets every rendered checkbox and requires a fresh choice");
            SavePreview(window, "reset-unchecked");
        }
        finally { window.Close(); }
    }

    private static void Layout(MainWindow window)
    {
        var surface = (FrameworkElement)window.Content;
        surface.Measure(new Size(1440, 900));
        surface.Arrange(new Rect(0, 0, 1440, 900));
        surface.UpdateLayout();
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static void SavePreview(MainWindow window, string state)
    {
        if (Environment.GetEnvironmentVariable("AINEXUS_RENDER_STARTUP") != "1") return;
        var folder = Path.Combine(AppContext.BaseDirectory, "Reports", "Startup");
        Directory.CreateDirectory(folder);
        var destination = Path.Combine(folder, $"{Guid.NewGuid():N}-{state}.png");
        var bitmap = new RenderTargetBitmap(1440, 900, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render((Visual)window.Content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write);
        encoder.Save(stream);
        Console.WriteLine("Startup selection preview: " + destination);
    }
}
