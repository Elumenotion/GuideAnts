# Validates installer PowerShell parse, bash syntax, and compose fragment merges.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)

$psFiles = @(
    (Join-Path $root 'scripts/guideants-launcher.ps1'),
    (Join-Path $root 'scripts/stop-guideants-launcher.ps1'),
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

foreach ($rel in @('guideants.cmd', 'stop_guideants.cmd')) {
    $path = Join-Path $root $rel
    if (-not (Test-Path -LiteralPath $path)) {
        Write-Host "FAIL missing: $rel"
        exit 1
    }
    Write-Host "PASS present: $rel"
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
$searxngSettings = Join-Path $dockerDir 'volumes/searxng/config/settings.yml'
$searxngLimiter = Join-Path $dockerDir 'volumes/searxng/config/limiter.toml'
if (-not (Test-Path -LiteralPath $searxngSettings)) {
    Write-Host "FAIL missing SearXNG settings seed: $searxngSettings"
    exit 1
}
if (-not (Test-Path -LiteralPath $searxngLimiter)) {
    Write-Host "FAIL missing SearXNG limiter seed: $searxngLimiter"
    exit 1
}
Write-Host 'PASS searxng config seeds present'

Push-Location $dockerDir
try {
    $combos = @(
        @('compose/base.yml', 'compose/core-bundled.yml'),
        @('compose/base.yml', 'compose/core-separate.yml'),
        @('compose/base.yml', 'compose/core-bundled.yml', 'compose/ai-slim.yml'),
        @('compose/base.yml', 'compose/core-separate.yml', 'compose/ai-cuda13.yml', 'compose/docling-cuda.yml', 'compose/documentserver.yml', 'compose/plantuml.yml', 'compose/searxng.yml'),
        @('compose/base.yml', 'compose/core-separate.yml', 'compose/ai-rocm.yml')
    )
    foreach ($combo in $combos) {
        $composeArgs = @('--project-directory', $dockerDir)
        foreach ($f in $combo) { $composeArgs += '-f'; $composeArgs += $f }
        & docker compose @composeArgs --env-file .env config --quiet
        if ($LASTEXITCODE -ne 0) {
            Write-Host "FAIL compose: $($combo -join ', ')"
            exit 1
        }
        Write-Host "PASS compose: $($combo[-1])"
    }

    $searxngArgs = @(
        '--project-directory', $dockerDir,
        '-f', 'compose/base.yml',
        '-f', 'compose/core-bundled.yml',
        '-f', 'compose/searxng.yml',
        '--env-file', '.env',
        'config'
    )
    $rendered = & docker compose @searxngArgs
    if ($LASTEXITCODE -ne 0) {
        Write-Host 'FAIL searxng compose config render'
        exit 1
    }
    $expectedConfig = (Join-Path $dockerDir 'volumes\searxng\config').Replace('\', '/')
    $wrongConfig = (Join-Path $dockerDir 'compose\volumes\searxng\config').Replace('\', '/')
    $normalized = ($rendered -join "`n").Replace('\', '/')
    if ($normalized -notmatch [regex]::Escape($expectedConfig)) {
        Write-Host "FAIL searxng bind must resolve under docker/volumes/searxng/config (got wrong project root?)"
        exit 1
    }
    if ($normalized -match [regex]::Escape($wrongConfig)) {
        Write-Host 'FAIL searxng bind incorrectly resolves under compose/volumes/'
        exit 1
    }
    Write-Host 'PASS searxng bind path resolves to docker/volumes/searxng'
}
finally {
    Pop-Location
}

Write-Host 'All installer validation checks passed.'
