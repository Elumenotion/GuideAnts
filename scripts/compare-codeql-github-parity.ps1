[CmdletBinding()]
param(
    [Parameter()]
    [string]$GitHubAlertsPath = "scan-results.txt",

    [Parameter()]
    [string]$CSharpSarif = ".codeql/results-csharp.sarif",

    [Parameter()]
    [string]$PythonSarif = ".codeql/results-python.sarif",

    [Parameter()]
    [string]$JavascriptSarif = ".codeql/results-javascript.sarif",

    [Parameter()]
    [string]$ExpectedCommitSha = "",

    [Parameter()]
    [int]$LineTolerance = 3,

    [Parameter()]
    [string]$ExportCsv = "",

    [Parameter()]
    [switch]$FailOnMismatch
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
    param(
        [string]$Path,
        [string]$Language = ""
    )

    $normalized = ($Path -replace "\\", "/").ToLowerInvariant()
    if ($Language -eq "js" -or $normalized.StartsWith("src/client/")) {
        $normalized = $normalized -replace '^src/client/', ''
    }

    return $normalized
}

function Get-RuleLanguage {
    param([string]$RuleId)

    if ($RuleId -match '^([^/]+)/') {
        return $Matches[1]
    }

    return ""
}

function Get-LocalResultsByLanguage {
    param([hashtable]$SarifByLanguage)

    $map = @{
        cs = $SarifByLanguage["csharp"]
        py = $SarifByLanguage["python"]
        js = $SarifByLanguage["javascript"]
    }

    $results = @{
        cs = @()
        py = @()
        js = @()
    }

    foreach ($lang in @("cs", "py", "js")) {
        $path = $map[$lang]
        if ([string]::IsNullOrWhiteSpace($path)) {
            continue
        }

        $sarif = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        $raw = $sarif.runs[0].results
        if ($null -eq $raw) {
            $results[$lang] = @()
        }
        elseif ($raw -is [System.Array]) {
            $results[$lang] = @($raw | Where-Object { $_.ruleId -like "$lang/*" })
        }
        else {
            $results[$lang] = @(@($raw) | Where-Object { $_.ruleId -like "$lang/*" })
        }
    }

    return $results
}

function Find-LocalMatch {
    param(
        [string]$RuleId,
        [string]$Path,
        [int]$Line,
        [object]$LocalResults,
        [int]$Tolerance,
        [string]$Language = ""
    )

    $resultsToScan = @(
        if ($null -eq $LocalResults) { @() }
        elseif ($LocalResults -is [System.Array]) { $LocalResults }
        else { @($LocalResults) }
    )

    $normPath = Normalize-RepoPath -Path $Path -Language $Language
    foreach ($result in $resultsToScan) {
        if ([string]$result.ruleId -ne $RuleId) {
            continue
        }

        if ($null -eq $result.locations -or $result.locations.Count -lt 1) {
            continue
        }

        $loc = $result.locations[0].physicalLocation
        if ($null -eq $loc -or $null -eq $loc.artifactLocation) {
            continue
        }

        $localPath = Normalize-RepoPath -Path ([string]$loc.artifactLocation.uri) -Language $Language
        $localLine = 0
        if ($null -ne $loc.region -and $null -ne $loc.region.startLine) {
            $localLine = [int]$loc.region.startLine
        }

        if ($localPath -ne $normPath) {
            continue
        }

        if ([Math]::Abs($localLine - $Line) -le $Tolerance) {
            return [PSCustomObject]@{
                Matched   = $true
                LocalLine = [string]$localLine
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
        [string]$LocalReproduced = "",
        [string]$CommitSha = ""
    )

    if ($null -ne $Alert) {
        $loc = $Alert.most_recent_instance.location
        $rule = $Alert.rule
        $ruleId = [string]$rule.id
        $path = [string]$loc.path
        $line = 0
        if ($null -ne $loc -and $null -ne $loc.start_line) {
            $line = [int]$loc.start_line
        }
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

        $commitSha = [string]$Alert.most_recent_instance.commit_sha

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
            GitHubCommit    = $commitSha
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
        GitHubCommit    = $CommitSha
        HtmlUrl         = ""
    }
}

function Assert-RuleLevelParity {
    param(
        [object[]]$GitHubAlerts,
        [string]$ExpectedCommitSha,
        [hashtable]$LocalByLang,
        [int]$LineTolerance
    )

    $atCommit = @(
        $GitHubAlerts | Where-Object {
            $ruleId = [string]$_.rule.id
            (Get-RuleLanguage -RuleId $ruleId) -in @("cs", "py", "js") -and
            (Test-CommitMatchesExpected -AlertCommit ([string]$_.most_recent_instance.commit_sha) -Expected $ExpectedCommitSha)
        }
    )

    if ($atCommit.Count -eq 0) {
        return
    }

    $under = New-Object System.Collections.ArrayList

    foreach ($group in ($atCommit | Group-Object { [string]$_.rule.id })) {
        $ruleId = [string]$group.Name
        $language = Get-RuleLanguage -RuleId $ruleId
        $localResults = @($LocalByLang[$language] | Where-Object { [string]$_.ruleId -eq $ruleId })

        $matchedInstances = 0
        foreach ($alert in $group.Group) {
            $loc = $alert.most_recent_instance.location
            $lineNumber = 0
            if ($null -ne $loc.start_line) {
                $lineNumber = [int]$loc.start_line
            }

            $match = Find-LocalMatch -RuleId $ruleId -Path ([string]$loc.path) -Line $lineNumber `
                -LocalResults $localResults -Tolerance $LineTolerance -Language $language
            if ($match.Matched) {
                $matchedInstances++
            }
        }

        if ($matchedInstances -lt $group.Count) {
            [void]$under.Add([PSCustomObject]@{
                    RuleId           = $ruleId
                    GitHubInstances  = $group.Count
                    LocalMatched     = $matchedInstances
                    LocalRuleResults = $localResults.Count
                })
        }
    }

    if ($under.Count -eq 0) {
        return
    }

    Write-Host ""
    Write-Host "Rule-level parity failures (local must reproduce every GitHub instance at this commit):"
    $under | Sort-Object RuleId | Format-Table RuleId, GitHubInstances, LocalMatched, LocalRuleResults -AutoSize

    $rules = ($under | ForEach-Object { $_.RuleId }) -join ", "
    Write-Error "Parity failed: under-reproduced rule(s): $rules. Use default C# build-mode none (see docs/codeql-local-solution-runbook.md)."
    exit 1
}

function Test-CommitMatchesExpected {
    param(
        [string]$AlertCommit,
        [string]$Expected
    )

    if ([string]::IsNullOrWhiteSpace($Expected)) {
        return $true
    }

    if ([string]::IsNullOrWhiteSpace($AlertCommit)) {
        return $false
    }

    $expectedNorm = $Expected.Trim().ToLowerInvariant()
    $alertNorm = $AlertCommit.Trim().ToLowerInvariant()
    return $alertNorm.StartsWith($expectedNorm) -or $expectedNorm.StartsWith($alertNorm)
}

$ghFile = Resolve-InputPath -Path $GitHubAlertsPath
$sarifByLanguage = @{
    csharp     = Resolve-InputPath -Path $CSharpSarif -Optional
    python     = Resolve-InputPath -Path $PythonSarif -Optional
    javascript = Resolve-InputPath -Path $JavascriptSarif -Optional
}

$ghDoc = Get-Content -LiteralPath $ghFile -Raw | ConvertFrom-Json
if ($null -eq $ghDoc.alerts) {
    $ghAlerts = @()
}
else {
    $ghAlerts = @($ghDoc.alerts)
}
$ghAlerts = @($ghAlerts | Sort-Object { [int]$_.number })
$localByLang = Get-LocalResultsByLanguage -SarifByLanguage $sarifByLanguage

$rows = [System.Collections.ArrayList]::new()
$skippedWrongCommit = 0

foreach ($alert in $ghAlerts) {
    try {
        $ruleId = [string]$alert.rule.id
        $language = Get-RuleLanguage -RuleId $ruleId
        if ($language -notin @("cs", "py", "js")) {
            continue
        }

        $alertCommit = [string]$alert.most_recent_instance.commit_sha
        if (-not (Test-CommitMatchesExpected -AlertCommit $alertCommit -Expected $ExpectedCommitSha)) {
            $skippedWrongCommit++
            continue
        }

        $loc = $alert.most_recent_instance.location
        $sarifPath = switch ($language) {
            "cs" { $sarifByLanguage["csharp"] }
            "py" { $sarifByLanguage["python"] }
            "js" { $sarifByLanguage["javascript"] }
            default { $null }
        }

    if ([string]::IsNullOrWhiteSpace($sarifPath)) {
        [void]$rows.Add((New-TriageRow -Status "not_scanned_locally" -Alert $alert -LocalReproduced "n/a"))
        continue
    }

    $localResults = @($localByLang[$language])
    $lineNumber = 0
    if ($null -ne $loc.start_line) {
        $lineNumber = [int]$loc.start_line
    }

    $match = Find-LocalMatch -RuleId $ruleId -Path ([string]$loc.path) -Line $lineNumber `
        -LocalResults $localResults -Tolerance $LineTolerance -Language $language

    if ($match.Matched) {
        $row = New-TriageRow -Status "still_finding" -Alert $alert `
            -LocalReproduced "yes" -LocalLine ([string]$match.LocalLine)
        [void]$rows.Add($row)
    }
    else {
        $row = New-TriageRow -Status "missing_in_local_sarif" -Alert $alert `
            -LocalReproduced "no" -LocalLine ""
        [void]$rows.Add($row)
    }
    }
    catch {
        throw "Parity compare failed on GitHub alert #$($alert.number) ($ruleId): $($_.Exception.Message)"
    }
}

foreach ($lang in @("cs", "py", "js")) {
    $sarifKey = switch ($lang) {
        "cs" { "csharp" }
        "py" { "python" }
        "js" { "javascript" }
    }
    if ([string]::IsNullOrWhiteSpace($sarifByLanguage[$sarifKey])) {
        continue
    }

    [string[]]$ghRules = @(
        $ghAlerts | Where-Object {
            (Get-RuleLanguage -RuleId ([string]$_.rule.id)) -eq $lang -and
            (Test-CommitMatchesExpected -AlertCommit ([string]$_.most_recent_instance.commit_sha) -Expected $ExpectedCommitSha)
        } | ForEach-Object { [string]$_.rule.id }
    )
    $ghRuleSet = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($rule in $ghRules) {
        if (-not [string]::IsNullOrWhiteSpace($rule)) {
            [void]$ghRuleSet.Add($rule)
        }
    }

    foreach ($result in @($localByLang[$lang])) {
        if (-not $ghRuleSet.Contains([string]$result.ruleId)) {
            continue
        }

        $loc = $result.locations[0].physicalLocation
        $path = [string]$loc.artifactLocation.uri
        $line = 0
        if ($null -ne $loc.region -and $null -ne $loc.region.startLine) {
            $line = [int]$loc.region.startLine
        }

        $onGitHub = $false
        foreach ($alert in $ghAlerts) {
            if ($alert.rule.id -ne $result.ruleId) {
                continue
            }
            if (-not (Test-CommitMatchesExpected -AlertCommit ([string]$alert.most_recent_instance.commit_sha) -Expected $ExpectedCommitSha)) {
                continue
            }

            $ghLoc = $alert.most_recent_instance.location
            $ghLine = 0
            if ($null -ne $ghLoc.start_line) {
                $ghLine = [int]$ghLoc.start_line
            }

            if ((Normalize-RepoPath -Path $ghLoc.path -Language $lang) -eq (Normalize-RepoPath -Path $path -Language $lang) -and
                [Math]::Abs($ghLine - $line) -le $LineTolerance) {
                $onGitHub = $true
                break
            }
        }

        if (-not $onGitHub) {
            $row = New-TriageRow -Status "new_local_only" `
                -RuleId ([string]$result.ruleId) -Path $path -Line $line `
                -Message ([string]$result.message.text) -LocalReproduced "yes" -LocalLine ([string]$line)
            [void]$rows.Add($row)
        }
    }
}

$inScope = @($rows.ToArray())

if (-not [string]::IsNullOrWhiteSpace($ExpectedCommitSha) -and @($inScope).Count -eq 0 -and @($ghAlerts).Count -gt 0) {
    throw @"
No GitHub alerts in $GitHubAlertsPath match commit $ExpectedCommitSha.
Run: powershell -NoProfile -ExecutionPolicy Bypass -File scripts/fetch-github-code-scanning.ps1
Then re-run the local CodeQL scan at the same commit.
"@
}

$missing = @($inScope | Where-Object { $_.LocalStatus -eq "missing_in_local_sarif" })
$notScanned = @($inScope | Where-Object { $_.LocalStatus -eq "not_scanned_locally" })
$newLocal = @($inScope | Where-Object { $_.LocalStatus -eq "new_local_only" })
$matched = @($inScope | Where-Object { $_.LocalStatus -eq "still_finding" })

Write-Host ""
Write-Host "GitHub baseline: $ghFile ($($inScope.Count) alert(s) in parity scope)"
if (-not [string]::IsNullOrWhiteSpace($ExpectedCommitSha)) {
    Write-Host "Expected commit: $ExpectedCommitSha (skipped $skippedWrongCommit alert(s) on other SHAs)"
}
Write-Host "Local SARIF: csharp=$($sarifByLanguage['csharp']) python=$($sarifByLanguage['python']) javascript=$($sarifByLanguage['javascript'])"
Write-Host ""
Write-Host "still_finding: $($matched.Count)"
Write-Host "missing_in_local_sarif: $($missing.Count)"
Write-Host "new_local_only: $($newLocal.Count)"
Write-Host "not_scanned_locally: $($notScanned.Count)"

if ($missing.Count -gt 0) {
    Write-Host ""
    Write-Host "GitHub open alerts NOT reproduced locally (fix extraction or refresh scan-results.txt):"
    $missing | Sort-Object RuleId, Path, Line |
        Format-Table AlertNumber, RuleId, Path, Line, GitHubCommit -AutoSize
}

if ($newLocal.Count -gt 0) {
    Write-Host ""
    Write-Host "Local-only findings (not on GitHub snapshot at this commit):"
    $newLocal | Sort-Object RuleId, Path, Line |
        Format-Table RuleId, Path, Line -AutoSize
}

if (-not [string]::IsNullOrWhiteSpace($ExportCsv)) {
    $csvPath = if ([System.IO.Path]::IsPathRooted($ExportCsv)) {
        $ExportCsv
    }
    else {
        Join-Path (Get-Location) $ExportCsv
    }

    $sorted = @($inScope | Sort-Object {
            $numText = [string]$_.AlertNumber
            if ([string]::IsNullOrWhiteSpace($numText)) { [int]::MaxValue }
            else { [int]$numText }
        }, RuleId, Path, Line)

    $sorted | Export-Csv -LiteralPath $csvPath -NoTypeInformation -Encoding utf8
    Write-Host ""
    Write-Host "Wrote parity CSV: $csvPath"
}

if ($FailOnMismatch) {
    if ($notScanned.Count -gt 0) {
        Write-Error "Parity failed: $($notScanned.Count) GitHub alert(s) have no local SARIF for that language. Run scripts/run-codeql-sln-triage.ps1 -Languages all."
        exit 1
    }

    Assert-RuleLevelParity -GitHubAlerts @($ghAlerts) -ExpectedCommitSha $ExpectedCommitSha `
        -LocalByLang $localByLang -LineTolerance $LineTolerance

    Write-Host ""
    Write-Host "GitHub parity check passed ($($matched.Count) alert(s) reproduced locally)."
}

exit 0
