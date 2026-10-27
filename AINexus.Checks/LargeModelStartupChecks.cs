using System.IO;
using System.Net;
using System.Net.Sockets;
using AINexus.App;

namespace AINexus.Checks;

internal static class LargeModelStartupChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        using (var controller = new LlamaServerController())
        {
            check(controller.StartupTimeout == TimeSpan.FromMinutes(5) && !controller.IsRunning,
                "Large-model startup has a bounded five-minute default without launching any process");
            controller.CancelStartup();
            controller.CancelStartup();
            check(!controller.IsRunning && controller.Pid is null,
                "Cancelling an idle startup is harmless and acquires no process ownership");
        }

        foreach (var timeout in new[] { TimeSpan.Zero, TimeSpan.FromMilliseconds(999), TimeSpan.FromSeconds(-1), TimeSpan.FromMinutes(10) + TimeSpan.FromTicks(1) })
        {
            var rejected = false;
            try { using var controller = new LlamaServerController(timeout); }
            catch (ArgumentOutOfRangeException) { rejected = true; }
            check(rejected, "Startup timeout configuration rejects values outside the bounded supported range");
        }
        using (var shortest = new LlamaServerController(TimeSpan.FromSeconds(1)))
        using (var longest = new LlamaServerController(TimeSpan.FromMinutes(10)))
            check(shortest.StartupTimeout == TimeSpan.FromSeconds(1) && longest.StartupTimeout == TimeSpan.FromMinutes(10),
                "Explicit startup deadlines accept the documented boundary values");

        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var executable = Path.Combine(root, "llama-server.exe");
            var model = Path.Combine(root, "fixture.gguf");
            await File.WriteAllTextAsync(executable, "Inert startup fixture. Never execute.");
            await File.WriteAllBytesAsync(model, "GGUF"u8.ToArray());
            var reachedProbe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var controller = new LlamaServerController(async (_, _, _, token) =>
            {
                reachedProbe.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            });
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();

            var startup = controller.StartAsync(executable, model, port, offlineMode: true);
            await reachedProbe.Task.WaitAsync(TimeSpan.FromSeconds(5));
            controller.CancelStartup();
            var cancelled = false;
            try { await startup.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { cancelled = true; }
            check(cancelled && !controller.IsRunning && controller.Pid is null && controller.Endpoint is null,
                "Explicit load cancellation interrupts an in-progress startup probe before any model process launches");
            await controller.StopAsync();
            check(!controller.IsRunning && (await File.ReadAllBytesAsync(model)).SequenceEqual("GGUF"u8.ToArray()),
                "Cancelled startup completes ownership cleanup without modifying the selected model");
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
