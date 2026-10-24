using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AINexus.App;

namespace AINexus.Checks;

/// <summary>Filesystem and socket fixtures only. This suite never starts an executable or model.</summary>
internal static class LlamaServerChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var fixtureBase = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "fixtures"));
        var root = Path.GetFullPath(Path.Combine(fixtureBase, Guid.NewGuid().ToString("N")));
        if (!root.StartsWith(fixtureBase + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Fixture directory escaped its intended parent.");
        Directory.CreateDirectory(root);
        try
        {
            var executable = Path.Combine(root, "llama-server.exe");
            var model = Path.Combine(root, "fixture with spaces.gguf");
            var badModel = Path.Combine(root, "invalid.gguf");
            var shortModel = Path.Combine(root, "short.gguf");
            var unrelated = Path.Combine(root, "personal-fixture.txt");
            File.WriteAllText(executable, "Fixture only: this is not a program and must never be executed.");
            File.WriteAllBytes(model, "GGUF"u8.ToArray()); // Header fixture only, not a usable model.
            File.WriteAllText(badModel, "NOT_A_GGUF_MODEL");
            File.WriteAllBytes(shortModel, [0, 1]);
            File.WriteAllText(unrelated, "Unrelated fixture contents must remain unchanged.");

            await using var controller = new LlamaServerController();
            check(!controller.IsRunning && controller.Pid is null && controller.Endpoint is null,
                "Portable controller construction never launches or claims an external server");
            controller.StatusChanged += _ => throw new InvalidOperationException("Fixture observer failure");
            await controller.StopAsync();
            await controller.StopAsync();
            check(!controller.IsRunning, "Stopping an empty portable controller is idempotent");

            check(await ThrowsAsync<ArgumentException>(() => controller.StartAsync("llama-server.exe", model, 8081)),
                "Portable server executable paths must be absolute");
            check(await ThrowsAsync<ArgumentException>(() => controller.StartAsync(executable, "relative.gguf", 8081)),
                "Portable model paths must be absolute");
            check(await ThrowsAsync<ArgumentException>(() => controller.StartAsync(Path.Combine(root, "other.exe"), model, 8081)),
                "Portable lifecycle only accepts the selected llama-server executable name");
            check(await ThrowsAsync<FileNotFoundException>(() => controller.StartAsync(Path.Combine(root, "missing", "llama-server.exe"), model, 8081)),
                "A missing portable executable is reported before process creation");
            check(await ThrowsAsync<FileNotFoundException>(() => controller.StartAsync(executable, Path.Combine(root, "missing.gguf"), 8081)),
                "A missing GGUF model is reported before process creation");
            check(await ThrowsAsync<InvalidDataException>(() => controller.StartAsync(executable, badModel, 8081)),
                "Invalid GGUF signatures cannot start a portable runtime");
            check(await ThrowsAsync<InvalidDataException>(() => controller.StartAsync(executable, shortModel, 8081)),
                "Files shorter than the GGUF header cannot start a portable runtime");
            foreach (var port in new[] { 1023, 65536 })
                check(await ThrowsAsync<ArgumentOutOfRangeException>(() => controller.StartAsync(executable, model, port)),
                    "Portable server ports must remain inside the configured unprivileged range");
            foreach (var context in new[] { 511, 32769 })
                check(await ThrowsAsync<ArgumentOutOfRangeException>(() => controller.StartAsync(executable, model, 8081, context)),
                    "Portable context size must remain inside the configured token-policy range");
            check(!controller.IsRunning && controller.Pid is null && controller.Endpoint is null,
                "Rejected portable configurations never acquire or fabricate process ownership");

            await LlamaServerController.ValidateConfigurationAsync(executable, model, 8081, 4096);
            check(new FileInfo(model).Length == 4, "GGUF preflight inspects the header without pretending to validate or load a complete model");
            var info = LlamaServerController.CreateStartInfo(executable, model, 8081, 4096);
            var arguments = info.ArgumentList.ToArray();
            check(!info.UseShellExecute && info.CreateNoWindow && info.RedirectStandardOutput && info.RedirectStandardError,
                "Portable server starts hidden with redirected streams and no shell");
            check(arguments[Array.IndexOf(arguments, "--model") + 1] == model
                  && arguments[Array.IndexOf(arguments, "--host") + 1] == "127.0.0.1"
                  && arguments[Array.IndexOf(arguments, "--port") + 1] == "8081"
                  && arguments[Array.IndexOf(arguments, "--ctx-size") + 1] == "4096"
                  && arguments[Array.IndexOf(arguments, "--parallel") + 1] == "1"
                  && arguments[Array.IndexOf(arguments, "--n-gpu-layers") + 1] == "auto",
                "Portable launch arguments preserve selected paths, loopback binding, context policy, serial slots, and automatic GPU detection");
            check(arguments.Contains("--no-ui-mcp-proxy")
                  && arguments[Array.IndexOf(arguments, "--cors-origins") + 1] == "http://127.0.0.1:8081",
                "Portable launch disables its UI tool proxy and restricts allowed browser origin");
            check(!info.Environment.Keys.Any(name => name.StartsWith("LLAMA_", StringComparison.OrdinalIgnoreCase)
                  || name.Contains("TOKEN", StringComparison.OrdinalIgnoreCase) || name.Contains("APIKEY", StringComparison.OrdinalIgnoreCase)
                  || name.Contains("API_KEY", StringComparison.OrdinalIgnoreCase)),
                "Portable child environment excludes API keys, tokens, tool settings, and runtime overrides");

            controller.AppendOutput("Old fixture marker\n" + new string('x', 128 * 1024));
            controller.AppendOutput("Fixture tail");
            check(Encoding.UTF8.GetByteCount(controller.RecentOutput) == 64 * 1024
                  && !controller.RecentOutput.Contains("Old fixture marker") && controller.RecentOutput.EndsWith("Fixture tail"),
                "Portable diagnostics retain only the latest bounded output without creating log files");

            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                check(await ThrowsAsync<OperationCanceledException>(() => controller.StartAsync(executable, model, 8081, 4096, cancellation.Token))
                      && !controller.IsRunning, "Precancelled portable startup launches no process");
            }

            // This socket represents an existing external service. The invalid executable fixture
            // must never be launched: port ownership is rejected first, without inspecting a PID.
            var existing = new TcpListener(IPAddress.Loopback, 0);
            existing.Server.ExclusiveAddressUse = true;
            existing.Start();
            try
            {
                var occupiedPort = ((IPEndPoint)existing.LocalEndpoint).Port;
                check(await ThrowsAsync<InvalidOperationException>(() => controller.StartAsync(executable, model, occupiedPort)),
                    "An occupied loopback port is rejected before creating a portable child process");
                await controller.StopAsync();
                using var client = new TcpClient(AddressFamily.InterNetwork);
                await client.ConnectAsync(IPAddress.Loopback, occupiedPort).WaitAsync(TimeSpan.FromSeconds(3));
                using var accepted = await existing.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(3));
                await client.GetStream().WriteAsync(new byte[] { 42 });
                var payload = new byte[1];
                await accepted.GetStream().ReadExactlyAsync(payload).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
                check(payload[0] == 42 && !controller.IsRunning && controller.Pid is null,
                    "Failed startup and Stop leave an existing external listener usable and unowned");
            }
            finally { existing.Stop(); }
            check(File.ReadAllText(unrelated) == "Unrelated fixture contents must remain unchanged."
                  && File.ReadAllBytes(model).AsSpan().SequenceEqual("GGUF"u8),
                "Portable lifecycle preflight preserves model bytes and unrelated files");

            var firstDispose = controller.DisposeAsync().AsTask();
            var secondDispose = controller.DisposeAsync().AsTask();
            await Task.WhenAll(firstDispose, secondDispose);
            check(ReferenceEquals(firstDispose, secondDispose), "Concurrent portable disposal shares the same completion task");
            await controller.StopAsync();
            check(await ThrowsAsync<ObjectDisposedException>(() => controller.StartAsync(executable, model, 8081)),
                "A disposed portable controller cannot launch another server");
        }
        finally
        {
            // This unique absolute directory was created above and verified under the fixture base.
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<bool> ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action().WaitAsync(TimeSpan.FromSeconds(5)); return false; }
        catch (T) { return true; }
    }
}
