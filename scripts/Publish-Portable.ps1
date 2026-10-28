[CmdletBinding()]
param(
    [ValidateLength(1, 128)]
    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-(?:(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*))*))?$')]
    [string]$Version = '0.2.0-preview.8',
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

# Self-contained publishing does not copy every redistributable notice from its NuGet packages.
# Bind notices to the versions actually selected in this published application's dependency graph.
$dependencyPath = Join-Path $publishDirectory 'AINexus.App.deps.json'
$assetsPath = Join-Path $repositoryRoot 'AINexus.App\obj\project.assets.json'
$dependencies = Get-Content -LiteralPath $dependencyPath -Raw | ConvertFrom-Json
$assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json
$packageFolders = @($assets.packageFolders.PSObject.Properties.Name)
if ($packageFolders.Count -eq 0) { throw 'The restore assets identify no NuGet package folders for required notices.' }
$noticeComponents = @(
    [pscustomobject]@{ LibraryName = 'runtimepack.Microsoft.NETCore.App.Runtime.win-x64'; RequireNotice = $true },
    [pscustomobject]@{ LibraryName = 'runtimepack.Microsoft.WindowsDesktop.App.Runtime.win-x64'; RequireNotice = $false },
    [pscustomobject]@{ LibraryName = 'Microsoft.Web.WebView2'; RequireNotice = $true },
    [pscustomobject]@{ LibraryName = 'System.Memory'; RequireNotice = $true }
)
$noticeDirectory = Join-Path $publishDirectory 'ThirdPartyNotices'
if (Test-Path -LiteralPath $noticeDirectory) { throw 'The package notice directory already exists; it will not be overwritten.' }
New-Item -ItemType Directory -Path $noticeDirectory | Out-Null
$noticeIndex = [Collections.Generic.List[string]]::new()
$noticeIndex.Add('AI Nexus bundled third-party licenses and notices')
$noticeIndex.Add('')
$noticeIndex.Add('The files below were copied unchanged from local restored NuGet package content.')
$noticeIndex.Add('Package identities and exact versions come from the published AINexus.App.deps.json.')
$noticeIndex.Add('Keep these notices with the corresponding redistributed components.')
$noticeIndex.Add('External AI engines and model weights are not included in this package.')
$noticeIndex.Add('')
foreach ($component in $noticeComponents) {
    $libraryMatches = @($dependencies.libraries.PSObject.Properties | Where-Object {
        $_.Name.StartsWith($component.LibraryName + '/', [StringComparison]::OrdinalIgnoreCase)
    })
    if ($libraryMatches.Count -ne 1) {
        throw "Expected exactly one published dependency for $($component.LibraryName); required notices cannot be resolved."
    }
    $identityParts = $libraryMatches[0].Name.Split('/')
    if ($identityParts.Count -ne 2 -or $identityParts[1] -notmatch '^[0-9A-Za-z][0-9A-Za-z.+-]*$') {
        throw 'A published dependency has an invalid package version for notice resolution.'
    }
    $nugetPackageName = ($identityParts[0] -replace '^runtimepack\.', '').ToLowerInvariant()
    $nugetVersion = $identityParts[1].ToLowerInvariant()
    $packageDirectories = @($packageFolders | ForEach-Object {
        Join-Path (Join-Path $_ $nugetPackageName) $nugetVersion
    } | Where-Object { Test-Path -LiteralPath $_ -PathType Container })
    if ($packageDirectories.Count -eq 0) {
        throw "The restored NuGet package $nugetPackageName/$nugetVersion is unavailable for notice collection."
    }
    $sourceNotices = @(Get-ChildItem -LiteralPath $packageDirectories[0] -File | Where-Object {
        $_.Name -match '^(LICENSE|NOTICE|THIRD-PARTY-NOTICES)'
    } | Sort-Object Name)
    $licenses = @($sourceNotices | Where-Object { $_.Name -match '^LICENSE' -and $_.Length -gt 0 })
    $notices = @($sourceNotices | Where-Object { $_.Name -match '^(NOTICE|THIRD-PARTY-NOTICES)' -and $_.Length -gt 0 })
    if ($licenses.Count -eq 0 -or ($component.RequireNotice -and $notices.Count -eq 0)) {
        throw "Required license/notice text is missing from $nugetPackageName/$nugetVersion."
    }
    $componentNoticeDirectory = Join-Path (Join-Path $noticeDirectory $nugetPackageName) $nugetVersion
    [IO.Directory]::CreateDirectory($componentNoticeDirectory) | Out-Null
    $noticeIndex.Add("Package: $nugetPackageName/$nugetVersion")
    $noticeIndex.Add("Published dependency: $($libraryMatches[0].Name)")
    foreach ($sourceNotice in $sourceNotices) {
        [IO.File]::Copy($sourceNotice.FullName, (Join-Path $componentNoticeDirectory $sourceNotice.Name), $false)
        $noticeIndex.Add("File: $nugetPackageName/$nugetVersion/$($sourceNotice.Name)")
    }
    $noticeIndex.Add('')
}
# SQLite packages declare SPDX license expressions in their nuspecs but do not
# ship license text. Bind the vendored upstream texts to the actual dependency
# versions and retain each package's unchanged copyright/license declaration.
$sqliteSourceRoot = Join-Path $repositoryRoot 'docs\third-party\sqlite'
$sqliteSources = Get-Content -LiteralPath (Join-Path $sqliteSourceRoot 'sources.json') -Raw | ConvertFrom-Json
foreach ($source in $sqliteSources) {
    $licensePath = Join-Path $sqliteSourceRoot $source.File
    if ((Get-FileHash -LiteralPath $licensePath -Algorithm SHA256).Hash -ne $source.Sha256) { throw 'Vendored SQLite notice hash mismatch.' }
}
$sqliteLibraries = @($dependencies.libraries.PSObject.Properties | Where-Object { $_.Name -match '^(Microsoft\.Data\.Sqlite(?:\.Core)?|SQLitePCLRaw\.[^/]+)/' })
if ($sqliteLibraries.Count -lt 2) { throw 'The published catalog has no complete SQLite dependency graph.' }
foreach ($library in $sqliteLibraries) {
    $parts = $library.Name.Split('/')
    $sqlitePackageName = $parts[0].ToLowerInvariant()
    $packageVersion = $parts[1]
    $isMicrosoft = $sqlitePackageName.StartsWith('microsoft.data.sqlite', [StringComparison]::Ordinal)
    $expectedVersion = if ($isMicrosoft) { '10.0.14' } else { '2.1.13' }
    $expectedLicense = if ($isMicrosoft) { 'MIT' } else { 'Apache-2.0' }
    if ($packageVersion -ne $expectedVersion) { throw 'SQLite package version changed; review and update notice bindings before packaging.' }
    $nuspecCandidates = @($packageFolders | ForEach-Object { Join-Path (Join-Path (Join-Path $_ $sqlitePackageName) $packageVersion) ($sqlitePackageName + '.nuspec') } | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })
    if ($nuspecCandidates.Count -eq 0) { throw "Missing SQLite package declaration for $($library.Name)." }
    [xml]$declaration = Get-Content -LiteralPath $nuspecCandidates[0] -Raw
    if ($declaration.package.metadata.license.InnerText -ne $expectedLicense) { throw 'SQLite package license changed; notice review required.' }
    $targetDirectory = Join-Path (Join-Path $noticeDirectory $sqlitePackageName) $packageVersion
    [IO.Directory]::CreateDirectory($targetDirectory) | Out-Null
    [IO.File]::Copy($nuspecCandidates[0], (Join-Path $targetDirectory ($sqlitePackageName + '.nuspec')), $false)
    $licenseName = if ($isMicrosoft) { 'Microsoft-MIT.txt' } else { 'Apache-2.0.txt' }
    [IO.File]::Copy((Join-Path $sqliteSourceRoot $licenseName), (Join-Path $targetDirectory 'LICENSE.txt'), $false)
    if (!$isMicrosoft) {
        [IO.File]::Copy((Join-Path $sqliteSourceRoot 'SQLitePCLRaw-NOTICE.txt'), (Join-Path $targetDirectory 'NOTICE.txt'), $false)
    }
    if ($sqlitePackageName -eq 'sqlitepclraw.lib.e_sqlite3') {
        [IO.File]::Copy((Join-Path $sqliteSourceRoot 'SQLite-copyright.html'), (Join-Path $targetDirectory 'SQLite-copyright.html'), $false)
    }
    $noticeIndex.Add("Published SQLite dependency: $($library.Name); $expectedLicense; copyright retained in unchanged nuspec.")
}
[IO.File]::Copy((Join-Path $sqliteSourceRoot 'sources.json'), (Join-Path $noticeDirectory 'sqlite-notice-sources.json'), $false)
[IO.File]::WriteAllText((Join-Path $noticeDirectory 'README.txt'), ($noticeIndex -join "`n"), [Text.UTF8Encoding]::new($false))

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
https://github.com/HopeCrushersInc/AINexus
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
    SourceRepository = 'https://github.com/HopeCrushersInc/AINexus'
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
