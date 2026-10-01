<#
.SYNOPSIS
    Runs the connector against a live EnrolHQ instance using the token in .env.

.DESCRIPTION
    Gets an access token with get-access-token.ps1, stores it in the PQTest
    credential store, runs each query in tests\, and prints a summary.
    The token is never written to the console. Row data is only printed with -ShowRows.

.PARAMETER Filter
    Only run test queries whose file name matches this wildcard.

.PARAMETER ShowRows
    Print the rows each query returned. These contain student data.
#>
param(
    [string]$Filter = '*',
    [switch]$ShowRows
)

$ErrorActionPreference = 'Stop'
$root   = $PSScriptRoot
$mez    = Join-Path $root 'bin\EnrolHQ.mez'
$pqtest = Join-Path $root 'tools\sdktools\tools\PQTest.exe'
$envFile = Join-Path $root '.env'

if (-not (Test-Path $envFile)) { throw "No .env found. Copy .env.example to .env and fill it in." }
if (-not (Test-Path $mez))     { throw "No connector built. Run .\build.ps1 first." }

$settings = @{}
foreach ($line in Get-Content $envFile) {
    $line = $line.Trim()
    if (-not $line -or $line.StartsWith('#') -or -not $line.Contains('=')) { continue }
    $name, $value = $line.Split('=', 2)
    $settings[$name.Trim()] = $value.Trim().Trim('"', "'")
}

$instance = $settings['ENROLHQ_INSTANCE']
if (-not $instance) { throw 'ENROLHQ_INSTANCE is missing from .env' }
# Tests share one access token with Power BI. Exchanging the API token here
# would cancel the token Power BI is using.
$token = & (Join-Path $root 'get-access-token.ps1') -PassThru

# Test queries use {{instance}} so the same files work for any school.
$work = Join-Path $root 'bin\tests'
if (Test-Path $work) { Remove-Item $work -Recurse -Confirm:$false }
New-Item -ItemType Directory -Force $work | Out-Null
$queries = foreach ($file in Get-ChildItem (Join-Path $root 'tests') -Filter "$Filter.query.pq") {
    $target = Join-Path $work $file.Name
    (Get-Content $file.FullName -Raw).Replace('{{instance}}', $instance) | Set-Content $target -Encoding utf8
    Get-Item $target
}
if (-not $queries) { throw "No test queries match '$Filter'." }

$credential = @{
    AuthenticationKind       = 'Key'
    AuthenticationProperties = @{ Key = $token }
    PrivacySetting           = 'None'
    Permissions              = @()
} | ConvertTo-Json -Compress
$set = $credential | & $pqtest set-credential -e $mez -q $queries[0].FullName | ConvertFrom-Json
if ($set.Status -ne 'Success') { throw "Could not store the credential: $($set.Message)" }

$failed = 0
foreach ($query in $queries) {
    $started = Get-Date
    $result = (& $pqtest run-test -e $mez -q $query.FullName | ConvertFrom-Json)[0]
    $seconds = [math]::Round(((Get-Date) - $started).TotalSeconds, 1)
    if ($result.Status -eq 'Passed') {
        Write-Output ("PASS  {0,-32} {1,7} rows  {2,6}s" -f $query.BaseName, $result.RowCount, $seconds)
        if ($ShowRows) { $result.Output | ConvertTo-Json -Depth 8 }
    }
    else {
        $failed++
        Write-Output ("FAIL  {0,-32} {1}" -f $query.BaseName, $result.Error.Message)
        # Error details can quote values from the rows, so they follow -ShowRows.
        if ($ShowRows -and $result.Error.Details) { $result.Error.Details | ConvertTo-Json -Depth 4 }
    }
}

if ($failed -gt 0) { throw "$failed test quer$(if ($failed -eq 1) { 'y' } else { 'ies' }) failed." }
