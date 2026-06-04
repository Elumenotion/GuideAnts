[CmdletBinding()]
param(
    [Parameter()]
    [string]$GitHubAlertsPath = "scan-results.txt",

    [Parameter()]
    [string]$SarifPath = ".codeql/results-csharp.sarif",

    [Parameter()]
    [int]$LineTolerance = 3,

    [Parameter()]
    [string]$ExportCsv = "",

    [Parameter()]
    [switch]$CsOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Resolve-InputPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [switch]$Optional
    )

    if ([System.IO.Path]::IsPathRooted($Path)) {
        $candidate = $Path
    }
    else {
        $candidate = Join-Path (Get-Location) $Path
    }

    if (-not (Test-Path -LiteralPath $candidate)) {
        if ($Optional) {
            return $null
        }

        throw "Path not found: $candidate"
    }

    return (Resolve-Path -LiteralPath $candidate).ProviderPath
}

function Normalize-RepoPath {
    param([string]$Path)
    return ($Path -replace "\\", "/").ToLowerInvariant()
}

function Get-RuleLanguage {
    param([string]$RuleId)

    if ($RuleId -match '^([^/]+)/') {
        return $Matches[1]
    }

    return ""
}

function Find-LocalMatch {
    param(
        [string]$RuleId,
        [string]$Path,
        [int]$Line,
        [object[]]$LocalResults,
        [int]$Tolerance
    )

    $normPath = Normalize-RepoPath $Path
    foreach ($result in $LocalResults) {
        if ([string]$result.ruleId -ne $RuleId) {
            continue
        }

        $loc = $result.locations[0].physicalLocation
        $localPath = Normalize-RepoPath $loc.artifactLocation.uri
        $localLine = [int]$loc.region.startLine

        if ($localPath -ne $normPath) {
            continue
        }

        if ([Math]::Abs($localLine - $Line) -le $Tolerance) {
            return [PSCustomObject]@{
                Matched   = $true
                LocalLine = $localLine
                Message   = [string]$result.message.text
            }
        }
    }

    return [PSCustomObject]@{
        Matched   = $false
        LocalLine = ""
        Message   = ""
    }
}

function New-TriageRow {
    param(
        [string]$Status,
        [object]$Alert = $null,
        [string]$RuleId = "",
        [string]$Path = "",
        [int]$Line = 0,
        [string]$Message = "",
        [string]$LocalLine = "",
        [string]$LocalReproduced = ""
    )

    if ($null -ne $Alert) {
        $loc = $Alert.most_recent_instance.location
        $rule = $Alert.rule
        $ruleId = [string]$rule.id
        $path = [string]$loc.path
        $line = [int]$loc.start_line
        $message = ""
        if ($Alert.most_recent_instance.message.PSObject.Properties.Name -contains "text") {
            $message = [string]$Alert.most_recent_instance.message.text
        }

        $severity = ""
        if ($rule.PSObject.Properties.Name -contains "security_severity_level") {
            $severity = [string]$rule.security_severity_level
        }
        if ([string]::IsNullOrWhiteSpace($severity) -and $rule.PSObject.Properties.Name -contains "severity") {
            $severity = [string]$rule.severity
        }

        return [PSCustomObject]@{
            AlertNumber     = $Alert.number
            RuleId          = $ruleId
            Language        = Get-RuleLanguage -RuleId $ruleId
            Severity        = $severity
            Path            = $path
            Line            = $line
            Message         = $message
            LocalStatus     = $Status
            LocalReproduced = $LocalReproduced
            LocalLine       = $LocalLine
            GitHubRef       = [string]$Alert.most_recent_instance.ref
            HtmlUrl         = [string]$Alert.html_url
        }
    }

    return [PSCustomObject]@{
        AlertNumber     = ""
        RuleId          = $RuleId
        Language        = Get-RuleLanguage -RuleId $RuleId
        Severity        = ""
        Path            = $Path
        Line            = $Line
        Message         = $Message
        LocalStatus     = $Status
        LocalReproduced = $LocalReproduced
        LocalLine       = $LocalLine
        GitHubRef       = ""
        HtmlUrl         = ""
    }
}

$ghFile = Resolve-InputPath -Path $GitHubAlertsPath
$sarifFile = Resolve-InputPath -Path $SarifPath -Optional

$ghDoc = Get-Content -LiteralPath $ghFile -Raw | ConvertFrom-Json
$ghAlerts = @($ghDoc.alerts | Sort-Object { [int]$_.number })

$localResults = @()
if ($null -ne $sarifFile) {
    $sarif = Get-Content -LiteralPath $sarifFile -Raw | ConvertFrom-Json
    $localResults = @($sarif.runs[0].results | Where-Object { $_.ruleId -like "cs/*" })
}

$rows = New-Object System.Collections.Generic.List[object]

foreach ($alert in $ghAlerts) {
    if ($CsOnly -and $alert.rule.id -notlike "cs/*") {
        continue
    }

    $loc = $alert.most_recent_instance.location
    $ruleId = [string]$alert.rule.id
    $language = Get-RuleLanguage -RuleId $ruleId

    if ($language -ne "cs") {
        $rows.Add((New-TriageRow -Status "not_scanned_locally" -Alert $alert -LocalReproduced "n/a"))
        continue
    }

    if ($null -eq $sarifFile) {
        $rows.Add((New-TriageRow -Status "github_open" -Alert $alert -LocalReproduced "unknown"))
        continue
    }

    $match = Find-LocalMatch -RuleId $ruleId -Path $loc.path -Line ([int]$loc.start_line) `
        -LocalResults $localResults -Tolerance $LineTolerance

    if ($match.Matched) {
        $rows.Add((New-TriageRow -Status "still_finding" -Alert $alert `
                -LocalReproduced "yes" -LocalLine $match.LocalLine))
    }
    else {
        $rows.Add((New-TriageRow -Status "fixed_locally" -Alert $alert `
                -LocalReproduced "no" -LocalLine ""))
    }
}

if ($null -ne $sarifFile) {
    $ghRules = @($ghAlerts | ForEach-Object { $_.rule.id } | Select-Object -Unique)
    foreach ($result in $localResults) {
        if ($result.ruleId -notin $ghRules) {
            continue
        }

        $loc = $result.locations[0].physicalLocation
        $path = [string]$loc.artifactLocation.uri
        $line = [int]$loc.region.startLine

        $onGitHub = $false
        foreach ($alert in $ghAlerts) {
            if ($alert.rule.id -ne $result.ruleId) {
                continue
            }

            $ghLoc = $alert.most_recent_instance.location
            if ((Normalize-RepoPath $ghLoc.path) -eq (Normalize-RepoPath $path) -and
                [Math]::Abs([int]$ghLoc.start_line - $line) -le $LineTolerance) {
                $onGitHub = $true
                break
            }
        }

        if (-not $onGitHub) {
            $rows.Add((New-TriageRow -Status "new_local_only" `
                    -RuleId $result.ruleId -Path $path -Line $line `
                    -Message ([string]$result.message.text) -LocalReproduced "yes" -LocalLine $line))
        }
    }
}

$githubOpen = @($ghDoc.alerts).Count
if ($CsOnly) {
    $githubOpen = @($ghAlerts | Where-Object { $_.rule.id -like "cs/*" }).Count
}

Write-Host ""
Write-Host "GitHub baseline: $ghFile ($githubOpen open alert(s) in scope)"
if ($null -ne $sarifFile) {
    Write-Host "Local C# SARIF: $sarifFile"
}
else {
    Write-Host "Local C# SARIF: (none — C# rows without local match marked github_open)"
}
Write-Host "Triage rows: $($rows.Count)"
Write-Host ""

$rows | Group-Object LocalStatus | Sort-Object Name |
    Select-Object Count, Name | Format-Table -AutoSize

$fixed = @($rows | Where-Object { $_.LocalStatus -eq "fixed_locally" })
if ($fixed.Count -gt 0) {
    Write-Host "Fixed locally (open on GitHub until merge + scan):"
    $fixed | Sort-Object RuleId, Path, Line |
        Format-Table AlertNumber, RuleId, Path, Line -AutoSize
}

if (-not [string]::IsNullOrWhiteSpace($ExportCsv)) {
    $csvPath = if ([System.IO.Path]::IsPathRooted($ExportCsv)) {
        $ExportCsv
    }
    else {
        Join-Path (Get-Location) $ExportCsv
    }

    $sorted = @($rows | Sort-Object {
            if ([string]::IsNullOrWhiteSpace($_.AlertNumber)) { [int]::MaxValue }
            else { [int]$_.AlertNumber }
        }, RuleId, Path, Line)

    $sorted | Export-Csv -LiteralPath $csvPath -NoTypeInformation -Encoding utf8
    Write-Host ""
    Write-Host "Wrote triage CSV: $csvPath"
}
