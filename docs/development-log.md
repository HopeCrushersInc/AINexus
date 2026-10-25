# Development activity

## 2026-10-25 — Native storefront and real local images

User authorization: continue development, redesign the interface around a store/library workspace, install the specifically reviewed ComfyUI/SD1.5 stack on F:, maintain an F: activity log, and update GitHub. System-wide Windows changes still require approval.

Implemented:

- Native Home/sidebar workspace with Discover, Library, Images, Assistant, Downloads & setup and Performance. Original tools remain reachable under Advanced tools; hardware specifications still come from actual probes.
- Separate browsing, model download controls, runtime package details and compatibility/source evidence. Improved typography, spacing, contrast, keyboard navigation and accessibility.
- Local ComfyUI inspection, actual checkpoint choices, explicit standard diffusion generation, exact-job history/output retrieval, decoded PNG preview/save and targeted cancellation.
- Hidden owned-engine lifecycle with loopback binding, no browser auto-launch, custom nodes disabled, model-hub offline flags, and portable-folder caches. Close waits for active work cancellation; stopping retains ownership if exit cannot be confirmed.

Approved workstation setup:

- Official ComfyUI v0.42.1 portable CUDA 12.6 archive, 1,910,153,206 bytes. SHA-256 `0506abc6563ea5928a052fe0bc0b32c2a063098558bd593fe39345a27f1e47fc`.
- Comfy-Org SD1.5 FP16 safetensors checkpoint, pinned revision `9cfd069101959ca3828bf9c04a4419870832b74f`, 2,132,696,762 bytes. SHA-256 `e9476a13728cd75d8279f6ec8bad753a66a1957ca375a1464dc63b37db6e3916`.
- Verified downloads, inspected 67,951 archive entries and 5,619,088,436 declared expanded bytes, then extracted into a new F: runtime folder. Used the existing 7-Zip installation. Preserved existing runtimes/models. No system-wide Windows settings, services, registry, PATH, firewall or drivers changed.
- Activity log: `F:\AINexus\Logs\development-activity.md`. Exact approved setup review: `F:\AINexus\Logs\image-generation-setup-review.md`.

Verification:

- Release build passed without warnings/errors.
- 472 deterministic/native checks passed; the actual local image run passed 476 checks.
- Real ComfyUI 0.42.1 generation returned a decoded 512×512 PNG from the installed SD1.5 checkpoint, seed 42753. PNG, hash, prompt, job identity and diagnostics are under `F:\AINexus\Logs\ImageVerification`.
- The owned test server stopped after generation. Native engine Start/Stop and real checkpoint discovery were also checked without another generation; the portable root was saved through the application preferences.
- Visually checked Home and Image studio; verified the selected-content template exposes page controls to accessibility tools.

Boundaries: image-model online discovery, broader diffusion architectures/editing and audio/video workflows remain incomplete. CPU/RAM admission is a point-in-time policy check; local generation/offline flags are not OS network-isolation proof or a quality/performance benchmark. Runtime/model binaries, caches and private verification logs are not committed.

GitHub synchronization uses the development branch `phase11/store-and-images`, preserving the existing remote branches. Remote update results are also recorded in the F: activity log.
