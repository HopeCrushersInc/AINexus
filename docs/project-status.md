# Current project status

Release: **0.2.0-preview.6**, Windows x64 development preview, 2026-10-27.

## Working within tested limits

- Simpler task/action buttons and a static black-hole backdrop. Standard starts each session; LCARS is optional and Shift-clicking LCARS opens hidden Retro. Shift-wheel or the Size menu scales text/buttons from 75% to 175%, with reset to 100%. Native title-bar dragging and all-edge/corner resizing remain available.
- Actual CPU, RAM, GPU/driver and fixed-volume scans. Workload choices start unchecked and are not remembered.
- Confirmed workloads initiate source searches. GGUF text-model artifacts have source links, pinned revisions, available sizes/hashes/licenses and local resource estimates. User selection is required; no model is selected automatically.
- Supported GGUF acquisition: exact-plan approval, bounded transfer, byte/SHA-256 verification, matching existing-file reuse, receipts and no overwrite of a different file.
- Existing llama.cpp configuration, model loading, smoke generation and local multi-turn chat. The runtime runs with its offline-mode flag; OS-enforced network isolation is not claimed.
- New native CPU-runtime ZIP review/download/inspection/extraction with separate approvals. Existing approved GPU-capable runtimes can be selected; automatic GPU runtime/dependency setup remains incomplete.
- Persistent SQLite source catalog and offline Library filtering. Private tracked-folder receipt links report missing files, size mismatches and source conflicts; a historical receipt is not current integrity or runtime proof.
- Explicit local integrity checks and read-only recovery reviews. Recovery review does not perform repair.
- Optional per-card NVIDIA power inspection and user-approved changes within reported bounds. No fixed wattage, automatic restoration or power-control launch requirement.
- Connection to an existing ComfyUI engine/checkpoint and standard image generation. One image-engine/model combination has actual validation evidence.

## Partial, unfinished or unverified

- Images/video/music and other non-text categories currently search tool-repository metadata. Complete offline-model inventory, dependencies, hardware requirements and automated installation recipes are not implemented for those categories.
- No general multi-item installation queue, automatic text/image server switching, model updates/repair/rollback or direct installed-Library launch.
- Four-GPU identity/control fixtures pass, but actual four-GPU execution and arbitrary model splitting are unverified. Available VRAM is not silently pooled. Scaling beyond the immediate 1–4 local GPU scope is deferred.
- No implemented automatic fan control, thermal shutdown policy, training pipeline, persistent worker services or finished dockable/popout workflows.
- Linux, macOS and ARM64 portal releases remain planned. Current packages are Windows x64 only.
- iCUE investigation has installer inventory evidence, not a rebuilt/custom iCUE package. Ghidra-based analysis and feature transplantation remain unfinished.
- Coding/agent category labels are search scope, not proof of capability or quality. Source metadata is publisher-supplied; missing requirements remain unknown.

## Validation and release identity

- **862 default regression/native checks** passed on the development PC.
- **Seven additional live checks recorded for preview.5** verified actual source discovery, explicit result selection, exact review/approval, installed-file hash/reuse, existing runtime configuration, real generation, two chat turns and owned-process cleanup. That earlier run did not freshly download weights or install a new runtime; preview.6 appearance validation does not claim a fresh live model run.
- Earlier live tests separately established a real model download and one ComfyUI generation path. They are specific model/runtime/hardware evidence, not all-model guarantees or recommendations.
- Appearance checks cover palettes, hidden Retro selection, scaling/reset, native window settings and unchanged workload/approval state. Rendered previews were inspected at 100% and 150%; physical mouse/keyboard interaction is not established by those checks.
- GitHub Windows CI is required for publication; its actual result is linked in release notes.

The published ZIP's `PACKAGE-INFO.json` records the exact source commit and file hashes. The release tag and local active package must match that source commit and archive checksum. Historical packages and working branches are preserved rather than relabeled as current. Local paths, settings, machine logs, receipts and model weights are excluded from release assets.

See [release instructions](releases.md), [catalog storage](catalog.md), [technical details](technical-overview.md) and [roadmap](development-roadmap.md).
