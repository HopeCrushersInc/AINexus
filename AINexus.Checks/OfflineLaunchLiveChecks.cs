using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using AINexus.App;

namespace AINexus.Checks;

/// <summary>Opt-in actual runtime launch; default checks never load this model.</summary>
internal static class OfflineLaunchLiveChecks
{
    public static async Task RunAsync(string executable, string model, Action<bool, string> check)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        int port;
        try { listener.Start(); port = ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
        var endpoint = new Uri($"http://127.0.0.1:{port}");
        await using var controller = new LlamaServerController();
        string response;
        string modelId;
        try
        {
            await controller.StartAsync(executable, model, port, 4096, deadline.Token, offlineMode: true);
            check(controller.IsRunning && controller.Endpoint == endpoint && controller.Pid is not null,
                "Actual offline-mode launch passes runtime capability preflight and owns a healthy loopback server");
            using var http = OllamaClient.CreateLocalHttpClient();
            var client = new LlamaCppClient(http, maximumTokens: 64);
            var models = await client.GetModelsAsync(endpoint, deadline.Token);
            check(models.Count == 1, "Actual offline-mode launch reports exactly one loaded model");
            modelId = models.Single();
            response = await client.GenerateAsync(endpoint, modelId, "Answer briefly. Do not use tools.",
                "Reply with exactly these two words: Portal ready.", deadline.Token);
            check(!string.IsNullOrWhiteSpace(response), "Actual offline-mode launch completes a nonempty generation");
        }
        finally { await controller.StopAsync(CancellationToken.None); }
        check(!controller.IsRunning && controller.Pid is null && controller.Endpoint is null,
            "Actual offline-mode launch releases owned-process state after Stop");
        using (var connection = new TcpClient())
        {
            var closed = false;
            try { await connection.ConnectAsync(IPAddress.Loopback, port, deadline.Token); }
            catch (SocketException) { closed = true; }
            check(closed, "Actual offline-mode launch closes its owned listening port after Stop");
        }
        var reports = Path.Combine(AppContext.BaseDirectory, "Reports", "OfflineLaunch");
        Directory.CreateDirectory(reports);
        var report = Path.Combine(reports, Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new
        {
            CompletedAt = DateTimeOffset.UtcNow, Executable = Path.GetFullPath(executable), Model = Path.GetFullPath(model),
            ModelId = modelId, ContextSize = 4096, Response = response, Endpoint = endpoint,
            Arguments = LlamaServerController.CreateStartInfo(executable, model, port, 4096, offlineMode: true).ArgumentList.ToArray(),
            OwnedServerStopped = true, OSNetworkIsolationTested = false,
            Limitations = "Development smoke test only. Does not establish publisher authenticity, model quality, artifact integrity, or future readiness."
        }, new JsonSerializerOptions { WriteIndented = true }), deadline.Token);
        Console.WriteLine($"Offline-launch response: {response}");
        Console.WriteLine($"Offline-launch report: {report}");
    }
}
