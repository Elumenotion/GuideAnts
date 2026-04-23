param(
    [ValidateSet('auto', 'docling-cpu', 'docling-cuda')]
    [string]$DoclingProfile = 'auto',
    [switch]$SkipWebApiUi,
    [switch]$SkipPlantUml,
    [switch]$SkipSearXng
)

$ErrorActionPreference = 'Stop'

function Read-EnvFile {
    param([string]$Path)

    $map = @{}
    if (-not (Test-Path $Path)) {
        return $map
    }

    foreach ($line in (Get-Content -Path $Path)) {
        if ([string]::IsNullOrWhiteSpace($line) -or $line -match '^\s*#') {
            continue
        }

        if ($line -match '^\s*([A-Za-z_][A-Za-z0-9_]*)=(.*)$') {
            $map[$matches[1]] = $matches[2]
        }
    }

    return $map
}

$dockerRoot = $PSScriptRoot
$envPath = Join-Path $dockerRoot '.env'
$envMap = Read-EnvFile -Path $envPath
$aiImage = if ($envMap.ContainsKey('GA_AI_IMAGE')) { $envMap['GA_AI_IMAGE'] } else { '' }

if ($DoclingProfile -eq 'auto') {
    if ($envMap.ContainsKey('DOCLING_PROFILE') -and
        ($envMap['DOCLING_PROFILE'] -in @('docling-cpu', 'docling-cuda'))) {
        $DoclingProfile = $envMap['DOCLING_PROFILE']
    }
    elseif ($aiImage -match ':cuda') {
        $DoclingProfile = 'docling-cuda'
    }
    else {
        $DoclingProfile = 'docling-cpu'
    }
}

$doclingService = if ($DoclingProfile -eq 'docling-cuda') { 'docling-serve-cuda' } else { 'docling-serve-cpu' }
$otherProfile = if ($DoclingProfile -eq 'docling-cuda') { 'docling-cpu' } else { 'docling-cuda' }
$otherService = if ($DoclingProfile -eq 'docling-cuda') { 'docling-serve-cpu' } else { 'docling-serve-cuda' }

$services = @('mssql-express', 'guideants-ai', $doclingService)
if (-not $SkipWebApiUi) { $services += 'guideants-webapi-ui' }
if (-not $SkipPlantUml) { $services += 'plantuml' }
if (-not $SkipSearXng) { $services += 'searxng' }

Write-Host "Starting compose stack from $dockerRoot" -ForegroundColor Cyan
Write-Host "GuideAnts AI image: $aiImage" -ForegroundColor Cyan
Write-Host "Docling profile: $DoclingProfile ($doclingService)" -ForegroundColor Cyan

Push-Location $dockerRoot
try {
    # Ensure the opposite Docling profile is not left running.
    docker compose --profile $otherProfile stop $otherService | Out-Null
    docker compose --profile $otherProfile rm -f $otherService | Out-Null

    docker compose --profile $DoclingProfile up -d @services
    if ($LASTEXITCODE -ne 0) {
        throw "docker compose up failed with exit code $LASTEXITCODE"
    }
}
finally {
    Pop-Location
}

Write-Host "Started services: $($services -join ', ')" -ForegroundColor Green
