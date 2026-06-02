param(
    [string]$Solution = "src/server/GuideAntsApi.sln",
    [string]$OutputDir = "artifacts/unused-code-analysis",
    [string]$Configuration = "Debug",
    [switch]$Restore,
    [switch]$FailOnBuildError
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$solutionPath = Join-Path $repoRoot $Solution
$outputPath = Join-Path $repoRoot $OutputDir
$logPath = Join-Path $outputPath "build.log"
$markdownPath = Join-Path $outputPath "unused-code-analysis.md"
$csvPath = Join-Path $outputPath "unused-code-analysis.csv"
$jsonPath = Join-Path $outputPath "unused-code-analysis.json"

New-Item -ItemType Directory -Path $outputPath -Force | Out-Null

$dotnetArgs = @(
    "build",
    $solutionPath,
    "-c",
    $Configuration,
    "--no-incremental",
    "/v:minimal",
    "/clp:NoSummary",
    "/flp:logfile=$logPath;verbosity=normal"
)

if (-not $Restore) {
    $dotnetArgs += "--no-restore"
}

Write-Host "Running: dotnet $($dotnetArgs -join ' ')"
& dotnet @dotnetArgs
$buildExitCode = $LASTEXITCODE

$trackedRules = "IDE0051", "IDE0052", "IDE0060", "CA1801", "CA1811", "CA1823"
$trackedRulePattern = ($trackedRules | ForEach-Object { [regex]::Escape($_) }) -join "|"
$warningPattern = "^\s*(?:\d+>)?(?<file>.*?\.cs)\((?<line>\d+),(?<column>\d+)\): warning (?<rule>$trackedRulePattern): (?<message>.*?) \((?<help>https?://[^)]*)\) \[(?<project>.*?\.csproj)\]$"
$items = New-Object System.Collections.Generic.List[object]

if (Test-Path $logPath) {
    Get-Content $logPath | ForEach-Object {
        if ($_ -match $warningPattern) {
            $items.Add([pscustomobject]@{
                Rule = $Matches.rule
                File = $Matches.file.Trim()
                Line = [int]$Matches.line
                Column = [int]$Matches.column
                Message = $Matches.message
                Project = $Matches.project
                Help = $Matches.help
            })
        }
    }
}

$orderedItems = $items |
    Sort-Object Rule, Project, File, Line, Column, Message -Unique

$orderedItems |
    Export-Csv -Path $csvPath -NoTypeInformation -Encoding UTF8

$orderedItems |
    ConvertTo-Json -Depth 5 |
    Set-Content -Path $jsonPath -Encoding UTF8

$generatedAt = Get-Date -Format "yyyy-MM-dd HH:mm:ss zzz"
$byRule = $orderedItems | Group-Object Rule | Sort-Object Name

$markdown = New-Object System.Collections.Generic.List[string]
$markdown.Add("# Unused Code Analysis")
$markdown.Add("")
$markdown.Add("Generated: $generatedAt")
$markdown.Add("")
$markdown.Add("Solution: ``$Solution``")
$markdown.Add("")
$markdown.Add("Build exit code: ``$buildExitCode``")
$markdown.Add("")
$markdown.Add("Raw build log: ``$OutputDir/build.log``")
$markdown.Add("")
$markdown.Add("CSV: ``$OutputDir/unused-code-analysis.csv``")
$markdown.Add("")
$markdown.Add("JSON: ``$OutputDir/unused-code-analysis.json``")
$markdown.Add("")
$markdown.Add("## Summary")
$markdown.Add("")
$markdown.Add("Tracked rules: ``$($trackedRules -join '`, `')``")
$markdown.Add("")
$markdown.Add("Scope: Visual Studio/Roslyn analyzers. These rules report unused private members, unread fields, and unused parameters. They do not prove that public API members or public classes are used.")
$markdown.Add("")
$markdown.Add("Total findings: **$($orderedItems.Count)**")
$markdown.Add("")

foreach ($group in $byRule) {
    $markdown.Add("- ``$($group.Name)``: $($group.Count)")
}

if ($orderedItems.Count -eq 0) {
    $markdown.Add("")
    $markdown.Add("No tracked unused-code findings were found in the build log.")
} else {
    $markdown.Add("")
    $markdown.Add("## Findings")
    $markdown.Add("")
    $markdown.Add("| Rule | Location | Message | Project |")
    $markdown.Add("| --- | --- | --- | --- |")

    foreach ($item in $orderedItems) {
        $relativeFile = Resolve-Path -Path $item.File -Relative -ErrorAction SilentlyContinue
        if (-not $relativeFile) {
            $relativeFile = $item.File
        }

        $location = "${relativeFile}:$($item.Line):$($item.Column)"
        $message = ($item.Message -replace '\|', '\|')
        $projectName = Split-Path $item.Project -Leaf
        $markdown.Add("| ``$($item.Rule)`` | ``$location`` | $message | ``$projectName`` |")
    }
}

$markdown.Add("")
$markdown.Add("## Important Limitations")
$markdown.Add("")
$markdown.Add("- Visual Studio/Roslyn does not generally flag unused public classes or public methods as dead code because they can be entry points for callers outside the solution, ASP.NET routing, dependency injection, serializers, reflection, generated code, or tests.")
$markdown.Add("- A zero count for public classes or public methods in this report means the configured first-party analyzers did not report them; it does not mean none exist.")

$markdown |
    Set-Content -Path $markdownPath -Encoding UTF8

Write-Host "Wrote $markdownPath"
Write-Host "Wrote $csvPath"
Write-Host "Wrote $jsonPath"
Write-Host "Wrote $logPath"

if ($FailOnBuildError -and $buildExitCode -ne 0) {
    exit $buildExitCode
}

exit 0
