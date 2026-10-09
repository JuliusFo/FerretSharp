<#
.SYNOPSIS
    Runs the tests with code coverage and writes an HTML report to artifacts/coverage/report – the same measurement as
    the coverage badge in CI (settings: tests/coverage.config).

.DESCRIPTION
    Core and UI tests always run; the integration tests too unless -SkipIntegration is given (without Docker they are
    skipped and cover nothing, so the result is lower than in CI). ReportGenerator is a local dotnet tool
    (dotnet-tools.json), restored on the first run.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools/coverage/Get-Coverage.ps1 -Open

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools/coverage/Get-Coverage.ps1 -SkipIntegration
#>
param(
    # Core and UI tests only (fast, no Docker).
    [switch]$SkipIntegration,
    # Opens the report in the browser.
    [switch]$Open
)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$results = Join-Path $root 'artifacts\coverage'
$settings = Join-Path $root 'tests\coverage.config'

# Results of an earlier run would be merged into the report.
if (Test-Path $results) {
    Get-ChildItem $results -Filter '*.cobertura.xml' | Remove-Item
}

$projects = @('Core', 'UI')
if (-not $SkipIntegration) { $projects += 'Integration' }
foreach ($project in $projects) {
    dotnet test --project (Join-Path $root "tests\FerretSharp.$project.Tests") --coverage --coverage-output-format cobertura `
        --coverage-settings $settings --results-directory $results --coverage-output "$($project.ToLowerInvariant()).cobertura.xml"
    if ($LASTEXITCODE -ne 0) { throw "Tests of FerretSharp.$project.Tests failed." }
}

Push-Location $root
try {
    dotnet tool restore | Out-Null
    dotnet reportgenerator "-reports:$results\*.cobertura.xml" "-targetdir:$results\report" '-reporttypes:Html;TextSummary' `
        '-title:FerretSharp' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'ReportGenerator failed.' }
}
finally {
    Pop-Location
}

Get-Content (Join-Path $results 'report\Summary.txt') | Select-Object -First 12
$index = Join-Path $results 'report\index.html'
Write-Host "Report: $index"
if ($Open) { Start-Process $index }
