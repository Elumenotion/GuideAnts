# Validates installer PowerShell parse, bash syntax, and compose fragment merges.
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

$bash = Get-Command bash -ErrorAction SilentlyContinue
if ($null -eq $bash) {
    Write-Host 'SKIP bash -n: bash not found on PATH'
}
else {
    Push-Location $root
    try {
        foreach ($rel in @(
            'guideants.sh',
            'stop_guideants.sh',
            'scripts/installer-wizard.sh',
            'scripts/guideants-host-mount.sh',
            'scripts/rocm-runtime-compose.sh',
            'scripts/rocm-probe.sh',
            'scripts/install-rocm-wsl.sh',
            'scripts/validate-installer.sh'
        )) {
            $path = Join-Path $root $rel
            if (-not (Test-Path -LiteralPath $path)) {
                Write-Host "FAIL missing: $rel"
                exit 1
            }
            & bash -n $rel
            if ($LASTEXITCODE -ne 0) {
                Write-Host "FAIL bash -n: $rel"
                exit 1
            }
            Write-Host "PASS bash -n: $rel"
        }
    }
    finally {
        Pop-Location
    }
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
