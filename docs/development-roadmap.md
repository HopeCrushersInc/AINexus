# Project roadmap and projection

Updated 2026-10-29 for preview.16. This is a delivery order, not a date commitment.

## Current checkpoint

**Working within tested limits:** Windows hardware scans; user-selected workload discovery; private source catalog and offline browsing; exact approved model downloads; narrow CPU-runtime installation; local text chat; optional image-tool actions after explicit source selection; ten visible styles and hidden Retro. No AI engines, models or tool archives are preinstalled or selected automatically. Preview.16 passed 1,359 regression/native checks; historical live text/image evidence remains specific to its tested hardware, files and runtimes.

**Next milestone:** complete user-selected tool setup: inspect the exact acquired archive, review bundled terms and expanded storage, separately approve extraction into a new folder, verify the installed engine, then explicitly configure and launch it. Download approval is never installation or execution approval. Read-only 7z archive inspection is now implemented in a resource-limited portal worker. Bundled-terms content review and separately approved extraction/verification are next; inspection does not complete installation.

**Still unfinished:** broader optional tool adapters; complete video/music setup; general repair/update/rollback; automatic server switching; dockable/popout workflows; services; training; Linux/macOS/ARM portal packages. The immediate hardware scope is 1–4 local GPUs, with actual multi-GPU execution still unverified.



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
