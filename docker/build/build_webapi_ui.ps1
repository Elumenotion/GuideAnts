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

function Get-DockerContainerLabels {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ContainerName
    )

    $labelJson = docker inspect -f '{{json .Config.Labels}}' $ContainerName 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($labelJson)) {
        return $null
    }

    return ($labelJson | ConvertFrom-Json)
}

function Read-InstallerStateComposeFiles {
    param(
        [Parameter(Mandatory = $true)]
        [string]$StateFilePath,

        [Parameter(Mandatory = $true)]
        [string]$DockerRoot
    )

    if (-not (Test-Path $StateFilePath)) {
        return @()
    }

    $composeFile = $null
    $overrideFile = $null
    $dockerDirectory = 'docker'

    foreach ($line in Get-Content -LiteralPath $StateFilePath) {
        $trimmed = $line.Trim()
        if ([string]::IsNullOrWhiteSpace($trimmed) -or $trimmed.StartsWith('#')) {
            continue
        }

        $separatorIndex = $trimmed.IndexOf('=')
        if ($separatorIndex -lt 1) {
            continue
        }

        $key = $trimmed.Substring(0, $separatorIndex).Trim()
        $value = $trimmed.Substring($separatorIndex + 1).Trim()
        switch ($key) {
            'COMPOSE_FILE' { $composeFile = $value }
            'HOST_MOUNT_OVERRIDE_FILE' { $overrideFile = $value }
            'DOCKER_DIRECTORY' { $dockerDirectory = $value }
        }
    }

    if ([string]::IsNullOrWhiteSpace($composeFile)) {
        return @()
    }

    $composeRoot = if ([System.IO.Path]::IsPathRooted($dockerDirectory)) {
        $dockerDirectory
    }
    else {
        Join-Path (Split-Path $StateFilePath -Parent) $dockerDirectory
    }

    $paths = @((Join-Path $composeRoot $composeFile))
    if (-not [string]::IsNullOrWhiteSpace($overrideFile)) {
        $paths += (Join-Path $composeRoot $overrideFile)
    }

    return $paths
}

function Resolve-ExistingComposeConfigFiles {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$ConfigFilePaths,

        [Parameter(Mandatory = $true)]
        [string]$DockerRoot
    )

    $resolved = @()
    foreach ($configFile in $ConfigFilePaths) {
        $path = if ([System.IO.Path]::IsPathRooted($configFile)) {
            $configFile
        }
        else {
            Join-Path $DockerRoot $configFile
        }

        if (Test-Path $path) {
            $resolved += $path
        }
        else {
            Write-Warning "Skipping missing compose file '$path'."
        }
    }

    return $resolved
}

function Get-ComposeContextForContainer {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ContainerName,

        [Parameter(Mandatory = $true)]
        [string]$DockerRoot,

        [Parameter(Mandatory = $true)]
        [string]$RepoRoot
    )

    $projectName = 'guideants'
    $configPaths = @()
    $labels = Get-DockerContainerLabels -ContainerName $ContainerName

    if ($null -ne $labels) {
        if ($labels.'com.docker.compose.project') {
            $projectName = [string]$labels.'com.docker.compose.project'
        }

        $configFilesLabel = $labels.'com.docker.compose.project.config_files'
        if ($configFilesLabel) {
            $configPaths = @([string]$configFilesLabel -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
        }
    }

    if ($configPaths.Count -eq 0) {
        $composeJson = docker compose ls --format json
        if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($composeJson)) {
            $parsed = $composeJson | ConvertFrom-Json
            $projects = if ($parsed -is [System.Array]) { @($parsed) } else { @($parsed) }
            $project = $projects | Where-Object { $_.Name -eq $projectName } | Select-Object -First 1
            if ($null -ne $project -and $project.ConfigFiles) {
                $configPaths = @([string]$project.ConfigFiles -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
            }
        }
    }

    if ($configPaths.Count -eq 0) {
        $configPaths = Read-InstallerStateComposeFiles -StateFilePath (Join-Path $RepoRoot '.installer_state.env') -DockerRoot $dockerRoot
    }

    $resolved = Resolve-ExistingComposeConfigFiles -ConfigFilePaths $configPaths -DockerRoot $DockerRoot
    if ($resolved.Count -eq 0) {
        throw "Could not resolve any compose config files for container '$ContainerName'. Start the stack first or pass -NoRecreate."
    }

    return @{
        ProjectName = $projectName
        ConfigFiles = $resolved
    }
}

function New-ImageOverrideComposeFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$DockerRoot,

        [Parameter(Mandatory = $true)]
        [string]$ServiceName,

        [Parameter(Mandatory = $true)]
        [string]$ImageTag
    )

    $content = @"
services:
  ${ServiceName}:
    image: ${ImageTag}
    pull_policy: never
"@
    $utf8NoBom = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText($overridePath, $content, $utf8NoBom)
    return $overridePath
}

function Invoke-RecreateComposeServiceWithImage {
    param(
        [Parameter(Mandatory = $true)]
        [string]$DockerRoot,

        [Parameter(Mandatory = $true)]
        [hashtable]$ComposeContext,

        [Parameter(Mandatory = $true)]
        [string]$ServiceName,

        [Parameter(Mandatory = $true)]
        [string]$ImageTag
    )

    $overridePath = New-ImageOverrideComposeFile -DockerRoot $DockerRoot -ServiceName $ServiceName -ImageTag $ImageTag
    Push-Location $DockerRoot
    try {
        $composeArgs = @('compose', '-p', $ComposeContext.ProjectName)
        foreach ($configFile in $ComposeContext.ConfigFiles) {
            $composeArgs += @('-f', $configFile)
        }
        $composeArgs += @('-f', $overridePath, 'up', '-d', '--no-deps', '--force-recreate', '--pull', 'never', $ServiceName)
        docker @composeArgs
        return $LASTEXITCODE
    }
    finally {
        Pop-Location
        Remove-Item -LiteralPath $overridePath -ErrorAction SilentlyContinue
    }
}

function Test-DockerContainerExists {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ContainerName
    )

    docker inspect $ContainerName 2>$null | Out-Null
    return $LASTEXITCODE -eq 0
}

function Set-DotEnvValue {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Key,

        [Parameter(Mandatory = $true)]
        [string]$Value
    )

    $envLine = "$Key=$Value"
    $lines = @()
    if (Test-Path $Path) {
        $lines = @(Get-Content -Path $Path)
    }

    $replaced = $false
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match "^\s*$([regex]::Escape($Key))=") {
            $lines[$i] = $envLine
            $replaced = $true
            break
        }
    }

    if (-not $replaced) {
        $lines += $envLine
    }

    if ($PSVersionTable.PSVersion.Major -ge 6) {
        Set-Content -Path $Path -Value $lines -Encoding utf8NoBOM
    }
    else {
        $utf8NoBom = [System.Text.UTF8Encoding]::new($false)
        [System.IO.File]::WriteAllLines($Path, [string[]]$lines, $utf8NoBom)
    }
}

switch ($Flavor) {
    'Slim' {
        $dockerTarget = 'runtime-slim'
        $imageRepository = 'guideants-webapi-ui-slim'
        $imageEnvKey = 'GA_WEBAPI_UI_SLIM_IMAGE'
        $composeFileName = $null
        $serviceName = 'guideants-webapi-ui-slim'
        $useComposeFile = $false
    }
    'Mssql' {
        $dockerTarget = 'runtime-mssql'
        $imageRepository = 'guideants-webapi-ui-mssql'
        $imageEnvKey = 'GA_WEBAPI_UI_MSSQL_IMAGE'
        $composeFileName = 'docker-compose.mssql.yml'
        $serviceName = 'guideants-webapi-ui-mssql'
        $containerName = 'guideants-webapi-ui-mssql'
        $useComposeFile = $true
    }
    default {
        $dockerTarget = 'runtime'
        $imageRepository = 'guideants-webapi-ui'
        $imageEnvKey = 'GA_WEBAPI_UI_IMAGE'
        $serviceName = 'guideants-webapi-ui'
        $containerName = 'guideants-webapi-ui'
        $composeFileName = $null
        $useRunningComposeStack = $true
    }
}

if (-not (Test-Path $dockerfilePath)) {
    Write-Error "Dockerfile not found at $dockerfilePath"
    exit 1
}

# Build a unique tag per build, and also maintain a stable latest tag.
$julianDay = "$(Get-Date -Format 'yy')$((Get-Date).DayOfYear.ToString('000'))"
$timeStamp = Get-Date -Format 'HHmm'
$imageTag = "${imageRepository}:${julianDay}.${timeStamp}"
$latestImageTag = "${imageRepository}:latest"

Write-Host "============================================" -ForegroundColor Cyan
Write-Host "  Building GuideAnts API + Browser UI ($Flavor)" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan
Write-Host "Image tag: $imageTag"
Write-Host "Latest alias: $latestImageTag"
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
    '-t', $latestImageTag,
    '-f', $dockerfilePath,
    $buildContext
)

docker @dockerArgs
if ($LASTEXITCODE -ne 0) {
    Write-Error "Image build failed with exit code $LASTEXITCODE"
    exit 1
}

$envFile = Join-Path $dockerRoot '.env'
$envLine = "${imageEnvKey}=$latestImageTag"
Set-DotEnvValue -Path $envFile -Key $imageEnvKey -Value $latestImageTag

$envRawAfterWrite = Get-Content -Path $envFile -Raw
$envEntriesAfterWrite = @{}
foreach ($line in ($envRawAfterWrite -split "`r?`n")) {
    if ([string]::IsNullOrWhiteSpace($line)) { continue }
    if ($line -match '^\s*#') { continue }
    if ($line -match '^\s*([A-Za-z_][A-Za-z0-9_]*)=(.*)$') {
        $envEntriesAfterWrite[$matches[1]] = $matches[2]
    }
}

if (-not $envEntriesAfterWrite.ContainsKey($imageEnvKey) -or $envEntriesAfterWrite[$imageEnvKey] -ne $latestImageTag) {
    Write-Error "Failed to persist ${imageEnvKey}=$latestImageTag to $envFile"
    exit 1
}

Write-Host "Image built: $imageTag" -ForegroundColor Green
Write-Host "Wrote $envLine to $envFile" -ForegroundColor Green
Write-Host ""

$composeFile = if ($composeFileName) { Join-Path $dockerRoot $composeFileName } else { $null }

if (-not $NoRecreate -and ($useRunningComposeStack -or ($composeFile -and (Test-Path $composeFile)))) {
    Write-Host "Recreating $containerName to apply image $latestImageTag..." -ForegroundColor Cyan
    try {
        if ($useRunningComposeStack) {
            if (-not (Test-DockerContainerExists -ContainerName $containerName)) {
                throw "Container '$containerName' was not found. Start the stack before rebuilding with recreate enabled, or pass -NoRecreate."
            }

            $composeContext = Get-ComposeContextForContainer -ContainerName $containerName -DockerRoot $dockerRoot -RepoRoot $repoRoot
            $exitCode = Invoke-RecreateComposeServiceWithImage `
                -DockerRoot $dockerRoot `
                -ComposeContext $composeContext `
                -ServiceName $serviceName `
                -ImageTag $latestImageTag
        }
        else {
            $composeContext = @{
                ProjectName = 'guideants'
                ConfigFiles = @((Resolve-Path $composeFile).Path)
            }
            $exitCode = Invoke-RecreateComposeServiceWithImage `
                -DockerRoot $dockerRoot `
                -ComposeContext $composeContext `
                -ServiceName $serviceName `
                -ImageTag $latestImageTag
        }

        if ($exitCode -ne 0) {
            throw "docker compose recreate exited with code $exitCode"
        }
    }
    catch {
        Write-Warning "Failed to recreate $containerName ($($_.Exception.Message))"
        Write-Host "Use: pass -NoRecreate to build only, or ensure container '$containerName' exists and compose config files are reachable." -ForegroundColor Yellow
        exit 1
    }

    Write-Host "Recreated $containerName with image $latestImageTag" -ForegroundColor Green
}
elseif ($NoRecreate) {
    Write-Host "Skipping compose service recreate (-NoRecreate)." -ForegroundColor Yellow
    Write-Host "To apply this image to an existing container, run:" -ForegroundColor Yellow
    if ($useRunningComposeStack) {
        Write-Host "Rebuild then recreate container guideants-webapi-ui (or pass -NoRecreate)." -ForegroundColor Yellow
    }
    elseif ($useComposeFile) {
        Write-Host "docker compose -f $composeFileName up -d --no-deps --force-recreate $serviceName" -ForegroundColor Yellow
    }
}
else {
    Write-Host "Image was built but not applied to a compose service. The standalone guideants-webapi-ui-slim image is orthogonal to docker-compose.slim.yml." -ForegroundColor Yellow
}
