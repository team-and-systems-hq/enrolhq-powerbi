<#
.SYNOPSIS
    Runs the anonymisation rule checks in tests\unit against synthetic data.
    Needs no API token and makes no network requests.
#>
$ErrorActionPreference = 'Stop'
$root   = $PSScriptRoot
$mez    = Join-Path $root 'bin\EnrolHQ.mez'
$pqtest = Join-Path $root 'tools\sdktools\tools\PQTest.exe'

if (-not (Test-Path $mez)) { throw "No connector built. Run .\build.ps1 first." }

$failed = 0
foreach ($query in Get-ChildItem (Join-Path $root 'tests\unit') -Filter '*.query.pq') {
    $result = (& $pqtest run-test -e $mez -q $query.FullName | ConvertFrom-Json)[0]
    if ($result.Status -ne 'Passed') {
        Write-Output "ERROR $($query.BaseName): $($result.Error.Message)"
        if ($result.Error.Details) { $result.Error.Details | ConvertTo-Json -Depth 4 }
        $failed++
        continue
    }
    foreach ($check in $result.Output) {
        if ($check.Passed -eq $true) { Write-Output "PASS  $($check.Check)" }
        else { Write-Output "FAIL  $($check.Check)"; $failed++ }
    }
}

if ($failed -gt 0) { throw "$failed masking check(s) failed." }
Write-Output 'All masking checks passed.'
