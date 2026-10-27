using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows.Controls;
using AINexus.App;

namespace AINexus.Checks;

internal static class ImageDimensionsChecks
{
    private const string Checkpoint = "Fixture.safetensors";
    private const string Job = "b95c65b4-d607-41ad-9c23-e77adb026c6c";

    internal static void RunNative(Action<bool, string> check)
    {
        var window = new MainWindow { GuidedPersistPreferences = false };
        var requests = 0;
        var factories = 0;
        var admissions = 0;
        string? submitted = null;
        try
        {
            GuidedInstallChecks.Pump(async () =>
            {
                var width = (TextBox)window.FindName("ImageWidthText");
                var height = (TextBox)window.FindName("ImageHeightText");
                var selector = (ComboBox)window.FindName("ImageModelSelector");
                var generate = (Button)window.FindName("ImageGenerateButton");
                window.ImageAdmissionOverride = _ => { admissions++; return Task.FromResult(new ResourceAdmission(true, "Fixture admission, not actual hardware evidence.")); };
                window.ImageHttpFactoryOverride = () =>
                {
                    factories++;
                    return new HttpClient(new Handler(async request =>
                    {
                        requests++;
                        if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/prompt")
                            submitted = await request.Content!.ReadAsStringAsync();
                        return Reply(request);
                    }));
                };
                check(width.Text == "512" && height.Text == "512" && selector.SelectedIndex == -1 && !generate.IsEnabled,
                    "Image size starts at 512 by 512 without selecting a checkpoint or enabling generation");
                await window.GenerateSelectedImageAsync();
                check(admissions == 0 && requests == 0 && factories == 0,
                    "Image size defaults do not connect, enqueue or generate without an explicit checkpoint choice");

                typeof(MainWindow).GetField("_imageConnectedEndpoint", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, new Uri("http://127.0.0.1:8188"));
                typeof(MainWindow).GetField("_imageInventory", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, new ComfyInventory("fixture", [Checkpoint], DateTimeOffset.UtcNow));
                selector.ItemsSource = new[] { Checkpoint };
                selector.SelectedIndex = 0;
                ((TextBox)window.FindName("ImagePromptText")).Text = "Fixture dimensions request";
                foreach (var invalid in new[] { ("", "512"), ("255", "512"), ("1025", "512"), ("513", "512"), ("512", "NaN"), ("512", "0"), ("+512", "512") })
                {
                    width.Text = invalid.Item1; height.Text = invalid.Item2;
                    await window.GenerateSelectedImageAsync();
                    check(!generate.IsEnabled && admissions == 0 && requests == 0 && factories == 0
                        && ((TextBlock)window.FindName("ImageStatusText")).Text.Contains("multiples of 64", StringComparison.Ordinal),
                        $"Invalid image dimensions '{invalid.Item1}' by '{invalid.Item2}' block generation before admission or HTTP");
                }
                width.Text = "256"; height.Text = "1024";
                check(window.TryReadImageDimensions(out var minimum, out var maximum, out _) && minimum == 256 && maximum == 1024 && generate.IsEnabled,
                    "Image size accepts the existing workflow's lower and upper bounds without choosing a different checkpoint");
                width.Text = "1024"; height.Text = "768";
                var admission = new TaskCompletionSource<ResourceAdmission>(TaskCreationOptions.RunContinuationsAsynchronously);
                window.ImageAdmissionOverride = _ => { admissions++; return admission.Task; };
                var running = window.GenerateSelectedImageAsync();
                check(!width.IsEnabled && !height.IsEnabled && !generate.IsEnabled && requests == 0,
                    "Image size controls lock while one explicit generation is waiting for resource admission");
                await window.GenerateSelectedImageAsync();
                check(admissions == 1 && factories == 0 && requests == 0,
                    "A repeated image action during admission cannot enqueue a competing job");
                admission.SetResult(new(true, "Fixture resource admission."));
                await running;
                using var body = JsonDocument.Parse(submitted ?? throw new InvalidOperationException("The selected image request was not submitted."));
                var latent = body.RootElement.GetProperty("prompt").GetProperty("5").GetProperty("inputs");
                var loader = body.RootElement.GetProperty("prompt").GetProperty("4").GetProperty("inputs");
                check(latent.GetProperty("width").GetInt32() == 1024 && latent.GetProperty("height").GetInt32() == 768
                    && latent.GetProperty("batch_size").GetInt32() == 1 && loader.GetProperty("ckpt_name").GetString() == Checkpoint
                    && factories == 1 && requests > 0,
                    "Explicit native image dimensions reach the actual ComfyUI workflow payload with exactly the selected checkpoint");
                check(width.IsEnabled && height.IsEnabled && generate.IsEnabled && selector.SelectedItem as string == Checkpoint,
                    "Completing the fixture image restores controls without changing the selected checkpoint or dimensions");
                window.ImageAdmissionOverride = _ => Task.FromResult(new ResourceAdmission(false, "Fixture admission denied."));
                var previousRequests = requests;
                await window.GenerateSelectedImageAsync();
                check(requests == previousRequests && factories == 1,
                    "Valid image dimensions do not bypass resource denial or contact the engine when admission fails");
            }, TimeSpan.FromSeconds(15), () => window.Close());
        }
        finally { window.Close(); }
        var fresh = new MainWindow { GuidedPersistPreferences = false };
        try
        {
            check(((TextBox)fresh.FindName("ImageWidthText")).Text == "512" && ((TextBox)fresh.FindName("ImageHeightText")).Text == "512",
                "Image dimensions are session-only and restart at 512 by 512 in a fresh window");
        }
        finally { fresh.Close(); }
    }

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value)) };

    private static HttpResponseMessage Reply(HttpRequestMessage request) => request.RequestUri!.AbsolutePath switch
    {
        "/system_stats" => Json(new { system = new { comfyui_version = "dimension-fixture" } }),
        "/object_info" => Json(new Dictionary<string, object>
        {
            ["CheckpointLoaderSimple"] = new { input = new { required = new { ckpt_name = new object[] { new[] { Checkpoint } } } } },
            ["KSampler"] = new { input = new { required = new { sampler_name = new object[] { new[] { "euler" } }, scheduler = new object[] { new[] { "normal" } } } } },
            ["CLIPTextEncode"] = new { }, ["EmptyLatentImage"] = new { }, ["VAEDecode"] = new { }, ["SaveImage"] = new { }
        }),
        "/queue" => Json(new { queue_running = Array.Empty<object>(), queue_pending = Array.Empty<object>() }),
        "/prompt" => Json(new { prompt_id = Job }),
        "/history/" + Job => Json(new Dictionary<string, object>
        {
            [Job] = new { status = new { completed = true, status_str = "success" }, outputs = new Dictionary<string, object>
                { ["9"] = new { images = new[] { new { filename = "fixture.png", subfolder = "", type = "output" } } } } }
        }),
        "/view" => new(HttpStatusCode.OK) { Content = new ByteArrayContent(ImageWorkflowChecks.FixturePng()) },
        _ => throw new InvalidOperationException("Unexpected fixture image request " + request.RequestUri)
    };

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await reply(request);
            response.RequestMessage = request;
            return response;
        }
    }
}
