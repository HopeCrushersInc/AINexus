# AI Nexus Portal

**Your hardware. Your workflow. Your choice.**

A local AI library and launcher inspired by Steam, Playnite and Ninite. Find models and tools for your computer, approve their setup, and use supported models offline.

### [Download for Windows x64](https://github.com/HopeCrushersInc/AINexus/releases/download/v0.2.0-preview.15/AINexus-0.2.0-preview.15-win-x64.zip)

**0.2.0-preview.15 · Development preview** · [Release notes & checksums](https://github.com/HopeCrushersInc/AINexus/releases/tag/v0.2.0-preview.15)

## How it works

1. **Scan** your hardware.
2. **Choose** your tasks, then browse online models and tools.
3. **Review and approve** each supported download, setup and launch.

No AI engines, tools or models are bundled or preinstalled. Nothing is selected or installed automatically.

![Workload choices start unchecked; fixture hardware shown](docs/assets/workload-choices.png)

## What works today

- **Text and coding:** online model discovery, approved GGUF downloads and local chat.
- **Images:** discover models and tools, choose your engine, then review available setup or local-use actions.
- **Library:** save source details and local download records; browse them offline.
- **Appearance:** ten visible styles, adjustable text/buttons and resizable windows.

**Still being built:** generator-engine installation, complete video setup/generation, automatic server switching, and releases for other platforms. A search result does not guarantee compatibility or an available installer.

Windows x64 only for now. The app includes .NET; models and AI engines are separate. No purchase or Microsoft sign-in is required.

[Start here](docs/start-here.md) · [What works / what is unfinished](docs/project-status.md) · [Changelog](CHANGELOG.md) · [Roadmap](docs/development-roadmap.md)

[Discussions](https://github.com/HopeCrushersInc/AINexus/discussions) · [Report a bug](https://github.com/HopeCrushersInc/AINexus/issues)

## Build and validate

```powershell
dotnet build AINexus.App/AINexus.App.csproj -c Release
dotnet run --project AINexus.Checks/AINexus.Checks.csproj -c Release
```

[Packaging](docs/releases.md) · [Technical details](docs/technical-overview.md) · [Community](docs/community.md)
