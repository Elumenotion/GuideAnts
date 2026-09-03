# Mechanical enforcer for composite telemetry. Run before any composite job or in CI.
#   pwsh scripts/Test-CompositeTelemetryContract.ps1
$ErrorActionPreference = "Stop"
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$violations = [System.Collections.Generic.List[string]]::new()

function Add-Violation([string]$Path, [string]$Message) {
    $rel = $Path.Replace($RepoRoot + [IO.Path]::DirectorySeparatorChar, "").Replace($RepoRoot + "/", "")
    $violations.Add("${rel}: $Message")
}

function Get-CodeLines([string]$Content) {
    $Content -split "`r?`n" | ForEach-Object {
        $line = $_
        if ($line -match '^\s*#') { return }
        if ($line -match '^\s*//') { return }
        $line
    } | Where-Object { $_ -ne $null -and $_.Trim() -ne "" }
}

$scanRoots = @(
    (Join-Path $RepoRoot "scripts"),
    (Join-Path $RepoRoot "artifacts")
)

foreach ($root in $scanRoots) {
    if (-not (Test-Path $root)) { continue }
    Get-ChildItem -Path $root -Recurse -Include *.ps1, *.sh -File | ForEach-Object {
        $content = Get-Content -LiteralPath $_.FullName -Raw
        if ($content -notmatch "run-corridorkey-composite\.py") { return }

        $isHelper = $_.Name -eq "Invoke-CorridorKeyCompositeLog.ps1"
        $isContractTest = $_.Name -eq "Test-CompositeTelemetryContract.ps1"
        $isMonitor = $_.Name -match "monitor-|watch-composite|^wait-"
        if ($isHelper -or $isContractTest -or $isMonitor) { return }

        $invokesComposite = $content -match "run-corridorkey-composite\.py"
        if (-not $invokesComposite) { return }

        if ($content -notmatch "Invoke-CorridorKeyCompositeLog|exec-composite-telemetry\.sh") {
            Add-Violation $_.FullName "calls run-corridorkey-composite without Invoke-CorridorKeyCompositeLog or exec-composite-telemetry.sh"
        }

        foreach ($line in (Get-CodeLines $content)) {
            if ($line -match '2>&1\s*\|') {
                Add-Violation $_.FullName "code uses 2>&1 | ($line.Trim())"
                break
            }
            if ($line -match 'run-corridorkey-composite' -and $line -match '\|\s*Tee-Object') {
                Add-Violation $_.FullName "pipes composite stdout through Tee-Object ($line.Trim())"
                break
            }
        }
    }
}

$wrapper = Join-Path $RepoRoot "docker/build/comfyui-video/scripts/exec-composite-telemetry.sh"
if (-not (Test-Path $wrapper)) {
    Add-Violation $wrapper "missing exec-composite-telemetry.sh (container log tee)"
}

if ($violations.Count -gt 0) {
    Write-Host "COMPOSITE TELEMETRY CONTRACT FAILED ($($violations.Count) violation(s)):" -ForegroundColor Red
    foreach ($v in $violations) { Write-Host "  - $v" }
    Write-Host ""
    Write-Host "Required: scripts/Invoke-CorridorKeyCompositeLog.ps1 or exec-composite-telemetry.sh"
    exit 1
}

Write-Host "Composite telemetry contract: OK"
exit 0
