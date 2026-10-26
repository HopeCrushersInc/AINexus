using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Windows.Media.Imaging;

namespace AINexus.App;

public sealed record ComfyInventory(string Version, IReadOnlyList<string> Checkpoints, DateTimeOffset CheckedAt);
public sealed record ComfyImageResult(string JobId, string Checkpoint, string Prompt, long Seed, byte[] Png, DateTimeOffset CompletedAt);

/// <summary>Explicit local standard-checkpoint image workflow. No downloads, custom nodes or cloud APIs.</summary>
public sealed class ComfyImageClient(HttpClient http)
{
    private static readonly string[] RequiredNodes = ["CheckpointLoaderSimple", "CLIPTextEncode", "EmptyLatentImage", "KSampler", "VAEDecode", "SaveImage"];

    public static void ValidateEndpoint(Uri endpoint)
    {
        OllamaClient.ValidateEndpoint(endpoint);
        if (!IPAddress.TryParse(endpoint.Host.Trim('[', ']'), out var address) || !IPAddress.IsLoopback(address))
            throw new ArgumentException("Use a literal loopback address such as http://127.0.0.1:8188. Network servers require separate authorization.");
    }

    public async Task<ComfyInventory> InspectAsync(Uri endpoint, CancellationToken token = default)
    {
        ValidateEndpoint(endpoint);
        using var stats = await JsonAsync(endpoint, "/system_stats", null, token);
        var version = stats.RootElement.GetProperty("system").GetProperty("comfyui_version").GetString();
        if (string.IsNullOrWhiteSpace(version)) throw new InvalidDataException("The server did not identify a ComfyUI version.");
        using var nodes = await JsonAsync(endpoint, "/object_info", null, token);
        foreach (var node in RequiredNodes)
            if (!nodes.RootElement.TryGetProperty(node, out _)) throw new NotSupportedException("The engine is missing a standard workflow node: " + node);
        var checkpoints = Choices(nodes.RootElement, "CheckpointLoaderSimple", "ckpt_name");
        if (!Choices(nodes.RootElement, "KSampler", "sampler_name").Contains("euler")
            || !Choices(nodes.RootElement, "KSampler", "scheduler").Contains("normal"))
            throw new NotSupportedException("The standard Euler/normal workflow is not available on this engine.");
        return new(version, checkpoints, DateTimeOffset.UtcNow);
    }

    public async Task<ComfyImageResult> GenerateAsync(Uri endpoint, string checkpoint, string prompt, string negative,
        int width, int height, int steps, long seed, CancellationToken token = default)
    {
        ValidateEndpoint(endpoint);
        if (string.IsNullOrWhiteSpace(prompt) || prompt.Length > 4000 || negative.Length > 4000
            || width is < 256 or > 1024 || height is < 256 or > 1024 || width % 64 != 0 || height % 64 != 0
            || steps is < 1 or > 50 || seed < 0) throw new ArgumentException("Choose a prompt, dimensions from 256–1024 in multiples of 64, and 1–50 steps.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        var inventory = await InspectAsync(endpoint, deadline.Token);
        if (!inventory.Checkpoints.Contains(checkpoint, StringComparer.Ordinal)) throw new InvalidOperationException("Select an actual checkpoint reported by this engine.");
        using var queue = await JsonAsync(endpoint, "/queue", null, deadline.Token);
        if (queue.RootElement.GetProperty("queue_running").GetArrayLength() > 0 || queue.RootElement.GetProperty("queue_pending").GetArrayLength() > 0)
            throw new InvalidOperationException("The image engine already has work. Wait until its queue is idle before submitting a new image.");
        string? id = null;
        try
        {
            using var accepted = await JsonAsync(endpoint, "/prompt", new { prompt = CreateWorkflow(checkpoint, prompt, negative, width, height, steps, seed) }, deadline.Token);
            id = accepted.RootElement.GetProperty("prompt_id").GetString();
            if (!Guid.TryParse(id, out _)) throw new InvalidDataException("The image engine did not return a valid job identity.");
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                using var history = await JsonAsync(endpoint, "/history/" + id, null, deadline.Token);
                if (history.RootElement.TryGetProperty(id!, out var job))
                {
                    var status = job.GetProperty("status");
                    var outcome = status.GetProperty("status_str").GetString();
                    if (outcome is "error" or "interrupted") throw new InvalidOperationException("The image engine reported " + outcome + ". No image was verified.");
                    if (status.TryGetProperty("completed", out var completed) && completed.ValueKind == JsonValueKind.True && outcome == "success")
                    {
                        var image = job.GetProperty("outputs").GetProperty("9").GetProperty("images")[0];
                        var filename = image.GetProperty("filename").GetString()!;
                        var subfolder = image.GetProperty("subfolder").GetString() ?? "";
                        if (image.GetProperty("type").GetString() != "output" || !SafePart(filename) || filename.Contains('/') || filename.Contains('\\')
                            || subfolder.StartsWith('/') || subfolder.StartsWith('\\')
                            || subfolder.Split(['/', '\\']).Any(part => part.Length > 0 && !SafePart(part)))
                            throw new InvalidDataException("The engine returned an unsafe image reference.");
                        var path = "/view?filename=" + Uri.EscapeDataString(filename) + "&subfolder=" + Uri.EscapeDataString(subfolder) + "&type=output";
                        var bytes = await ReadAsync(endpoint, path, null, 16 * 1024 * 1024, deadline.Token);
                        ValidatePng(bytes);
                        return new(id!, checkpoint, prompt, seed, bytes, DateTimeOffset.UtcNow);
                    }
                }
                await Task.Delay(500, deadline.Token);
            }
        }
        catch (OperationCanceledException)
        {
            var cancelled = false;
            if (id is not null && Guid.TryParse(id, out _))
            {
                try
                {
                    using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    using var response = await JsonAsync(endpoint, "/api/jobs/" + id + "/cancel", new { }, cancel.Token);
                    cancelled = response.RootElement.TryGetProperty("cancelled", out var value) && value.ValueKind == JsonValueKind.True;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or OperationCanceledException) { }
            }
            throw new OperationCanceledException(cancelled ? "The engine accepted cancellation of this image job."
                : "Stopped waiting. Cancellation was not confirmed; the image engine may still be working. Other jobs were not interrupted.", token);
        }
    }

    internal static Dictionary<string, object> CreateWorkflow(string checkpoint, string prompt, string negative, int width, int height, int steps, long seed) => new()
    {
        ["4"] = new { class_type = "CheckpointLoaderSimple", inputs = new { ckpt_name = checkpoint } },
        ["5"] = new { class_type = "EmptyLatentImage", inputs = new { width, height, batch_size = 1 } },
        ["6"] = new { class_type = "CLIPTextEncode", inputs = new { text = prompt, clip = new object[] { "4", 1 } } },
        ["7"] = new { class_type = "CLIPTextEncode", inputs = new { text = negative, clip = new object[] { "4", 1 } } },
        ["3"] = new { class_type = "KSampler", inputs = new { seed, steps, cfg = 7, sampler_name = "euler", scheduler = "normal", denoise = 1,
            model = new object[] { "4", 0 }, positive = new object[] { "6", 0 }, negative = new object[] { "7", 0 }, latent_image = new object[] { "5", 0 } } },
        ["8"] = new { class_type = "VAEDecode", inputs = new { samples = new object[] { "3", 0 }, vae = new object[] { "4", 2 } } },
        ["9"] = new { class_type = "SaveImage", inputs = new { images = new object[] { "8", 0 }, filename_prefix = "AINexus" } }
    };

    private static string[] Choices(JsonElement nodes, string node, string key) => nodes.GetProperty(node).GetProperty("input").GetProperty("required").GetProperty(key)[0]
        .EnumerateArray().Take(5000).Select(value => value.GetString()).Where(value => !string.IsNullOrWhiteSpace(value) && value.Length < 512).Cast<string>().ToArray();
    private static bool SafePart(string value) => value.Length is > 0 and < 256 && value is not ("." or "..") && !value.Any(c => char.IsControl(c) || c is ':' or '?' or '#');
    internal static void ValidatePng(byte[] png)
    {
        if (png.Length < 24 || !png.AsSpan(0, 8).SequenceEqual(new byte[] { 137,80,78,71,13,10,26,10 })
            || !png.AsSpan(12,4).SequenceEqual("IHDR"u8)) throw new InvalidDataException("The engine did not return a PNG image.");
        var w = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16,4));
        var h = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20,4));
        if (w is < 1 or > 2048 || h is < 1 or > 2048 || (long)w*h > 4_194_304) throw new InvalidDataException("The returned image exceeds preview limits.");
        using var stream = new MemoryStream(png);
        var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        if (frame.Format.BitsPerPixel is < 1 or > 64 || frame.PixelWidth != w || frame.PixelHeight != h)
            throw new InvalidDataException("The returned PNG dimensions or pixel format are unsupported.");
        var stride = checked((frame.PixelWidth * frame.Format.BitsPerPixel + 7) / 8);
        frame.CopyPixels(new byte[checked(stride * frame.PixelHeight)], stride, 0);
    }
    private async Task<JsonDocument> JsonAsync(Uri endpoint, string path, object? body, CancellationToken token) => JsonDocument.Parse(await ReadAsync(endpoint, path, body, 8*1024*1024, token));
    private async Task<byte[]> ReadAsync(Uri endpoint, string path, object? body, int limit, CancellationToken token)
    {
        ValidateEndpoint(endpoint);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var uri = new Uri(endpoint, path);
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, uri);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (response.RequestMessage?.RequestUri != uri || (int)response.StatusCode is >= 300 and < 400) throw new InvalidDataException("Image-engine redirects are not allowed.");
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("The image-engine response exceeds its size limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var data = new MemoryStream();
        var buffer = new byte[32768];
        int read;
        while ((read = await stream.ReadAsync(buffer, deadline.Token)) != 0)
        { if (data.Length + read > limit) throw new InvalidDataException("The image-engine response exceeds its size limit."); await data.WriteAsync(buffer.AsMemory(0,read), deadline.Token); }
        return data.ToArray();
    }
}
