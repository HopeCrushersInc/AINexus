# Releases and portable Windows packages

AI Nexus releases are development previews. Published previews and their exact assets are listed on the release page; the current source packaging example is **0.2.0-preview.14**. A version number in this guide does not mean that its release has already been uploaded; use the [GitHub Releases page](https://github.com/HopeCrushersInc/AINexus/releases) for actual published assets.

## Run a published package

1. Open the matching prerelease on GitHub Releases. Download `AINexus-<version>-win-x64.zip` and its version-specific `SHA256SUMS.txt` file from that release.
2. Compare the downloaded ZIP's SHA-256 with the checksum file. For example, use `Get-FileHash -LiteralPath '<downloaded ZIP>' -Algorithm SHA256` in PowerShell. A checksum detects changed bytes; it is not a publisher signature.
3. Extract the **complete ZIP** into a new folder. Keep all the EXE, managed/native DLLs, runtime/configuration files, and notices together.
4. Open the extracted package folder and run `AINexus.App.exe`. Read `README-FIRST.txt` before configuring a runtime or model.

This is an unsigned preview. Windows may show publisher or reputation prompts. Check the release source and checksum and follow your PC's security policy; the project does not ask you to disable protection or change account types.

The `win-x64` package includes the .NET runtime, so running it does not require a separate .NET SDK or .NET Desktop Runtime installation. It is a Windows x64 package; Linux, macOS and ARM64 portal releases are not established by this package.

Embedded local web interfaces require the separate free [Microsoft Edge WebView2 Runtime](https://learn.microsoft.com/microsoft-edge/webview2/concepts/distribution), which is not bundled here. Browser initialization happens only when opening an embedded local interface; missing WebView2 does not trigger automatic installation. Use Microsoft's documented distribution options if you need that feature.

No model weights, llama.cpp binaries, image engine, or other AI applications are included. Those remain separate reviewed acquisitions with their own dependencies and terms. Extracting/running this package does not register Windows services or request a Microsoft account. Windows can remain on its local account, and no paid plan or purchase is required by the package.

“Portable” describes the application directory. Preferences, logs and embedded-browser data use `%LOCALAPPDATA%\AINexus`. The catalog defaults to local application data; its folder can be selected explicitly or supplied by a local portable catalog-root.txt configuration file. Private machine-specific configuration is not included in public ZIPs. Existing settings may be read, but workload selections are session-only and start unchecked. Replacing a package does not delete models, runtimes or user settings. Keep a previous working package until the new one has been checked; do not overwrite a running application.

Checkpoint-folder connections are a separate explicit configuration operation for an already installed portable ComfyUI engine. Review and approve the exact folders, contents, existing hash and backup before applying. Only `ainexus-approved-checkpoints.json` and a uniquely named recovery backup are written inside that chosen engine; existing `ainexus-models.yaml` stays unchanged. Those private files are not release assets. Installed folder mappings persist and are read on a later explicit engine start; this does not restore workload/model selections or approvals, download models, or automatically restart an engine. See [configuration details](technical-overview.md#approved-checkpoint-folder-connections).

## Build a package from source

Building requires Windows, Git and the .NET 8 SDK. Run the repository's build and regression checks first; see [README](../README.md#build-and-validate). The package script publishes the application but does **not** run those checks or certify a model/runtime.

From the repository root:

```powershell
./scripts/Publish-Portable.ps1 -Version '0.2.0-preview.14'
```

Optional parameters:

- `-OutputRoot 'F:\AINexus\ReleasePackages'` selects the output location; the default is the repository's `artifacts` folder.
- `-SourceCommit '<full 40-character commit hash>'` binds the build to the currently checked-out commit; it must match `HEAD`.

Versions accept a SemVer numeric core and optional prerelease suffix. Build metadata is reserved for the source commit appended to the informational version. Publish settings are Release, `net8.0-windows`, `win-x64`, self-contained, non-single-file, and untrimmed. The full directory is intentional: copying only the EXE omits required dependencies. The script collects the exact published .NET/WebView2/System.Memory package notices and version-bound SQLite dependency licenses/notices, and stops if required text is missing. Preserve these third-party files; packaging does not grant additional rights to external models or engines.

The script creates a fresh GUID staging folder, never recursively deletes an existing folder, and refuses to overwrite an existing archive or checksum file. A failed operation preserves its stage for inspection. Repeated builds of the same version need a different output directory or an explicit manual review of old outputs.

Outputs:

| File | Purpose |
| --- | --- |
| `AINexus-<version>-win-x64.zip` | Complete application folder, bundled .NET files, dependencies, first-run instructions and manifest |
| `AINexus-<version>-win-x64-SHA256SUMS.txt` | SHA-256 of the ZIP |
| `PACKAGE-INFO.json` inside the package | Source commit, dirty-tree indicator, UTC build time, SDK, RID, publish flags and relative-path/size/SHA-256 inventory |

The inventory excludes its own manifest to avoid a circular hash. The ZIP checksum covers the manifest too. Build metadata describes inputs; it is not signed provenance or a promise of byte-for-byte reproducible builds. The script refuses a dirty working tree so every release is tied to committed source.

## Release publication checklist

- Use a clean, reviewed source commit and confirm its build/check results. Tag that exact commit, for example `v0.1.0-preview.1`; do not substitute a newer branch tip after validation.
- Publish the complete package, verify ZIP extraction and manifest hashes, and smoke-test the extracted EXE and owned-process cleanup. Distinguish local checks from CI checks and untested hardware.
- Keep the release marked **prerelease** while it remains a development preview. Include the source commit, build/check evidence, package target, WebView2 prerequisite and known limitations in release notes.
- Upload the ZIP and matching checksums together. Link the [changelog](../CHANGELOG.md). Retain required notices and keep private settings, account data, logs, model weights and unrelated tools out of assets.
- GitHub workflow/release publication and any stronger provenance verification are separate from this local packaging script. No installer, paid certificate, service modification or Microsoft account is needed for this portable package.
