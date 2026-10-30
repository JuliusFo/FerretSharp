<#
.SYNOPSIS
    Closes a test instance started by Start-TestInstance.ps1 and counts warnings and errors in its log.

.DESCRIPTION
    Only closes the process if its executable lies below <Root>\app - never an instance of the user or of another
    session. Closes the window first (the app saves its workspace state), kills the process after 10 s.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools/e2e/Stop-TestInstance.ps1 -Root <scratch>\e2e -ProcessId 12345
#>
param(
    [Parameter(Mandatory)][string]$Root,
    [Parameter(Mandatory)][int]$ProcessId
)
$ErrorActionPreference = 'Stop'
$Root = [IO.Path]::GetFullPath($Root)
$app = (Join-Path $Root 'app').TrimEnd('\') + '\'

$process = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
if ($process) {
    if (-not $process.Path -or -not $process.Path.StartsWith($app, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Process $ProcessId ($($process.Path)) is not a test instance below $app - left running."
    }
    $process.CloseMainWindow() | Out-Null
    if (-not $process.WaitForExit(10000)) { Stop-Process -Id $ProcessId }
    "Closed $ProcessId."
} else {
    "Process $ProcessId is not running."
}

Get-ChildItem (Join-Path $Root 'data\logs') -Filter '*.log' -ErrorAction SilentlyContinue | ForEach-Object {
    $hits = @(Select-String -Path $_.FullName -Pattern '\[(ERR|WRN)\]')
    "$($_.Name): $($hits.Count) warnings/errors"
    $hits | Select-Object -First 10 | ForEach-Object { '  ' + $_.Line }
}
