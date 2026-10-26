using System.IO;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Threading;
using AINexus.App;

namespace AINexus.Checks;

internal static class ModelMaintenanceUiChecks
{
    public static void RunNative(MainWindow window, Action<bool, string> check)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var grid = (DataGrid)window.FindName("ModelFileGrid");
        var previousItems = grid.ItemsSource;
        try
        {
            Pump(async () =>
            {
                var checkButton = (Button)window.FindName("LibraryCheckButton");
                var cancelButton = (Button)window.FindName("LibraryCancelCheckButton");
                var status = (TextBlock)window.FindName("LibraryMaintenanceStatusText");
                grid.ItemsSource = Array.Empty<ModelFile>();
                check(!checkButton.IsEnabled && !cancelButton.IsEnabled,
                    "Library integrity checking starts disabled without an explicitly selected model");

                var bytes = "GGUF native maintenance fixture"u8.ToArray();
                var models = Path.Combine(root, "models");
                Directory.CreateDirectory(models);
                var report = AcquisitionPlanChecks.FixtureReport(models, bytes);
                var plan = ModelAcquisitionPlanner.Create(report, report.Catalog.Items.Single().Id, models);
                await File.WriteAllBytesAsync(plan.DestinationPath, bytes);
                var evidence = await ModelArtifactVerifier.VerifyExistingAsync(plan);
                var receipt = new ArtifactAcquisitionReceipt(1, plan, new(evidence, true, DateTimeOffset.UtcNow), DateTimeOffset.UtcNow);
                var receiptPath = Path.Combine(root, "receipt.json");
                await File.WriteAllTextAsync(receiptPath, JsonSerializer.Serialize(receipt));
                var selected = new ModelFile(Path.GetFileName(plan.DestinationPath), plan.DestinationPath,
                    "GGUF", bytes.Length, DateTimeOffset.UtcNow);
                var unsupported = new ModelFile("other.safetensors", Path.Combine(models, "other.safetensors"),
                    "Safetensors", 23, DateTimeOffset.UtcNow);
                grid.ItemsSource = new[] { selected, unsupported };
                grid.SelectedItem = unsupported;
                check(!checkButton.IsEnabled,
                    "Library receipt checking does not claim support for non-GGUF formats");
                grid.SelectedItem = selected;
                check(checkButton.IsEnabled && !cancelButton.IsEnabled,
                    "Selecting a GGUF enables explicit receipt checking without starting work");

                await window.CheckLibraryModelAsync(selected, receiptPath);
                check(status.Text.Contains("Matches receipt") && status.Text.Contains("not publisher authentication or runtime/inference verification")
                    && checkButton.IsEnabled && !cancelButton.IsEnabled && grid.IsEnabled,
                    "Native library integrity completion shows actual receipt comparison and restores controls without a runtime claim");

                grid.SelectedItem = unsupported;
                check(!status.Text.Contains("Matches receipt") && !checkButton.IsEnabled,
                    "Changing library selection clears prior integrity success");
                grid.SelectedItem = selected;
                await window.CheckLibraryModelAsync(selected, receiptPath);
                var malformed = Path.Combine(root, "malformed.json");
                await File.WriteAllTextAsync(malformed, "{broken fixture");
                await window.CheckLibraryModelAsync(selected, malformed);
                check(status.Text.Contains("Integrity check unavailable") && !status.Text.Contains("Matches receipt")
                    && checkButton.IsEnabled && !cancelButton.IsEnabled,
                    "Malformed receipt failure replaces old native success and restores check controls");

                var unrelated = new ModelFile("unrelated.gguf", Path.Combine(models, "unrelated.gguf"),
                    "GGUF", bytes.Length, DateTimeOffset.UtcNow);
                grid.ItemsSource = new[] { unrelated };
                grid.SelectedItem = unrelated;
                await window.CheckLibraryModelAsync(unrelated, receiptPath);
                check(status.Text.Contains("different model path") && !status.Text.Contains("Matches receipt")
                    && !File.Exists(unrelated.FullPath),
                    "Native checking rejects another model's receipt without creating or reading that model");
                check((await File.ReadAllBytesAsync(plan.DestinationPath)).SequenceEqual(bytes),
                    "Native library checks preserve the selected model bytes");
            });
        }
        finally
        {
            grid.ItemsSource = previousItems;
            Directory.Delete(root, recursive: true);
        }
    }

    private static void Pump(Func<Task> action)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var previous = SynchronizationContext.Current;
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        timer.Tick += (_, _) => frame.Continue = false;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var task = action();
            _ = task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
            timer.Start();
            if (!task.IsCompleted) Dispatcher.PushFrame(frame);
            if (!task.IsCompleted) throw new TimeoutException("Native model maintenance checks exceeded their deadline.");
            task.GetAwaiter().GetResult();
        }
        finally
        {
            timer.Stop();
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }
}
