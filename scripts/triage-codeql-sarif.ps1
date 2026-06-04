[CmdletBinding()]
param(
    [Parameter()]
    [string]$SarifPath = ".codeql/results-csharp.sarif",

    [Parameter()]
    [string]$RuleId = "",

    [Parameter()]
    [int]$Top = 0,

    [Parameter()]
    [switch]$GroupByRule,

    [Parameter()]
    [string]$ExportCsv = "",

    [Parameter()]
    [string]$Language = "",

    [Parameter()]
    [switch]$PassThru
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Resolve-InputPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if ([System.IO.Path]::IsPathRooted($Path)) {
        if (-not (Test-Path -LiteralPath $Path)) {
            throw "SARIF file not found: $Path"
        }

        return (Resolve-Path -LiteralPath $Path).ProviderPath
    }

    $resolved = Join-Path (Get-Location) $Path
    if (-not (Test-Path -LiteralPath $resolved)) {
        throw "SARIF file not found: $resolved"
    }

    return (Resolve-Path -LiteralPath $resolved).ProviderPath
}

function Resolve-OutputPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return $Path
    }

    return (Join-Path (Get-Location) $Path)
}

function Get-RuleLookup {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Run
    )

    $lookup = @{}
    $rules = @($Run.tool.driver.rules)
    foreach ($rule in $rules) {
        if ($null -eq $rule -or -not $rule.id) {
            continue
        }

        $defaultLevel = ""
        if ($rule.PSObject.Properties.Name -contains "defaultConfiguration" -and
            $null -ne $rule.defaultConfiguration -and
            $rule.defaultConfiguration.PSObject.Properties.Name -contains "level") {
            $defaultLevel = [string]$rule.defaultConfiguration.level
        }

        $securitySeverity = ""
        if ($rule.PSObject.Properties.Name -contains "properties" -and $null -ne $rule.properties) {
            if ($rule.properties.PSObject.Properties.Name -contains "security-severity") {
                $securitySeverity = [string]$rule.properties."security-severity"
            }
        }

        $precision = ""
        if ($rule.PSObject.Properties.Name -contains "properties" -and $null -ne $rule.properties) {
            if ($rule.properties.PSObject.Properties.Name -contains "precision") {
                $precision = [string]$rule.properties.precision
            }
        }

        $ruleDescription = ""
        if ($rule.PSObject.Properties.Name -contains "shortDescription" -and
            $null -ne $rule.shortDescription -and
            $rule.shortDescription.PSObject.Properties.Name -contains "text") {
            $ruleDescription = [string]$rule.shortDescription.text
        }

        $lookup[$rule.id] = [pscustomobject]@{
            RuleId = [string]$rule.id
            RuleName = [string]$rule.name
            RuleDescription = $ruleDescription
            DefaultLevel = $defaultLevel
            SecuritySeverity = $securitySeverity
            Precision = $precision
        }
    }

    return $lookup
}

function Convert-ResultToRow {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Result,

        [Parameter(Mandatory = $true)]
        [hashtable]$RuleLookup,

        [Parameter(Mandatory = $true)]
        [int]$Index
    )

    $ruleId = [string]$Result.ruleId
    $ruleMeta = $null
    if ($RuleLookup.ContainsKey($ruleId)) {
        $ruleMeta = $RuleLookup[$ruleId]
    }

    $primaryLocation = $null
    if ($Result.PSObject.Properties.Name -contains "locations" -and $Result.locations.Count -gt 0) {
        $primaryLocation = $Result.locations[0].physicalLocation
    }

    $file = ""
    $line = 0
    if ($null -ne $primaryLocation) {
        $file = [string]$primaryLocation.artifactLocation.uri
        if ($primaryLocation.PSObject.Properties.Name -contains "region" -and $null -ne $primaryLocation.region) {
            $line = [int]$primaryLocation.region.startLine
        }
    }

    $level = ""
    if ($Result.PSObject.Properties.Name -contains "level") {
        $level = [string]$Result.level
    }
    if ([string]::IsNullOrWhiteSpace($level) -and $null -ne $ruleMeta) {
        $level = [string]$ruleMeta.DefaultLevel
    }

    $securitySeverity = ""
    if ($null -ne $ruleMeta) {
        $securitySeverity = [string]$ruleMeta.SecuritySeverity
    }

    $message = ""
    if ($Result.PSObject.Properties.Name -contains "message" -and
        $null -ne $Result.message -and
        $Result.message.PSObject.Properties.Name -contains "text") {
        $message = [string]$Result.message.text
    }

    if (-not [string]::IsNullOrWhiteSpace($message)) {
        $message = ($message -replace "[\r\n]+", " ").Trim()
    }

    $row = [ordered]@{
        Index = $Index
        Language = $Language
        RuleId = $ruleId
        Level = $level
        SecuritySeverity = $securitySeverity
        Precision = if ($null -ne $ruleMeta) { [string]$ruleMeta.Precision } else { "" }
        File = $file
        Line = $line
        Message = $message
    }

    if ([string]::IsNullOrWhiteSpace($Language)) {
        $row.Remove("Language")
    }

    return [pscustomobject]$row
}

$resolvedSarif = Resolve-InputPath -Path $SarifPath
$sarif = Get-Content -LiteralPath $resolvedSarif -Raw | ConvertFrom-Json

$run = $sarif.runs[0]
if ($null -eq $run) {
    throw "SARIF file does not contain any runs: $resolvedSarif"
}

$ruleLookup = Get-RuleLookup -Run $run
$results = @($run.results)

$rows = @()
for ($i = 0; $i -lt $results.Count; $i++) {
    $rows += Convert-ResultToRow -Result $results[$i] -RuleLookup $ruleLookup -Index ($i + 1)
}

if (-not [string]::IsNullOrWhiteSpace($RuleId)) {
    $rows = @($rows | Where-Object { $_.RuleId -eq $RuleId })
}

$rows = @($rows | Sort-Object RuleId, File, Line)

if ($Top -gt 0) {
    $rows = @($rows | Select-Object -First $Top)
}

if ($PassThru) {
    Write-Output -NoEnumerate $rows
    exit 0
}

Write-Host ""
Write-Host "CodeQL SARIF: $resolvedSarif"
Write-Host "Total alerts in run: $($results.Count)"
Write-Host "Rows after filters: $($rows.Count)"
if (-not [string]::IsNullOrWhiteSpace($RuleId)) {
    Write-Host "Rule filter: $RuleId"
}
Write-Host ""

if ($GroupByRule) {
    $rows |
        Group-Object RuleId |
        Sort-Object @{ Expression = "Count"; Descending = $true }, @{ Expression = "Name"; Descending = $false } |
        Select-Object Count, Name |
        Format-Table -AutoSize | Out-Host
}

if (-not [string]::IsNullOrWhiteSpace($ExportCsv)) {
    $csvPath = Resolve-OutputPath -Path $ExportCsv
    $rows | Export-Csv -LiteralPath $csvPath -NoTypeInformation -Encoding UTF8
    Write-Host "Exported CSV: $csvPath"
    Write-Host ""
}

$displayProps = @("Index", "RuleId", "Level", "SecuritySeverity", "File", "Line", "Message")
if (-not [string]::IsNullOrWhiteSpace($Language)) {
    $displayProps = @("Index", "Language", "RuleId", "Level", "SecuritySeverity", "File", "Line", "Message")
}

$rows | Select-Object $displayProps | Format-Table -AutoSize -Wrap
