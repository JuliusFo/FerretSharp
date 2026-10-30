<#
.SYNOPSIS
    Builds FerretSharp into a scratch folder and starts a test instance on its own data and WebView2 folders with a
    debug port for CDP (tools/e2e/cdp.mjs).

.DESCRIPTION
    Everything lives below -Root: app\ (build output), data\ (--data-dir, log in data\logs) and webview2\. The user's
    data, WebView2 folder and a running instance are not touched; the build does not go to bin\Debug, which a running
    instance of the user would lock.

    Refuses a debug port that is already taken (another session's instance would get every CDP call) and checks
    afterwards that the port belongs to the new instance. The window is placed at 100,100 with 1440x900 on the
    primary screen without taking the focus, so clicks never land on another screen.

    Prints the process id; close the instance with Stop-TestInstance.ps1, never all FerretSharp processes.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools/e2e/Start-TestInstance.ps1 -Root <scratch>\e2e -Lang de

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools/e2e/Start-TestInstance.ps1 -Root <scratch>\e2e -Port 9334 -NoBuild
#>
param(
    # Scratch folder for build, data and WebView2 (for example in the session's scratchpad).
    [Parameter(Mandatory)][string]$Root,
    [int]$Port = 9333,
    [ValidateSet('en', 'de')][string]$Lang = 'en',
    [ValidateSet('dark', 'light')][string]$Theme = 'dark',
    # Starts the build already in <Root>\app.
    [switch]$NoBuild,
    # More arguments for the app, for example a ferretsharp:// link or --import-appdata=<scratch>.
    [string[]]$AppArgs = @()
)
$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$Root = [IO.Path]::GetFullPath($Root)
$app = Join-Path $Root 'app'
$data = Join-Path $Root 'data'
$webView = Join-Path $Root 'webview2'
New-Item -ItemType Directory -Force $app, $data, $webView | Out-Null

if (Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue) {
    throw "Port $Port is taken (another test instance?). Use -Port with a free port."
}

if (-not $NoBuild) {
    dotnet build (Join-Path $repo 'src\FerretSharp.App') -o $app -v q -nologo
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
}

$saved = @{
    WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = $env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS
    WEBVIEW2_USER_DATA_FOLDER = $env:WEBVIEW2_USER_DATA_FOLDER
}
try {
    $env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = "--remote-debugging-port=$Port"
    $env:WEBVIEW2_USER_DATA_FOLDER = $webView
    $arguments = @("--data-dir=`"$data`"", "--lang=$Lang", "--theme=$Theme") + $AppArgs
    $process = Start-Process (Join-Path $app 'FerretSharp.exe') -ArgumentList $arguments -PassThru
} finally {
    foreach ($name in $saved.Keys) { Set-Item "env:$name" -Value $saved[$name] }
}

$ready = $false
for ($i = 0; $i -lt 60 -and -not $ready; $i++) {
    Start-Sleep -Milliseconds 500
    if ($process.HasExited) { throw "The instance exited with code $($process.ExitCode); see $data\logs." }
    try {
        $targets = Invoke-RestMethod "http://127.0.0.1:$Port/json" -TimeoutSec 2
        $ready = [bool]($targets | Where-Object url -eq 'https://0.0.0.1/')
    } catch { }
}
if (-not $ready) { throw "No FerretSharp page on port $Port after 30 s." }

$owner = (Get-NetTCPConnection -State Listen -LocalPort $Port).OwningProcess | Select-Object -First 1
$parent = (Get-CimInstance Win32_Process -Filter "ProcessId=$owner").ParentProcessId
if ($parent -ne $process.Id) {
    Stop-Process -Id $process.Id
    throw "Port $Port belongs to process $owner, not to the new instance - stopped it."
}

Add-Type -Namespace E2E -Name Window -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);'
$process.Refresh()
for ($i = 0; $i -lt 20 -and $process.MainWindowHandle -eq [IntPtr]::Zero; $i++) { Start-Sleep -Milliseconds 250; $process.Refresh() }
[E2E.Window]::SetWindowPos($process.MainWindowHandle, [IntPtr]::Zero, 100, 100, 1440, 900, 0x0014) | Out-Null # NOZORDER | NOACTIVATE

[pscustomobject]@{ ProcessId = $process.Id; Port = $Port; DataDir = $data; Log = Join-Path $data 'logs' }
