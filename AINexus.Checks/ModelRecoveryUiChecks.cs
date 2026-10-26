using System.IO;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Threading;
using AINexus.App;

namespace AINexus.Checks;

internal static class ModelRecoveryUiChecks
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
                var recoveryButton = (Button)window.FindName("LibraryRecoveryButton");
                var details = (TextBox)window.FindName("LibraryRecoveryDetailsText");
                var bytes = "GGUF native recovery fixture"u8.ToArray();
                var models = Path.Combine(root, "models");
                var recoveryDirectory = Path.Combine(root, "recovery");
                Directory.CreateDirectory(models);
                Directory.CreateDirectory(recoveryDirectory);
                var report = AcquisitionPlanChecks.FixtureReport(models, bytes);
                var plan = ModelAcquisitionPlanner.Create(report, report.Catalog.Items.Single().Id, models);
                await File.WriteAllBytesAsync(plan.DestinationPath, bytes);
                var evidence = await ModelArtifactVerifier.VerifyExistingAsync(plan);
                var receipt = new ArtifactAcquisitionReceipt(1, plan, new(evidence, true, DateTimeOffset.UtcNow), DateTimeOffset.UtcNow);
                var receiptPath = Path.Combine(root, "receipt.json");
                await File.WriteAllTextAsync(receiptPath, JsonSerializer.Serialize(receipt));
                var selected = new ModelFile(Path.GetFileName(plan.DestinationPath), plan.DestinationPath,
                    "GGUF", bytes.Length, DateTimeOffset.UtcNow);
                var alternate = new ModelFile("unrelated.gguf", Path.Combine(models, "unrelated.gguf"),
                    "GGUF", bytes.Length, DateTimeOffset.UtcNow);
                grid.ItemsSource = new[] { selected, alternate };
                grid.SelectedItem = selected;

                await window.ReviewLibraryRecoveryAsync(selected, receiptPath, recoveryDirectory);
                check(!recoveryButton.IsEnabled && details.Text.Length == 0
                    && !Directory.EnumerateFileSystemEntries(recoveryDirectory).Any(),
                    "Native recovery review requires a completed failed integrity audit before exposing a plan");

                await window.CheckLibraryModelAsync(selected, receiptPath);
                check(!recoveryButton.IsEnabled && details.Text.Length == 0,
                    "A healthy library integrity result does not enable recovery planning");

                var changed = bytes.ToArray();
                changed[^1] ^= 1;
                await File.WriteAllBytesAsync(plan.DestinationPath, changed);
                await window.CheckLibraryModelAsync(selected, receiptPath);
                check(recoveryButton.IsEnabled && details.Text.Length == 0,
                    "A failed current integrity comparison enables only explicit recovery review");

                await window.ReviewLibraryRecoveryAsync(selected, receiptPath, recoveryDirectory);
                check(details.Text.Length > 0 && details.Text.Contains(recoveryDirectory)
                    && (await File.ReadAllBytesAsync(plan.DestinationPath)).SequenceEqual(changed)
                    && !Directory.EnumerateFileSystemEntries(recoveryDirectory).Any(),
                    "Native recovery review describes the alternate destination while preserving changed bytes and writing no payload");

                grid.SelectedItem = alternate;
                check(details.Text.Length == 0 && !recoveryButton.IsEnabled,
                    "Changing the selected library model discards the old recovery plan and approval path");
                grid.SelectedItem = selected;
                await window.CheckLibraryModelAsync(selected, receiptPath);
                await window.ReviewLibraryRecoveryAsync(selected, receiptPath, recoveryDirectory);
                await File.WriteAllBytesAsync(plan.DestinationPath, bytes);
                await window.ReviewLibraryRecoveryAsync(selected, receiptPath, recoveryDirectory);
                check(details.Text.Length == 0 && !Directory.EnumerateFileSystemEntries(recoveryDirectory).Any()
                    && (await File.ReadAllBytesAsync(plan.DestinationPath)).SequenceEqual(bytes),
                    "Recovery review rechecks repaired model bytes and removes obsolete recovery details without acquiring files");

                await File.WriteAllBytesAsync(plan.DestinationPath, changed);
                await window.CheckLibraryModelAsync(selected, receiptPath);
                await window.ReviewLibraryRecoveryAsync(selected, receiptPath, recoveryDirectory);
                grid.SelectedItem = alternate;
                await window.CheckLibraryModelAsync(alternate, receiptPath);
                check(details.Text.Length == 0 && !recoveryButton.IsEnabled && !File.Exists(alternate.FullPath),
                    "Wrong-receipt audit failure cannot retain a previous recovery plan or create the selected model");
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
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(25) };
        timer.Tick += (_, _) => frame.Continue = false;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var task = action();
            _ = task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
            timer.Start();
            if (!task.IsCompleted) Dispatcher.PushFrame(frame);
            if (!task.IsCompleted) throw new TimeoutException("Native model recovery checks exceeded their deadline.");
            task.GetAwaiter().GetResult();
        }
        finally
        {
            timer.Stop();
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }
}
