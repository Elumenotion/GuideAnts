[CmdletBinding()]
param(
    [Parameter()]
    [string]$OwnerRepo = "Elumenotion/GuideAnts",

    [Parameter()]
    [ValidateSet("open", "closed", "dismissed", "fixed")]
    [string]$State = "open",

    [Parameter()]
    [string]$OutputPath = "scan-results.txt",

    [Parameter()]
    [string]$Ref = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-GitHubTokenFromCredentialManager {
    $inputText = "protocol=https`nhost=github.com`n`n"
    $filled = $inputText | git credential fill 2>$null
    if (-not $filled) {
        throw "git credential fill returned no data. Sign in to GitHub (Git Credential Manager) or set GITHUB_TOKEN."
    }

    foreach ($line in $filled) {
        if ($line -match '^password=(.+)$') {
            return $Matches[1]
        }
    }

    throw "No password/token in git credential fill output for github.com."
}

function Get-GitHubHeaders {
    param([string]$Token)

    return @{
        Authorization          = "Bearer $Token"
        Accept                 = "application/vnd.github+json"
        "X-GitHub-Api-Version" = "2022-11-28"
        "User-Agent"           = "quality-alerts-fetch-code-scanning"
    }
}

$token = $env:GITHUB_TOKEN
if ([string]::IsNullOrWhiteSpace($token)) {
    $token = Get-GitHubTokenFromCredentialManager
}

$headers = Get-GitHubHeaders -Token $token
$baseUri = "https://api.github.com/repos/$OwnerRepo/code-scanning/alerts"
$all = New-Object System.Collections.Generic.List[object]
$page = 1

do {
    $query = "state=$State&per_page=100&page=$page"
    if (-not [string]::IsNullOrWhiteSpace($Ref)) {
        $query += "&ref=$([uri]::EscapeDataString($Ref))"
    }

    $batch = Invoke-RestMethod -Uri "$baseUri`?$query" -Headers $headers -Method Get
    if ($null -eq $batch -or $batch.Count -eq 0) {
        break
    }

    foreach ($item in $batch) {
        [void]$all.Add($item)
    }

    $page++
} while ($batch.Count -eq 100)

$payload = [ordered]@{
    repository = $OwnerRepo
    fetched_at = (Get-Date).ToString("o")
    endpoint   = $baseUri
    state      = $State
    ref        = $(if ([string]::IsNullOrWhiteSpace($Ref)) { $null } else { $Ref })
    count      = $all.Count
    alerts     = $all
}

$resolvedOutput = if ([System.IO.Path]::IsPathRooted($OutputPath)) {
    $OutputPath
} else {
    Join-Path (Get-Location) $OutputPath
}

$json = $payload | ConvertTo-Json -Depth 30
Set-Content -LiteralPath $resolvedOutput -Value $json -Encoding utf8

Write-Host "Fetched $($all.Count) $State alert(s) for $OwnerRepo -> $resolvedOutput"
$all | Group-Object { $_.rule.id } | Sort-Object Count -Descending |
    Select-Object Count, Name | Format-Table -AutoSize
