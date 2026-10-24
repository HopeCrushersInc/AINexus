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

No code executes generated model output. No model files are deleted or relocated. No packages or model weights are installed automatically. All development commits remain local.

## Verification

- Release compilation succeeds without warnings/errors.
- Deterministic regression checks cover hardware parsing/merging, subprocess deadlines/cancellation, software discovery, model indexing/settings, HTTP limits/failure states, resource admission, serial scheduling, and cancellation/recovery.
- Live checks read the restored workstation's actual hardware and software, initialize the installed WebView2 runtime, render a loopback fixture, and block off-origin fixture requests/navigation.
- WinGet package metadata was queried successfully without installing or updating applications.
- Actual application installer execution and real model inference have not been exercised.

## Dependency checkpoint

No Ollama server responds at `http://127.0.0.1:11434`. To validate generation against actual model weights, select an existing local server/model or choose a runtime/model installation. The runtime/model choice determines downloads, storage use, license terms, and host changes beyond this repository. A local AI web interface address is likewise required to validate embedding an actual AI frontend; the embedded browser itself has passed runtime checks.

## Remaining product work

- Broader installer support and update availability/version plans. Preserve package-specific scope and data handling constraints.
- Additional runtime adapters and explicit model acquisition workflows.
- Richer model library organization and metadata validation. Current file extensions are descriptive only; model shards are individual files.
- More GPU vendors and resource policies. Current governance is admission control, not OS-enforced CPU/RAM/VRAM quotas or memory reservation.
- Multi-step inter-agent workflows and durable task histories, with deliberate data retention controls. Current tasks are serial local generation requests with no tools or host permissions.
- Real-runtime integration validation after the dependency checkpoint is resolved.
