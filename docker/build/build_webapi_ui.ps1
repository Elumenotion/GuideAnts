param(
    [switch]$NoCache,
    [switch]$NoRecreate,
    [switch]$UseAppBuildCache
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$dockerRoot = Split-Path $PSScriptRoot -Parent
$buildContext = Join-Path $repoRoot 'src'
$dockerfilePath = Join-Path $PSScriptRoot 'webapi-ui\Dockerfile'
$clientRoot = Join-Path $repoRoot 'src\client'
$clientNodeModules = Join-Path $clientRoot 'node_modules'
$clientDistBrowser = Join-Path $clientRoot 'dist-browser'

if (-not (Test-Path $dockerfilePath)) {
    Write-Error "Dockerfile not found at $dockerfilePath"
    exit 1
}

# Julian date (2-digit year + day-of-year) + time tag: e.g. 26099.1530
$julianDay = "$(Get-Date -Format 'yy')$((Get-Date).DayOfYear.ToString('000'))"
$timeStamp = Get-Date -Format 'HHmm'
$imageTag = "guideants-webapi-ui:${julianDay}.${timeStamp}"

Write-Host "============================================" -ForegroundColor Cyan
Write-Host "  Building GuideAnts API + Browser UI" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan
Write-Host "Image tag: $imageTag"
Write-Host "No cache:  $NoCache"
Write-Host "App cache: $UseAppBuildCache"
Write-Host "Recreate: $(-not $NoRecreate)"
Write-Host ""

# Build browser UI on host so Vite can consume local .env files.
if (-not (Test-Path (Join-Path $clientRoot 'package.json'))) {
    Write-Error "Client package.json not found at $(Join-Path $clientRoot 'package.json')"
    exit 1
}

Write-Host "Building browser UI locally (src/client)..." -ForegroundColor Cyan
Push-Location $clientRoot
try {
    if (-not (Test-Path $clientNodeModules)) {
        Write-Host "Installing client dependencies (npm ci)..." -ForegroundColor Cyan
        npm ci
        if ($LASTEXITCODE -ne 0) {
            Write-Error "npm ci failed with exit code $LASTEXITCODE"
            exit 1
        }
    }

    npm run browser:build:docker
    if ($LASTEXITCODE -ne 0) {
        Write-Error "npm run browser:build:docker failed with exit code $LASTEXITCODE"
        exit 1
    }
}
finally {
    Pop-Location
}

if (-not (Test-Path $clientDistBrowser)) {
    Write-Error "Expected browser build output was not found at $clientDistBrowser"
    exit 1
}

Write-Host "Browser UI build complete: $clientDistBrowser" -ForegroundColor Green
Write-Host ""

$dockerArgs = @('build')
if ($NoCache) {
    $dockerArgs += '--no-cache'
}
elseif (-not $UseAppBuildCache) {
    # Rebuild API stage each run so packaging is deterministic.
    $dockerArgs += @('--no-cache-filter', 'api-build')
}

$dockerArgs += @(
    '-t', $imageTag,
    '-f', $dockerfilePath,
    $buildContext
)

docker @dockerArgs
if ($LASTEXITCODE -ne 0) {
    Write-Error "Image build failed with exit code $LASTEXITCODE"
    exit 1
}

$envFile = Join-Path $dockerRoot '.env'
$envLine = "GA_WEBAPI_UI_IMAGE=$imageTag"

if (Test-Path $envFile) {
    $raw = Get-Content -Path $envFile -Raw
    $entries = [ordered]@{}

    foreach ($line in ($raw -split "`r?`n")) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        if ($line -match '^\s*#') { continue }
        if ($line -match '^\s*([A-Za-z_][A-Za-z0-9_]*)=(.*)$') {
            $entries[$matches[1]] = $matches[2]
        }
    }

    # Recover malformed concatenated GA_* entries (e.g. GA_AI_IMAGE=...GA_WEBAPI_UI_IMAGE=...).
    $compactRaw = $raw -replace '\s', ''
    $gaMatches = [regex]::Matches($compactRaw, '(?<key>GA_[A-Z0-9_]*)=(?<value>.*?)(?=(?:GA_[A-Z0-9_]*=)|$)')
    foreach ($match in $gaMatches) {
        $key = $match.Groups['key'].Value
        $value = $match.Groups['value'].Value
        if ($key) {
            $entries[$key] = $value
        }
    }

    $entries['GA_WEBAPI_UI_IMAGE'] = $imageTag
    $lines = @($entries.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" })
    Set-Content -Path $envFile -Value (($lines -join "`r`n") + "`r`n") -Encoding UTF8
}
else {
    Set-Content -Path $envFile -Value ($envLine + "`r`n") -Encoding UTF8
}

$envRawAfterWrite = Get-Content -Path $envFile -Raw
$envEntriesAfterWrite = @{}
foreach ($line in ($envRawAfterWrite -split "`r?`n")) {
    if ([string]::IsNullOrWhiteSpace($line)) { continue }
    if ($line -match '^\s*#') { continue }
    if ($line -match '^\s*([A-Za-z_][A-Za-z0-9_]*)=(.*)$') {
        $envEntriesAfterWrite[$matches[1]] = $matches[2]
    }
}

if (-not $envEntriesAfterWrite.ContainsKey('GA_WEBAPI_UI_IMAGE') -or $envEntriesAfterWrite['GA_WEBAPI_UI_IMAGE'] -ne $imageTag) {
    Write-Error "Failed to persist GA_WEBAPI_UI_IMAGE=$imageTag to $envFile"
    exit 1
}

Write-Host "Image built: $imageTag" -ForegroundColor Green
Write-Host "Wrote $envLine to $envFile" -ForegroundColor Green
Write-Host ""

$composeFile = Join-Path $dockerRoot 'docker-compose.yml'
$serviceName = 'guideants-webapi-ui'

if (-not $NoRecreate -and (Test-Path $composeFile)) {
    Write-Host "Recreating $serviceName to apply the new image tag..." -ForegroundColor Cyan
    Push-Location $dockerRoot
    try {
        docker compose --profile webapi-ui up -d --no-deps --force-recreate $serviceName
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "Failed to recreate $serviceName (exit code $LASTEXITCODE)."
            Write-Host "Use: docker compose --profile webapi-ui up -d --no-deps --force-recreate $serviceName" -ForegroundColor Yellow
            exit 1
        }
    }
    finally {
        Pop-Location
    }

    Write-Host "Recreated $serviceName with image $imageTag" -ForegroundColor Green
}
elseif ($NoRecreate) {
    Write-Host "Skipping compose service recreate (-NoRecreate)." -ForegroundColor Yellow
    Write-Host "To apply this image to an existing container, run:" -ForegroundColor Yellow
    Write-Host "docker compose --profile webapi-ui up -d --no-deps --force-recreate $serviceName" -ForegroundColor Yellow
}
else {
    Write-Host "docker-compose.yml not found at $composeFile; image was built but not applied to a running service." -ForegroundColor Yellow
}
