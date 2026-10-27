# Project roadmap and projection

Updated 2026-10-27. This replaces the early baseline audit with the current implementation state and proposed delivery order. It is a direction and dependency plan, not a release-date commitment. See the [feature overview](feature-overview.md) for the user experience and platform matrix.

## Current checkpoint

**Implemented, within documented limits:** Windows WPF hardware/software scans; hardware-aware GGUF discovery; saved metadata; exact-plan model acquisition and integrity receipts; runtime release/file reviews; approved single native CPU ZIP download, inspection and extraction; local library and generation; owned llama.cpp offline-mode launch and explicit smoke-test evidence; native workload navigation; narrow local ComfyUI generation.

Read-only per-model library integrity checking against an explicit acquisition receipt is now available; a separate read-only recovery review now proposes a preserved-source replacement path and fresh-review steps. Neither feature is automated repair or update management.

**Not yet implemented:** general model repair/update/rollback, live artifact editing, automatic switching, dockable/popout workspaces, durable multi-step workflows, service mode, training and non-Windows portal releases. GPU/multiple-archive runtime installation and dependency/source-build recipes remain incomplete. Preview.5 validation includes 839 default local regression/native checks and seven additional live selected-model checks. Publication CI is reported on the release; these checks do not establish all-model, four-GPU execution or cross-platform support.

## Projected phases

Immediate scope: reliable, user-directed scan → find → select → review/approve → download/install/configure → verify → local use, with correct inventory and per-device choices for 1–4 local GPUs. Larger fleets and distributed scheduling are deferred. Detecting four devices is not proof that an arbitrary model/runtime supports splitting a model across them; VRAM is never silently pooled by the planner.

| Phase | Deliverables | Exit gate | Dependencies |
| --- | --- | --- | --- |
| 1. Reliable setup and lifecycle | Broader runtime/dependency recipes; inventory health, repair/relocation plans; side-by-side model/runtime updates and rollback | Exact plans and approvals; corrupt/missing artifacts actionable; interrupted operations recover; candidate failure preserves known-good state; actual offline launch reverified after changes | Current acquisition/receipts, architecture/backend evidence and storage checks |
| 2. Editable workspace | Live prompt/configuration editor; reusable revisioned workflow definitions; custom panels, layouts and popouts | Preview/apply/revert works; active jobs retain their revision; detached views share state without duplicate jobs; multi-monitor and accessibility checks pass | Stable job identity/configuration boundary; editing can begin alongside phase 1 |
| 3. Coordinated execution | Durable typed workflow jobs; opt-in model routing; resource-aware queues; worker/service lifecycle | Routing reasons visible; switching is at safe boundaries; overload/load failure recovers without retry loops; cancellation targets owned jobs; restart recovery and explicit background enable/disable tested | Verified model inventory, phase 1 lifecycle and revisioned definitions |
| 4. Platform expansion | Portable core and platform adapters; desktop shell selection/prototype; target-specific packages and CI | Each supported OS/architecture passes install/configure/scan/acquire/verify/offline launch/update/rollback/uninstall checks on real hardware | Core extraction and cross-platform shell decision start alongside phase 1; reusable window logic must avoid new WPF coupling; supported releases depend on lifecycle gates and access to target test machines |
| 5. Training and broader workloads | Dataset review/versioning; isolated adapter tuning; checkpoints/evaluation/promotion; further media adapters | Immutable base/dataset/config identities; bounded resource plan; cancellation/resume where supported; baseline comparison; independently approved promotion; tested recipe for each new workload | Worker/resource isolation, stable lifecycle, suitable hardware/storage and model/license support |

Remote workers are a separate extension after authenticated transport, target approval, permission and cost controls exist. They are not a prerequisite for offline desktop use. Individual platform/backend deliveries may overlap; completion of one target never implies parity on the others.

## Feature acceptance outline

### Live editing and automatic switching

- Edits have a draft, visible diff, validation result and explicit apply/revert. Record the revision each job actually used.
- Model routing is disabled until explicitly configured. Candidate models must be installed, verified for the task and permitted by local/offline policy.
- Check current resources before loading. Release only owned idle runtimes; never terminate another application's server to make room.
- Preserve task context/artifacts deliberately; disclose incompatible context formats and require a fresh session when necessary.
- Test queued/running jobs, model load failure, insufficient memory, cancellation and fallback. No silent cloud fallback or unapproved model download.

### Maintenance, updates and training

- Bind inventory and dependents to exact artifact/version evidence. Preview cleanup, repair and move actions before modifying files.
- Compare current/candidate versions and terms. Stage without overwriting, verify candidate, activate explicitly, retain a selectable rollback and test interruption/disk exhaustion. Include configuration/workflow schema migration and backward recovery, not just binary replacement.
- Establish a separate supported training runtime and hardware capability plan; an installed inference engine does not establish training support. Keep inference, adapter tuning, merging/export and full training distinct. Start with dataset inspection and a supported small tuning recipe rather than promising arbitrary training.
- Review dataset origin, access rights, license and sensitive content before use. No dataset export by default; use immutable versions and deliberate retention.
- Record configuration, checkpoints, resource use and evaluation results. A completed tuning run is not proof of better quality or safe deployment.

### Windows, workflows, popouts and services

- Shared job ownership and state across docked and detached views; no duplicated submission when windows reopen.
- Explicitly saved layouts and definitions remain distinct from startup workload selection and action approvals, which are never silently restored.
- Workflow steps declare inputs/outputs, permissions and dependencies. Failed steps preserve inspectable artifacts; retries cannot repeat irreversible actions silently.
- Define an authenticated local worker interface and separate worker and GUI ownership. Make continued background execution visible and optional; test close, restart, pause, cancellation and recovery.
- Service installation/startup changes are reviewed and reversible, with least privilege, bounded logs and loopback defaults. Remote access requires its own security design and tests.

### Platform completion checklist

For each target in the [platform matrix](feature-overview.md#platform-direction): build/package; native OS/architecture detection; CPU/RAM/storage probes; supported driver/backend evidence; path/symlink/permission checks; approved acquisition and dependency resolution; process ownership and shutdown; actual generation; offline-readiness verification; upgrade/rollback/uninstall; accessibility and window behavior. Test real devices as well as CI. Unsupported hardware and unavailable acceleration must remain explicit.

## Planning and publication

Near-term priority is lifecycle reliability before adding unattended execution. Forecast dates only after the next phase is scoped into reviewable increments and representative hardware is available; no delivery dates or performance numbers are invented here. Main risks are cross-platform UI architecture, target hardware access, backend/dependency diversity, storage requirements and evolving upstream formats.

Each increment should publish its implemented behavior, verification and limitations in [CHANGELOG](../CHANGELOG.md). Keep proposals separate from completed work. Use isolated branches, preserve other contributors' changes and publish through protected pull requests when authorized. Maintain a clear distinction between automated checks, workstation-specific live checks and untested targets.

See the [community guide](community.md) for announcements, questions, ideas, bug reports and the proposed private Discord workspace.
