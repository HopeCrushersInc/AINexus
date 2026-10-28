# AI Nexus Portal

**Your hardware. Your workflow. Your choice.**

AI Nexus scans the computer, shows workload categories, and searches live internet sources after the user asks. It does not choose a model, promote a provider, or install software automatically.

### [Download for Windows x64](https://github.com/HopeCrushersInc/AINexus/releases/download/v0.2.0-preview.10/AINexus-0.2.0-preview.10-win-x64.zip)

**0.2.0-preview.10 · Development preview**

[Release notes & checksums](https://github.com/HopeCrushersInc/AINexus/releases/tag/v0.2.0-preview.10) · [What works / what is unfinished](docs/project-status.md) · [Changelog](CHANGELOG.md) · [Discussions](https://github.com/HopeCrushersInc/AINexus/discussions)

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
| Image discovery | Live pinned Safetensors artifact metadata plus tool repositories; image files remain unverified candidates |
| Other workload discovery | Live public tool-repository metadata; install adapters vary and are explicitly identified |
| Persistent catalog and Library | SQLite source catalog, merged task categories, offline browsing and private acquisition-receipt links |
| Supported data acquisition | Explicit GGUF and image-Safetensors file reviews, exact approval, size/SHA-256 verification, private receipts and no conflicting overwrite |
| Runtime and local use | Explicit engine setup, local chat, image-engine configuration and owned-process controls |

**Discovery is not installation. A search result is not a recommendation or proof of compatibility.** Automated installation is currently limited to implemented recipes; finding an art or music tool does not mean its installer is supported.

Image discovery searches public Hugging Face text-to-image repositories for bounded root Safetensors candidates and also lists GitHub tools. Pinned file sizes, hashes and licenses are shown when supplied. Image file candidates appear by default with an explicit unverified notice; a file may be a component or adapter, and its hardware fit and complete checkpoint contents are not established. Video and other non-text categories still retrieve tool repositories. Nexus does not apply text-model memory rules to these results. Explicit image-file setup can download the selected data after exact source/license/destination approval, with hash verification. It does not connect model folders, install an engine or establish a working checkpoint.

The new Library keeps saved source facts separate from private local receipts. Browsing and filtering do not contact the internet, rehash large models, install software or start engines. A receipt means an acquisition was recorded; current integrity and successful offline execution need their own checks. See [catalog design and storage](docs/catalog.md).

Supported GGUF models can also be opened from Library: choose the model, select its exact installed copy, review the saved source and local path, then explicitly load it with an existing engine. Local review starts no download or engine. Loading checks the current file hash and hardware capacity before testing generation and opening chat.

Image folders can be connected to an existing portable ComfyUI engine through a **separate review and approval**. The review shows the exact folders, configuration contents, existing file hash and recovery backup. Applying writes only the dedicated configuration; the original engine YAML stays unchanged. It affects the **next explicit engine start**, then you refresh the checkpoint list and choose a model yourself. Installed folder configuration persists; workload/model choices and approvals are never restored.

Images offers session-only width and height controls from **256 to 1024 pixels**, in multiples of 64. Explicit local generation was verified with SD1.5 at 512×512 and SDXL Base at 1024×1024 using the existing ComfyUI engine and both folder configurations. These are specific tested combinations, not default model choices or a guarantee for every checkpoint, size and computer.

## Appearance and controls

The simpler Home screen uses task and action buttons over a black-hole backdrop. Standard is the startup appearance. Select **Appearance → LCARS** for the alternate palette; hold **Shift** while clicking LCARS to reveal the hidden Retro style. Appearance is session-only.

Hold **Shift** and scroll the mouse wheel to resize text and buttons from **75% to 175%**. The **Size** menu also offers smaller, larger and reset to 100%. Ordinary scrolling still scrolls. Use the native title bar to drag the window and any edge or corner to resize it.

## Preview limits

Windows x64 only. .NET is bundled; models and external engines are separate. Embedded web interfaces need WebView2. Nexus leaves GPU power settings unchanged unless the user explicitly applies a supported per-card limit in Performance. There is no universal wattage or NVIDIA power-control requirement for model launch. Other platforms, broader image installation, automatic server switching, thermal/fan controls and iCUE investigation remain unfinished.

Windows stays on its local account. No purchase or Microsoft sign-in is required. Existing model chat tests are recorded as validation examples, not promoted or preferred choices.

## Build and validate

```powershell
dotnet build AINexus.App/AINexus.App.csproj -c Release
dotnet run --project AINexus.Checks/AINexus.Checks.csproj -c Release
```

[Installation guide](docs/releases.md) · [Technical details](docs/technical-overview.md) · [Roadmap](docs/development-roadmap.md) · [Community](docs/community.md) · [Report a bug](https://github.com/HopeCrushersInc/AINexus/issues)
