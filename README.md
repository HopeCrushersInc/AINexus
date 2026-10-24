# AI Nexus Portal

A native C# .NET 8 WPF dashboard for local AI tooling on Windows.

## Current capabilities

- Hardware discovery: actual CPU, installed RAM, graphics adapters, and fixed-drive capacity/free space. NVIDIA VRAM comes from the installed driver utility; unsupported VRAM remains unavailable.
- AI software discovery: Ollama, LM Studio, GPT4All, Jan, AnythingLLM, Docker tooling, Python, and Git.
- Read-only software checks inspect PATH, common installation directories, and Windows uninstall registrations. Each result distinguishes an executable found from registration-only evidence and includes location/version when available.
- Independent rescan actions with cancellation on window close.

Discovery does not launch discovered applications, install software, or contact remote services. A missing result means no evidence was found in the locations checked; arbitrary portable/custom installations may be missed. Discovery does not establish runtime health. Python Windows Store execution aliases are excluded.

## Build and validate

Requires Windows and the .NET 8 SDK with the Windows Desktop runtime.

```powershell
dotnet build AINexus.App/AINexus.App.csproj --configuration Release
dotnet run --project AINexus.Checks/AINexus.Checks.csproj --configuration Release
dotnet run --project AINexus.Checks/AINexus.Checks.csproj --configuration Release -- --live
```

The dependency-free checks exercise detection fixtures and WPF XAML loading without showing a window. `--live` also checks real local software and hardware discovery. Build output is under `AINexus.App/bin/Release/net8.0-windows`.

## Incremental roadmap

1. Hardware discovery and monitoring.
2. AI software discovery.
3. Installer/updater with explicit plans, verified package identities, and captured outcomes.
4. AI libraries and local models.
5. Embedded interfaces and resource governance.
6. Agent orchestration within the unified native dashboard.

Install/update, resource limits, and agent controls will only be exposed as those operations are implemented and validated. Repository changes are committed locally; pushing requires authorization.
