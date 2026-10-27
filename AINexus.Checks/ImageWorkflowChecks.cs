using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AINexus.App;

namespace AINexus.Checks;

internal static class ImageWorkflowChecks
{
    private const string Job = "981bfa43-7430-48da-9100-d1105cf064d7";
    private static readonly Uri Endpoint = new("http://127.0.0.1:8188");
    internal static byte[] FixturePng()
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[16], 8)));
        using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { var response = send(request); response.RequestMessage ??= request; return Task.FromResult(response); }
    }
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value)) };
    private static HttpResponseMessage Reply(HttpRequestMessage request, bool empty = false, bool busy = false, bool fail = false)
    {
        return request.RequestUri!.AbsolutePath switch
        {
            "/system_stats" => Json(new { system = new { comfyui_version = "fixture" } }),
            "/object_info" => Json(new Dictionary<string, object>
            {
                ["CheckpointLoaderSimple"] = new { input = new { required = new { ckpt_name = new object[] { empty ? Array.Empty<string>() : new[] { "Fixture.safetensors" } } } } },
                ["KSampler"] = new { input = new { required = new { sampler_name = new object[] { new[] { "euler" } }, scheduler = new object[] { new[] { "normal" } } } } },
                ["CLIPTextEncode"] = new {}, ["EmptyLatentImage"] = new {}, ["VAEDecode"] = new {}, ["SaveImage"] = new {}
            }),
            "/queue" => Json(new { queue_running = busy ? new object[] { new[] { "Other job" } } : Array.Empty<object>(), queue_pending = Array.Empty<object>() }),
            "/prompt" => Json(new { prompt_id = Job }),
            "/history/" + Job => Json(new Dictionary<string, object> { [Job] = new { status = new { completed = true, status_str = fail ? "error" : "success" },
                outputs = new Dictionary<string, object> { ["9"] = new { images = new[] { new { filename = "fixture.png", subfolder = "", type = "output" } } } } } }),
            "/view" => new(HttpStatusCode.OK) { Content = new ByteArrayContent(FixturePng()) },
            "/api/jobs/" + Job + "/cancel" => Json(new { cancelled = true }),
            _ => throw new InvalidOperationException("Unexpected image request " + request.RequestUri)
        };
    }
    public static async Task RunAsync(Action<bool, string> check)
    {
        var requests = new List<string>();
        string? submitted = null;
        using var http = new HttpClient(new Handler(r =>
        {
            requests.Add(r.Method + " " + r.RequestUri!.AbsolutePath);
            if (r.Method == HttpMethod.Post && r.RequestUri.AbsolutePath == "/prompt") submitted = r.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Reply(r);
        }));
        var client = new ComfyImageClient(http);
        var inventory = await client.InspectAsync(Endpoint);
        check(inventory.Checkpoints.SequenceEqual(new[] { "Fixture.safetensors" }) && requests.All(r => r.StartsWith("GET")),
            "Image-engine inspection reports actual supplied checkpoint names without starting generation");
        var result = await client.GenerateAsync(Endpoint, "Fixture.safetensors", "Fixture prompt", "", 512, 512, 20, 7);
        check(result.JobId == Job && result.Png.SequenceEqual(FixturePng()) && submitted!.Contains("Fixture prompt")
            && submitted.Contains("CheckpointLoaderSimple") && requests.Count(r => r.StartsWith("POST")) == 1,
            "Image adapter submits the explicit standard workflow and returns the job's decoded PNG fixture, not fabricated inference");
        var blocked = false;
        try { ComfyImageClient.ValidateEndpoint(new Uri("http://192.168.1.1:8188")); } catch (ArgumentException) { blocked = true; }
        check(blocked, "Image workflow rejects network engines without authorization");
        using var empty = new HttpClient(new Handler(r => Reply(r, empty: true)));
        var missing = false;
        try { await new ComfyImageClient(empty).GenerateAsync(Endpoint, "Missing.safetensors", "Fixture", "", 512, 512, 20, 0); }
        catch (InvalidOperationException) { missing = true; }
        check(missing, "Missing image checkpoints cannot be submitted as working models");
        var postedBusy = false;
        using var busy = new HttpClient(new Handler(r => { postedBusy |= r.Method == HttpMethod.Post; return Reply(r, busy: true); }));
        var busyRejected = false;
        try { await new ComfyImageClient(busy).GenerateAsync(Endpoint, "Fixture.safetensors", "Fixture", "", 512, 512, 20, 0); }
        catch (InvalidOperationException) { busyRejected = true; }
        check(busyRejected && !postedBusy, "An occupied image engine cannot receive an unnecessary competing GPU job");
        using var error = new HttpClient(new Handler(r => Reply(r, fail: true)));
        var failed = false;
        try { await new ComfyImageClient(error).GenerateAsync(Endpoint, "Fixture.safetensors", "Fixture", "", 512, 512, 20, 0); }
        catch (InvalidOperationException) { failed = true; }
        check(failed, "Image-engine execution errors do not produce success or a preview");
        var canceledPaths = new List<string>();
        using var cancel = new CancellationTokenSource();
        using var cancellation = new HttpClient(new Handler(r =>
        {
            canceledPaths.Add(r.RequestUri!.AbsolutePath);
            if (r.RequestUri.AbsolutePath.StartsWith("/history/")) { cancel.Cancel(); throw new OperationCanceledException(cancel.Token); }
            return Reply(r);
        }));
        var ownCancelled = false;
        try { await new ComfyImageClient(cancellation).GenerateAsync(Endpoint, "Fixture.safetensors", "Fixture", "", 512, 512, 20, 0, cancel.Token); }
        catch (OperationCanceledException ex) { ownCancelled = ex.Message.Contains("accepted cancellation"); }
        check(ownCancelled && canceledPaths.Contains("/api/jobs/" + Job + "/cancel") && !canceledPaths.Contains("/interrupt"),
            "Image cancellation targets only the owned job identity and never sends a global interrupt");
        var malformed = false;
        try { ComfyImageClient.ValidatePng(new byte[32]); } catch (InvalidDataException) { malformed = true; }
        check(malformed, "An invalid image payload cannot be reported as a PNG result");
        using var redirect = new HttpClient(new Handler(_ => new(HttpStatusCode.Redirect)));
        var redirectRejected = false;
        try { await new ComfyImageClient(redirect).InspectAsync(Endpoint); } catch (InvalidDataException) { redirectRejected = true; }
        check(redirectRejected, "Image-engine redirects are rejected rather than followed to a remote service");
    }
    public static void RunNative(MainWindow window, Action<bool, string> check)
    {
        check(((TabItem)window.FindName("DiscoveryTab")).IsSelected && window.FindName("DownloadsTab") is TabItem,
            "Confirmed workloads enter online discovery and keep installation setup in its own page");
        var navigation = (StackPanel)window.FindName("StoreNavigation");
        var images = navigation.Children.OfType<RadioButton>().Single(b => b.Tag as string == "ImageStudioTab");
        images.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        check(((TabItem)window.FindName("ImageStudioTab")).IsSelected && !((Button)window.FindName("ImageGenerateButton")).IsEnabled,
            "Store navigation reaches the native image studio without pretending an engine or checkpoint is installed");
        window.RenderImageResult(new(Job, "Fixture checkpoint, not an AI model", "Fixture preview", 0, FixturePng(), DateTimeOffset.UtcNow));
        check(((Image)window.FindName("ImagePreview")).Source is BitmapImage { PixelWidth: 2, PixelHeight: 2 },
            "Native image preview decodes actual PNG fixture pixels");
        ((RadioButton)window.FindName("HomeNav")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        check(((TabItem)window.FindName("StoreHomeTab")).IsSelected && window.FindName("LlamaCppTab") is TabItem,
            "Store navigation preserves access to existing local runtime features");
    }

    public static async Task RunLiveAsync(string root, string checkpoint, string outputDirectory, Action<bool, string> check)
    {
        using var server = new ComfyServerController();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        try
        {
            await server.StartAsync(root, 8188, deadline.Token);
            using var http = OllamaClient.CreateLocalHttpClient();
            var client = new ComfyImageClient(http);
            var inventory = await client.InspectAsync(Endpoint, deadline.Token);
            check(inventory.Checkpoints.Contains(checkpoint), "Actual local image engine reports the approved installed checkpoint");
            var monitor = new ResourceMonitor(); await monitor.SampleAsync(deadline.Token); await Task.Delay(250, deadline.Token);
            var admission = new ResourcePolicy(MinimumAvailableRamGb: 2).Evaluate(await monitor.SampleAsync(deadline.Token));
            check(admission.Allowed, "Actual available resources admit one explicit image smoke test");
            var result = await client.GenerateAsync(Endpoint, checkpoint,
                "A small red cabin beside a quiet mountain lake at sunrise, pine forest, landscape photography, soft natural light, detailed", "text, watermark, blurry", 512, 512, 20, 42753, deadline.Token);
            ComfyImageClient.ValidatePng(result.Png);
            Directory.CreateDirectory(outputDirectory);
            var path = Path.Combine(outputDirectory, "AINexus-" + result.JobId + ".png");
            await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await file.WriteAsync(result.Png, deadline.Token);
            var evidence = new { result.JobId, result.Checkpoint, result.Prompt, result.Seed, result.CompletedAt, inventory.Version,
                ImagePath = path, Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(result.Png)), Bytes = result.Png.Length,
                Width = 512, Height = 512, Steps = 20, Endpoint = Endpoint.ToString(), NoCloudInferenceRequested = true,
                Limitation = "Local runtime generation and PNG decoding verified; this is not a Windows network-isolation test or a benchmark." };
            await File.WriteAllTextAsync(Path.ChangeExtension(path, ".json"), JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }), deadline.Token);
            check(result.Png.Length > 1024, "Actual local image job completes and returns a decoded PNG rather than a placeholder");
            Console.WriteLine("Actual generated image: " + path);
        }
        finally
        {
            server.Stop();
            check(!server.IsRunning, "The owned image engine stops after the live smoke test");
            Directory.CreateDirectory(outputDirectory);
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, "image-engine-diagnostics.txt"), server.Diagnostics);
        }
    }
}
