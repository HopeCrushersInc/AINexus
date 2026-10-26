[CmdletBinding()]
param(
    [ValidateLength(1, 128)]
    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-(?:(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*))*))?$')]
    [string]$Version = '0.1.0-preview.1',
    [string]$OutputRoot,
    [string]$SourceCommit
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'Publish this Windows WPF package on Windows with the .NET 8 SDK.' }

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $repositoryRoot 'AINexus.App\AINexus.App.csproj'
if (!(Test-Path -LiteralPath $project -PathType Leaf)) { throw 'The application project is missing.' }
$headOutput = & git -C $repositoryRoot rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw 'Could not resolve the checked-out source commit.' }
$headCommit = ($headOutput -join '').Trim()
if ([string]::IsNullOrWhiteSpace($SourceCommit)) { $SourceCommit = $headCommit }
if ($SourceCommit -notmatch '^[0-9a-fA-F]{40}$') { throw 'SourceCommit must be a complete 40-character Git commit hash.' }
if (!$SourceCommit.Equals($headCommit, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'SourceCommit must match the checked-out HEAD; do not label a package with another revision.'
}
$SourceCommit = $SourceCommit.ToLowerInvariant()
$dirtyOutput = @(& git -C $repositoryRoot status --porcelain --untracked-files=normal)
if ($LASTEXITCODE -ne 0) { throw 'Could not inspect the source working tree.' }
$workingTreeDirty = $dirtyOutput.Count -gt 0
if ($workingTreeDirty) { throw 'Package a clean committed working tree so the source commit identifies the complete release.' }
$sdkOutput = & dotnet --version
if ($LASTEXITCODE -ne 0) { throw 'The .NET SDK is unavailable.' }
$sdkVersion = ($sdkOutput -join '').Trim()

if ([string]::IsNullOrWhiteSpace($OutputRoot)) { $OutputRoot = Join-Path $repositoryRoot 'artifacts' }
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$packageName = "AINexus-$Version-win-x64"
$archivePath = Join-Path $OutputRoot "$packageName.zip"
$checksumPath = Join-Path $OutputRoot "$packageName-SHA256SUMS.txt"
foreach ($destination in @($archivePath, $checksumPath)) {
    if (Test-Path -LiteralPath $destination) { throw "Output already exists and will not be overwritten: $destination" }
}
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
$stage = Join-Path $OutputRoot ('stage-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage | Out-Null
$publishDirectory = Join-Path $stage $packageName
New-Item -ItemType Directory -Path $publishDirectory | Out-Null
Write-Host "Publishing into new staging directory: $publishDirectory"

$publishArguments = @(
    'publish', $project, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
    '-p:PublishSingleFile=false', '-p:PublishTrimmed=false',
    "-p:Version=$Version", "-p:InformationalVersion=$Version+$SourceCommit",
    '-p:IncludeSourceRevisionInInformationalVersion=false', '-o', $publishDirectory
)
& dotnet @publishArguments
if ($LASTEXITCODE -ne 0) { throw "Publish failed. Staging was preserved for inspection: $stage" }
if (!(Test-Path -LiteralPath (Join-Path $publishDirectory 'AINexus.App.exe') -PathType Leaf)) {
    throw "Publish produced no AINexus.App.exe. Staging was preserved: $stage"
}

$readme = @"
AI Nexus Portal $Version - Windows x64 development preview
Source commit: $SourceCommit

Extract the COMPLETE ZIP into a new folder, then run AINexus.App.exe from that
folder. Keep its DLLs, native files, configuration files and notices together.
Do not run the EXE from inside the ZIP or copy it away from its dependencies.

This package includes the .NET runtime. No .NET SDK installation is needed to
run it. The separate free Microsoft Edge WebView2 Runtime is required for
embedded local web interfaces; it is not bundled in this ZIP. The normal
hardware/discovery interface does not initialize that browser automatically.
Official WebView2 information:
https://developer.microsoft.com/microsoft-edge/webview2/

Windows x64 is the only target of this package. Linux, macOS and ARM64 portal
packages are not included or verified. This preview includes no model weights,
llama.cpp/ComfyUI engines or other external AI applications. Acquire and review
those separately using the supported workflows and their own license terms.

Application files are portable, but preferences, logs and browser profile data
use the current user's LocalAppData\AINexus folder. Existing preferences can
be read; workload choices are session-only and start unchecked. No model or
runtime starts merely because this package was extracted or launched.

No installer, Windows service registration or Microsoft account sign-in is
performed by extracting/running this package. Windows may remain a local
account. This package does not require a paid subscription or purchase.

This preview is unsigned. Windows may display publisher/reputation prompts.
Verify the release source and published checksum and follow your PC's policy.
A matching SHA-256 checks bytes; it is not publisher authentication. Preserve
any bundled third-party license and notice files when copying this package.

PACKAGE-INFO.json records build inputs and file hashes. The manifest is not a
signature and does not claim every model, runtime or hardware setup was tested.
Project instructions and changelog:
https://github.com/unrelatednda-code/AINexus
"@
[IO.File]::WriteAllText((Join-Path $publishDirectory 'README-FIRST.txt'), $readme, [Text.UTF8Encoding]::new($false))

$inventory = @(Get-ChildItem -LiteralPath $publishDirectory -File -Recurse | Sort-Object FullName | ForEach-Object {
    [ordered]@{
        Path = $_.FullName.Substring($publishDirectory.Length + 1).Replace('\', '/')
        SizeBytes = $_.Length
        Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
})
$packageInfo = [ordered]@{
    SchemaVersion = 1
    Version = $Version
    SourceRepository = 'https://github.com/unrelatednda-code/AINexus'
    SourceCommit = $SourceCommit
    WorkingTreeDirty = $workingTreeDirty
    BuiltAtUtc = [DateTime]::UtcNow.ToString('o')
    DotNetSdkVersion = $sdkVersion
    TargetFramework = 'net8.0-windows'
    RuntimeIdentifier = 'win-x64'
    Configuration = 'Release'
    SelfContained = $true
    PublishSingleFile = $false
    PublishTrimmed = $false
    InformationalVersion = "$Version+$SourceCommit"
    ApplicationChecksRunByThisScript = $false
    ManifestIncludesItself = $false
    Files = $inventory
}
$manifestPath = Join-Path $publishDirectory 'PACKAGE-INFO.json'
[IO.File]::WriteAllText($manifestPath, ($packageInfo | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))

# Stage the complete ZIP first. Move without overwrite, preserving diagnostics on failure.
$stagedArchive = Join-Path $stage "$packageName.zip"
Compress-Archive -LiteralPath $publishDirectory -DestinationPath $stagedArchive -CompressionLevel Optimal
[IO.File]::Move($stagedArchive, $archivePath)
$archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
$checksumText = "$archiveHash  $packageName.zip`n"
$checksumStream = [IO.File]::Open($checksumPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
try {
    $checksumBytes = [Text.UTF8Encoding]::new($false).GetBytes($checksumText)
    $checksumStream.Write($checksumBytes, 0, $checksumBytes.Length)
}
finally { $checksumStream.Dispose() }

[pscustomobject]@{
    ArchivePath = $archivePath
    ChecksumPath = $checksumPath
    Sha256 = $archiveHash
    PublishDirectory = $publishDirectory
    PackageInfoPath = $manifestPath
    SourceCommit = $SourceCommit
    WorkingTreeDirty = $workingTreeDirty
}
