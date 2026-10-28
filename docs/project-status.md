# Current project status

Release: **0.2.0-preview.8**, Windows x64 development preview, 2026-10-27.

## Working within tested limits

- Simpler task/action buttons and a static black-hole backdrop. Standard starts each session; LCARS is optional and Shift-clicking LCARS opens hidden Retro. Shift-wheel or the Size menu scales text/buttons from 75% to 175%, with reset to 100%. Native title-bar dragging and all-edge/corner resizing remain available.
- Actual CPU, RAM, GPU/driver and fixed-volume scans. Workload choices start unchecked and are not remembered.
- Confirmed workloads initiate source searches. GGUF text-model artifacts have source links, pinned revisions, available sizes/hashes/licenses and local resource estimates. User selection is required; no model is selected automatically.
- Image discovery retrieves bounded public text-to-image Safetensors artifact metadata and separate tool repositories. Exact repository queries inspect that source directly. Pinned identity and available bytes/hashes/licenses are preserved; missing facts stay unknown. Candidates are visible as unverified, with no fabricated hardware-fit or complete-checkpoint claim.
- Supported GGUF acquisition: exact-plan approval, bounded transfer, byte/SHA-256 verification, matching existing-file reuse, receipts and no overwrite of a different file.
- Existing llama.cpp configuration, model loading, smoke generation and local multi-turn chat. The runtime runs with its offline-mode flag; OS-enforced network isolation is not claimed.
- New native CPU-runtime ZIP review/download/inspection/extraction with separate approvals. Existing approved GPU-capable runtimes can be selected; automatic GPU runtime/dependency setup remains incomplete.
- Persistent SQLite source catalog and offline Library filtering. Private tracked-folder receipt links report missing files, size mismatches and source conflicts; a historical receipt is not current integrity or runtime proof.
- Supported GGUF models can be chosen in Library with an explicit installed-copy selection and local source/path review, followed by explicit loading with an existing engine. Review starts no search, download or engine. Loading binds the receipt to that exact plan/artifact/path, verifies the current hash, checks current hardware capacity and tests generation; the model is protected against writes during those steps.
- Explicit local integrity checks and read-only recovery reviews. Recovery review does not perform repair.
- Optional per-card NVIDIA power inspection and user-approved changes within reported bounds. No fixed wattage, automatic restoration or power-control launch requirement.
- Connection to an existing ComfyUI engine/checkpoint and standard image generation. One image-engine/model combination has actual validation evidence.
- Session-only image width/height inputs from 256 to 1024 pixels, in multiples of 64. Invalid inputs stop before resource admission or HTTP. The actual workflow payload carries the explicitly requested dimensions and checkpoint; no checkpoint is chosen automatically.

## Partial, unfinished or unverified

- Image artifact discovery is metadata only: automatic acquisition/configuration, complete checkpoint/companion-asset proof and evidence-backed hardware requirements remain unfinished. Video/music and other non-text categories still search tool repositories.
- No general multi-item installation queue, automatic text/image server switching or model updates/repair/rollback. The installed-Library flow covers supported GGUF text models and existing engines; other model/tool launch adapters remain incomplete.
- Actual SD1.5 generation at 1024×768 is verified. A second image checkpoint remains unverified. Size bounds do not establish that every checkpoint supports every size or fits available memory.
- Four-GPU identity/control fixtures pass, but actual four-GPU execution and arbitrary model splitting are unverified. Available VRAM is not silently pooled. Scaling beyond the immediate 1–4 local GPU scope is deferred.
- No implemented automatic fan control, thermal shutdown policy, training pipeline, persistent worker services or finished dockable/popout workflows.
- Linux, macOS and ARM64 portal releases remain planned. Current packages are Windows x64 only.
- iCUE investigation has installer inventory evidence, not a rebuilt/custom iCUE package. Ghidra-based analysis and feature transplantation remain unfinished.
- Coding/agent category labels are search scope, not proof of capability or quality. Source metadata is publisher-supplied; missing requirements remain unknown.

## Validation and release identity

- **919 default regression/native checks** passed on the development PC, including exact-copy binding, same-size corruption, changed receipts, write protection during hardware admission, insufficient capacity and image-dimension payloads/validation.
- **Six additional live Library checks per model passed for two existing models:** Qwen2.5-Coder-7B-Instruct and Qwen2.5-32B-Instruct. Each required explicit model/copy selection and review, verified its installed bytes, loaded the selected existing engine, generated an initial response and completed two chat turns. Both owned servers were closed. These Library runs used existing weights/runtime without an online source lookup or re-download; they do not establish multi-GPU execution, model quality or all-model support. Model names identify validation examples, not recommendations.
- Two additional live image-metadata checks verified an explicit public repository query and conservative assessment against actual hardware. Three source-backed image artifacts were returned; no weights were downloaded or executed.
- Earlier source-selected workflow checks separately verified actual online discovery, exact review/approval and installed-file hash/reuse. They are distinct from the offline Library runs.
- Five additional live image checks verified the existing ComfyUI/SD1.5 path at an explicit 1024×768, decoded the actual PNG, checked its pixel dimensions and stopped the owned engine. One image-checkpoint combination is verified; a second remains pending.
- Earlier live tests separately established a real model download and one ComfyUI generation path. They are specific model/runtime/hardware evidence, not all-model guarantees or recommendations.
- Appearance checks cover palettes, hidden Retro selection, scaling/reset, native window settings and unchanged workload/approval state. Rendered previews were inspected at 100% and 150%; physical mouse/keyboard interaction is not established by those checks.
- GitHub Windows CI is required for publication; its actual result is linked in release notes.

The published ZIP's `PACKAGE-INFO.json` records the exact source commit and file hashes. The release tag and local active package must match that source commit and archive checksum. Historical packages and working branches are preserved rather than relabeled as current. Local paths, settings, machine logs, receipts and model weights are excluded from release assets.

See [release instructions](releases.md), [catalog storage](catalog.md), [technical details](technical-overview.md) and [roadmap](development-roadmap.md).
