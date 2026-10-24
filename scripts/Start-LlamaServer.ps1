[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$RuntimeDirectory,
    [Parameter(Mandatory = $true)][string]$ModelPath,
    [ValidateRange(1024, 65535)][int]$Port = 8080,
    [ValidateRange(512, 32768)][int]$ContextSize = 4096,
    [ValidateSet('auto', 'all', '0')][string]$GpuLayers = 'auto',
    [string]$LogDirectory = (Join-Path $env:LOCALAPPDATA 'AINexus\Logs')
)
$ErrorActionPreference = 'Stop'
$executable = (Resolve-Path -LiteralPath (Join-Path $RuntimeDirectory 'llama-server.exe')).ProviderPath
$model = (Resolve-Path -LiteralPath $ModelPath).ProviderPath
if ([IO.Path]::GetExtension($model) -ine '.gguf') { throw 'Select a GGUF model file.' }
if ([Environment]::GetEnvironmentVariable('LLAMA_ARG_TOOLS')) {
    throw 'Inherited LLAMA_ARG_TOOLS must be unset before starting this tool-free server.'
}
if ([Environment]::GetEnvironmentVariable('LLAMA_ARG_API_KEY')) {
    throw 'An inherited API key is configured. This local setup does not store or transmit credentials.'
}
$probe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $Port)
try { $probe.Start() } finally { $probe.Stop() }
New-Item -ItemType Directory -Path $LogDirectory -Force | Out-Null
$logRoot = (Resolve-Path -LiteralPath $LogDirectory).ProviderPath
$runId = [guid]::NewGuid().ToString('N')
$stdout = Join-Path $logRoot "llama-$runId.stdout.log"
$stderr = Join-Path $logRoot "llama-$runId.stderr.log"
# Paths are resolved files, so they cannot contain embedded quotes or end in a slash.
# Start-Process invokes the executable directly; no command shell interprets these arguments.
$arguments = @('--model', ('"' + $model + '"'), '--host', '127.0.0.1', '--port', "$Port",
    '--ctx-size', "$ContextSize", '--parallel', '1', '--n-gpu-layers', $GpuLayers, '--no-ui-mcp-proxy',
    '--cors-origins', "http://127.0.0.1:$Port")
$process = Start-Process -FilePath $executable -ArgumentList $arguments -WorkingDirectory ([IO.Path]::GetDirectoryName($executable)) `
    -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
$receiptPath = Join-Path $logRoot "llama-$runId.process.json"
$receipt = [ordered]@{
    ProcessId = $process.Id
    StartedAtUtc = $process.StartTime.ToUniversalTime().ToString('o')
    Executable = $executable
    Model = $model
    Endpoint = "http://127.0.0.1:$Port"
    ContextSize = $ContextSize
    GpuLayers = $GpuLayers
    StdoutLog = $stdout
    StderrLog = $stderr
}
$receipt | ConvertTo-Json | Set-Content -LiteralPath $receiptPath -Encoding UTF8
$ready = $false
for ($attempt = 0; $attempt -lt 60; $attempt++) {
    $process.Refresh()
    if ($process.HasExited) { throw "llama-server exited with code $($process.ExitCode). Inspect $stderr" }
    try {
        $health = Invoke-RestMethod -Uri ($receipt.Endpoint + '/health') -TimeoutSec 1
        if ($health.status -eq 'ok') { $ready = $true; break }
    } catch { }
    Start-Sleep -Milliseconds 500
}
if (!$ready) { throw "The server is still starting or unhealthy. Inspect $stderr. Process receipt: $receiptPath" }
Write-Output "Local llama.cpp server ready at $($receipt.Endpoint)"
Write-Output "Process receipt: $receiptPath"
