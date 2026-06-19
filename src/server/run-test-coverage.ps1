# Runs GuideAnts server tests with code coverage and emits an HTML + text summary report.
param(
    [ValidateSet('All', 'Unit', 'Integration')]
    [string]$Scope = 'All',
    [switch]$NoReport
)

$ErrorActionPreference = 'Stop'
$ServerRoot = $PSScriptRoot
$CoverageReportDir = Join-Path $ServerRoot 'coverage-report'
$RunSettings = Join-Path $ServerRoot 'coverlet.runsettings'

Push-Location $ServerRoot
try {
    $testResultsDir = Join-Path $ServerRoot 'TestResults'
    if (Test-Path $testResultsDir) {
        Remove-Item $testResultsDir -Recurse -Force
    }

    $projects = switch ($Scope) {
        'Unit' {
            @(
                'GuideAntsApi.Tests\GuideAntsApi.Tests.csproj',
                'ScriptExecutionAgent.Tests\ScriptExecutionAgent.Tests.csproj'
            )
        }
        'Integration' { @('GuideAntsApi.IntegrationTests\GuideAntsApi.IntegrationTests.csproj') }
        default { @('GuideAntsApi.sln') }
    }

    foreach ($project in $projects) {
        Write-Host "Running tests with coverage: $project" -ForegroundColor Cyan
        dotnet test $project `
            --collect:"XPlat Code Coverage" `
            --settings $RunSettings `
            --results-directory (Join-Path $ServerRoot 'TestResults') `
            --verbosity minimal

        if ($LASTEXITCODE -ne 0) {
            throw "Tests failed for $project (exit code $LASTEXITCODE)."
        }

        if ($project -like '*IntegrationTests*') {
            $repoRoot = (Resolve-Path -LiteralPath (Join-Path $ServerRoot '..' '..')).ProviderPath
            $cleanScript = Join-Path $repoRoot 'scripts/clean-codeql-blocking-artifacts.ps1'
            if (Test-Path -LiteralPath $cleanScript) {
                Write-Host 'Cleaning integration-test docker volume artifacts for CodeQL compatibility...' -ForegroundColor Yellow
                & powershell -NoProfile -ExecutionPolicy Bypass -File $cleanScript -RepoRoot $repoRoot
            }
        }
    }

    $coverageFiles = Get-ChildItem -Path (Join-Path $ServerRoot 'TestResults') -Recurse -Filter 'coverage.cobertura.xml' -ErrorAction SilentlyContinue
    if (-not $coverageFiles) {
        throw 'No coverage.cobertura.xml files were produced.'
    }

    Write-Host "`nCoverage files:" -ForegroundColor Yellow
    $coverageFiles | ForEach-Object { Write-Host "  $($_.FullName)" }

    if ($NoReport) {
        return
    }

    $reportGenerator = Get-Command reportgenerator -ErrorAction SilentlyContinue
    if (-not $reportGenerator) {
        Write-Host 'Installing ReportGenerator global tool...' -ForegroundColor Yellow
        dotnet tool install --global dotnet-reportgenerator-globaltool
    }

    if (Test-Path $CoverageReportDir) {
        Remove-Item $CoverageReportDir -Recurse -Force
    }

    $reportPattern = Join-Path $ServerRoot 'TestResults' '**' 'coverage.cobertura.xml'
    reportgenerator `
        -reports:$reportPattern `
        -targetdir:$CoverageReportDir `
        -reporttypes:'Html;TextSummary'

    $summaryPath = Join-Path $CoverageReportDir 'Summary.txt'
    if (Test-Path $summaryPath) {
        Write-Host "`n--- Coverage summary ---" -ForegroundColor Green
        Get-Content $summaryPath | Write-Host
    }

    $indexPath = Join-Path $CoverageReportDir 'index.html'
    Write-Host "`nHTML report: $indexPath" -ForegroundColor Green
}
finally {
    Pop-Location
}
