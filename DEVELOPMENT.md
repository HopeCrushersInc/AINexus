# Development state

## Approved local runtime extraction and model-list cleanup

Downloads & setup now supports an explicit installation review after exact-file review and local ZIP inspection. The initial recipe accepts a single native Windows CPU runtime archive with a nonempty root server and bounded readable bundled license files. Reading verified terms grants no approval: separate declared-license, bundled-terms and destination-write acceptances are required. The service rehashes the held archive, reinspects paths and sizes, checks current disk space, extracts into a new sibling staging folder, validates the complete output inventory, writes a receipt and publishes without overwriting. It never downloads or executes a runtime. Failures/cancellation retain clearly identified staging for inspection instead of recursively deleting possibly changed paths. Use installed runtime selects it for a separate actual offline test; extraction is not successful execution evidence. GPU/multiple-archive recipes, missing license text, acquisition and dependency installers remain unsupported.

Model library, discovery and Ollama tables now show adaptive byte units instead of rounding small files to zero GiB. Unknown model sizes stay unknown; zero-byte local placeholders are omitted with a warning and preserved on disk. No model files are deleted.

Validation: clean Release build, 539 integrated regression/native checks passed, including the preserved workload onboarding. Tests use inert generated archives and cover exact approvals, changed identities, no-overwrite behavior, terms bounds/encoding, staged extra files, cancellation, malformed decompression sizes, upstream tagged archive names and native installation/configuration controls. No real runtime ZIP was installed by this increment. Native layout was rendered and visually inspected. Invalid saved workload settings now recover to defaults with a warning while preserving the original settings file.

## Everyday offline launch

The native owned llama.cpp Start action now explicitly requests runtime offline mode. After local-drive/reparse/configuration checks and occupied-port rejection, a shared hidden, sanitized, bounded `--help` probe must advertise the exact `--offline` option. Unsupported runtimes fail before model launch, with no online fallback. Offline verification uses the same gate. Existing external servers remain separately owned; no Windows network policy changes are made.

Validation: Release build passed with zero warnings/errors. 487 default regression checks plus five opt-in real-launch checks passed (492 total). The existing official Qwen2.5-Coder-7B Q4_K_M model returned `Portal ready.`; Stop cleared ownership and closed its port. This establishes runtime offline-mode generation, not OS-enforced network isolation, publisher authenticity, comprehensive coding quality, or continued readiness after artifacts change. Gemini and local Qwen supplied independent design-review suggestions; unsupported fallback advice was rejected.

The work is isolated on `collaboration/offline-launch` in `F:\AINexus\Workspace\AINexus-Codex`. The original phase11 checkout's uncommitted workload setup is preserved. This branch also carries the earlier tested stale-verification display fix.

## Verification display integrity

Changing the acquisition receipt, runtime executable or context clears displayed offline-verification evidence and asks for a fresh audit/test. Starting another operation clears earlier evidence; failure and cancellation also clear partial evidence. Saved evidence files remain intact. Native regression checks exercise each input and operation recovery without loading a model.

## Completed increments

The restored Phase 1 hardware dashboard remains native .NET 8 WPF. Subsequent local commits add:

1. Read-only AI software discovery with installation evidence, unknown states, and independent rescan controls.
2. Hardware scan deadlines/cancellation, partial results, mixed-vendor inventory, and invariant NVIDIA memory parsing.
3. Actual live CPU/RAM and NVIDIA GPU/VRAM monitoring.
4. Explicit WinGet package review and user-scoped Ollama install/update operations with output capture and result logs. GPT4All remains review-only.
5. Selected-folder model metadata indexing and existing Ollama model inventories.
6. Embedded loopback application interfaces using WPF WebView2.
7. Serial local agent generation tasks gated by actual resource admission readings.
8. Portable llama.cpp model discovery, chat generation, native owned-server Start/Stop/close cleanup, and saved launch preferences.
9. Hardware-aware metadata discovery, selected-volume compatibility estimates, source provenance, and saved-search offline review. Initial model recipe scope is standalone text-generation GGUF artifacts.
10. Review-only model acquisition plans and non-destructive existing-artifact integrity verification. Stale/cached evidence, mismatched target volumes and unsafe paths are rejected. No acquisition execution is exposed by this increment.
11. Explicit approved model-data acquisition with streamed HTTPS downloads, current disk-space checks, exact size/hash verification, no-overwrite publication, cancellation/close cleanup, verified reuse and saved integrity receipts. This adds acquisition execution after the review-only foundation; runtime installers and Windows changes are excluded.

No code executes generated model output. No model files are deleted or relocated. No packages or model weights are installed automatically. All development commits remain local.

## Verification

- Release compilation succeeds without warnings/errors.
- Deterministic regression checks cover hardware parsing/merging, subprocess deadlines/cancellation, software discovery, model indexing/settings, HTTP limits/failure states, resource admission, serial scheduling, and cancellation/recovery.
- Live checks read the restored workstation's actual hardware and software, initialize the installed WebView2 runtime, render a loopback fixture, and block off-origin fixture requests/navigation.
- WinGet package metadata was queried successfully without installing or updating applications.
- Real llama.cpp inference passed through both the C# client/scheduler and native WPF task controls; the model generated `Portal ready.`.
- Native hidden-server startup, health, model discovery, Stop, ownership isolation, and window-close cleanup passed against the selected real runtime/model. The actual llama.cpp web UI rendered in WebView2 without script errors.
- Actual WinGet application installer execution remains untested; Ollama was not installed.
- Discovery regression checks cover redirects/rate limits, response caps, cancellation, partial source failures, split GGUF exclusion, immutable identities, unknown metadata and offline re-evaluation. Live public-source checks retrieve actual artifact hashes/sizes; compatibility estimates remain explicitly unverified until a separate actual runtime test is performed.
- Directory link protection is exercised even without Windows symlink privileges, using a same-fixture directory junction. Scanner rejection, direct-root rejection and target-preserving cleanup are checked without changing system policy.
- Approved acquisition passed mocked-transport failure/cancellation/redirect/race tests and native WPF approval/completion/close-cleanup checks. A real duplicate of the already-approved Qwen GGUF was downloaded to an isolated F: evidence folder, checked against the upstream byte count/SHA-256, and independently rehashed. The original model was preserved. Integrity receipts do not claim offline inference was verified by acquisition.

## Resolved runtime checkpoint

The user selected portable llama.cpp in place of an Ollama installation. Verified upstream CUDA binaries and an official Apache-2.0 Qwen2.5-1.5B-Instruct Q4_K_M model are under `F:\AINexus`, outside Git. The local server and embedded frontend use loopback port 8080. No persistent PATH/startup/service changes were made. See [setup and provenance](docs/llama-cpp-setup.md). Ollama remains an optional supported adapter.

## Remaining product work

- Broader installer support and update availability/version plans. Preserve package-specific scope and data handling constraints.
- Additional runtime adapters and explicit model acquisition workflows.
- Richer model library organization and metadata validation. Current file extensions are descriptive only; model shards are individual files.
- More GPU vendors and resource policies. Current governance is admission control, not OS-enforced CPU/RAM/VRAM quotas or memory reservation.
- Multi-step inter-agent workflows and durable task histories, with deliberate data retention controls. Current tasks are serial local generation requests with no tools or host permissions.
- Broader real-model/runtime compatibility and load testing beyond the short generation smoke tests.

## Phase 10: persistent offline verification

Implemented a native receipt audit and explicit offline-mode inference test. Evidence binds the actual completion to model SHA-256, executable/top-level DLL hashes, context, launch arguments, current hardware and resource admission. Receipts are historical pinned identities; offline auditing does not pretend to refresh publisher metadata. Missing/modified artifacts, unsafe paths, unsupported runtimes and resource rejection cannot produce success evidence. The live Qwen/llama.cpp test returned `Portal ready.` and confirmed the owned port was closed before success. This is runtime offline mode, not OS-enforced network isolation.

Next priority: runtime release discovery and reviewable installation/build recipes, followed by broader workflow adapters. Downloading or installing a runtime still requires approval of a concrete plan.

## Runtime catalog increment

Supporting runtime discovery now preserves published archive identity, size, digest and release flags in native details and saved evidence. Current upstream stable metadata can contain a marker rather than binaries; this is reported honestly and never silently resolved into a nightly download. Regression checks validate malformed/duplicate/foreign entries and saved-catalog boundaries. This adds metadata visibility, not runtime installation or verified architecture/backend compatibility.

## Runtime setup review increment

Implemented native runtime setup review with OS/architecture comparison, NVIDIA-query evidence, explicit unprobed Vulkan requirements, CUDA companion roles and safe new-folder validation. Runtime rows remain unverified and direct users to setup review rather than applying GGUF model-memory estimates. The pure planner and WPF action perform no network request, filesystem write or runtime launch. Review identifies source files and ordered approval/acquisition/inspection/configuration/verification requirements; installation execution and source compilation remain incomplete.

Validation: Release build passed with zero warnings/errors; 431 checks passed including native runtime-review controls and a real hardware/public-metadata/saved-catalog/setup-review pipeline. The live review correctly reported zero archives in the upstream stable release and did not create its proposed runtime destination. No runtime download, installation or model load was performed for this increment.

## Exact runtime selection increment

Added explicit native archive selection and an exact-file review with reproducible identity. It binds current hardware/source evidence, release flags, files/hashes/bytes, license and destination. Recognized CUDA support companions must match release/backend/architecture and be unambiguous; CPU plans exclude unrelated GPU alternatives. Review invalidation follows input changes. This is not an executable install plan and saves no approval. Pending: actual archive inspection, dependency verification and approval-bound runtime acquisition.

Validation: Release build passed without warnings/errors; 439 checks passed, including exact file identity, same-release companions, ambiguous/missing dependencies, wrong-platform and foreign-origin rejection, native selection and review invalidation. No download, installation or runtime launch occurred.

## Explicit release lookup increment

Added explicit upstream tag lookup and tag-specific saved metadata to the native discovery screen. The provider makes only repository/tag API requests, validates the returned tag, preserves cancellation/source failures and never silently falls back. Runtime caches cannot substitute another tag's metadata; ordinary-search cache keys retain their previous format. Exact file selection remains review-only with no install authorization.

Validation: Release build passed without warnings/errors; 451 checks passed including live explicit `b11669` metadata (36 archives) and an actual native-platform CPU-file review. No runtime archive was downloaded, extracted or installed; the proposed destination remained absent.

## Local runtime archive inspection increment

Added native read-only inspection of exact reviewed ZIPs. The service verifies local byte counts/SHA-256 under read leases, bounds the central directory before ZIP indexing, and checks entry paths, attributes, collisions and declared expansion policy across all selected archives. Evidence includes the full exact-file plan. No payload is decompressed, extracted or executed; expanded sizes and server names are declarations. Installing, approval-bound downloading, extraction and dependency/runtime verification remain separate incomplete stages.

Validation: Release build passed without warnings/errors; 460 checks passed. Read-only live inspection matched both existing approved CUDA ZIPs against the upstream-backed exact-file review, checking 55 entries with 1,199,457,053 declared expanded bytes. The proposed destination remained absent. No download, payload decompression, extraction, installation, model launch or Windows configuration change occurred.

## 2026-10-25 — Store shell and local images

- Replaced the crowded top-level tabs with a native store-style Home/sidebar workspace. Preserved existing tools and separated setup from browsing; retained keyboard/accessibility access through the tab content host.
- Added real local ComfyUI checkpoint discovery, standard image workflow submission, job-specific history/output retrieval, bounded decoded PNG preview, explicit save, and targeted cancellation.
- Added hidden owned-engine startup/stop with loopback/offline flags and portable-folder caches. Engine configuration is remembered; no cloud inference or custom nodes are used by this recipe.
- Installed the specifically approved official portable engine and pinned SD1.5 FP16 model on F: after size/SHA-256 and path checks. Logs/verification artifacts remain on F: outside Git.
- Release compilation passed; 472 deterministic/native checks passed. The opt-in actual image run passed 476 checks and returned a real PNG; its owned engine stopped afterward.
- User authorized GitHub updates. Verified GitHub authentication and remote branch inventory; completed work is being synced on `phase11/store-and-images`, preserving the existing remote branches.

Incomplete: general image-model discovery/installation UI, broader diffusion architectures and editing, audio/video workflows, and OS-enforced resource reservations. Admission checks and runtime offline flags are not guarantees or firewall isolation tests.
