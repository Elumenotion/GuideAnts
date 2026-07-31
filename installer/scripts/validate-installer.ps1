# Validates installer PowerShell parse + compose fragment merges.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)

$psFiles = @(
    (Join-Path $root 'guideants.ps1'),
    (Join-Path $root 'stop_guideants.ps1'),
    (Join-Path $root 'scripts/installer-wizard.ps1'),
    (Join-Path $root 'scripts/guideants-host-mount.ps1')
)

foreach ($path in $psFiles) {
    $errs = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile($path, [ref]$null, [ref]$errs)
    if ($errs -and $errs.Count -gt 0) {
        Write-Host "FAIL parse: $path"
        $errs | ForEach-Object { Write-Host "  $($_.Message)" }
        exit 1
    }
    Write-Host "PASS parse: $(Split-Path -Leaf $path)"
}

$dockerDir = Join-Path $root 'docker'
Push-Location $dockerDir
try {
    $combos = @(
        @('compose/base.yml', 'compose/core-bundled.yml'),
        @('compose/base.yml', 'compose/core-separate.yml'),
        @('compose/base.yml', 'compose/core-bundled.yml', 'compose/ai-slim.yml'),
        @('compose/base.yml', 'compose/core-separate.yml', 'compose/ai-cuda13.yml', 'compose/docling-cuda.yml', 'compose/documentserver.yml', 'compose/plantuml.yml', 'compose/searxng.yml')
    )
    foreach ($combo in $combos) {
        $composeArgs = @()
        foreach ($f in $combo) { $composeArgs += '-f'; $composeArgs += $f }
        & docker compose @composeArgs --env-file .env config --quiet
        if ($LASTEXITCODE -ne 0) {
            Write-Host "FAIL compose: $($combo -join ', ')"
            exit 1
        }
        Write-Host "PASS compose: $($combo[-1])"
    }
}
finally {
    Pop-Location
}

Write-Host 'All installer validation checks passed.'
