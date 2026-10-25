using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using AINexus.App;

namespace AINexus.Checks;

internal static class ModelChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var models = Path.Combine(root, "models");
            Directory.CreateDirectory(Path.Combine(models, "nested"));
            File.WriteAllBytes(Path.Combine(models, "fixture.GGUF"), new byte[17]);
            File.WriteAllBytes(Path.Combine(models, "nested", "fixture.safetensors"), new byte[23]);
            File.WriteAllText(Path.Combine(models, "personal.txt"), "Unrelated fixture - leave untouched");
            var scan = ModelLibrary.ScanLocations([models, models.ToUpperInvariant(), Path.Combine(models, "nested")]);
            check(scan.Files.Count == 2 && scan.Files.Sum(file => file.SizeBytes) == 40, "Model library detects formats and exact file sizes without duplicates");
            check(File.ReadAllText(Path.Combine(models, "personal.txt")) == "Unrelated fixture - leave untouched", "Library scan preserves unrelated files");
            check(ModelLibrary.ScanLocations([models], maxEntries: 1).Truncated, "Library traversal enforces its entry limit");
            check(ModelLibrary.ScanLocations([models], maxDepth: 0).Truncated, "Library traversal enforces its depth limit");
            check(ModelLibrary.ScanLocations([Path.GetPathRoot(root)!]).Files.Count == 0, "Whole-drive model scans are rejected");
            check(ModelLibrary.ScanLocations([Path.Combine(root, "missing")]).Warnings.Count > 0, "Missing model folders are reported");
            var link = Path.Combine(models, "outside-link");
            var outside = Path.Combine(root, "outside-models");
            Directory.CreateDirectory(outside);
            File.WriteAllBytes(Path.Combine(outside, "must-not-be-indexed.gguf"), new byte[29]);
            try
            {
                Directory.CreateSymbolicLink(link, outside);
            }
            catch (IOException ex) when (OperatingSystem.IsWindows() && (ex.HResult & 0xffff) == 1314)
            {
                // Both paths are this run's newly created fixture directories. A directory
                // junction exercises the same reparse-point protection without changing policy.
                if (new[] { link, outside }.Any(path => path.IndexOfAny("%\"&|<>^()".ToCharArray()) >= 0 || path.Any(char.IsControl)))
                    throw new InvalidOperationException("Fixture paths cannot be safely passed to the Windows junction command.");
                await HardwareScanner.RunProcessAsync(Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                    ["/d", "/v:off", "/c", "mklink", "/J", link, outside], CancellationToken.None, TimeSpan.FromSeconds(5));
                Console.WriteLine($"INFO: Using a directory-junction fixture on {Path.GetPathRoot(root)}; Windows symlink privilege was unavailable. No Windows settings changed.");
            }
            check(File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint), "Link fixture is an actual filesystem reparse point");
            var linkedScan = ModelLibrary.ScanLocations([models]);
            check(linkedScan.Warnings.Any(warning => warning.Contains("symbolic link or junction"))
                && linkedScan.Files.All(file => file.Name != "must-not-be-indexed.gguf"),
                "Model traversal rejects directory links and never indexes the outside target");
            check(ModelLibrary.ScanLocations([link]).Files.Count == 0,
                "Selecting a linked root does not bypass the model-library traversal boundary");
            RemoveFixtureLink(root);
            check(File.Exists(Path.Combine(outside, "must-not-be-indexed.gguf")),
                "Removing the temporary directory link preserves its target files");

            var settingsPath = Path.Combine(root, "settings.json");
            var store = new PortalSettingsStore(settingsPath);
            check((await store.LoadAsync()).Settings.ModelRoots.Count == 0, "Missing settings produce an empty library");
            await store.SaveAsync(new([models], "http://127.0.0.1:11434")
            {
                LlamaCppEndpoint = "http://127.0.0.1:8081", LlamaCppContextSize = 2048,
                LlamaCppExecutablePath = Path.Combine(root, "llama-server.exe"), LlamaCppModelPath = Path.Combine(models, "fixture.GGUF")
            });
            check((await store.LoadAsync()).Settings.ModelRoots.Single() == models, "Selected library roots survive a settings round trip");
            var saved = (await store.LoadAsync()).Settings;
            check(saved.LlamaCppContextSize == 2048 && saved.LlamaCppEndpoint == "http://127.0.0.1:8081" && saved.LlamaCppExecutablePath.EndsWith("llama-server.exe"),
                "Portable llama.cpp launch preferences survive a settings round trip");
            File.WriteAllText(settingsPath, "{\"ModelRoots\":[],\"OllamaEndpoint\":\"http://127.0.0.1:11434\",\"LlamaCppEndpoint\":\"invalid URI\"}");
            check((await store.LoadAsync()).Warning is not null, "Malformed saved llama.cpp endpoints cannot crash startup");
            File.WriteAllText(settingsPath, "corrupt fixture");
            check((await store.LoadAsync()).Warning is not null && File.ReadAllText(settingsPath) == "corrupt fixture", "Corrupt settings are reported and preserved");
        }
        finally
        {
            RemoveFixtureLink(root);
            Directory.Delete(root, recursive: true);
        }

        var parsed = OllamaClient.ParseModels("""
            {"models":[{"name":"fixture:small","size":1024,"details":{"parameter_size":"fixture","quantization_level":"Q4"}},{"model":"fixture:unknown","size":-1}]}
            """);
        check(parsed.Count == 2 && parsed[0].SizeBytes == 1024 && parsed[1].SizeBytes is null && parsed[1].VramSizeBytes is null,
            "Ollama fields use actual API values and leave missing or invalid data unknown");
        foreach (var invalid in new[] { "https://example.com", "file:///C:/", "http://user:password@localhost:11434", "http://localhost:11434/api/tags" })
        {
            var rejected = false;
            try { OllamaClient.ValidateEndpoint(new Uri(invalid)); }
            catch (ArgumentException) { rejected = true; }
            check(rejected, "Ollama endpoint rejects remote addresses, credentials and non-base URIs");
        }

        var requests = new List<string>();
        using (var http = new HttpClient(new FixtureHandler((request, _) =>
        {
            requests.Add(request.Method + " " + request.RequestUri!.AbsolutePath);
            return Task.FromResult(request.RequestUri.AbsolutePath == "/api/tags"
                ? Json(HttpStatusCode.OK, "{\"models\":[]}") : Json(HttpStatusCode.ServiceUnavailable, "unavailable"));
        })))
        {
            var snapshot = await new OllamaClient(http).ScanAsync(new("http://127.0.0.1:11434"));
            check(snapshot.AvailableModelsSucceeded && snapshot.AvailableModels.Count == 0 && !snapshot.RunningModelsSucceeded,
                "Independent Ollama failures preserve successful empty inventories");
            check(requests.Order().SequenceEqual(new[] { "GET /api/ps", "GET /api/tags" }), "Ollama inventory only issues the two read-only GET requests");
        }
        using (var http = new HttpClient(new FixtureHandler((_, _) => Task.FromResult(Json(HttpStatusCode.Redirect, "")))))
        {
            var snapshot = await new OllamaClient(http).ScanAsync(new("http://localhost:11434"));
            check(!snapshot.AvailableModelsSucceeded && snapshot.Notes.All(note => note.Contains("Redirects")), "Local inventory rejects redirects");
        }
        using (var http = new HttpClient(new FixtureHandler((_, _) =>
        {
            var response = Json(HttpStatusCode.OK, "{}");
            response.Content.Headers.ContentLength = OllamaClient.MaximumResponseBytes + 1;
            return Task.FromResult(response);
        })))
        {
            var snapshot = await new OllamaClient(http).ScanAsync(new("http://localhost:11434"));
            check(!snapshot.AvailableModelsSucceeded && snapshot.Notes.All(note => note.Contains("size limit")), "Ollama responses are bounded before parsing");
        }
        using (var http = new HttpClient(new FixtureHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Json(HttpStatusCode.OK, "{\"models\":[]}");
        })))
        using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50)))
        {
            var cancelled = false;
            try { await new OllamaClient(http).ScanAsync(new("http://localhost:11434"), cancellation.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            check(cancelled, "Ollama inventory propagates cancellation");
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body) };

    private static void RemoveFixtureLink(string root)
    {
        var fixtureBase = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "fixtures")) + Path.DirectorySeparatorChar;
        var absoluteRoot = Path.GetFullPath(root);
        if (!absoluteRoot.StartsWith(fixtureBase, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(Path.GetFileName(absoluteRoot), "N", out _))
            throw new InvalidOperationException("Link cleanup is restricted to this run's uniquely named fixture directory.");
        var link = Path.Combine(absoluteRoot, "models", "outside-link");
        FileAttributes attributes;
        try { attributes = File.GetAttributes(link); }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        if (!attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException("The fixture link changed into a normal entry; cleanup stopped.");
        // RemoveDirectoryW unlinks a junction without deleting or traversing its target.
        // https://learn.microsoft.com/windows/win32/api/fileapi/nf-fileapi-removedirectoryw
        if (!RemoveDirectoryLink(link)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    [DllImport("kernel32.dll", EntryPoint = "RemoveDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveDirectoryLink(string path);

    private sealed class FixtureHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
