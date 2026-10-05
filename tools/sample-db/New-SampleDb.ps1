<#
.SYNOPSIS
    Creates a local Oracle sample database for FerretSharp in Docker (Oracle 23 Free) and fills it with the sample
    schema: all *.sql files in name order (01-schema.sql, 02-data.sql …), as the application user FERRET.

.DESCRIPTION
    The container listens on 127.0.0.1:<Port> only and restarts with Docker (--restart unless-stopped), so the port
    stays the same and a saved connection keeps working. Data lives inside the container: removing it removes the
    data; run the script again to recreate both.

    Scripts are copied into the container and run there with sqlplus (piping them from PowerShell would prepend a
    BOM, SP2-0734).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools/sample-db/New-SampleDb.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools/sample-db/New-SampleDb.ps1 -Force -Password Ferret2026
#>
param(
    [string]$Name = 'ferret-sample',
    [int]$Port = 1522,
    # Password of the application user FERRET; generated if omitted. Letters, digits and _ only (passed to sqlplus).
    [string]$Password,
    # Removes an existing container of that name (and its data) first.
    [switch]$Force
)
$ErrorActionPreference = 'Stop'
$image = 'gvenzl/oracle-free:23-slim-faststart'

function New-Secret([string]$prefix) {
    $prefix + (-join ((1..12) | ForEach-Object { '{0:x}' -f (Get-Random -Maximum 16) }))
}

if (-not $Password) { $Password = New-Secret 'Fs' }
if ($Password -notmatch '^[A-Za-z][A-Za-z0-9_]{7,29}$') {
    throw 'The password must start with a letter and contain 8-30 letters, digits or _.'
}

docker version --format '{{.Server.Version}}' *> $null
if ($LASTEXITCODE -ne 0) { throw 'Docker is not running.' }

if (docker ps -a --filter "name=^$Name$" --format '{{.Names}}') {
    if (-not $Force) { throw "Container '$Name' already exists. Use -Force to recreate it (its data is lost)." }
    docker rm -f $Name | Out-Null
}

Write-Host "Starting $Name on 127.0.0.1:$Port ..."
docker run -d --name $Name --restart unless-stopped -p "127.0.0.1:${Port}:1521" `
    -e "ORACLE_PASSWORD=$(New-Secret 'Sys')" -e APP_USER=ferret -e "APP_USER_PASSWORD=$Password" $image | Out-Null
if ($LASTEXITCODE -ne 0) { throw "docker run failed (port $Port in use?)." }

$deadline = (Get-Date).AddMinutes(5)
while (-not ((docker logs $Name 2>&1) -match 'DATABASE IS READY TO USE')) {
    if ((Get-Date) -gt $deadline) { throw "The database is not ready after 5 minutes (docker logs $Name)." }
    Start-Sleep -Seconds 3
}

foreach ($script in Get-ChildItem $PSScriptRoot -Filter '*.sql' | Sort-Object Name) {
    Write-Host "== $($script.Name)"
    docker cp $script.FullName "${Name}:/tmp/$($script.Name)" | Out-Null
    docker exec $Name bash -c "NLS_LANG=GERMAN_GERMANY.AL32UTF8 sqlplus -s ferret/$Password@FREEPDB1 @/tmp/$($script.Name)" |
        Where-Object { $_ -ne '' }
    if ($LASTEXITCODE -ne 0) { throw "$($script.Name) failed." }
}

Write-Host ''
Write-Host 'Done. Connection for FerretSharp:'
Write-Host "  Host 127.0.0.1, port $Port, service FREEPDB1, user FERRET, password $Password"
