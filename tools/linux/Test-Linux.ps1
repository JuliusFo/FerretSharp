<#
.SYNOPSIS
    Builds and runs the Core and UI unit tests under Linux in Docker, like the first job of the CI.

.DESCRIPTION
    The CI builds and tests on ubuntu-latest first, locally only Windows runs: path separators, case-sensitive file
    names, FileShare locks or test order dependencies only show up after the push (.claude/rules/tests.md).

    This copies the working tree - tracked files and untracked ones that are not ignored, so uncommitted changes
    count - into a container with the .NET 10 SDK and the .NET 8 runtimes, builds Core, UI and AppTrace with
    -warnaserror and runs the tests there. Nothing is written into the repository: bin and obj stay in the
    container, which is removed afterwards. NuGet packages are kept in the Docker volume ferretsharp-linux-nuget.

    Not part of it: the integration tests (Oracle in Docker) and the JavaScript tests (no Node in the image).
    The CI runs both.

.PARAMETER Project
    Test projects to run, relative to the repository root. Default: the Core and UI tests.

.EXAMPLE
    pwsh -NoProfile -File tools/linux/Test-Linux.ps1

.EXAMPLE
    pwsh -NoProfile -File tools/linux/Test-Linux.ps1 -Project tests/FerretSharp.UI.Tests
#>
param(
    [string[]]$Project = @('tests/FerretSharp.Core.Tests', 'tests/FerretSharp.UI.Tests')
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$image = 'ferretsharp-linux-test'

docker info --format '{{.ServerVersion}}' *> $null
if ($LASTEXITCODE -ne 0) { throw 'Docker is not running.' }

docker build --quiet --tag $image $PSScriptRoot | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Building the image $image failed." }

# git writes file names as UTF-8; a tracked file deleted in the working tree is left out.
$encoding = [Console]::OutputEncoding
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
try {
    $names = git -C $root ls-files -z --cached --others --exclude-standard
    if ($LASTEXITCODE -ne 0) { throw 'git ls-files failed.' }
} finally {
    [Console]::OutputEncoding = $encoding
}
$files = ($names -join '') -split "`0" | Where-Object { $_ -and (Test-Path -LiteralPath (Join-Path $root $_) -PathType Leaf) }

$list = [IO.Path]::GetTempFileName()
try {
    [IO.File]::WriteAllText($list, ($files -join "`0"), [Text.UTF8Encoding]::new($false))
    docker run --rm `
        --volume "${root}:/src:ro" `
        --volume "${list}:/list:ro" `
        --volume 'ferretsharp-linux-nuget:/root/.nuget/packages' `
        $image bash /src/tools/linux/run-tests.sh @Project
    $code = $LASTEXITCODE
} finally {
    Remove-Item -LiteralPath $list -ErrorAction SilentlyContinue
}
exit $code
