<#
.SYNOPSIS
    Regenerates the file the sync tool's parity test compares against.

.DESCRIPTION
    The Power BI connector and the sync tool must mask identically. This runs
    the fixture in sync\EnrolHQ.Sync.Tests\Parity\fixture.json through the
    connector's masking and saves the result as expected-from-connector.json.

    Run it after changing a masking rule in connector\EnrolHQ.pq, make the same
    change in sync\EnrolHQ.Sync\Anonymise, then run the tests. The fixture
    holds made-up data only. Needs no token and makes no network requests.
#>
$ErrorActionPreference = 'Stop'
$root   = $PSScriptRoot
$parity = Join-Path $root 'sync\EnrolHQ.Sync.Tests\Parity'
$work   = Join-Path $root 'bin\parity'
$pqtest = Join-Path $root 'tools\sdktools\tools\PQTest.exe'

& (Join-Path $root 'build.ps1') | Out-Null

# The fixture goes into the query as a text literal; M doubles quotes to escape them.
$fixture = (Get-Content (Join-Path $parity 'fixture.json') -Raw).Replace('"', '""')
$query = @"
let
    fixture = Json.Document("$fixture"),
    tables = Record.FieldNames(fixture),
    masked = Record.FromList(List.Transform(tables, (table) => EnrolHQ.Anonymise(Record.Field(fixture, table), table)), tables)
in
    #table({"json"}, {{Text.FromBinary(Json.FromValue(masked))}})
"@
New-Item -ItemType Directory -Force $work | Out-Null
$queryFile = Join-Path $work 'Parity.query.pq'
Set-Content $queryFile -Value $query -Encoding utf8

$result = (& $pqtest run-test -e (Join-Path $root 'bin\EnrolHQ.mez') -q $queryFile | ConvertFrom-Json)[0]
if ($result.Status -ne 'Passed') { throw "The connector could not mask the fixture: $($result.Error.Message)" }

$target = Join-Path $parity 'expected-from-connector.json'
[IO.File]::WriteAllText($target, $result.Output[0].json, (New-Object Text.UTF8Encoding($false)))
Write-Output "Wrote $target"
