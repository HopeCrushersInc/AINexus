# AI Nexus Portal

**Your hardware. Your workflow. Your choice.**

AI Nexus scans the computer, shows workload categories, and searches live internet sources after the user asks. It does not choose a model, promote a provider, or install software automatically.

### [Download for Windows x64](https://github.com/HopeCrushersInc/AINexus/releases/download/v0.2.0-preview.2/AINexus-0.2.0-preview.2-win-x64.zip)

**0.2.0-preview.2 · Development preview**

[Release notes & checksums](https://github.com/HopeCrushersInc/AINexus/releases/tag/v0.2.0-preview.2) · [Changelog](CHANGELOG.md) · [Discussions](https://github.com/HopeCrushersInc/AINexus/discussions)

![Workload-first startup with unchecked categories](docs/assets/workload-choices.png)

*Startup regression-check capture using fixture hardware. Categories are visible and unchecked; no model is selected.*

## The workflow

1. **Open Nexus.** It scans local hardware; no software is installed or started.
2. **Choose your tasks.** Coding, art/images, music/audio, video, speech, 3D, CAD, 3D printing, game development, reverse engineering, streaming, assistants and agents are visible. Nothing is checked or remembered.
3. **Find online choices.** Confirming the checked tasks searches every selected category using the startup hardware snapshot. Estimated-fit results appear first; a separate toggle reveals unassessed or insufficient-resource results. No result is preselected or labeled “best.” A category or query can narrow a later search.
4. **Choose a result.** Inspect the actual source, license and available evidence. Unknown information stays unknown.
5. **Approve a specific action.** Supported download/install recipes require review and explicit approval. Configuration and launch remain separate actions.

## What is available

| Area | Current behavior |
| --- | --- |
| Text/coding model discovery | Live public GGUF metadata with pinned source identities and resource estimates |
| Other workload discovery | Live public tool-repository metadata; install adapters vary and are explicitly identified |
| Persistent catalog and Library (local development build) | SQLite source catalog, merged task categories, offline browsing and private acquisition-receipt links |
| Supported model acquisition | Exact-plan approval, size/SHA-256 verification, receipts and no overwrite of different files |
| Runtime and local use | Explicit engine setup, local chat, image-engine configuration and owned-process controls |

**Discovery is not installation. A search result is not a recommendation or proof of compatibility.** Automated installation is currently limited to implemented recipes; finding an art or music tool does not mean its installer is supported.

Image/video discovery currently retrieves tool repositories, not complete offline model packages with verified requirements. Those results appear under compatibility review, never as established hardware-fit matches. The UI reports when no fit can be established; it does not substitute a text-model memory rule.

The new Library keeps saved source facts separate from private local receipts. Browsing and filtering do not contact the internet, rehash large models, install software or start engines. A receipt means an acquisition was recorded; current integrity and successful offline execution need their own checks. See [catalog design and storage](docs/catalog.md). This work is not yet included in the linked preview.2 download.

## Preview limits

Windows x64 only. .NET is bundled; models and external engines are separate. Embedded web interfaces need WebView2. The current guided launch power gate requires a verifiable250W NVIDIA policy. Other platforms, broader image installation, automatic server switching, thermal/fan controls and iCUE investigation remain unfinished.

Windows stays on its local account. No purchase or Microsoft sign-in is required. Existing model chat tests are recorded as validation examples, not promoted or preferred choices.

## Build and validate

```powershell
dotnet build AINexus.App/AINexus.App.csproj -c Release
dotnet run --project AINexus.Checks/AINexus.Checks.csproj -c Release
```

[Installation guide](docs/releases.md) · [Technical details](docs/technical-overview.md) · [Roadmap](docs/development-roadmap.md) · [Community](docs/community.md) · [Report a bug](https://github.com/HopeCrushersInc/AINexus/issues)
