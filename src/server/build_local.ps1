<#
.SYNOPSIS
    Builds the combined GuideAnts server + browser UI folder locally, without Docker.

.DESCRIPTION
    Mirrors the docker/build/webapi-ui/Dockerfile "runtime-base" stage on the host:
      1. (optional) Builds the browser UI -> src/client/dist-browser
      2. dotnet publish GuideAntsApi (Release)                -> <OutDir>
      3. Copies GuideAntsApi/Resources/bootstrap/{assistants,guides} -> <OutDir>/Resources/bootstrap
      4. Copies dist-browser                                   -> <OutDir>/ui

    The result is the same layout as /app in the docker image. Run the API from <OutDir>
    with Ui__RootPath=<OutDir>/ui.

    When invoked via "npm run server:build:local", the UI build is done by npm before
    this script runs (npm is not on the PATH of the pwsh session it spawns). Pass
    -NoUiBuild in that case. Run standalone (without -NoUiBuild) to do everything.

.PARAMETER OutDir
    Output folder. Defaults to <repoRoot>/dist-local. Relative paths resolve against the repo root.

.PARAMETER NoUi
    Skip the browser UI copy entirely (server only).

.PARAMETER NoUiBuild
    Skip the "npm run browser:build:docker" step; expect dist-browser to already exist.
    Used by the npm action, which builds the UI before invoking this script.

.PARAMETER NoServer
    Skip the dotnet publish (UI only, into an existing OutDir).
#>
param(
    [string]$OutDir = '',
    [switch]$NoUi,
    [switch]$NoUiBuild,
    [switch]$NoServer
)

$ErrorActionPreference = 'Stop'

$serverRoot  = $PSScriptRoot
$srcRoot     = Split-Path $serverRoot -Parent
$repoRoot    = Split-Path $srcRoot -Parent
$clientRoot  = Join-Path $repoRoot 'src/client'
$apiProject  = Join-Path $serverRoot 'GuideAntsApi/GuideAntsApi.csproj'
$bootstrap   = Join-Path $serverRoot 'GuideAntsApi/Resources/bootstrap'
$distBrowser = Join-Path $clientRoot 'dist-browser'

if ([string]::IsNullOrWhiteSpace($OutDir)) {
    $OutDir = Join-Path $repoRoot 'dist-local'
}
if (-not [System.IO.Path]::IsPathRooted($OutDir)) {
    $OutDir = Join-Path $repoRoot $OutDir
}

Write-Host "============================================" -ForegroundColor Cyan
Write-Host "  GuideAnts local combined build (no Docker)" -ForegroundColor Cyan
Write-Host "  Output: $OutDir" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan

function Build-Ui {
    Write-Host "Building browser UI (src/client)..." -ForegroundColor Cyan
    Push-Location $clientRoot
    try {
        if (-not (Test-Path (Join-Path $clientRoot 'node_modules'))) {
            Write-Host "Installing client dependencies (npm ci)..." -ForegroundColor Cyan
            npm ci
            if ($LASTEXITCODE -ne 0) { throw "npm ci failed with exit code $LASTEXITCODE" }
        }
        npm run browser:build:docker
        if ($LASTEXITCODE -ne 0) { throw "npm run browser:build:docker failed with exit code $LASTEXITCODE" }
    }
    finally { Pop-Location }

    if (-not (Test-Path $distBrowser)) {
        throw "Expected browser build output at $distBrowser"
    }
    Write-Host "Browser UI build complete: $distBrowser" -ForegroundColor Green
}

function Copy-Ui {
    if (-not (Test-Path $distBrowser)) {
        throw "Expected browser build output at $distBrowser (build the UI first, e.g. 'npm run browser:build:docker' in src/client)"
    }
    $uiDst = Join-Path $OutDir 'ui'
    New-Item -ItemType Directory -Force -Path $uiDst | Out-Null
    Copy-Item -Path (Join-Path $distBrowser '*') -Destination $uiDst -Recurse -Force
    Write-Host "UI copied to: $uiDst" -ForegroundColor Green
}

# 1. Browser UI (skip when the npm action already built it)
if (-not $NoUi -and -not $NoUiBuild) {
    Build-Ui
}

# 2. Server publish
if (-not $NoServer) {
    Write-Host "Publishing GuideAntsApi (Release)..." -ForegroundColor Cyan
    if (Test-Path $OutDir) { Remove-Item $OutDir -Recurse -Force }
    dotnet publish $apiProject -c Release -o $OutDir -p:UseAppHost=false
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

    # 3. Bootstrap resources
    foreach ($res in @('assistants', 'guides')) {
        $src = Join-Path $bootstrap $res
        if (Test-Path $src) {
            $dst = Join-Path $OutDir "Resources/bootstrap/$res"
            New-Item -ItemType Directory -Force -Path $dst | Out-Null
            Copy-Item -Path (Join-Path $src '*') -Destination $dst -Recurse -Force
        }
    }
    Write-Host "Server published to: $OutDir" -ForegroundColor Green
}

# 4. UI -> <OutDir>/ui
if (-not $NoUi) {
    Copy-Ui
}

Write-Host ""
Write-Host "Combined local build complete: $OutDir" -ForegroundColor Green
Write-Host "  server : $OutDir" -ForegroundColor Green
if (-not $NoUi) { Write-Host "  ui     : $(Join-Path $OutDir 'ui')" -ForegroundColor Green }
Write-Host "Run with : dotnet `"$OutDir/GuideAntsApi.dll`"   (set Ui__RootPath=`"$OutDir/ui`")" -ForegroundColor Yellow
