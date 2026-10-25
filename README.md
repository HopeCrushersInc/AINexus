# AI Nexus Portal

A native C# .NET 8 WPF dashboard for local AI tooling on Windows.

## Current capabilities

- Hardware-aware online discovery: explicit metadata searches on Hugging Face and supporting upstream llama.cpp release metadata. Results include declared licensing, source links, immutable model revisions, artifact sizes and SHA-256 when reported. Initial supported interests are local assistants, coding, and agents; other workflow adapters are explicitly marked unavailable.
- Explainable GGUF resource assessments compare estimates with actual free RAM, individual NVIDIA VRAM readings/driver evidence, and the selected storage volume. CPU fallback is assessed separately. A resource candidate is an unverified estimate, never an installation approval or a performance guarantee.
- Acquisition planning creates a review document with a pinned source URL, revision, byte size, SHA-256, destination and declared license reference. Planning does not download or install anything. Existing files can be checked for byte-count/hash identity without overwriting them; artifact integrity is distinct from runtime verification.
- Approved acquisition downloads public GGUF model data after explicit exact-plan/license/destination approval. Transfers follow permitted HTTPS CDN redirects, enforce source byte count and SHA-256, and publish atomically without overwriting existing files. Matching files are verified for reuse without artifact HTTP. Cancellation removes only the operation's own partial file; closing waits for cleanup. No runtime installer or Windows setup is part of a model-data acquisition.
- Completed acquisitions save a receipt under the model folder's `.ainexus/receipts`. Receipts retain pinned source and actual integrity evidence, explicitly mark runtime execution as unverified, and omit ephemeral signed CDN URLs. The acquired folder can then be added to the local library.
- Saved search evidence can be loaded without network requests and re-evaluated against fresh local readings. Cached metadata retains its retrieval timestamp. No model or runtime is needed to use discovery.
- Hardware discovery: actual CPU, installed RAM, graphics adapters, and fixed-drive capacity/free space. NVIDIA VRAM comes from the installed driver utility; unsupported VRAM remains unavailable.
- AI software discovery: llama.cpp, Ollama, LM Studio, GPT4All, Jan, AnythingLLM, Docker tooling, Python, and Git. Configured portable runtime folders are inspected without changing PATH.
- Read-only software checks inspect PATH, common installation directories, and Windows uninstall registrations. Each result distinguishes an executable found from registration-only evidence and includes location/version when available.
- Independent rescan actions with cancellation on window close.
- Live CPU and physical RAM usage from Windows counters, with NVIDIA utilization and VRAM telemetry where supported. Monitoring pauses while disabled, hidden, or minimized; the first CPU sample remains unknown until an interval is available.
- WinGet package review and explicit current-user Ollama install/update controls with captured output and result logs. GPT4All is review-only because its reviewed manifest does not declare user scope and upgrades uninstall the previous version.
- Model file library: selected folders are indexed for GGUF, SafeTensors, and ONNX metadata. Whole-drive roots and symbolic links are skipped; traversal is bounded. Removing a tracked folder does not delete its files. Shards are separate file entries rather than invented logical models.
- Existing local Ollama servers can be queried for available and loaded models using the official [tags](https://docs.ollama.com/api/tags) and [ps](https://docs.ollama.com/api/ps) endpoints. Queries accept loopback addresses only and preserve partial failures. Empty lists and unavailable endpoints are distinct states.
- Embedded local application interfaces use the WPF WebView2 control. Enter an existing loopback web-interface address to open it inside the portal. No server or external browser is started. Navigation and intercepted resources are restricted to the selected origin; downloads and popups are blocked. This is not an operating-system network sandbox, and applications needing external authentication/assets may not work with these restrictions.
- Portable llama.cpp runtime controls select an existing executable/GGUF, start a hidden loopback server, wait for health, and stop only portal-owned processes. Closing the portal stops a portal-owned server; external servers remain untouched. No runtime starts automatically.
- Local agent tasks send actual requests to a llama.cpp or Ollama model selected from its server's inventory. One serial queue captures each task's backend, endpoint, and model. Tasks support cancellation, truthful failure states, and configurable CPU/RAM/optional NVIDIA VRAM admission checks. This is a local generation task queue, without tool execution or inter-agent dependency workflows. llama.cpp currently uses a 128-token output policy; truncation is reported explicitly.

Discovery does not launch discovered applications, install software, or contact remote services. A missing result means no evidence was found in the locations checked; arbitrary portable/custom installations may be missed. Discovery does not establish runtime health. Python Windows Store execution aliases are excluded.

Package review explicitly contacts the WinGet community source. Installation/update requires reviewing the package and accepting agreements in the installer tab. Operations target one exact catalog package, use user scope and silent mode, and preserve WinGet security checks; dependencies are not installed automatically. Active installers cannot be cancelled by closing the portal. Completed operation results are saved under `%LOCALAPPDATA%/AINexus/Logs`. No applications are installed automatically at portal startup. Actual installation has not been exercised as part of automated checks.

Selected model roots, runtime paths, context policy, and local server endpoints are saved to `%LOCALAPPDATA%/AINexus/settings.json`. No folders are indexed until selected/configured. Model inventory queries do not generate text or download weights. Model file extensions do not validate file contents or compatibility. Available-model disk size, loaded memory size, and VRAM usage are displayed as distinct values.

The Agent Tasks tab performs inference only when a task is explicitly queued. Resource admission uses actual samples before a request; thresholds are user policy choices, not hardware specifications or model-fit estimates. Admission checks do not impose OS quotas or reserve memory, and server-side processing may continue after an HTTP request is cancelled. Prompts and replies are held in session memory, never written by the portal to logs. The local model server controls its own logging and retention.

Embedded browsing requires Microsoft's free Edge WebView2 Runtime, which is already installed on the restored workstation. The pinned WebView2 SDK is restored from the repository-scoped nuget.org source. Browser profile data is isolated under `%LOCALAPPDATA%/AINexus/WebView2`. Embedded app pages may retain their own data in this profile.

CPU sampling follows [GetSystemTimes](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getsystemtimes); systems with multiple processor groups are explicitly labeled with the sampled group. RAM uses [GlobalMemoryStatusEx](https://learn.microsoft.com/en-us/windows/win32/api/sysinfoapi/nf-sysinfoapi-globalmemorystatusex). Install/update semantics follow [WinGet install](https://learn.microsoft.com/en-us/windows/package-manager/winget/install) and [upgrade](https://learn.microsoft.com/en-us/windows/package-manager/winget/upgrade).

## Build and validate

Requires Windows and the .NET 8 SDK with the Windows Desktop runtime.

```powershell
dotnet build AINexus.App/AINexus.App.csproj --configuration Release
dotnet run --project AINexus.Checks/AINexus.Checks.csproj --configuration Release
dotnet run --project AINexus.Checks/AINexus.Checks.csproj --configuration Release -- --live
dotnet run --project AINexus.Checks/AINexus.Checks.csproj --configuration Release -- --live --embedded-live
dotnet run --project AINexus.Checks/AINexus.Checks.csproj --configuration Release -- --llama-live
dotnet run --project AINexus.Checks/AINexus.Checks.csproj --configuration Release -- --live --discovery-live
dotnet run --project AINexus.Checks/AINexus.Checks.csproj --configuration Release -- --acquisition-live '<existing approved model path>'
```

The checks use a small console harness without a separate test framework. Default checks exercise deterministic fixtures, HTTP behavior, resource policies, task scheduling, and WPF XAML loading without showing a window. `--live` adds actual local software/hardware readings. `--embedded-live` additionally initializes the installed WebView2 runtime in an invisible WPF window, renders an ephemeral loopback test document, and verifies navigation/resource restrictions. Build output is under `AINexus.App/bin/Release/net8.0-windows`.

`--discovery-live` checks public metadata retrieval and the complete hardware/search/assessment/cache pipeline. It makes no model download, installation or inference request. Its evidence report is saved under the checks build's `Reports/Discovery` folder. If Windows lacks symbolic-link creation privilege, the link-protection check uses a directory junction confined to its temporary fixture instead. It verifies that the scanner cannot enter the target and that link cleanup preserves target files. No developer-mode or privilege settings are changed.

`--acquisition-live` refreshes public source evidence and verifies reuse of the selected existing approved Qwen model without an artifact download. The separate explicit `--acquisition-download-live '<existing approved model path>'` test additionally downloads a duplicate of that same artifact under the checks build's `Reports/Acquisition`, independently rehashes it, and saves a receipt. The real 1,117,320,736-byte Qwen transfer passed size and SHA-256 checks; no inference or system-wide installer ran as part of acquisition testing.

Generation HTTP and scheduling are validated with explicit test fixtures. Opt-in live checks also passed against the portable llama.cpp runtime and real Qwen GGUF, including native Start/Stop, task generation, ownership isolation, close cleanup, and the actual embedded server UI. Automated checks do not install applications or download weights. See [portable setup](docs/llama-cpp-setup.md) for workstation paths, commands, provenance, and live validation flags; see [DEVELOPMENT.md](DEVELOPMENT.md) for remaining work.

## Incremental roadmap

The definitive workflow and prioritized remaining work are documented in [the master development roadmap](docs/development-roadmap.md). System-wide Windows changes require approval; project work and evidence artifacts remain separate from system configuration.

1. Hardware discovery and monitoring.
2. AI software discovery.
3. Installer/updater with explicit plans, verified package identities, and captured outcomes.
4. AI libraries and local models.
5. Embedded interfaces and resource governance.
6. Agent orchestration within the unified native dashboard.

Install/update, resource limits, and agent controls will only be exposed as those operations are implemented and validated. Repository changes are committed locally; pushing requires authorization.

## Offline verification checkpoint

The **Verify Offline** tab separates a read-only artifact audit from an explicit model-loading smoke test. Select a saved acquisition receipt and an existing llama.cpp executable. Audit checks the receipt and actual model hash without launching a runtime. The smoke test checks runtime file identities, current hardware/resources and advertised `--offline` support, then starts one owned loopback server, requests a brief completion, stops the server and saves successful evidence beside the model under `.ainexus/verification`.

This establishes actual generation with runtime offline mode; it does not establish Windows firewall isolation, response quality or future compatibility after files/hardware change. No Windows settings are changed. Cancellation and window close stop only the test's owned server. Existing model bytes do not require another download's worth of free disk during verification.

Opt-in real verification (loads the selected model and can use the GPU):

```powershell
dotnet run --project AINexus.Checks/AINexus.Checks.csproj -c Release -- --offline-verification-live '<acquisition receipt path>' '<llama-server.exe path>'
```

The restored workstation passed this test with its existing approved Qwen artifact and portable llama.cpp runtime: response `Portal ready.`, recorded model/runtime hashes, saved evidence, and a closed owned server port. Default checks do not launch real inference.
