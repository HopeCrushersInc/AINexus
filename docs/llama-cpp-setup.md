# Portable llama.cpp setup

The restored workstation uses an upstream Windows x64 CUDA 12.4 build of llama.cpp and a small GGUF model for local integration testing. Ollama was not installed.

## Files on this workstation

- Runtime: `F:\AINexus\Runtimes\llama.cpp\b11669\llama-server.exe`
- Model: `F:\AINexus\Models\Qwen2.5-1.5B-Instruct\qwen2.5-1.5b-instruct-q4_k_m.gguf`
- Model license: Apache-2.0; a copy is beside the model.
- Runtime license and download provenance: beside the runtime.
- Verified source archives: under `F:\AINexus\Downloads`, outside the Git repository.

Downloads were verified against upstream SHA-256 values before extraction/use:

| Artifact | SHA-256 |
| --- | --- |
| llama.cpp b11669 Windows CUDA 12.4 archive | `c2da1f73ed176ae093ac5aceb978a7b167a16113f6d74c6789eeaee2135cb8dc` |
| CUDA 12.4 runtime DLL archive | `8c79a9b226de4b3cacfd1f83d24f962d0773be79f1e7b75c6af4ded7e32ae1d6` |
| Qwen2.5-1.5B-Instruct Q4_K_M GGUF | `6a1a2eb6d15622bf3c96857206351ba97e1af16c30d7a74ee38970e434e9407e` |

Sources: [upstream llama.cpp release](https://github.com/ggml-org/llama.cpp/releases/tag/b11669), [pinned official Qwen model revision](https://huggingface.co/Qwen/Qwen2.5-1.5B-Instruct-GGUF/tree/91cad51170dc346986eccefdc2dd33a9da36ead9).

No system-wide installation, persistent PATH changes, startup registration, or automatic model downloads are needed. Runtime and model files are outside the source repository and are not committed to Git.

## Native portal controls

Open **llama.cpp Models**, select the executable and GGUF file, and choose **Start server**. Startup uses a hidden process bound to loopback, one server slot, automatic GPU offload, and a configurable context size. The default 4096-token context is a policy choice, not a hardware specification. Model IDs come from the running server.

The portal stops only the server process it started. Existing external servers remain separate. No server is started automatically when the portal opens. Closing the portal stops a portal-owned server. User-selected launch paths/context and endpoint are saved in the application's preferences.

For a server already running, query its endpoint without starting another process. In **Agent Tasks**, select **llama.cpp**, refresh model IDs, select an actual ID, and queue a prompt. Ollama remains an optional supported adapter.

For the server's local web interface, use **Embedded Apps** with `http://127.0.0.1:8080`. The host retains its same-origin navigation/resource restrictions and blocks external windows/downloads.

## Optional script controls

The scripts also run the server hidden and do not register a Windows service:

```powershell
.\scripts\Start-LlamaServer.ps1 `
  -RuntimeDirectory 'F:\AINexus\Runtimes\llama.cpp\b11669' `
  -ModelPath 'F:\AINexus\Models\Qwen2.5-1.5B-Instruct\qwen2.5-1.5b-instruct-q4_k_m.gguf' `
  -LogDirectory 'F:\AINexus\Logs'
```

The script prints a process receipt path. To stop that specific script-started server:

```powershell
.\scripts\Stop-LlamaServer.ps1 -ReceiptPath '<printed process receipt path>'
```

Stop verifies process identity and creation time before acting. It preserves model files and logs. Native portal Stop controls do not stop a server launched by this script.

## Validation

```powershell
dotnet build AINexus.App/AINexus.App.csproj --configuration Release
dotnet run --project AINexus.Checks/AINexus.Checks.csproj --configuration Release -- --live --embedded-live --llama-live
```

`--llama-live` explicitly submits a short real prompt through the telemetry-gated serial scheduler. It reads the single actual model ID from `http://127.0.0.1:8080/v1/models`; it does not start a server or download weights. The initial integration test generated `Portal ready.` from the real model. Model behavior beyond the smoke prompt is not established by that check.

To additionally exercise actual native Start/Stop, generation controls, the embedded llama.cpp web UI, and owned-process cleanup on close:

```powershell
dotnet run --project AINexus.Checks/AINexus.Checks.csproj --configuration Release -- `
  --live --embedded-live --llama-live `
  --llama-ui-live `
  'F:\AINexus\Runtimes\llama.cpp\b11669\llama-server.exe' `
  'F:\AINexus\Models\Qwen2.5-1.5B-Instruct\qwen2.5-1.5b-instruct-q4_k_m.gguf'
```

The native UI check uses an additional temporary loopback port and stops only its owned child, confirming that the existing server on 8080 remains healthy. It saves launch preferences through the actual UI code; restore the preferred endpoint/context afterward if needed.
