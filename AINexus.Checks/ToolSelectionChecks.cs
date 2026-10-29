using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using AINexus.App;

namespace AINexus.Checks;

internal static class ToolSelectionChecks
{
    internal static DiscoveredItem OfficialComfyFixture() => new("fixture-comfy-tool", "ComfyUI", "Comfy-Org",
        DiscoveryInterest.Images, "Tool", null, null,
        [new DiscoverySource("Official tool fixture", new Uri("https://github.com/Comfy-Org/ComfyUI"), DateTimeOffset.UtcNow)],
        "Fixture source identity only; not installed, downloaded or compatibility verified.");

    internal static void RunNative(Action<bool, string> check) => WorkloadDiscoveryChecks.Pump(async () =>
    {
        var window = new MainWindow { GuidedPersistPreferences = false };
        var imageRequests = 0;
        var archiveRequests = 0;
        var admissions = 0;
        window.ImageHttpFactoryOverride = () => { imageRequests++; return new HttpClient(new DenyHandler()); };
        window.ComfyPortableHttpOverride = () => { archiveRequests++; return new HttpClient(new DenyHandler()); };
        window.ImageAdmissionOverride = _ => { admissions++; return Task.FromResult(new ResourceAdmission(false, "Not authorized by this fixture.")); };
        try
        {
            var official = OfficialComfyFixture();
            check(!window.SelectDiscoveredTool(official) && !window.UseSelectedImageTool(),
                "A tool adapter cannot be chosen before a hardware scan and explicit image workload");
            window.CompleteWorkloadScan(DiscoveryPipelineChecks.FixtureMachine());
            window.ApplyWorkloadSelection([DiscoveryInterest.Images]);
            var landing = Find<FrameworkElement>(window, "ImageToolLanding");
            var adapter = Find<FrameworkElement>(window, "ImageAdapterPanel");
            check(landing.Visibility == Visibility.Visible && adapter.Visibility == Visibility.Collapsed
                && Find<TextBox>(window, "ImageEnginePathText").Text == ""
                && Find<TextBox>(window, "ImageEndpointText").Text == ""
                && Find<ComboBox>(window, "ImageModelSelector").SelectedIndex == -1,
                "Choosing Images opens a neutral landing with no engine, endpoint, installation path or checkpoint selected");
            check(!Find<Button>(window, "ImageStartButton").IsEnabled && !Find<Button>(window, "ImageConnectButton").IsEnabled
                && !Find<Button>(window, "ImageGenerateButton").IsEnabled,
                "Image engine start, connection and generation remain unavailable before explicit tool selection");
            await window.LookupComfyPortableReleaseAsync();
            check(archiveRequests == 0, "A fresh image workspace cannot look up a predetermined ComfyUI archive");

            var alternative = official with
            {
                Id = "fixture-other-tool", Name = "Another image tool", Publisher = "fixture",
                Sources = [new DiscoverySource("Alternative tool fixture", new Uri("https://github.com/AUTOMATIC1111/stable-diffusion-webui"), DateTimeOffset.UtcNow)]
            };
            check(window.SelectDiscoveredTool(alternative) && Find<TabItem>(window, "ToolDetailsTab").IsSelected
                && Find<TextBlock>(window, "SelectedToolTitleText").Text.Contains(alternative.Name),
                "Selecting an alternative image tool opens its own identity and source instead of substituting ComfyUI");
            check(!Find<Button>(window, "SelectedToolUseExistingButton").IsEnabled
                && !Find<Button>(window, "SelectedToolArchiveButton").IsEnabled && !window.UseSelectedImageTool()
                && adapter.Visibility == Visibility.Collapsed,
                "A tool without a supported adapter remains an explicit manual choice rather than masquerading as an installed engine");

            check(window.SelectDiscoveredTool(official) && Find<TabItem>(window, "ToolDetailsTab").IsSelected
                && Find<Button>(window, "SelectedToolUseExistingButton").IsEnabled
                && Find<Button>(window, "SelectedToolArchiveButton").IsEnabled && adapter.Visibility == Visibility.Collapsed,
                "Selecting the official ComfyUI tool exposes its optional actions without opening or selecting an installed engine");
            Find<TextBox>(window, "ImageEnginePathText").Text = "previous path must not become a fresh selection";
            check(window.UseSelectedImageTool() && adapter.Visibility == Visibility.Visible && landing.Visibility == Visibility.Collapsed
                && Find<TextBox>(window, "ImageEnginePathText").Text == ""
                && Find<ComboBox>(window, "ImageModelSelector").SelectedIndex == -1
                && !Find<Button>(window, "ImageGenerateButton").IsEnabled,
                "Only explicit Use existing installation reveals the selected adapter and still requires the user's installation and checkpoint choices");
            check(window.SelectDiscoveredTool(alternative) && adapter.Visibility == Visibility.Collapsed
                && !Find<Button>(window, "ImageStartButton").IsEnabled && !window.UseSelectedImageTool(),
                "Selecting a different tool revokes the previous image adapter instead of retaining a preferred engine");
            check(!window.SelectDiscoveredTool(official with { Kind = "Model" }),
                "An image-model result cannot silently become an engine selection");
            check(!window.SelectDiscoveredTool(official with { Interest = DiscoveryInterest.MusicAudio }),
                "A tool selected for another workload cannot silently become the image engine");
            check(window.SelectDiscoveredTool(official) && window.UseSelectedImageTool(),
                "A fresh explicit image-tool choice can establish adapter context before changing workloads");
            window.ApplyWorkloadSelection([DiscoveryInterest.Coding]);
            await window.LookupComfyPortableReleaseAsync();
            check(!window.UseSelectedImageTool() && archiveRequests == 0
                && !Find<Button>(window, "ImageStartButton").IsEnabled,
                "Switching away from Images revokes its tool choice and prevents stale archive lookup or engine use");
            window.ApplyWorkloadSelection([DiscoveryInterest.Images]);
            await window.LookupComfyPortableReleaseAsync();
            check(landing.Visibility == Visibility.Visible && adapter.Visibility == Visibility.Collapsed
                && !window.UseSelectedImageTool() && archiveRequests == 0
                && Find<TextBox>(window, "ImageEnginePathText").Text == ""
                && Find<TextBox>(window, "ImageEndpointText").Text == "",
                "Returning to Images requires a fresh tool selection instead of restoring its previous engine context");
            check(imageRequests == 0 && archiveRequests == 0 && admissions == 0 && !window.OwnedLlamaServerRunning
                && !Find<Button>(window, "ImageStopButton").IsEnabled
                && Find<CheckBox>(window, "ComfyPortableApprovalCheck").IsChecked != true
                && Find<CheckBox>(window, "ComfyFolderApprovalCheck").IsChecked != true,
                "Tool browsing and explicit adapter selection perform no server query, archive lookup, acquisition approval or engine launch");
        }
        finally { window.Close(); }
    });

    private static T Find<T>(MainWindow window, string name) => (T)window.FindName(name);
    private sealed class DenyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
    }
}
