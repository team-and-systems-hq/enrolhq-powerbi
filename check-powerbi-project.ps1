<#
.SYNOPSIS
    Checks the generated Power BI project with Microsoft's own Power BI tool.

.DESCRIPTION
    Loads the project's model definition the way Power BI does, without
    opening Power BI Desktop, and reports the tables, relationships and
    measures it finds or the error it hit. Reads definitions only, not data.

    With -Desktop it checks the model that is open in Power BI Desktop
    instead, and counts the rows loaded in each table.

.PARAMETER Project
    The folder holding EnrolHQ.pbip. Default: the newest one under data\.

.PARAMETER Desktop
    Check the model open in Power BI Desktop.

.PARAMETER Refresh
    With -Desktop: load the data before counting rows.

.PARAMETER Query
    With -Desktop: run this DAX query and return its rows, in place of the checks.
#>
param(
    [string]$Project,
    [switch]$Desktop,
    [switch]$Refresh,
    [string]$Query
)

$ErrorActionPreference = 'Stop'
$root   = $PSScriptRoot
$server = Join-Path $root 'tools\powerbi-mcp\package\extension\server\powerbi-modeling-mcp.exe'
if (-not (Test-Path $server)) { throw "Microsoft's Power BI tool is not at $server." }

if (-not $Project) {
    $found = Get-ChildItem (Join-Path $root 'data') -Recurse -Filter 'EnrolHQ.pbip' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $found) { throw 'No Power BI project found under data\. Run enrolhq-sync first.' }
    $Project = $found.DirectoryName
}

$start = New-Object System.Diagnostics.ProcessStartInfo
$start.FileName = $server
$start.Arguments = $(if ($Refresh) { '--readwrite' } else { '--readonly' }) + ' --accept-eula'
$start.UseShellExecute = $false
$start.RedirectStandardInput = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$start.CreateNoWindow = $true
$process = [System.Diagnostics.Process]::Start($start)
$script:nextId = 0

function Send($message) {
    $process.StandardInput.WriteLine(($message | ConvertTo-Json -Depth 20 -Compress))
    $process.StandardInput.Flush()
}

function Receive([int]$id, [int]$seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        $task = $process.StandardOutput.ReadLineAsync()
        if (-not $task.Wait([int][Math]::Max(1, ($deadline - (Get-Date)).TotalMilliseconds))) { break }
        if ($null -eq $task.Result) { break }
        $message = $task.Result | ConvertFrom-Json
        if ($message.id -eq $id) { return $message }
    }
    throw "Microsoft's Power BI tool did not answer within $seconds seconds."
}

# Calls one tool and returns what it answered, parsed from JSON where it is JSON.
function Invoke-Tool([string]$name, [hashtable]$request, [int]$seconds = 120) {
    $id = ++$script:nextId
    Send @{ jsonrpc = '2.0'; id = $id; method = 'tools/call'; params = @{ name = $name; arguments = @{ request = $request } } }
    $answer = Receive $id $seconds
    if ($answer.error) { throw "$name $($request.operation): $($answer.error.message)" }
    $text = ($answer.result.content | Where-Object { $_.type -eq 'text' } | ForEach-Object { $_.text }) -join "`n"
    $parsed = try { $text | ConvertFrom-Json } catch { $text }
    if ($answer.result.isError -or ($parsed.PSObject.Properties.Name -contains 'success' -and -not $parsed.success)) {
        throw "$name $($request.operation) failed: $text"
    }
    return $parsed
}

try {
    $id = ++$script:nextId
    Send @{ jsonrpc = '2.0'; id = $id; method = 'initialize'; params = @{ protocolVersion = '2024-11-05'; capabilities = @{}; clientInfo = @{ name = 'check-powerbi-project'; version = '1.0' } } }
    $null = Receive $id 30
    Send @{ jsonrpc = '2.0'; method = 'notifications/initialized' }

    if ($Desktop) {
        $instances = Invoke-Tool 'connection_operations' @{ operation = 'ListLocalInstances' }
        $instances | ConvertTo-Json -Depth 6 | Write-Verbose
        $local = @($instances.data) | Select-Object -First 1
        if (-not $local) { throw 'Power BI Desktop is not open with a file.' }
        $connect = @{ operation = 'Connect'; dataSource = "localhost:$($local.port)" }
        $null = Invoke-Tool 'connection_operations' $connect
        Write-Output "Connected to Power BI Desktop: $($local.parentWindowTitle)"
        if ($Refresh) {
            Write-Output 'Loading data...'
            $null = Invoke-Tool 'model_operations' @{ operation = 'RefreshWithXMLA'; refreshType = 'Full' } 1800
        }
    }
    else {
        $null = Invoke-Tool 'connection_operations' @{ operation = 'ConnectFolder'; folderPath = (Join-Path $Project 'EnrolHQ.SemanticModel') }
        Write-Output "Loaded the model definition in $Project"
    }

    if ($Query) {
        if (-not $Desktop) { throw '-Query needs -Desktop: only a model open in Power BI Desktop holds data.' }
        $answer = Invoke-Tool 'dax_query_operations' @{ operation = 'Execute'; query = $Query; resultMode = 'Inline' } 300
        if ($answer.data.rows) { return $answer.data.rows }
        return $answer.data
    }

    $tables = @((Invoke-Tool 'table_operations' @{ operation = 'List' }).data)
    $relationships = @((Invoke-Tool 'relationship_operations' @{ operation = 'List' }).data)
    # Measures come back grouped by table, and cut off at 200 unless maxResults says otherwise.
    $answer = Invoke-Tool 'measure_operations' @{ operation = 'List'; filter = @{ maxResults = [int]::MaxValue } }
    $truncated = @($answer.warnings) -like '*truncated*'
    if ($truncated) { throw "measure_operations List: $($truncated -join ' ')" }
    $measures = @($answer.data | ForEach-Object {
        $table = $_.tableName
        $_.measures | Select-Object @{ Name = 'tableName'; Expression = { $table } }, *
    })
    Write-Output "Tables: $($tables.Count)   Relationships: $($relationships.Count)   Measures: $($measures.Count)"

    $rows = $null
    if ($Desktop) {
        # Counts only. No record data is read.
        $counts = ($tables | ForEach-Object { """$($_.name)"", COUNTROWS('$($_.name)')" }) -join ', '
        $answer = Invoke-Tool 'dax_query_operations' @{ operation = 'Execute'; query = "EVALUATE ROW($counts)"; resultMode = 'Inline' } 300
        $first = @($answer.data.rows)[0]
        if (-not $first) { $first = @($answer.data)[0] }
        $rows = foreach ($property in $first.PSObject.Properties) {
            [pscustomobject]@{ Table = $property.Name.Trim('[', ']'); Rows = $property.Value }
        }
        $loaded = @($rows | Where-Object { $_.Rows -gt 0 }).Count
        Write-Output "Tables holding data: $loaded of $($tables.Count)"
    }

    [pscustomobject]@{ Tables = $tables; Relationships = $relationships; Measures = $measures; Rows = $rows }
}
finally {
    if (-not $process.HasExited) { $process.Kill() }
}
