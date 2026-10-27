# Current workflow requirement

Open Nexus → scan hardware → user chooses workload(s) → explicit live online search → user chooses a result → separate review/approval for supported acquisition → separate verification/launch. All categories are visible and unchecked. No model/provider is promoted or preselected. Older featured-card instructions below describe preview.1 and are superseded by this requirement.

# Technical overview and validation

[Back to the project homepage](../README.md). Detailed implementation boundaries and opt-in validation commands follow.

A native C# .NET 8 WPF dashboard for local AI tooling on Windows.

**Development preview · Updated 2026-10-26.** See the [changelog](../CHANGELOG.md) for published changes, validation results and known limitations. The workflow is hardware scan → compatible-model discovery → explicit installation/configuration approval → verification → local offline launch. Compatibility estimates remain unverified until an actual runtime test succeeds.

## Project guide and communication

- [Feature overview](feature-overview.md): live editing, model switching, maintenance, training, custom windows/workflows and services.
- [Roadmap and platform projection](development-roadmap.md): delivery gates for Windows, Linux, macOS and ARM64.
- [Community and wiki-style guide](community.md): where to check updates, ask questions and contribute ideas.
- [Project forum](https://github.com/HopeCrushersInc/AINexus/discussions) · [Wiki](https://github.com/HopeCrushersInc/AINexus/wiki) · [Changelog](../CHANGELOG.md). Wiki feature is enabled; its first page is pending. Private Discord is proposed, not yet created.

## Download for Windows

Get the portable **Windows x64 preview** from [GitHub Releases](https://github.com/HopeCrushersInc/AINexus/releases). Download the ZIP asset, extract the whole folder, and run `AINexus.App.exe`. The package includes .NET; models and external AI engines are separate. Embedded interfaces need the free WebView2 Runtime. Windows stays on its local account; no Microsoft sign-in is required. See [release and installation guide](releases.md) for package contents, checksums and limitations.

## Install a model and talk to it

1. Open Nexus, let the hardware scan finish, choose workload categories and confirm to search their sources. No category or model is preselected.
2. Select an actual result, review its exact artifact size, source/license, current hardware estimate and destination, then explicitly approve its download. An exact repository query exposes more file variants within the result limit.
3. Use an existing llama.cpp engine or review/download/install the portable CPU engine on the same page.
4. Choose **Open chat**. Nexus checks model integrity, loads the model, and tests a real response before enabling chat. GPU power control is a separate optional action.

Previously acquired models can be reused by selecting their exact source artifact and existing model folder; bytes are checked against the reviewed identity before reuse and loading. Chat keeps actual multi-turn history in session memory. Closing the portal stops its owned server. Cold model loading has a five-minute bound and can be cancelled.

**Verified on the development PC:** Qwen 2.5 32B Q4_K_M (19,851,336,576 bytes) and official Qwen 2.5 Coder 7B Q4_K_M (4,683,073,536 bytes), each installed or integrity-verified through the guided UI and tested with two real chat turns. This is evidence for that PC/runtime/model combination, not a universal performance or hardware claim.

Earlier model-card fixtures and publisher-described adult-oriented examples remain documented as historical validation/source records in [model sources](featured-models.md). They are not the current startup catalog or recommendations; live discovery is driven by the user's selected categories and query.

There is no fixed GPU wattage or power-control launch gate. In Performance, the user can inspect reported NVIDIA power limits, select a specific card by UUID, enter a supported limit and explicitly approve applying it. Reading does not write; applying touches only that card and verifies the driver readback. No card, wattage or approval is restored automatically. Unsupported or denied control does not block ordinary model launch. The development PC's owner-requested 250 W setting is workstation-specific, not a product default. Changing a limit may need administrator rights; the app does not silently elevate or change Windows accounts. UPS VA rating and PSU capacity do not establish current system draw. Fan control, thermal shutdown actions, automatic text/image switching and iCUE work remain unfinished.

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
- Read-only recovery review: after a missing/changed GGUF result, choose **Review recovery steps** and an existing separate folder. The review rechecks the file, preserves nested artifact paths, shows historical source/hash/terms and a current disk-space snapshot, and explains fresh discovery, separate download approval, integrity verification and offline testing. It never downloads, replaces or deletes a file. Missing files can be checked from an existing library row; a rescan removes missing entries.
- Read-only library maintenance: select a GGUF, choose **Check selected file**, and select its AI Nexus acquisition receipt. The check validates the receipt/path and compares current bytes/SHA-256, distinguishing matches, missing files and size/hash mismatches. It preserves files and does not establish runtime readiness. Checks run only when explicitly requested; selection changes and rescans clear old results. Other formats and models without acquisition receipts remain unsupported by this check.
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

Generation HTTP and scheduling are validated with explicit test fixtures. Opt-in live checks also passed against the portable llama.cpp runtime and real Qwen GGUF, including native Start/Stop, task generation, ownership isolation, close cleanup, and the actual embedded server UI. Default checks do not install applications or download weights. See [portable setup](llama-cpp-setup.md) for workstation paths, commands, provenance, and live validation flags; see [DEVELOPMENT.md](../DEVELOPMENT.md) for remaining work.

## Incremental roadmap

The definitive workflow and prioritized remaining work are documented in [the master development roadmap](development-roadmap.md). System-wide Windows changes require approval; project work and evidence artifacts remain separate from system configuration.

1. Hardware discovery and monitoring.
2. AI software discovery.
3. Installer/updater with explicit plans, verified package identities, and captured outcomes.
4. AI libraries and local models.
5. Embedded interfaces and resource governance.
6. Agent orchestration within the unified native dashboard.

Install/update, resource limits, and agent controls will only be exposed as those operations are implemented and validated. Repository changes are committed locally; pushing requires authorization.

## Offline verification checkpoint

Normal **Start server** now requests llama.cpp runtime offline mode too. The selected runtime must advertise the exact `--offline` option in its bounded help query. Unsupported runtimes are rejected rather than started online. Runtime/model paths must be local and must not traverse reparse points. This does not establish Windows network isolation or create a new verification receipt.

The opt-in development check below loads an existing model, exercises actual generation, and verifies owned-server cleanup without changing saved launch preferences:

```powershell
dotnet run --project AINexus.Checks/AINexus.Checks.csproj -c Release -- --offline-launch-live '<llama-server.exe path>' '<existing GGUF path>'
```

The **Verify Offline** tab separates a read-only artifact audit from an explicit model-loading smoke test. Select a saved acquisition receipt and an existing llama.cpp executable. Audit checks the receipt and actual model hash without launching a runtime. The smoke test checks runtime file identities, current hardware/resources and advertised `--offline` support, then starts one owned loopback server, requests a brief completion, stops the server and saves successful evidence beside the model under `.ainexus/verification`.

This establishes actual generation with runtime offline mode; it does not establish Windows firewall isolation, response quality or future compatibility after files/hardware change. No Windows settings are changed. Cancellation and window close stop only the test's owned server. Existing model bytes do not require another download's worth of free disk during verification.

Opt-in real verification (loads the selected model and can use the GPU):

```powershell
dotnet run --project AINexus.Checks/AINexus.Checks.csproj -c Release -- --offline-verification-live '<acquisition receipt path>' '<llama-server.exe path>'
```

The restored workstation passed this test with its existing approved Qwen artifact and portable llama.cpp runtime: response `Portal ready.`, recorded model/runtime hashes, saved evidence, and a closed owned server port. Default checks do not launch real inference.

## Runtime release evidence

Discovery now retains the upstream llama.cpp release's published ZIP/tar archives, exact download links, byte counts, SHA-256 digests when supplied, and prerelease/immutability flags. The native details panel and saved catalog show this evidence. Invalid paths, foreign origins, wrong-release links, duplicate archives and malformed sizes are excluded; missing digests stay unknown. A release with only a marker file is explicitly reported as having no supported archives. Discovery does not follow that marker, substitute a nightly, download a runtime or claim archive compatibility.

Source schema: [GitHub release assets API](https://docs.github.com/en/rest/releases/assets). Architecture/backend selection, companion dependencies, safe extraction and approved runtime acquisition remain separate work.

## Hardware-aware runtime setup review

Select a runtime discovery result, enter a **new runtime folder** on the assessed volume and choose **Review runtime setup**. The review recognizes Windows CPU/CUDA/Vulkan package labels, compares native architecture with the actual scan, distinguishes CUDA support archives, and explains missing backend evidence. Existing folders, drive roots, network destinations and directory links are rejected. No folder is created by reviewing a plan.

A platform match does not establish instruction-set support, driver minima, compiled GPU targets, server contents or dependency completeness. Runtime results remain **More evidence needed**, with a setup-review path rather than model-memory estimates. The review lists exact source archives, hashes, sizes, license declaration, destination and ordered verification requirements. Missing archives/digests, stale hardware and cached metadata are explicit obstacles. This review does not approve or execute acquisition or extraction. Supported CPU acquisition/extraction use their separate explicit approval workflows; source builds and broader recipes remain incomplete. The existing runtime is preserved.

Naming/packaging references: [upstream release workflow](https://github.com/ggml-org/llama.cpp/blob/master/.github/workflows/release.yml) and [upstream build documentation](https://github.com/ggml-org/llama.cpp/blob/master/docs/build.md). Names are treated as publisher labels, not proof of archive contents.

## Exact runtime file review

The runtime archive selector requires an explicit choice. **Review selected files** lists that archive's exact URL, compressed bytes and SHA-256, plus a uniquely matching same-release CUDA support archive when one is published. CPU selection excludes GPU alternatives. Missing or ambiguous CUDA dependencies are reported; other releases and architectures are never substituted.

The review identity covers selected files, destination, declared license, release flags, source timestamps and hardware evidence. Changing the destination or selection clears the displayed review. Missing digests, stale evidence, dependency licensing, expanded size and runtime compatibility remain explicit requirements. Creating a review grants no approval and does not download, install or run anything. Supported CPU downloading and local extraction use the separate approval steps described below.

## Explicit runtime release lookup

Enter an exact upstream tag in **Explicit release tag**, then choose **Look up release**. This queries only the public llama.cpp repository and that named release; it performs no model search, follows no release-marker file, and never substitutes latest/nightly. **Load saved release** uses the tag-specific catalog and refreshes local hardware evidence without making an online request. Existing ordinary-search cache keys are preserved.

A release mismatch, missing tag, unsafe tag or unavailable source is reported rather than replaced. Successful metadata feeds the same hardware assessment and exact-file review. Named release lookup does not approve downloading or installing that release.

Opt-in metadata-only live check:

```powershell
dotnet run --project AINexus.Checks/AINexus.Checks.csproj -c Release -- --runtime-release-live '<explicit upstream release tag>'
```

## Read-only local ZIP inspection

After reviewing exact runtime files, choose **Inspect local ZIP files** and select the local folder containing those already acquired archives. The app rechecks the review identity, each file's byte count and SHA-256, then inspects ZIP metadata without extracting or running payloads. Inspection rejects traversal/device/stream paths, links, special nodes, duplicate/case-colliding destinations, file-versus-directory collisions, split/ZIP64 archives and excessive declared sizes, ratios or directory metadata. Metadata is bounded before indexing ZIP entries. No runtime download is performed by inspection.

The reported expanded size is declared by the ZIP directory. Decompression, executable authenticity, dependencies, licensing and successful runtime execution remain unverified. A root `llama-server.exe` filename is reported as a filename only, never as a working server. Inspection does not grant installation approval; all identities and destination checks must be repeated before a future extraction step.

Opt-in check against existing local archives:

```powershell
dotnet run --project AINexus.Checks/AINexus.Checks.csproj -c Release -- --runtime-archive-live '<saved release report.json>' '<local archive folder>' '<exact runtime archive name>'
```

## Store workspace and image generation

### Local portable runtime installation

To acquire a selected **native CPU-runtime ZIP**, first use fresh online release metadata and **Review selected files**. Choose an existing **download folder** on the assessed volume, choose **Review runtime download**, review the exact bytes/hash/source and folder, then explicitly approve and choose **Download approved archive**. Hash-verified existing ZIPs can be reused; mismatched files are preserved. Downloading writes only archive bytes and a historical receipt. It never extracts or runs the payload.

After transfer, **Inspect downloaded archive** feeds the same read-only inspection step used for existing ZIPs. Runtime installation and bundled-license acceptance still require their separate review and approvals. Closing or cancelling during transfer cleans only that operation's temporary file. A receipt-recording failure is reported as incomplete while preserving verified ZIP bytes.

Opt-in acquisition check (downloads at most one 512 MiB CPU ZIP; does not extract or execute it):

```powershell
dotnet run --project AINexus.Checks/AINexus.Checks.csproj -c Release -- --runtime-download-live 'b11669' '<local archive folder>'
```

For a reviewed **native Windows CPU ZIP**, choose **Inspect local zip files**, **Review local install**, and **Read bundled license terms**. After reviewing the exact source identities, new folder, storage requirements and terms, accept all three approvals and choose **Install reviewed files**. Existing destinations are preserved. The archive must contain a root server file and readable license text. GPU/support packages remain unsupported; downloading uses the separate approved workflow above.

Installation writes portable files and an extraction receipt, and runs no payload. **Use installed runtime** selects it for an explicit offline smoke test with your chosen model. Extraction alone does not verify dependencies or runtime compatibility. Cancelled/failed extraction may retain an identified sibling staging folder for inspection; automatic recursive cleanup is deliberately avoided.

Model-size columns use appropriate byte units. Empty model placeholders are excluded from the local list with an explanation, while their files are preserved. Missing model-size metadata is shown as unknown.

The native WPF shell starts with hardware scanning and workload selection, then opens Home with persistent sidebar navigation for Discover, Library, selected image/assistant workloads, Downloads & setup, and Performance. Every launch and rebuilt workload chooser starts with all options unchecked. Workload choices are session-only and are never saved or restored; legacy saved selections are ignored. Existing hardware, package, runtime, embedded-app and offline-verification pages remain under Advanced tools. Search results use shorter labels; exact file hashes/source details and runtime-package controls are separated from browsing. Sidebar navigation, PNG preview, checkpoint gating and source-plan behavior have regression coverage.

Image studio connects only to an explicitly chosen literal-loopback ComfyUI endpoint. It discovers actual standard nodes and installed checkpoints, requires explicit model selection, checks current CPU/RAM admission and an idle image-engine queue, then submits a standard 512×512 checkpoint workflow. It waits for that exact job's successful history and decodes the returned PNG before showing a result. Cancellation addresses only that job ID; an unavailable cancellation endpoint never falls back to a global interrupt. Model fit/architecture support are established by actual execution, not inferred from filenames. Broader image architectures, image editing, audio/video and online image-model discovery remain incomplete.

An existing portable engine can be started hidden from the native page. It binds loopback, disables browser auto-launch and custom nodes, sets offline model-hub flags and keeps its caches beneath its portable folder. Stop/close terminates only this window's owned engine. The selected portable root is remembered in the application's preferences. No system-wide PATH, service, driver, registry or firewall changes are made.

With explicit user approval, the official ComfyUI v0.42.1 CUDA 12.6 portable archive and a pinned Comfy-Org SD1.5 FP16 safetensors checkpoint were installed on F:. Source download size/SHA-256, archive paths and extracted setup were verified and logged. The model uses CreativeML OpenRAIL-M terms; bundled runtime components retain their own licenses. The actual engine returned a decoded 512×512 PNG for seed 42753; its owned server stopped after the test. Evidence is under `F:\AINexus\Logs\ImageVerification` and the activity log is `F:\AINexus\Logs\development-activity.md`.

Opt-in real image check (loads the GPU):

```powershell
dotnet run --project AINexus.Checks/AINexus.Checks.csproj -c Release -- --image-live '<approved ComfyUI portable root>' '<installed checkpoint name>' '<output folder>'
```

This verifies local runtime execution and image bytes, not Windows-enforced network isolation or image quality.
