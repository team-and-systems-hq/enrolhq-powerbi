<#
.SYNOPSIS
    Builds bin\EnrolHQ.mez from the connector folder and checks that it loads.

.PARAMETER Install
    Also copy the connector into this user's Power BI Desktop custom connectors folder.
#>
param([switch]$Install)

$ErrorActionPreference = 'Stop'
$root   = $PSScriptRoot
$source = Join-Path $root 'connector'
$bin    = Join-Path $root 'bin'
$mez    = Join-Path $bin 'EnrolHQ.mez'
$pqtest = Join-Path $root 'tools\sdktools\tools\PQTest.exe'

New-Item -ItemType Directory -Force $bin | Out-Null
if (Test-Path $mez) { Remove-Item $mez -Confirm:$false }

# A .mez is a zip of the connector source and its resources.
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($source, $mez)
Write-Output "Built $mez"

if (Test-Path $pqtest) {
    $info = & $pqtest info -e $mez | ConvertFrom-Json
    if ($info.ErrorStatus) { throw "Connector failed to load: $($info.ErrorStatus)" }
    Write-Output "Loaded $($info.Name) $($info.Version)"
}
else {
    Write-Warning 'PQTest not found under tools\sdktools; skipped the load check.'
}

if ($Install) {
    $target = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Power BI Desktop\Custom Connectors'
    New-Item -ItemType Directory -Force $target | Out-Null
    Copy-Item $mez $target -Force
    Write-Output "Installed to $target"
}
