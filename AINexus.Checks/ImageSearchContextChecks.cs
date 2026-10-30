using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using AINexus.App;

namespace AINexus.Checks;

internal static class ImageSearchContextChecks
{
    internal static void RunNative(Action<bool, string> check)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", "search-context-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var window = new MainWindow { GuidedPersistPreferences = false };
        try
        {
            var machine = DiscoveryPipelineChecks.FixtureMachine();
            var original = DiscoveryPipelineChecks.FixtureCatalog();
            var text = original.Items.Single() with { Name = "Qwen text fixture" };
            var textSnapshot = original with { Items = [text] };
            var image = text with { Id = "image-context-fixture", Name = "SDXL image fixture", Interest = DiscoveryInterest.Images,
                Artifact = text.Artifact! with { FileName = "image.safetensors", Format = "Safetensors" }, PipelineTag = "text-to-image" };
            window.DiscoveryHardwareOverride = _ => Task.FromResult(machine with { ScannedAt = DateTimeOffset.UtcNow });
            window.CompleteWorkloadScan(machine);
            ((ComboBox)window.FindName("DiscoveryVolumeSelector")).SelectedItem = machine.Volumes.Single();
            window.ApplyWorkloadSelection([DiscoveryInterest.LocalAssistants, DiscoveryInterest.Images]);
            var selector = (ComboBox)window.FindName("DiscoveryInterestSelector");
            var query = (TextBox)window.FindName("DiscoveryQueryText");
            var onlyFits = (CheckBox)window.FindName("DiscoveryShowUnassessedCheck");
            var order = (ComboBox)window.FindName("DiscoveryOrderSelector");
            var grid = (DataGrid)window.FindName("DiscoveryResultsGrid");
            SelectCategory(selector, DiscoveryInterest.LocalAssistants);
            window.RenderDiscoveryReport(DiscoveryPipeline.Evaluate(textSnapshot, machine, "C:\\"));
            query.Text = "Qwen";
            onlyFits.IsChecked = true;
            grid.SelectedIndex = 0;
            window.ReviewSelectedDiscoveryItem();
            ((CheckBox)window.FindName("GuidedModelApprovalCheck")).IsChecked = true;
            SelectCategory(selector, DiscoveryInterest.Images);
            check(grid.Items.Count == 0 && grid.SelectedIndex == -1 && query.Text == "" && onlyFits.IsChecked == false,
                "Changing Chat to Images removes stale text rows and narrowing terms and resets fit-only filtering before any new search");
            check(((TextBlock)window.FindName("GuidedModelTitleText")).Text == "No model selected"
                && ((CheckBox)window.FindName("GuidedModelApprovalCheck")).IsChecked == false
                && !((Button)window.FindName("DiscoveryPlanButton")).IsEnabled
                && ((FrameworkElement)window.FindName("ImageAdapterPanel")).Visibility == Visibility.Collapsed
                && ((ComboBox)window.FindName("ImageModelSelector")).SelectedIndex == -1,
                "A category change revokes idle text-model review and approval without choosing an image engine or checkpoint");
            window.RenderDiscoveryReport(DiscoveryPipeline.Evaluate(original with { Items = [image] }, machine, "C:\\"));
            check(grid.Items.Count == 1 && grid.SelectedIndex == -1
                && ((TextBlock)window.FindName("DiscoveryImageCandidatesNoticeText")).Visibility == Visibility.Visible,
                "The new image report shows its unverified candidate instead of hiding it behind the previous text fit filter");
            query.Text = "Qwen";
            onlyFits.IsChecked = true;
            order.SelectedIndex = 1;
            window.ApplyWorkloadSelection([DiscoveryInterest.Images]);
            check(query.Text == "" && onlyFits.IsChecked == false && order.SelectedIndex == -1 && grid.Items.Count == 0,
                "Changing confirmed workloads clears the old query, filter, ordering and result context");
            query.Text = "SDXL";
            order.SelectedIndex = 1;
            window.ApplyWorkloadSelection([DiscoveryInterest.Images]);
            check(query.Text == "SDXL" && order.SelectedIndex == 1,
                "Reapplying the identical workload profile preserves intentional search terms and ordering");

            GuidedInstallChecks.Pump(async () =>
            {
                window.ApplyWorkloadSelection([DiscoveryInterest.LocalAssistants, DiscoveryInterest.Images]);
                SelectCategory(selector, DiscoveryInterest.LocalAssistants);
                query.Text = "Qwen";
                var provider = new DelayedProvider(textSnapshot);
                SetPipeline(window, new DiscoveryPipeline(provider, new DiscoveryCacheStore(Path.Combine(root, "late-cache"))));
                var pending = window.FindSelectedWorkloadsAsync();
                try
                {
                    await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    SelectCategory(selector, DiscoveryInterest.Images);
                    check(provider.Calls == 1 && provider.Token.IsCancellationRequested && grid.Items.Count == 0,
                        "Changing category cancels the old pending search without initiating another source request");
                }
                finally
                {
                    // Deliberately return an old response after cancellation: it must not render.
                    provider.Release.TrySetResult();
                    await pending;
                }
                check(grid.Items.Count == 0 && query.Text == "" && ((DiscoveryWorkflow)selector.SelectedItem).Interest == DiscoveryInterest.Images,
                    "A noncooperative old text response cannot repopulate the Images view after scope cancellation");

                selector.SelectedIndex = -1;
                window.RenderDiscoveryReport(DiscoveryPipeline.Evaluate(textSnapshot, machine, "C:\\"));
                grid.SelectedIndex = 0;
                var exact = new ImmediateProvider(textSnapshot);
                SetPipeline(window, new DiscoveryPipeline(exact, new DiscoveryCacheStore(Path.Combine(root, "exact-cache"))));
                await window.BrowseSelectedModelFilesAsync();
                check(exact.Request is { Interest: DiscoveryInterest.LocalAssistants } request
                    && request.Query == text.Artifact!.Repository && query.Text == text.Artifact.Repository,
                    "Browsing a selected model changes category before setting its exact repository, preserving the user's explicit lookup");
                window.RenderDiscoveryReport(DiscoveryPipeline.Evaluate(original with { Items = [image] }, machine, "C:\\"));
                grid.SelectedIndex = 0;
                var exactImage = new ImmediateProvider(original with { Items = [image] });
                SetPipeline(window, new DiscoveryPipeline(exactImage, new DiscoveryCacheStore(Path.Combine(root, "image-exact-cache"))));
                check(((Button)window.FindName("DiscoveryBrowseVariantsButton")).IsEnabled,
                    "A selected image safetensors candidate can explicitly browse its repository's file variants");
                await window.BrowseSelectedModelFilesAsync();
                check(exactImage.Request is { Interest: DiscoveryInterest.Images, Limit: 12 } imageRequest
                    && imageRequest.Query == image.Artifact!.Repository && grid.SelectedIndex == -1,
                    "Image file browsing requests the exact chosen repository in the image category without choosing a file or engine");
            }, TimeSpan.FromSeconds(20), () => window.Close());
        }
        finally
        {
            window.Close();
            Directory.Delete(root, recursive: true);
        }
    }

    private static void SelectCategory(ComboBox selector, DiscoveryInterest interest) => selector.SelectedItem =
        ((IEnumerable<DiscoveryWorkflow>)selector.ItemsSource).Single(workflow => workflow.Interest == interest);

    private static void SetPipeline(MainWindow window, DiscoveryPipeline pipeline) =>
        typeof(MainWindow).GetField("_discoveryPipeline", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, pipeline);

    private sealed class ImmediateProvider(OnlineDiscoverySnapshot snapshot) : IOnlineDiscoveryProvider
    {
        internal OnlineDiscoveryRequest? Request { get; private set; }
        public Task<OnlineDiscoverySnapshot> SearchAsync(OnlineDiscoveryRequest request, CancellationToken cancellationToken = default)
        { Request = request; return Task.FromResult(snapshot); }
    }

    private sealed class DelayedProvider(OnlineDiscoverySnapshot snapshot) : IOnlineDiscoveryProvider
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Calls { get; private set; }
        internal CancellationToken Token { get; private set; }
        public async Task<OnlineDiscoverySnapshot> SearchAsync(OnlineDiscoveryRequest request, CancellationToken cancellationToken = default)
        {
            Calls++; Token = cancellationToken; Started.TrySetResult();
            await Release.Task;
            return snapshot;
        }
    }
}
