[CmdletBinding()]
param(
    [Parameter()]
    [string]$CodeqlPath = "",

    [Parameter()]
    [ValidateSet("csharp", "python", "javascript", "all")]
    [string]$Languages = "all",

    [Parameter()]
    [string]$SolutionPath = "src/server/GuideAntsApi.sln",

    [Parameter()]
    [string]$CsvPath = ".codeql/triage.csv",

    [Parameter()]
    [string]$ManifestPath = ".codeql/run-manifest.json",

    [Parameter()]
    [switch]$CleanBuildOutputs,

    [Parameter()]
    [switch]$CleanCodeqlOutputs,

    # Runbook default is all languages. Partial runs overwrite triage.csv with incomplete data.
    [Parameter()]
    [switch]$AllowPartialLanguages
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Resolve-CodeqlPath {
    param([string]$Candidate)

    if (-not [string]::IsNullOrWhiteSpace($Candidate)) {
        if (-not (Test-Path -LiteralPath $Candidate)) {
            throw "CodeQL not found at provided path: $Candidate"
        }
        return (Resolve-Path -LiteralPath $Candidate).ProviderPath
    }

    $fromPath = Get-Command codeql -ErrorAction SilentlyContinue
    if ($null -ne $fromPath) {
        return $fromPath.Source
    }

    $known = @(
        "C:\Users\dougl\tools\codeql\codeql.exe",
        "C:\tools\codeql\codeql.exe",
        "D:\tools\codeql\codeql.exe"
    )

    foreach ($path in $known) {
        if (Test-Path -LiteralPath $path) {
            return $path
        }
    }

    throw "Unable to locate codeql.exe. Install CodeQL or pass -CodeqlPath."
}

function Remove-IfExists {
    param([string]$Path, [switch]$Recurse)

    if (Test-Path -LiteralPath $Path) {
        if ($Recurse) {
            Remove-Item -LiteralPath $Path -Recurse -Force
        }
        else {
            Remove-Item -LiteralPath $Path -Force
        }
    }
}

function Get-GitValue {
    param([string[]]$GitArgs)

    $output = & git @GitArgs 2>$null
    if ($LASTEXITCODE -ne 0) {
        return ""
    }

    return ([string]$output).Trim()
}

function Get-AnalyzeCoverageLine {
    param([string]$LogDir)

    $analyzeLog = Get-ChildItem -LiteralPath $LogDir -Filter "database-analyze-*.log" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime | Select-Object -Last 1

    if ($null -eq $analyzeLog) {
        return ""
    }

    $lines = @()

    $stdoutPatterns = @(
        "CodeQL scanned .* out of .* C# files",
        "CodeQL scanned .* Python files",
        "CodeQL scanned .* JavaScript/TypeScript files"
    )
    foreach ($pattern in $stdoutPatterns) {
        $match = Select-String -Path $analyzeLog.FullName -Pattern $pattern | Select-Object -Last 1
        if ($null -ne $match) {
            $lines += $match.Line.Trim()
        }
    }

    # Analyze log often records baseline file counts instead of the stdout "scanned" line.
    $baseline = Select-String -Path $analyzeLog.FullName -Pattern "Found (\d+) baseline files for (\w+)" -AllMatches
    foreach ($m in $baseline.Matches) {
        $lines += "Found $($m.Groups[1].Value) baseline files for $($m.Groups[2].Value)"
    }

    return ($lines -join " | ")
}

function Assert-CSharpCoverage {
    param(
        [string]$CoverageLine,
        [int]$MinimumBaselineCSharp = 550
    )

    if ([string]::IsNullOrWhiteSpace($CoverageLine)) {
        throw "C# CodeQL coverage line missing from analyze log. Do not trust result counts."
    }

    if ($CoverageLine -match 'CodeQL scanned (\d+) out of (\d+) C# files') {
        $scanned = [int]$Matches[1]
        $total = [int]$Matches[2]
        if ($scanned -lt $MinimumBaselineCSharp -or $total -lt 590) {
            throw "C# CodeQL coverage too low ($scanned / $total). Expected ~597/598 after GuideAntsApi.sln rebuild."
        }
        return
    }

    if ($CoverageLine -match 'Found (\d+) baseline files for csharp') {
        $baseline = [int]$Matches[1]
        if ($baseline -lt $MinimumBaselineCSharp) {
            throw "C# CodeQL baseline file count too low ($baseline). Expected ~599 after GuideAntsApi.sln rebuild."
        }
        return
    }

    throw "C# CodeQL coverage line not recognized: $CoverageLine"
}

function Invoke-CodeqlLogged {
    param(
        [string]$CodeqlExe,
        [object[]]$Arguments
    )

    $previousErrorAction = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $output = & $CodeqlExe @Arguments 2>&1
        foreach ($line in $output) {
            Write-Host $line
        }
    }
    finally {
        $ErrorActionPreference = $previousErrorAction
    }
}

function Invoke-CodeqlLanguageRun {
    param(
        [string]$CodeqlExe,
        [hashtable]$Config
    )

    Write-Host ""
    Write-Host "=== $($Config.Language) ==="
    Write-Host "Database: $($Config.DatabasePath)"
    Write-Host "Query suite: $($Config.QuerySuite)"

    if ($Config.ContainsKey("BuildCommand") -and -not [string]::IsNullOrWhiteSpace($Config.BuildCommand)) {
        $createArgs = @(
            "database", "create", $Config.DatabasePath,
            "--language=$($Config.CodeqlLanguage)",
            "--command", $Config.BuildCommand
        )
        if (-not [string]::IsNullOrWhiteSpace($Config.SourceRoot)) {
            $createArgs += @("--source-root", $Config.SourceRoot)
        }
    }
    else {
        $createArgs = @(
            "database", "create", $Config.DatabasePath,
            "--language=$($Config.CodeqlLanguage)",
            "--source-root", $Config.SourceRoot,
            "--command", "cmd /c echo build"
        )
    }

    Invoke-CodeqlLogged -CodeqlExe $CodeqlExe -Arguments $createArgs
    $databaseYml = Join-Path $Config.DatabasePath "codeql-database.yml"
    if ($LASTEXITCODE -ne 0) {
        if (Test-Path -LiteralPath $databaseYml) {
            Write-Warning "codeql database create exited $LASTEXITCODE but $databaseYml exists (often a Windows file-lock during cleanup); continuing with analyze."
        }
        else {
            throw "codeql database create failed for $($Config.Language) (exit $LASTEXITCODE)."
        }
    }

    Invoke-CodeqlLogged -CodeqlExe $CodeqlExe -Arguments @(
        "database", "analyze", $Config.DatabasePath, $Config.QuerySuite,
        "--download", "--format=sarifv2.1.0", "--output=$($Config.SarifPath)"
    )
    if ($LASTEXITCODE -ne 0) {
        throw "codeql database analyze failed for $($Config.Language) (exit $LASTEXITCODE)."
    }

    $sarif = Get-Content -LiteralPath $Config.SarifPath -Raw | ConvertFrom-Json
    $resultCount = @($sarif.runs[0].results).Count
    $coverage = Get-AnalyzeCoverageLine -LogDir (Join-Path $Config.DatabasePath "log")

    $tempCsv = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), "codeql-triage-$($Config.Language)-$PID.csv")
    try {
        & powershell -NoProfile -ExecutionPolicy Bypass -File scripts/triage-codeql-sarif.ps1 `
            -SarifPath $Config.SarifPath -Language $Config.Language -ExportCsv $tempCsv | Out-Null
        $rows = @(Import-Csv -LiteralPath $tempCsv)
    }
    finally {
        Remove-IfExists -Path $tempCsv
    }

    return ,[PSCustomObject]@{
        Language     = $Config.Language
        DatabasePath = $Config.DatabasePath
        SarifPath    = $Config.SarifPath
        QuerySuite   = $Config.QuerySuite
        ResultCount  = $resultCount
        CoverageLine = $coverage
        RuleCounts   = @($sarif.runs[0].results | Group-Object ruleId | Sort-Object Count -Descending |
            ForEach-Object { [ordered]@{ rule_id = $_.Name; count = $_.Count } })
        Rows         = $rows
    }
}

$codeqlExe = Resolve-CodeqlPath -Candidate $CodeqlPath
$repoRoot = (Get-Location).ProviderPath
$solutionAbs = (Resolve-Path -LiteralPath $SolutionPath).ProviderPath

$selected = if ($Languages -eq "all") {
    @("csharp", "python", "javascript")
}
else {
    @($Languages)
}

if (-not $AllowPartialLanguages -and $Languages -ne "all") {
    throw @"
-Languages '$Languages' is not allowed for triage.csv (docs/codeql-local-solution-runbook.md).
Use default -Languages all, or pass -AllowPartialLanguages for a deliberate partial run.
"@
}

$languageConfigs = @{
    csharp = @{
        Language       = "csharp"
        CodeqlLanguage = "csharp"
        DatabasePath   = ".codeql/db-csharp"
        SarifPath      = ".codeql/results-csharp.sarif"
        QuerySuite     = "codeql/csharp-queries:codeql-suites/csharp-code-scanning.qls"
        SourceRoot     = ""
        BuildCommand   = "dotnet build `"$solutionAbs`" -c Debug -v minimal -t:Rebuild -p:UseSharedCompilation=false"
    }
    python = @{
        Language       = "python"
        CodeqlLanguage = "python"
        DatabasePath   = ".codeql/db-python"
        SarifPath      = ".codeql/results-python.sarif"
        QuerySuite     = "codeql/python-queries:codeql-suites/python-code-scanning.qls"
        SourceRoot     = "."
        BuildCommand   = ""
    }
    javascript = @{
        Language       = "javascript"
        CodeqlLanguage = "javascript"
        DatabasePath   = ".codeql/db-javascript"
        SarifPath      = ".codeql/results-javascript.sarif"
        QuerySuite     = "codeql/javascript-queries:codeql-suites/javascript-code-scanning.qls"
        SourceRoot     = "src/client"
        BuildCommand   = ""
    }
}

if ($CleanBuildOutputs -and ($selected -contains "csharp")) {
    Get-ChildItem src/server -Recurse -Directory -Force |
        Where-Object { $_.Name -in @("bin", "obj", "publish", "TestResults") } |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
}

if ($CleanCodeqlOutputs) {
    foreach ($lang in $selected) {
        $cfg = $languageConfigs[$lang]
        Remove-IfExists -Path $cfg.DatabasePath -Recurse
        Remove-IfExists -Path $cfg.SarifPath
    }
    Remove-IfExists -Path $CsvPath
    Remove-IfExists -Path $ManifestPath
}

$gitCommit = Get-GitValue -GitArgs @("rev-parse", "HEAD")
$gitCommitShort = Get-GitValue -GitArgs @("rev-parse", "--short", "HEAD")
$gitBranch = Get-GitValue -GitArgs @("rev-parse", "--abbrev-ref", "HEAD")
$codeqlVersion = (& $codeqlExe version 2>&1 | Select-Object -First 1).Trim()

Write-Host "Using CodeQL: $codeqlExe"
Write-Host "CodeQL version: $codeqlVersion"
Write-Host "Git commit: $gitCommitShort ($gitCommit)"
Write-Host "Git branch: $gitBranch"
Write-Host "Languages: $($selected -join ', ')"

$runResults = @()
foreach ($lang in $selected) {
    $run = Invoke-CodeqlLanguageRun -CodeqlExe $codeqlExe -Config $languageConfigs[$lang]
    if ($lang -eq "csharp") {
        Assert-CSharpCoverage -CoverageLine $run.CoverageLine
    }
    $runResults += $run
}

$mergedRows = New-Object System.Collections.Generic.List[object]
$index = 1
foreach ($run in $runResults) {
    foreach ($row in $run.Rows) {
        $mergedRows.Add([PSCustomObject]@{
                Index            = $index
                Language         = $run.Language
                RuleId           = $row.RuleId
                Level            = $row.Level
                SecuritySeverity = $row.SecuritySeverity
                Precision        = $row.Precision
                File             = $row.File
                Line             = $row.Line
                Message          = $row.Message
            })
        $index++
    }
}

$csvResolved = if ([System.IO.Path]::IsPathRooted($CsvPath)) { $CsvPath } else { Join-Path $repoRoot $CsvPath }
$mergedRows | Export-Csv -LiteralPath $csvResolved -NoTypeInformation -Encoding utf8

$manifestResolved = if ([System.IO.Path]::IsPathRooted($ManifestPath)) { $ManifestPath } else { Join-Path $repoRoot $ManifestPath }
$manifest = [ordered]@{
    generated_at     = (Get-Date).ToString("o")
    repository       = $repoRoot
    git_commit       = $gitCommit
    git_commit_short = $gitCommitShort
    git_branch       = $gitBranch
    codeql_version   = $codeqlVersion
    languages        = $selected
    triage_csv_path  = $CsvPath
    total_results    = ($runResults | Measure-Object -Property ResultCount -Sum).Sum
    triage_csv_rows  = $mergedRows.Count
    runs             = @($runResults | ForEach-Object {
            [ordered]@{
                language      = $_.Language
                database_path = $_.DatabasePath
                sarif_path    = $_.SarifPath
                query_suite   = $_.QuerySuite
                result_count  = $_.ResultCount
                coverage_line = $_.CoverageLine
                rule_counts   = $_.RuleCounts
            }
        })
}

$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestResolved -Encoding utf8

Write-Host ""
Write-Host "=== Summary ==="
foreach ($run in $runResults) {
    Write-Host "$($run.Language): $($run.ResultCount) results - $($run.CoverageLine)"
}
Write-Host "Total: $($manifest.total_results) results, $($manifest.triage_csv_rows) triage.csv rows"
Write-Host "triage.csv: $csvResolved"
Write-Host "Manifest: $manifestResolved"
