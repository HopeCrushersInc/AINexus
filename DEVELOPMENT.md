# Development state

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

## Resolved runtime checkpoint

The user selected portable llama.cpp in place of an Ollama installation. Verified upstream CUDA binaries and an official Apache-2.0 Qwen2.5-1.5B-Instruct Q4_K_M model are under `F:\AINexus`, outside Git. The local server and embedded frontend use loopback port 8080. No persistent PATH/startup/service changes were made. See [setup and provenance](docs/llama-cpp-setup.md). Ollama remains an optional supported adapter.

## Remaining product work

- Broader installer support and update availability/version plans. Preserve package-specific scope and data handling constraints.
- Additional runtime adapters and explicit model acquisition workflows.
- Richer model library organization and metadata validation. Current file extensions are descriptive only; model shards are individual files.
- More GPU vendors and resource policies. Current governance is admission control, not OS-enforced CPU/RAM/VRAM quotas or memory reservation.
- Multi-step inter-agent workflows and durable task histories, with deliberate data retention controls. Current tasks are serial local generation requests with no tools or host permissions.
- Broader real-model/runtime compatibility and load testing beyond the short generation smoke tests.
