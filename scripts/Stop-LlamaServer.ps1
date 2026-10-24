[CmdletBinding(SupportsShouldProcess = $true)]
param([Parameter(Mandatory = $true)][string]$ReceiptPath)
$ErrorActionPreference = 'Stop'
$receipt = Get-Content -LiteralPath $ReceiptPath -Raw | ConvertFrom-Json
$ownedProcess = Get-Process -Id ([int]$receipt.ProcessId) -ErrorAction SilentlyContinue
if (!$ownedProcess) { Write-Output 'The recorded server is no longer running.'; return }
$recordedStart = [DateTimeOffset]::Parse($receipt.StartedAtUtc).UtcDateTime
if ($ownedProcess.Path -ine $receipt.Executable -or $ownedProcess.StartTime.ToUniversalTime() -ne $recordedStart) {
    throw 'Process identity no longer matches the receipt; nothing was stopped.'
}
if ([IO.Path]::GetFileName($ownedProcess.Path) -ine 'llama-server.exe') {
    throw 'The receipt does not identify llama-server.exe; nothing was stopped.'
}
if ($PSCmdlet.ShouldProcess("llama-server process $($ownedProcess.Id)", 'Stop recorded local server')) {
    Stop-Process -InputObject $ownedProcess
    Write-Output 'Recorded llama.cpp server stopped. Model files and logs were preserved.'
}
