# Changelog

## 0.2.0-preview.20 — 2026-10-30 — Relevant image searches and clean category changes

- Fix Images discovery inspecting only four repositories from the default source order. Interleave popular, trending and recently updated public text-to-image feeds; inspect up to 24 unique repositories with four concurrent reads and at most 52 metadata requests including tool search. Distribute root Safetensors candidates fairly across sources without pinning a vendor/model family or inventing quality scores. Keep successful source results if another feed fails; source/response/result bounds remain.
- Clear the previous category's rows, narrowing terms, fit-only filter and idle model-review approvals when changing scope. Cancel the previous operation and reject late responses. Preserve deliberate terms on the initial workload confirmation and when rebinding the same profile. Changing a profile does not interrupt active engines or setup operations.
- Enable explicit repository-file browsing for image results, preserving the exact repository query after changing category. Give expanded image-search caches a new key; preserve earlier snapshots without reusing their narrow results or refreshing them silently.
- Validation: 1,514 regression/native checks passed in both test runs, plus two live source checks and five generation checks per tested model. The corrected blank Images search returned the official Stable Diffusion 1.5 and SDXL base repositories alongside other publishers. Existing local SD1.5 generated a real decoded 512×512 PNG; SDXL generated a real decoded 1024×1024 PNG. Each test-owned engine stopped afterward. Builds had zero warnings/errors; no models or engines were installed, and the personal 250W GPU setting was unchanged.
- Image source candidates are not universal hardware-fit or complete-checkpoint certifications. Nested/component-only pipelines and unsupported engine adapters still require separate setup recipes. Nothing is selected or installed automatically.

## 0.2.0-preview.19 — 2026-10-30 — Complete split-model inventories

- Discover complete canonical split-GGUF sets as one selectable text-model variant. Require all consecutive parts from one pinned repository revision with exact bytes and SHA-256; incomplete, duplicate, noncanonical, mixed or overflowing inventories stay unavailable. Existing metadata/result bounds remain; no parameter-size preset or preferred model is added.
- Sum every part for memory/storage estimates and display the complete inventory in download review. Bind approval to every file's source, identity and destination. Transfer files serially, reuse matching existing files, verify each part and save complete per-file integrity evidence. A later failure/cancellation retains completed verified parts, removes only the active owned partial and grants no complete-model success.
- Preserve legacy single-file JSON fingerprints. Receipts, catalog identity, library readiness and maintenance account for the entire inventory; one valid first file or offsetting part-size errors cannot establish readiness. Hold all files read-only and hash each before the selected runtime loads the first part and completes its initial response test.
- Explicitly block single-file repair for split inventories; full set repair and combined RAM/VRAM planning remain unfinished. No runtime, model or approval is chosen automatically, and no engine or model is bundled. Document limits in `docs/split-models.md`.
- Validation: 1,496 regression/native checks plus five actual split-model checks passed (1,501 total), with zero build warnings/errors. A temporary two-part copy derived from an existing local model completed approved reuse without HTTP, receipt/audit roundtrip and offline-mode generation; the owned server stopped and released its port. This is a specific development fixture, not a publisher-authenticity, model-quality or universal architecture claim. No new model or engine was downloaded; the existing personal 250W GPU setting was preserved.

## 0.2.0-preview.18 — 2026-10-29 — More file variants, no parameter presets

- Remove the two-file-per-repository truncation in broad text searches. Distribute additional standalone GGUF variants across repositories within the existing 48-result / 24-repository bounds, using the same metadata requests. Browse a selected repository for additional files. Alphabetical source order is not a model-size or quantization recommendation.
- Preserve exact source sizes, including large artifacts, for separate local memory and selected-drive checks. Parameter totals remain informational: unknown, 7B, 27B, 32B, 120B and 1.2T metadata cannot change eligibility for otherwise identical artifacts. No 27B preset or parameter-count cap existed in the inspected source.
- Give expanded text searches new cache keys, preserving earlier saved searches without silently reopening their truncated result sets or refreshing online.
- Remove the fixed 45-minute model-transfer deadline. Healthy approved downloads can continue; cancellation, 30-second network waits, exact byte/SHA-256 checks, destination protections and owned-partial cleanup remain.
- Actual development-PC checks reused an existing 7B coder through discovery, explicit review/approval, integrity verification, launch and two chat turns; an existing 32B assistant completed offline-mode launch and generation. Both test-owned servers stopped and released their ports. No new model or engine was installed, and neither model becomes a default or a recommendation.
- Split GGUF acquisition and combined RAM/VRAM planning remain unfinished. A large disk does not establish runtime memory fit or architecture support. The existing per-GPU and CPU estimates remain conservative and unverified until actual loading.
- Validation: 1,428 final regression/native checks passed. The earlier live run added 12 checks for the two existing models, including owned-server cleanup. Expanded-variant fixtures cover fair allocation, large artifact metadata and unchanged request bounds; earlier two-variant caches are preserved but not reused.

## 0.2.0-preview.17 — 2026-10-29 — Clear task groups and relevant source results

- Organize all 13 unchecked tasks under four parent headings: Assistants, Coding & software, Creative media, and CAD & 3D printing. CAD design and printing/slicing remain independent choices and searches. Group headings never select children. Compact the layout and make scanned hardware details expandable.
- Remove the automatic llama.cpp runtime append from text-model discovery, including empty searches. Explicit runtime-release lookup remains a separate action. Saved model searches omit runtime rows; earlier unscoped caches are preserved under separate keys.
- Remove permanent/default backend choices from the model-task panel. Show only explicitly queried connections, label them as engines, and list only their actual reported model IDs with no automatic selection or fallback.
- Scope Coding to source code/coding tags or explicit coder repository labels, and agent-model searches to function-calling/tool-use/tool-calling tags. Free text narrows those scopes; exact repository inspection remains a deliberate exception with capability unverified. Bound task-text searches to nine lists, 24 inspected repositories and 57 total metadata requests, with four concurrent reads. Missing source labels can omit capable models; no general-chat fallback or quality score is invented.
- Keep tool queries task-specific, constrain searched fields, treat user text as literal words, exclude unrelated/archived/disabled/fork metadata, and retain exact CAD versus printing topic roles. Live streaming metadata no longer accepts generic data-streaming descriptions as broadcasting evidence.
- Validation: 1,416 deterministic regression/native checks passed. Four separate live metadata checks returned relevant Coding, Agents, CAD and printing candidates; no model weights or tools were acquired. Inspected the rendered grouped chooser. Preserve the pre-existing category work in its original checkout and a private snapshot; integrate and test the reviewed increment in a separate worktree.

## 0.2.0-preview.16 — 2026-10-29 — Read-only tool archive inspection

- Add explicit inspection of a locally available, user-selected 7z archive. Recheck exact size/SHA-256 before typed metadata parsing; no automatic inspection follows download.
- Report declared entry/expanded sizes, engine filename markers and potential license filenames separately from installation, payload integrity, complete terms and runtime-readiness claims. Reject traversal, device/ADS names, duplicate/case-colliding destinations, parent-file conflicts, encryption, links, anti/split entries and excessive metadata/expansion.
- Parse in the portal's own hidden worker with Windows job memory/process limits, bounded request/report size and a parent deadline. Cancellation stops the owned worker. This limits resource use; it is not a general OS security sandbox.
- Inspection grants no download or extraction approval. It writes/extracts no files and executes no archive payload. Tool selection stays explicit; no AI tools, engines or models are bundled.
- Pin the SharpCompress 0.50.7 archive-reading application library, preserve its MIT license and unchanged package attribution in release notices, and restore development dependencies into the portable F: cache. Refresh the roadmap against current implementation.
- Validation: 1,359 regression/native checks passed, including real inert 7z containers, actual limited-worker success/rejection, active cancellation, read-only UI behavior and unsafe metadata policies. No real engine archive or model was downloaded, installed or run.

## 0.2.0-preview.15 — 2026-10-29 — No default or preinstalled AI tools

- Remove the direct ComfyUI download shortcut from Images. Start with neutral tool discovery and blank engine, path, endpoint and checkpoint choices. Selecting an image workload does not choose a tool.
- Review each selected image tool on its own source-details page. Optional managed actions appear only for the exact supported source selected by the user. Unsupported tools retain their own identity and manual setup information; no other engine is substituted.
- Require a separate explicit existing-installation action before exposing its image adapter. Archive lookup requires an explicit matching tool choice. Acquiring image data does not choose an engine or open a ComfyUI configuration page automatically.
- Stop restoring saved image-engine, text-engine, model and verification-runtime paths as startup selections. Preserve saved configuration and prior user-approved files on disk. Reset tool context and approvals when workloads/tools change; active owned work must finish or stop first.
- Add a publishing guard against AI model weights, engine archives and known engine executables. The portal ships its application dependencies, not preinstalled AI engines or models.
- Validation: 1,305 regression/native checks passed. Inspected the rendered neutral Images screen; tests cover alternatives, no default lookup, explicit tool actions, category changes, approval neutrality and no server queries or starts. No AI engine/model was installed, removed or run in this increment.

## 0.2.0-preview.14 — 2026-10-29 — Shorter project page and approved ComfyUI archives

- Shorten the README to a quick overview, download link and clear working/unfinished features. Move the detailed guide to `docs/start-here.md`; keep its instructions and links available. Shorten GitHub About and retain its Releases link.
- Add explicit official ComfyUI current/tag release lookup. No lookup runs on opening the pane; no archive, destination or approval is selected automatically. Show only supported canonical archive names with exact published size and SHA-256, without asserting GPU/driver compatibility.
- Review and approve the exact archive and local destination before transfer. Validate fresh metadata, storage and paths; verify complete bytes/hash, preserve conflicts, rehash existing matching files, clean owned partials on failure/cancel and write private acquisition receipts. Closing waits for owned-operation cleanup.
- Add source-terms access and route the exact official ComfyUI tool to this supported archive review. Other tools retain manual setup. Download approval is consumed and changing inputs revokes it.
- This acquires an archive only. Safe extraction, bundled dependency terms review, engine verification and installed-engine registration are separate unfinished steps. No engine or model starts after downloading.
- Validation: 1,290 deterministic regression/native checks and one live official-release metadata check passed (1,291 total). The archive page was rendered and inspected. Binary-transfer behavior used inert fixtures; no real ComfyUI archive download, extraction or inference run is claimed.

## 0.2.0-preview.13 — 2026-10-28 — More appearances, same user control

- Expand Appearance to ten visible choices: Standard, LCARS, Cyberpunk, Midnight, Ocean, Forest, Ember, Graphite, Paper and Terminal. Preserve the hidden Retro style through Shift-clicking LCARS; it has no separate visible menu entry.
- Give the added styles distinct palettes, fonts and corner treatments. Paper is a light reading surface, Graphite a quiet neutral surface and Terminal a static monospace grid. Space styles retain the black-hole backdrop; no animated workers or downloaded assets are required.
- Theme inputs, tables, selected cells, navigation and setup surfaces so the light palette stays readable. Keep exactly one appearance checked. Standard still starts each session; style selection does not restore tasks, models or approvals.
- Preserve native window dragging/resizing and Shift-wheel text/button zoom. Switching appearances does not search, download, install, change GPU settings or start servers.
- Validation: 1,247 regression/native checks passed, including primary-text contrast on five surfaces in every palette, menu exclusivity, hidden-mode selection and unchanged workflow/approval state. Rendered all eleven styles and inspected enlarged Retro previews. This appearance update adds no generator-engine installer or video generation support.

## 0.2.0-preview.12 — 2026-10-28 — Visible media choices, clear setup actions and saved evidence

- Show all returned model, tool and runtime results by default. Rename the optional unchecked filter to **Show only estimated-fit results**, so tools without a compatibility recipe no longer disappear from the initial list. Keep unknown requirements distinct from hardware-fit claims.
- Add an **Available action** column and an explicit **Open source page** action. Distinguish supported image-file downloads from generator-tool manual setup and video-pipeline inspection. Tool/video review stays in Discover with source details visible instead of navigating to an unrelated setup page. Opening a source page grants no install or launch approval.
- Clarify supported image acquisition: explicitly choose an existing destination folder, review the exact file and license, approve, then download/verify. Folder connection remains a separate approved action for an existing engine; generator engine installation is not implemented by image-file acquisition.
- Search live text-to-video and image-to-video model repository feeds, interleave/deduplicate up to eight pinned public repositories and preserve successful results when another source fails. Video entries contain no invented complete model artifact, size, hardware fit or installation support. Direct repository inspection does not substitute unrelated results; video GGUF is not routed into text-model setup.
- Earlier media-increment validation: 1,138 regression/native checks plus two live video-discovery checks passed (1,140 in that live run). Eight actual pinned video repository candidates were returned; a separate GitHub tools HTTP403 was reported while successful model-source results were retained. No model weights were downloaded or executed by that run.

- Save bounded, exact-repository public evaluation snapshots privately beside the configured catalog. Preserve original retrieval time, raw source facts, unknown flags and provenance; do not save model selections or approvals.
- Add explicit offline loading with a prominent saved/as-of label. Missing, corrupt or unavailable saved evidence never triggers an online fallback. Input/model changes cancel stale responses for both read modes.
- Publish cache files atomically, reject unsafe paths, mismatched identities, oversized data and future retrieval timestamps. Preserve corrupt files on read; clean only owned temporary writes. Live lookup remains usable with a warning if saving fails.
- Earlier saved-evidence increment validation: 1,119 regression/native checks and two actual public-snapshot save/reopen checks passed (1,121 in the live run). The real snapshot reopened from disk after disposal of its network client, preserving its original timestamp and two reported task values; no model was downloaded or loaded.
- Combined validation: 1,150 regression/native checks plus four live video-discovery and saved-evidence checks passed (1,154 in the live run). These establish metadata/action behavior, not new image/video inference or model-quality guarantees.

User-visible changes are recorded here. Dates identify publication updates, not a promise that every feature was first implemented on that date.

## 0.2.0-preview.11 — 2026-10-28 — Broader text discovery and reported benchmark evidence

- Added an explicit evidence action for selected GGUF source results. Opening copies only the repository; a separate public-metadata lookup shows exact benchmark/task values, evaluation dates, attribution, nullable source-verification flags and submission PR numbers. No inferred universal quality score or upstream-to-quantization inheritance.
- Bound parsing to 32 rows and transport to one MiB/20 seconds. Reject mismatched repository identities and redirects; skip malformed rows while preserving valid ones. Source links remain attribution data and are not followed. Evidence is session-only and does not approve, download, install or start any model.
- Changing the model, result set or repository cancels pending evidence and rejects late responses. Missing evaluations remain unknown; reported verification is distinguished from Nexus verification, and PR acceptance is not assumed.
- Validation: 1,107 regression/native checks plus four live metadata check instances across two runs passed (1,109 each). One actual base repository returned two source-reported task results; its GGUF counterpart returned none and inherited no scores. These cases establish API/attribution behavior, not model quality or exact evaluated weights.

- Combine popular, trending and recently updated text-generation GGUF feeds across all supported text workloads. Deduplicate repositories, interleave source lists and inspect up to 24 repositories with four concurrent reads; native text searches request up to 48 total results. Preserve source order within assessment groups instead of promoting smaller artifacts. Keep explicit single-order views and direct repository browsing available.
- Show source-reported repository parameters, update dates and downloads separately from hardware estimates and quality judgments. Missing or invalid metadata stays unknown. Source activity does not establish quality, and a repository modification is not necessarily a model release.
- Exclude known non-text tasks and files named as projectors, importance matrices, drafts or adapters from primary text-model results. Keep source failures partial, deadlines/cancellation bounded and saved searches isolated by ordering. Pagination and complete split-model support remain unfinished.
- Keep all returned model files visible, including missing evidence or unmet requirements, without selecting or approving them. Display selected-drive storage, runtime-memory estimates and the actual shortage reason separately.
- Add an explicit source-file browsing action. An exact repository lookup reads that repository directly; it does not depend on broad search ranking. Download, installation and runtime approvals remain separate.
- Add regression cases for a 101 GiB artifact with ample storage and insufficient memory, a quantized 32B-size artifact qualifying through RAM, repository coverage beyond the first four, neutral ordering and native selection/resource displays. Add opt-in live metadata search checks; no weights are downloaded by those checks.
- Earlier search-increment validation: 1,077 regression/native checks plus four live metadata check instances across separate general and `32B` searches passed (1,079 checks in each run). These establish metadata search behavior, not execution or quality of newly discovered models.
- Includes the separately reviewed image checkpoint-folder connections prepared for preview.10: exact configuration approval, original YAML preservation and application on the next explicit engine start. Actual SDXL Base 1024×1024 and SD1.5 512×512 generation were verified in their earlier, separate runs; this release does not claim a new inference run or all-model compatibility.

## 0.2.0-preview.10 preparation — 2026-10-28 — Approved image checkpoint-folder connections

This increment was prepared and tested locally but was not published as a separate GitHub release. Its changes are included in preview.11; the last earlier published release was preview.9.

- Added a separate folder-connection review for an explicitly chosen existing portable ComfyUI engine and local checkpoint folders. Engine/folder inputs begin empty; a completed image download can supply its already chosen folder only through an explicit action.
- Display exact configuration contents, the existing configuration hash, approved folders and a unique recovery-backup path. A fresh, exact approval is required; changing inputs, stale async results, failure, cancellation or close revokes it. No model or folder is selected automatically.
- Apply only the dedicated bounded JSON configuration and its recoverable backup. Preserve existing `ainexus-models.yaml` bytes. Reject malformed/foreign configs, stale identities, unsafe paths and occupied backup locations; report possible changes and actual paths if verification fails after publication.
- Read approved folder configuration alongside existing YAML on the next explicit engine start. Applying does not start, stop or reload an engine. Installed folder paths persist as configuration; workload/model choices and approvals are not remembered.
- Validation: 1,043 default regression/native checks plus 16 live check instances across three runs passed: six folder-configuration checks, five actual SDXL Base 1024×1024 image checks and five actual SD1.5 512×512 image checks. Folder apply started no engine; the separate image runs used explicit starts/selections, loaded both configurations, decoded actual PNGs and stopped their owned engines. These establish two specific PC/runtime/checkpoint combinations, not all-model, hardware or quality guarantees. Earlier preview.9 fresh-download/reuse and preview.7 text-chat/image evidence retain their separate provenance; acquisition receipts remain historical data-integrity records.

## 0.2.0-preview.9 — 2026-10-28 — Explicit image-file downloads

- Added a neutral image-file setup page reached only from an explicitly selected source result. Destination starts empty; review and approval are separate. No preset model or automatic download, configuration or launch.
- Exact pinned source/license/file/bytes/hash and current storage evidence bind a short-lived approval. Folder/source changes, failure or cancellation revoke it. Transfer consumes approval; close waits for safe cancellation and owned-partial cleanup.
- Verify real bytes and SHA-256, preserve conflicting files, rehash matching existing files, and record private image-specific receipts. Reuse-only reviews cannot silently turn into replacement downloads if a file disappears.
- Preserve unknown checkpoint completeness, hardware fit and runtime execution. Image-data approval is not a GGUF or engine-launch approval. Engine-folder connection remains a separate unfinished increment.
- Validation: zero-warning Release build; 988 regression/native checks plus six real image-acquisition checks passed. A fresh official SDXL base file (6,938,078,334 bytes) matched its published hash; separately approved reuse made no second transfer. No engine was configured or launched. Existing preview.7 text-chat and SD1.5 generation evidence is retained.
- Added ignore rules for model weights and private receipts to prevent accidental source publication.

## 0.2.0-preview.8 — 2026-10-27 — Neutral image artifact discovery

- Image searches now inspect public text-to-image model repositories for pinned root Safetensors candidates and retain separate GitHub tool listings. Exact repository queries inspect the named source directly; no featured/default model or silent substitute is chosen.
- Preserve source revision/file/bytes/SHA-256/license/access evidence. Unknown facts remain unknown. Bounded metadata requests reject unsafe paths, shards, known component basenames, contradictory sizes and conflicting duplicate identities.
- Show image file candidates by default with an explicit unverified notice and a count separate from estimated-fit text results. No GGUF memory rule or complete-checkpoint claim is applied. A candidate can still be an adapter/component and requires further recipe/content evidence.
- Explicit image review exposes exact source/file evidence in Discover. No image download, installation, configuration or launch is granted or performed. Changing storage clears stale image results/notices; other unassessed results retain an unchecked opt-in filter.
- Validation: 919 regression/native checks and two actual public image-metadata checks passed. The live query returned three candidates, all unassessed against actual hardware; no weights were downloaded. Existing preview.7 Library chat and 1024×768 SD1.5 execution evidence is retained without claiming a new runtime run or second image checkpoint.

## 0.2.0-preview.7 — 2026-10-27 — Installed models from Library and image size controls

- Added a supported GGUF Library flow: choose a model, choose its exact installed copy, review the saved source and path, then explicitly load it with an existing engine and open chat. Neither the model nor a local copy is selected automatically.
- Local review reads the selected private receipt without a source search, download, hardware scan or engine launch. Fresh-acquisition actions stay disabled for an installed-copy review.
- Bound the selected copy to its receipt plan fingerprint, artifact identity and destination. Changed, missing, malformed or conflicting evidence is rejected rather than repaired or downloaded automatically.
- Explicit loading verifies the current model hash, checks current hardware capacity and tests local generation before chat. A read lease prevents model writes/replacement during verification, hardware checking, startup and the initial response. An existing owned server still requires an explicit stop before another model can load.
- Added session-only image width and height controls, from 256 to 1024 pixels in multiples of 64. Invalid dimensions cannot reach resource admission or the engine. One explicit generation captures the chosen dimensions/checkpoint and prevents competing submissions while busy; no checkpoint is selected automatically.
- Validation: 893 default regression/native checks passed, including same-size corruption, receipt swapping, explicit copy selection, pending-load write protection, insufficient current capacity and image workflow dimension payloads. Fixture requests establish the selected dimensions and checkpoint in the real workflow payload. Five additional live image checks verified an actual 1024×768 decoded PNG using the existing SD1.5 checkpoint and stopped the owned image engine; this is one checkpoint/runtime combination, not all-checkpoint compatibility.
- Six additional live Library checks passed for each of two existing models: a 7B coding model and a 32B text model. Both completed verification, loading, initial generation and two chat turns, then closed their owned servers. Existing weights and engines were reused without online source lookup or re-download; these runs do not establish all-model or multi-GPU support. One image-checkpoint combination remains the extent of actual image-generation evidence.

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
