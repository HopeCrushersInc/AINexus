using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Controls;
using AINexus.App;

namespace AINexus.Checks;

internal static class ComfyPortableUiChecks
{
    internal static void RunNative(Action<bool, string> check) => WorkloadDiscoveryChecks.Pump(async () =>
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "fixtures", "comfy-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var window = new MainWindow { GuidedPersistPreferences = false };
        var calls = new List<Uri>();
        var includeAssets = true;
        var bytes = new byte[] { 1, 3, 5, 7, 11 };
        window.ComfyPortableHttpOverride = () => new HttpClient(new Handler(request =>
        {
            calls.Add(request.RequestUri!);
            if (request.RequestUri!.Host != "api.github.com") return new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
            {
                tag_name = "v0.43.0", draft = false, prerelease = false,
                html_url = "https://github.com/Comfy-Org/ComfyUI/releases/tag/v0.43.0",
                assets = includeAssets ? new object[] { new { name = "ComfyUI_windows_portable_nvidia.7z", size = bytes.Length,
                    digest = "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)), state = "uploaded",
                    browser_download_url = "https://github.com/Comfy-Org/ComfyUI/releases/download/v0.43.0/ComfyUI_windows_portable_nvidia.7z" } } : []
            })) };
        }));
        try
        {
            var selector = (ComboBox)window.FindName("ComfyPortableAssetSelector");
            var destination = (TextBox)window.FindName("ComfyPortableDestinationText");
            var consent = (CheckBox)window.FindName("ComfyPortableApprovalCheck");
            var download = (Button)window.FindName("ComfyPortableDownloadButton");
            check(ReferenceEquals(((TextBlock)consent.Content).Foreground, window.FindResource("NexusTextBrush")),
                "Archive approval text uses the active theme foreground instead of the native black checkbox default");
            check(calls.Count == 0 && selector.SelectedIndex == -1 && destination.Text == "" && !download.IsEnabled && consent.IsChecked != true,
                "Comfy archive startup has no network request, selected asset, destination or approval");
            await window.DownloadComfyPortableArchiveAsync();
            check(calls.Count == 0 && !Directory.EnumerateFiles(folder).Any(), "Comfy UI refuses download before an exact approved review");
            window.CompleteWorkloadScan(DiscoveryPipelineChecks.FixtureMachine());
            window.ApplyWorkloadSelection([DiscoveryInterest.Images]);
            window.SelectDiscoveredTool(ToolSelectionChecks.OfficialComfyFixture());
            await window.LookupComfyPortableReleaseAsync();
            check(calls.Count == 1 && calls[0].AbsolutePath.EndsWith("/releases/latest") && selector.Items.Count == 1 && selector.SelectedIndex == -1,
                "Explicit current-release lookup shows actual archives without choosing one");
            Uri? opened = null;
            window.ComfyPortableSourceOpenOverride = source => opened = source;
            window.OpenComfyPortableTerms();
            check(opened?.AbsoluteUri == "https://github.com/Comfy-Org/ComfyUI/blob/v0.43.0/LICENSE"
                && consent.IsChecked != true && calls.Count == 1,
                "Explicit terms action opens only the selected release's official license and grants no acquisition approval");
            selector.SelectedIndex = 0;
            destination.Text = folder;
            window.ReviewComfyPortableDownload();
            var firstPlan = window.ComfyPortableReviewedPlanId;
            consent.IsChecked = true;
            check(firstPlan is not null && download.IsEnabled && calls.Count == 1, "Exact Comfy review plus explicit consent enables only its archive download");
            destination.Text = folder + Path.DirectorySeparatorChar;
            check(window.ComfyPortableReviewedPlanId is null && consent.IsChecked != true && !download.IsEnabled,
                "Changing the Comfy destination revokes the reviewed plan and approval");
            window.ReviewComfyPortableDownload();
            consent.IsChecked = true;
            await window.DownloadComfyPortableArchiveAsync();
            var archive = Path.Combine(folder, "ComfyUI_windows_portable_nvidia.7z");
            check(calls.Count == 2 && File.ReadAllBytes(archive).SequenceEqual(bytes)
                && window.ComfyPortableReviewedPlanId is null && consent.IsChecked != true && !download.IsEnabled,
                "Approved native archive transfer verifies the chosen bytes and consumes approval");
            var receipt = Directory.EnumerateFiles(Path.Combine(folder, ".ainexus-comfy-download-receipts")).Single();
            using var json = JsonDocument.Parse(File.ReadAllText(receipt));
            check(json.RootElement.GetProperty("EngineInstalled").ValueKind == JsonValueKind.False
                && !Directory.Exists(Path.Combine(folder, "ComfyUI_windows_portable"))
                && ((TextBlock)window.FindName("ComfyPortableStatusText")).Text.Contains("NOT installed")
                && !window.OwnedLlamaServerRunning && !((Button)window.FindName("ImageStopButton")).IsEnabled,
                "Comfy download receipt and UI distinguish acquired archive from extraction, installation and engine execution");
            var inspections = 0;
            window.ComfyPortableInspectionOverride = (request, _) =>
            {
                inspections++;
                check(request.ArchivePath == archive && request.ExpectedBytes == bytes.Length
                    && request.ExpectedSha256.Equals(Convert.ToHexString(SHA256.HashData(bytes)), StringComparison.OrdinalIgnoreCase),
                    "Explicit archive inspection binds the selected acquired file's exact bytes and hash");
                return Task.FromResult(new ToolArchiveInspection(request.ArchivePath, request.ExpectedBytes, request.ExpectedSha256,
                    DateTimeOffset.UtcNow, 110, [], false, false, ["Synthetic metadata only; no extraction, license review or execution."]));
            };
            check(((Button)window.FindName("ComfyPortableInspectButton")).IsEnabled && inspections == 0,
                "Verified archive acquisition offers inspection without running it automatically");
            await window.InspectComfyPortableArchiveAsync();
            check(inspections == 1 && calls.Count == 2 && consent.IsChecked != true && !download.IsEnabled
                && ((TextBlock)window.FindName("ComfyPortableStatusText")).Text.Contains("Nothing extracted")
                && File.ReadAllBytes(archive).SequenceEqual(bytes),
                "Archive inspection is read-only, performs no download or execution and grants no installation approval");
            ((TextBox)window.FindName("ComfyPortableTagText")).Text = "v0.42.1";
            check(selector.Items.Count == 0 && selector.SelectedIndex == -1 && window.ComfyPortableReviewedPlanId is null,
                "Changing release tag clears stale archive choices without fetching a replacement");
            ((TextBox)window.FindName("ComfyPortableTagText")).Text = "";
            includeAssets = false;
            await window.LookupComfyPortableReleaseAsync();
            check(selector.Items.Count == 0 && !download.IsEnabled
                && ((TextBlock)window.FindName("ComfyPortableStatusText")).Text.Contains("No supported archives"),
                "An official release without usable checksum-bearing archives exposes no download or inferred fallback");
            var source = new DiscoverySource("fixture", new Uri("https://github.com/Comfy-Org/ComfyUI"), DateTimeOffset.UtcNow);
            var tool = new DiscoveredItem("fixture", "ComfyUI", "Comfy-Org", DiscoveryInterest.Images, "Tool", null, null, [source], "fixture");
            check(MainWindow.IsComfyPortableTool(tool), "Exact official ComfyUI tool can expose its supported archive review");
            check(!MainWindow.IsComfyPortableTool(tool with { Sources = [source with { Url = new Uri("https://github.com/Comfy-Org/ComfyUI-extra") }] })
                && !MainWindow.IsComfyPortableTool(tool with { Kind = "Model" }),
                "Similar source names and model candidates are never redirected into the ComfyUI archive recipe");
        }
        finally { window.Close(); Directory.Delete(folder, recursive: true); }
    });

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(response(request));
    }
}
