# Feature overview

Updated 2026-10-26. This is the product outline and direction, not a list of features already shipped. For delivery order and completion gates, see the [project roadmap](development-roadmap.md). For conversations and the documentation index, see the [community guide](community.md).

## Purpose

AI Nexus should help people discover, set up, maintain and use AI on the hardware they actually have. The core path remains **scan hardware → discover compatible candidates → review and approve acquisition/configuration → verify → launch locally offline**. No preinstalled language model should be required to browse discovery.

The longer-term workspace brings models, editing, workflows and services together, while showing what is installed, what is running, why a model was selected, and what needs attention.

## Current foundation

The current application is a Windows WPF development preview. Implemented foundations include hardware/software discovery, resource estimates, pinned public model metadata, approved GGUF acquisition, a local library, reviewed single native CPU runtime ZIP download/inspection/extraction, owned llama.cpp lifecycle, offline smoke-test evidence, local generation tasks and a narrow local ComfyUI image workflow. These have different coverage and limitations; see [README](../README.md) and [changelog](../CHANGELOG.md).

There is no general live editor, automatic model router, training system, dockable-window framework, durable workflow engine or background service mode yet. Linux, macOS and ARM64 portal builds are planned targets, not current supported products.

## Feature outline

| Area | Proposed experience | Delivery boundary |
| --- | --- | --- |
| Live editing | Edit prompts, workflow definitions and supported configuration with preview, diff, revision history and explicit apply/revert. Later add generated-text/code artifact editing. | Running jobs keep the revision they started with. Editing never silently executes generated code, changes active configuration or grants permissions. |
| Automatic switching | Opt in to routing tasks among installed, verified models according to capability, current resources, latency preference and local/offline policy. Show the selected model and reason. | Switch at a safe job boundary; drain or cancel owned work deliberately. Handle load failures and out-of-memory errors without loops. Downloads, cloud transfer and provider spending need separate approval. |
| Model maintenance | Show provenance, version, hash, license, disk use, health, dependencies, duplicates and last verification. Offer repair, relocation, archive and cleanup plans. | Distinguish missing/corrupt/unverified files. Preview exact affected files and workflows; never silently delete weights or break references. |
| Updates | Compare model/runtime versions, changed terms/dependencies and storage costs; stage side-by-side, verify, activate and retain rollback. | Update checks, downloads and activation are separate actions. A failed candidate leaves the prior working configuration available. |
| Training and tuning | Review local datasets, provenance, consent/license constraints, storage and hardware needs; progress toward isolated adapter fine-tuning, checkpoints and evaluation. | Dataset tooling comes first. Tuning is opt-in and never implied by inference. Keep base weights immutable; evaluate a new artifact before separate promotion. Full training from scratch is research, not a promised desktop capability. |
| Custom windows and popouts | Arrange panels for chat, editor, library, job queue, telemetry and logs; dock, resize, detach and use multiple monitors. | All views share job state and cannot duplicate execution. Restore inaccessible windows safely and support keyboard navigation. Saving a layout is explicit; it does not restore workload choices or approvals. |
| Reusable workflows | Build revisioned steps for inputs, model calls, transformations, evaluation and exports, with declared dependencies and visible results. | Begin with typed local steps and validation. Later permissions for tools/scripts are scoped per step; generated text never becomes executable authority. |
| Services and workers | Run approved jobs independently of the open window, with a visible queue, ownership, bounded logs, pause/resume and recovery. | Background startup/service registration is explicit and reversible. Use least privilege and loopback defaults. A closed window must clearly explain which authorized jobs continue. |
| Broader AI tools | Add speech, audio, image editing, video and specialist applications through distinct runtime/model adapters. | Each adapter needs its own acquisition, licensing, compatibility and actual execution tests; a search category is not working support. |
| Remote resources | Later connect explicitly selected local-network or online workers, with resource/cost policy and visible data flow. | No automatic LAN scan, credential sharing or external transfer. Authentication, transport, retention and spending controls precede remote execution. |

## Platform direction

ARM is a processor architecture, so support must be established for each OS/architecture combination. The current WPF shell cannot be treated as a Linux/macOS release merely because an underlying AI engine runs there.

| Target | Current portal evidence | Planned work before a supported release |
| --- | --- | --- |
| Windows x64 | Current workstation development and Windows CI; limited real runtime/model checks | Broader hardware/backend coverage, packaging, lifecycle/update recovery and release validation |
| Windows ARM64 | Not validated | Native build/package, architecture-correct runtime recipes, hardware probes, process and actual inference checks |
| Linux x64 | Not implemented as a portal release | Portable core, desktop shell decision, distribution/package scope, probes and end-to-end checks |
| Linux ARM64 | Not implemented as a portal release | Linux foundation plus device-specific memory, acceleration, runtime and packaging validation |
| macOS ARM64 | Not implemented as a portal release | Portable core/shell, native packaging, permission handling, compatible runtime/acceleration and end-to-end checks |
| macOS x64 | Not implemented as a portal release | Separate demand/feasibility review, build/runtime availability and lifecycle validation; no parity promise |

Start with CPU fallback where feasible, then validate GPU/accelerator backends individually. NVIDIA, AMD, Intel and Apple acceleration are separate adapter/test work; architecture alone does not establish backend support. Mobile operating systems and 32-bit ARM are outside the initial projection.

## Experience rules

- Show the difference between a candidate, acquired artifact, configured runtime and verified working setup.
- Keep fresh workload selection unchecked. Reusable workflow definitions and explicitly saved layouts do not authorize startup actions.
- Prefer portable, user-selected locations and configurable storage volumes; disclose caches, logs and service registrations.
- Preserve user data and known-good configurations during updates, migration and recovery.
- Show unknowns honestly. Offer local/offline operation where the selected workflow supports it, with network activity visible when needed.
- Communicate completed changes in the [changelog](../CHANGELOG.md), proposals in the roadmap, and decisions/questions through [Discussions](https://github.com/unrelatednda-code/AINexus/discussions).
