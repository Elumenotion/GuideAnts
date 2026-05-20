param(
    [ValidateSet('Full', 'Slim', 'Mssql')]
    [string]$Flavor = 'Full',
    [switch]$NoCache,
    [switch]$NoRecreate,
    [switch]$UseAppBuildCache,
    [switch]$NoAppBuildCache
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$dockerRoot = Split-Path $PSScriptRoot -Parent
$buildContext = $repoRoot
$dockerfilePath = Join-Path $PSScriptRoot 'webapi-ui\Dockerfile'
$clientRoot = Join-Path $repoRoot 'src\client'
$clientNodeModules = Join-Path $clientRoot 'node_modules'
$clientDistBrowser = Join-Path $clientRoot 'dist-browser'

function Get-RunningComposeFileArgs {
    param(
        [Parameter(Mandatory = $true)]
        [string]$DockerRoot,

        [string]$ProjectName = 'guideants'
    )

    $composeJson = docker compose ls --format json
    if ($LASTEXITCODE -ne 0) {
        throw "docker compose ls failed with exit code $LASTEXITCODE"
    }

    $projects = @()
    if (-not [string]::IsNullOrWhiteSpace($composeJson)) {
        $parsed = $composeJson | ConvertFrom-Json
        if ($parsed -is [System.Array]) {
            $projects = @($parsed)
        }
        elseif ($null -ne $parsed) {
            $projects = @($parsed)
        }
    }

    $project = $projects | Where-Object { $_.Name -eq $ProjectName -and $_.Status -match '^running' } | Select-Object -First 1
    if ($null -eq $project) {
        throw "No running Docker Compose project named '$ProjectName' was found. Start the stack before rebuilding with recreate enabled, or pass -NoRecreate."
    }

    $configFiles = @()
    if ($project.ConfigFiles) {
        $configFiles = @($project.ConfigFiles -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    }

    if ($configFiles.Count -eq 0) {
        throw "Running Docker Compose project '$ProjectName' did not report any config files."
    }

    $args = @()
    foreach ($configFile in $configFiles) {
        $resolved = if ([System.IO.Path]::IsPathRooted($configFile)) {
            $configFile
        }
        else {
            Join-Path $DockerRoot $configFile
        }

        if (-not (Test-Path $resolved)) {
            Write-Warning "Running compose project references missing config file '$resolved'; skipping it."
            continue
        }

        $args += @('-f', $resolved)
    }

    if ($args.Count -eq 0) {
        throw "None of the config files for running Docker Compose project '$ProjectName' exist on disk."
    }

    return $args
}

switch ($Flavor) {
    'Slim' {
        $dockerTarget = 'runtime-slim'
        $imageRepository = 'guideants-webapi-ui-slim'
        $imageEnvKey = 'GA_WEBAPI_UI_SLIM_IMAGE'
        $composeFileName = 'docker-compose.slim.yml'
        $serviceName = 'guideants-webapi-ui-slim'
        $useComposeFile = $true
    }
    'Mssql' {
        $dockerTarget = 'runtime-mssql'
        $imageRepository = 'guideants-webapi-ui-mssql'
        $imageEnvKey = 'GA_WEBAPI_UI_MSSQL_IMAGE'
        $composeFileName = 'docker-compose.mssql.yml'
        $serviceName = 'guideants-webapi-ui-mssql'
        $useComposeFile = $true
    }
    default {
        $dockerTarget = 'runtime'
        $imageRepository = 'guideants-webapi-ui'
        $imageEnvKey = 'GA_WEBAPI_UI_IMAGE'
        $serviceName = 'guideants-webapi-ui'
        $composeFileName = $null
        $useRunningComposeStack = $true
    }
}

if (-not (Test-Path $dockerfilePath)) {
    Write-Error "Dockerfile not found at $dockerfilePath"
    exit 1
}

# Julian date (2-digit year + day-of-year) + time tag: e.g. 26099.1530
$julianDay = "$(Get-Date -Format 'yy')$((Get-Date).DayOfYear.ToString('000'))"
$timeStamp = Get-Date -Format 'HHmm'
$imageTag = "${imageRepository}:${julianDay}.${timeStamp}"

Write-Host "============================================" -ForegroundColor Cyan
Write-Host "  Building GuideAnts API + Browser UI ($Flavor)" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan
Write-Host "Image tag: $imageTag"
Write-Host "Target:    $dockerTarget"
Write-Host "No cache:  $NoCache"
$appBuildCacheEnabled = $true
if ($NoAppBuildCache) {
    $appBuildCacheEnabled = $false
}
if ($UseAppBuildCache) {
    $appBuildCacheEnabled = $true
}
Write-Host "App cache: $appBuildCacheEnabled"
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
elseif (-not $appBuildCacheEnabled) {
    # Rebuild API stage each run so packaging is deterministic.
    $dockerArgs += @('--no-cache-filter', 'api-build')
}

$dockerArgs += @(
    '--target', $dockerTarget,
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
$envLine = "${imageEnvKey}=$imageTag"

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

    $entries[$imageEnvKey] = $imageTag
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

if (-not $envEntriesAfterWrite.ContainsKey($imageEnvKey) -or $envEntriesAfterWrite[$imageEnvKey] -ne $imageTag) {
    Write-Error "Failed to persist ${imageEnvKey}=$imageTag to $envFile"
    exit 1
}

Write-Host "Image built: $imageTag" -ForegroundColor Green
Write-Host "Wrote $envLine to $envFile" -ForegroundColor Green
Write-Host ""

$composeFile = if ($composeFileName) { Join-Path $dockerRoot $composeFileName } else { $null }

if (-not $NoRecreate -and ($useRunningComposeStack -or (Test-Path $composeFile))) {
    Write-Host "Recreating $serviceName to apply the new image tag..." -ForegroundColor Cyan
    Push-Location $dockerRoot
    try {
        $composeArgs = @('compose')
        if ($useRunningComposeStack) {
            $composeArgs += Get-RunningComposeFileArgs -DockerRoot $dockerRoot
        }
        elseif ($useComposeFile) {
            $composeArgs += @('-f', $composeFileName)
        }
        $composeArgs += @('up', '-d', '--no-deps', '--force-recreate', $serviceName)
        docker @composeArgs
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "Failed to recreate $serviceName (exit code $LASTEXITCODE)."
            if ($useRunningComposeStack) {
                Write-Host "Use: rerun this script after confirming the 'guideants' compose stack is running and its config files exist." -ForegroundColor Yellow
            }
            elseif ($useComposeFile) {
                Write-Host "Use: docker compose -f $composeFileName up -d --no-deps --force-recreate $serviceName" -ForegroundColor Yellow
            }
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
    if ($useRunningComposeStack) {
        Write-Host "docker compose <running stack config files> up -d --no-deps --force-recreate $serviceName" -ForegroundColor Yellow
    }
    elseif ($useComposeFile) {
        Write-Host "docker compose -f $composeFileName up -d --no-deps --force-recreate $serviceName" -ForegroundColor Yellow
    }
}
else {
    Write-Host "$composeFileName not found at $composeFile; image was built but not applied to a running service." -ForegroundColor Yellow
}
