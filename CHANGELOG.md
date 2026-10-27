# Changelog

User-visible changes are recorded here. Dates identify publication updates, not a promise that every feature was first implemented on that date.

## 0.2.0-preview.6 — 2026-10-27 — Simpler controls and optional appearances

- Simplified Home to task and action buttons, with a static black-hole backdrop. All 13 workload choices remain visible and unchecked; the search confirmation stays available below the scrollable choices.
- Added session-only Standard and LCARS appearances. Hold Shift while clicking LCARS to reveal the hidden Retro appearance with cyan/magenta controls, stars and scanlines. Startup uses Standard.
- Added Shift-wheel text/button scaling from 75% to 175%, plus a Size menu and reset to 100%. Sidebar and action widths scale with the controls. Ordinary scrolling retains its normal behavior.
- Preserved native title-bar dragging and resizing from all edges and corners. Appearance and sizing do not select workloads, approve downloads, start engines or change GPU power settings.
- Corrected portable checksum filenames so the checksum entry names the actual AI Nexus ZIP rather than a bundled dependency.
- Validation: 862 default regression/native checks, including appearance selection, startup choices, scaling bounds/reset, native window settings and unchanged approval state. Rendered previews cover Standard, LCARS, Retro, 150% and 175% sizing. The seven live model checks recorded for preview.5 remain earlier evidence; this appearance release does not claim a new live model download or chat run.

## 0.2.0-preview.5 — 2026-10-27 — User-selected setup and persistent catalog

This release collects the local preview.3–preview.5 increments; preview.3 and preview.4 were not separate GitHub releases. Validation: 839 default regression/native checks and seven additional live checks through the generic selected-model workflow. The live run reused existing weights and runtime, completed two actual chat turns and stopped its owned server. Four-card identity/power controls are covered by fixtures; actual four-GPU model execution remains unverified. See [current status](docs/project-status.md) for implemented behavior and remaining gaps.

- Removed the fixed 250 W all-GPU requirement and its unconditional model-launch gate. The development workstation's owner-selected setting is no longer a product default.
- Added optional per-GPU power inspection and explicit, range-checked changes under Performance. No GPU or wattage is selected automatically; unselected adapters are untouched and launch does not depend on power-control support.
- Fixed generic model setup failing when search returned multiple candidates: the review now uses only the explicitly selected result. Exact repository searches expose further file variants within the result budget, instead of always stopping after two files.
- Verified the generic source-selected model path on the development PC: actual hardware/source metadata, explicit selection and approval, existing-artifact hash/reuse, selected runtime configuration, local generation, two chat turns and owned-server cleanup. Existing weights were reused; this run does not claim a fresh network transfer or actual multi-GPU execution.

- Added a persistent SQLite catalog connected to successful source searches. A pinned model artifact has one canonical identity with multiple task categories; retrieval dates, sources, sizes, hashes and licenses survive restarts. Stale updates cannot replace newer facts, and conflicting pinned identities roll back the import.
- Added a Library view with offline text filtering, a local-receipt filter, source details and explicit source refresh in Discover. No entry is preselected; browsing starts no model or installation.
- Linked private acquisition receipts from explicitly tracked folders without automatically hashing model weights. Missing files, size mismatches, receipt/source conflicts and unverified current integrity remain distinct. Private receipt-derived rows are not inserted into public metadata tables.
- Added portable catalog location support, corruption/unknown-schema preservation and dependency notices. The initial catalog has a 10,000-entry limit; it is not yet a community feed, complete media requirements database or batch installer.

- Confirming workload choices now starts a source search for every checked category, without requiring another category selection or search click. The initial hardware scan and selected drive are reused for local assessment; hardware details are not sent to search providers.
- Default results show only estimated hardware-fit candidates. Unknown, insufficient-resource and unsupported-platform results remain available through an explicit review toggle with separate counts and category labels. No model is selected, downloaded, installed or started by discovery.
- Category source failures retain other category results, and cancellation stops remaining searches. Rescanning or changing storage clears obsolete fit evidence.
- Image/video results still lack dedicated model/runtime compatibility recipes and are explicitly shown as unassessed. This change does not claim complete offline image/video discovery.

## 0.2.0-preview.2 — Workload-first correction

- Restored every workload category directly after the hardware scan, visible and unchecked. Removed preset model promotion from startup and primary navigation.
- Confirmed choices open live discovery. No category is silently chosen when multiple were selected; no search result is automatically selected.
- Added live read-only tool-source metadata search for image, music, video, speech, 3D, CAD, printing, game, reverse-engineering and streaming workflows. Missing installation adapters stay explicit.
- A user-selected online GGUF can enter the supported review/download/chat flow without a preset-model restriction or changing the chosen workload.
- Preserved separate explicit transfer, runtime and installation approvals. Nothing installs or launches from startup, category selection or search.
- Updated repository presentation to describe user choice rather than recommend particular models. 0.2.0-preview.1 retains a regression notice.

## Repository presentation update

- Replaced the long landing page with a download-first guide, actual Qwen32B chat screenshot, short setup instructions, working-feature table and explicit preview limits.
- Preserved detailed implementation and opt-in test documentation in docs/technical-overview.md.

## 0.2.0-preview.1 — 2026-10-26

- Added prominent model cards and a guided download → engine setup → load → chat path. Advanced workspace choices are collapsed; no workload or adult-model option is preselected.
- Added exact-source catalog lookups for Qwen 7B, Qwen Coder 7B, Qwen 32B and opt-in NSFW Stheno/Lexi entries. Fresh metadata determines sizes, hashes and licenses; no substitute artifact is silently selected.
- Added real multi-turn local chat, explicit cancellation/truncation, model-stop binding resets and session-only history.
- Added a portable CPU-engine download/extraction route with separate archive and bundled-license approvals. Existing GPU-capable engines can be selected directly.
- Extended bounded cold-load health startup from 30 seconds to five minutes; added cancellation and lifecycle guards.
- Added pre-load 250 W NVIDIA power-cap enforcement/readback. Unsupported or denied control is reported instead of claiming a cap was set.
- Verified real Qwen 32B installation/generation/two-turn chat and official Qwen Coder 7B reuse/generation/two-turn chat. Latest default validation: 735 regression/native checks passed; live runs added five actual install/chat/cleanup checks each.
- Unfinished image-model acquisition/switching, active catalog, fan/thermal controls and iCUE research were saved separately and are not presented as complete in this release.

## 0.1.0-preview.2 — 2026-10-26

- Added read-only recovery reviews after a completed missing/changed GGUF integrity result. Show pinned historical source/terms, expected identity, a separate unoccupied destination and current storage evidence.
- Recheck the original file before review; repaired/healthy files are rejected. Preserve nested artifact paths so the proposed destination matches later acquisition planning.
- Clear recovery state on selection changes, rescans, new checks, failures and cancellation. Recovery review grants no download/replacement approval and never changes files.
- Require fresh source/hardware/license evidence, separate acquisition approval, complete integrity verification and explicit offline testing before switching to a replacement. Automated repair remains incomplete.
- Validation: Release build passed with zero warnings/errors and 639 regression/native checks passed; package verification is recorded on the release.

## 2026-10-26 — HopeCrushersInc organization transfer

- Repository transferred to https://github.com/HopeCrushersInc/AINexus while keeping the existing personal login.
- Verified public visibility, active branch rules, administrator access, Discussions/Wiki settings and unchanged release-asset hashes.
- Updated current documentation and future package source/support links. Existing published assets retain their original manifests and hashes; historical URLs redirect.

## 0.1.0-preview.1 — 2026-10-26

- First downloadable Windows x64 portable preview package, including the application EXE and .NET dependencies, with SHA-256 checksums and source/file inventory. Models, external runtimes and the WebView2 browser runtime are separate.
- Added explicit read-only GGUF library integrity checks against a selected acquisition receipt, with match/missing/size/hash outcomes and stale-result clearing.
- Added the preview version to the window title so installed builds can be distinguished.
- Verified that the current launcher has no startup workload preselections; added regression checks of actual rendered checkboxes, explicit selection and reset. No new startup-selection defect was reproduced.
- Validation: Release build with zero warnings/errors; 605 regression/native checks passed. Package validation is reported on the release.
- Windows local accounts remain supported; no Microsoft account sign-in, paid service or Windows service change is part of this release.

## 2026-10-26 — Feature direction and community

- Added a feature overview and phased projection for live editing, opt-in model switching, maintenance/updates/rollback, training, custom windows/popouts, workflows and services.
- Added explicit Windows/Linux/macOS and x64/ARM64 target boundaries, dependencies and acceptance gates; these planned features are not presented as implemented.
- Replaced the stale baseline roadmap and linked the planning documents from README.
- Enabled GitHub Discussions and the wiki feature. Added a versioned community/wiki-style guide; wiki first-page initialization and a private Discord server remain pending.
- Documentation update only; no application behavior changed.

## 2026-10-26 — Public development update

This update brings the integrated development work onto `main`. AI Nexus Portal remains a Windows development preview; compatibility estimates are not proof that a model will run.

### Added

- Real CPU, RAM, graphics and drive discovery, NVIDIA telemetry where available, and read-only discovery of installed AI tools.
- Hardware-aware Hugging Face model discovery with pinned revisions, declared licensing, sizes, hashes when available, and explainable resource estimates.
- Explicit model acquisition review and approval, integrity-checked downloads, safe cancellation, no-overwrite publication and receipts. Saved discovery evidence can be re-evaluated offline.
- Reviewed native Windows CPU llama.cpp ZIP downloads, bounded read-only archive inspection, bundled-license review, and separately approved portable extraction. Downloading, installation and execution are distinct steps.
- Local model library, loopback Ollama inventories, portal-owned llama.cpp controls, embedded local interfaces and resource-gated local generation tasks.
- Explicit offline smoke testing with evidence tied to the selected model/runtime/configuration. Owned llama.cpp launches require support for its offline mode.
- Hardware-first workload setup, Home/sidebar navigation, and an explicit local ComfyUI image workflow using actual installed checkpoints and decoded output images.
- Windows CI that builds the application and runs the regression checks for pull requests to `main`.

### Fixed

- Workload options start unchecked on every launch and whenever the chooser is rebuilt. Choices are never saved or restored; legacy selections are ignored without discarding unrelated preferences.
- Model sizes use appropriate byte units. Unknown sizes remain unknown, and empty local placeholders are omitted with a warning while the files are preserved.
- Changed verification inputs, new operations, failures and cancellation clear obsolete displayed success evidence.
- Invalid saved workload configuration recovers with a warning while preserving the original settings file.

### Verification

- Publication validation: Release build succeeded; all **582 deterministic and native regression checks passed** on Windows.
- Earlier opt-in local checks exercised actual llama.cpp generation and owned-server shutdown, a decoded 512×512 ComfyUI image, and a real CPU ZIP download with independent SHA-256 verification, receipt creation and exact-file reuse.
- The current default check run does not repeat large downloads, install applications or run every available model. Test fixtures do not establish that every upstream runtime works on every PC.

### Current limitations

- Automated portable runtime acquisition/extraction currently supports the reviewed single native CPU ZIP recipe. GPU package installation, source builds and dependency installation remain incomplete.
- Offline runtime flags are not Windows-enforced network isolation. Resource assessments are estimates and admission controls, not reserved memory or operating-system quotas.
- Image support requires an explicitly selected compatible local ComfyUI engine/checkpoint. Broader image architectures, editing, audio/video and online image-model discovery remain incomplete.
- Agent tasks perform local generation; general tool execution, multi-agent dependency workflows and durable task histories are not implemented.
- Model weights, installed engines, private preferences and workstation logs are not distributed with the source. Build instructions are in [README](README.md); engineering evidence and remaining work are in [DEVELOPMENT](DEVELOPMENT.md).

### Repository publication

- Repository made public without a paid plan.
- `main` requires pull requests, one approving review and resolved review discussions. The administrator can bypass review requirements through a pull request.
- Force pushes and branch deletion are blocked on `main` and both existing published phase branches.
