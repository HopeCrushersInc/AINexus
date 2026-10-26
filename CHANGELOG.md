# Changelog

User-visible changes are recorded here. Dates identify publication updates, not a promise that every feature was first implemented on that date.

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
