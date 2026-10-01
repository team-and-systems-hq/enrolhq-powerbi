<#
.SYNOPSIS
    Gets an EnrolHQ access token for Power BI and copies it to the clipboard.

.DESCRIPTION
    Exchanges the long-lived API token in .env for an access token. Access
    tokens last one hour.

    EnrolHQ allows 5 exchanges a minute, and each exchange cancels the access
    token issued before it. So this script keeps the token it was given
    (encrypted for this Windows user, in bin\) and hands the same one back
    until it is 50 minutes old or EnrolHQ stops accepting it.

    The token is never written to the console.

.PARAMETER PassThru
    Return the token to the calling script instead of copying it to the clipboard.

.PARAMETER Force
    Exchange for a new token even if the saved one still works. This cancels
    the saved one, including anywhere it is already in use.
#>
param(
    [switch]$PassThru,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$root    = $PSScriptRoot
$envFile = Join-Path $root '.env'
$cache   = Join-Path $root 'bin\access-token.xml'
$maxAge  = [TimeSpan]::FromMinutes(50)

if (-not (Test-Path $envFile)) { throw "No .env found. Copy .env.example to .env and fill it in." }
$settings = @{}
foreach ($line in Get-Content $envFile) {
    $line = $line.Trim()
    if (-not $line -or $line.StartsWith('#') -or -not $line.Contains('=')) { continue }
    $name, $value = $line.Split('=', 2)
    $settings[$name.Trim()] = $value.Trim().Trim('"', "'")
}
$instance = $settings['ENROLHQ_INSTANCE']
$apiToken = $settings['ENROLHQ_API_TOKEN']
if (-not $instance) { throw 'ENROLHQ_INSTANCE is missing from .env' }
if (-not $apiToken) { throw 'ENROLHQ_API_TOKEN is missing from .env' }
$baseUrl = "https://$instance/api/v2"

function ConvertTo-PlainText([securestring]$Secure) {
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Secure)
    try { [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
}

function Get-StatusCode($ErrorRecord) {
    if ($ErrorRecord.Exception.Response) { [int]$ErrorRecord.Exception.Response.StatusCode } else { $null }
}

function Test-AccessToken([string]$Token) {
    try {
        Invoke-RestMethod -Uri "$baseUrl/attendance-types/?page_size=1" -Headers @{ Authorization = "Token $Token" } | Out-Null
        $true
    }
    catch {
        if ((Get-StatusCode $_) -in 401, 403) { $false } else { throw }
    }
}

$token = $null
$issuedAt = $null
if (-not $Force -and (Test-Path $cache)) {
    $saved = Import-Clixml $cache
    if ($saved.Instance -eq $instance -and ((Get-Date) - $saved.IssuedAt) -lt $maxAge) {
        $candidate = ConvertTo-PlainText $saved.Token
        if (Test-AccessToken $candidate) {
            $token = $candidate
            $issuedAt = $saved.IssuedAt
        }
    }
}

if (-not $token) {
    $response = $null
    foreach ($attempt in 1..5) {
        try {
            $response = Invoke-RestMethod -Method Post -Uri "$baseUrl/accounts/refresh/" -Headers @{ Authorization = "Token $apiToken" }
            break
        }
        catch {
            $status = Get-StatusCode $_
            if ($status -eq 429 -and $attempt -lt 5) {
                Write-Host 'EnrolHQ allows 5 sign-ins a minute. Waiting 15 seconds...'
                Start-Sleep -Seconds 15
            }
            elseif ($status -in 401, 403) { throw 'EnrolHQ did not accept the API token in .env.' }
            else { throw }
        }
    }
    if (-not $response.access_token) { throw 'EnrolHQ did not return an access token.' }
    $token = $response.access_token
    $issuedAt = Get-Date
    New-Item -ItemType Directory -Force (Split-Path $cache) | Out-Null
    [pscustomobject]@{
        Instance = $instance
        IssuedAt = $issuedAt
        Token    = ConvertTo-SecureString $token -AsPlainText -Force
    } | Export-Clixml $cache
}

if ($PassThru) { return $token }

Set-Clipboard -Value $token
$expires = $issuedAt.AddHours(1)
Write-Host "Access token for $instance copied to the clipboard."
Write-Host ("It works until about {0:HH:mm}. Paste it into Power BI as the EnrolHQ credential." -f $expires)
