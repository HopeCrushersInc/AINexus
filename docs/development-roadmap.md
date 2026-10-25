# Master workflow roadmap

The product workflow is **Scan hardware → Search online → Find compatible AI → Install or compile → Configure → Verify → Run offline**.

## Baseline audit

| Workflow step | Verified existing behavior | Missing capability |
| --- | --- | --- |
| Scan hardware | CPU/RAM/GPU/storage discovery; live CPU/RAM/NVIDIA memory and utilization | Broader driver/backend evidence, OS/instruction-set inventory, selected-volume planning |
| Search online | Manual package metadata review only | Hardware-aware model/software discovery, provenance, saved catalog |
| Find compatible AI | Actual resource admission checks before tasks | Candidate requirement estimates, explainable comparisons, explicit unknowns |
| Install or compile | Narrow explicit WinGet actions; portable llama.cpp was set up with approval | General approved artifact acquisition, source-build recipes, rollback/recovery |
| Configure | Selected GGUF/executable, loopback port/context, automatic runtime GPU selection | Hardware-specific validated configuration plans across workflows |
| Verify | Real llama.cpp generation, owned-process lifecycle, embedded UI and deterministic checks | Persistent verification evidence tied to exact runtime/model/configuration |
| Run offline | Local llama.cpp GGUF inference; no cloud inference dependency | Offline readiness checklist, saved catalog and artifact completeness checks |

The working WPF shell, local library, optional Ollama adapter, package controls, telemetry, embedded interfaces, and serial task queue remain in place. No preinstalled language model is required to open or use discovery.

## Priorities

1. **Discovery and compatibility foundation.** Enrich actual machine evidence, query verified public metadata on explicit search, present source/licensing/size/revision/hash, compare GGUF candidates with actual memory and a selected volume, and save search evidence for offline review. Initial executable compatibility adapter: llama.cpp text-generation GGUFs for assistants/coding/agents. Other interests must disclose missing adapters.
2. **Runtime/software catalog.** Add upstream releases, target architectures/backends, exact source-build recipes and dependencies. Cover additional workflow interests with source-specific adapters and explicit support boundaries.
3. **Reviewable acquisition plans.** Produce exact download/build/configuration plans with disk estimates, origins, checksum verification and license terms. Installation/build execution requires the user's approval for that concrete plan; discovery never implies approval.
4. **Evidence-driven verification and offline readiness.** Tie actual smoke-test outcomes to artifact hashes, runtime versions, configuration and detected hardware. Detect missing assets before claiming offline readiness. A resource estimate must never become a verified-success claim.
5. **Additional workflows.** Add image/audio/speech/video adapters before specialized CAD/3D/game/streaming integrations. Keep acquisition and verification recipes distinct from model search results.
6. **Authorized network execution.** Keep loopback-only defaults. Add explicit authorization, target identity and transport controls before connecting to a user's network server. No LAN scanning occurs by default.
7. **Resource governance and workflows.** Extend vendor telemetry, configurable admission and serial execution toward workload coordination. OS-enforced limits, performance estimates and multi-step agents require separate implementation and validation.

## Engineering boundaries

- Hardware comes from local probes; no GPU/CPU model assumptions.
- Public-source search sends workflow/query text, not the hardware profile. Compatibility is evaluated locally.
- Unknown memory, license, driver, architecture or artifact metadata remains unknown.
- Requirement estimates include disclosed policy assumptions; there are no invented throughput benchmarks.
- Cached results show their retrieval timestamp and are never described as fresh online evidence.
- Metadata queries do not download weights, execute build scripts, install tools, contact inference subscriptions or start GPU workloads.
- Installation, source compilation and remote-server access remain separate authorization boundaries.
- Commit each verified increment locally; do not push.
