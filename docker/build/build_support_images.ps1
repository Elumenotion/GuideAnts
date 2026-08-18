param(
    [switch]$RebuildBase
)

$ErrorActionPreference = 'Stop'
$env:DOCKER_BUILDKIT = '1'

. (Join-Path $PSScriptRoot 'lib\combined-hash.ps1')

function Test-DockerImageExists {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ImageTag
    )

    $matches = docker image ls --format '{{.Repository}}:{{.Tag}}' $ImageTag 2>$null
    if ($LASTEXITCODE -ne 0) {
        return $false
    }

    return $matches -contains $ImageTag
}

function Get-HashFromFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (-not (Test-Path $Path)) {
        return $null
    }

    return (Get-Content -Path $Path -Raw).Trim()
}

function Get-FilePathsRecursive {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root
    )

    if (-not (Test-Path $Root)) {
        return @()
    }

    return @(
        Get-ChildItem -Path $Root -Recurse -File |
            Sort-Object FullName |
            Select-Object -ExpandProperty FullName
    )
}

function Build-ScriptExecutionAgent {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ServerPath,

        [Parameter(Mandatory = $true)]
        [string]$RepoRoot
    )

    $scriptAgentProject = Join-Path $ServerPath 'ScriptExecutionAgent'
    if (-not (Test-Path $scriptAgentProject)) {
        Write-Error "ScriptExecutionAgent directory not found at $scriptAgentProject"
        exit 1
    }

    $publishOutput = Join-Path $scriptAgentProject 'publish'
    $scriptAgentHashFile = Join-Path $publishOutput '.source-hash'
    $scriptAgentSourceFiles = Get-ChildItem -Path $scriptAgentProject -Recurse -File |
        Where-Object {
            $_.FullName -notlike "*\bin\*" -and
            $_.FullName -notlike "*\obj\*" -and
            $_.FullName -notlike "*\publish\*"
        } |
        Sort-Object FullName |
        Select-Object -ExpandProperty FullName

    $scriptAgentSourceHash = Get-CombinedHash -Paths $scriptAgentSourceFiles -RelativeTo $RepoRoot
    $canReusePublish = $false
    if ((Test-Path $publishOutput) -and (Test-Path $scriptAgentHashFile)) {
        $previousHash = (Get-Content -Path $scriptAgentHashFile -Raw).Trim()
        if ($previousHash -eq $scriptAgentSourceHash) {
            $canReusePublish = $true
        }
    }

    if ($canReusePublish) {
        Write-Host "ScriptExecutionAgent unchanged; reusing existing publish output." -ForegroundColor Green
        return $publishOutput
    }

    if (Test-Path $publishOutput) {
        Remove-Item -Path $publishOutput -Recurse -Force
    }

    Push-Location $scriptAgentProject
    try {
        $null = dotnet restore
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet restore failed with exit code $LASTEXITCODE"
        }

        $null = dotnet publish -c Release -o ./publish
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet publish failed with exit code $LASTEXITCODE"
        }

        Set-Content -Path $scriptAgentHashFile -Value $scriptAgentSourceHash -Encoding UTF8
        Write-Host "ScriptExecutionAgent built successfully." -ForegroundColor Green
    }
    catch {
        Write-Error "Failed to build ScriptExecutionAgent: $($_.Exception.Message)"
        exit 1
    }
    finally {
        Pop-Location
    }

    return $publishOutput
}

$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$dockerRoot = Split-Path $PSScriptRoot -Parent
$serverPath = Join-Path $repoRoot 'src\server'
$buildStateDir = Join-Path $dockerRoot '.build-state'

if (-not (Test-Path $buildStateDir)) {
    New-Item -ItemType Directory -Path $buildStateDir | Out-Null
}

$dockerBuildArgs = @()
if ($RebuildBase) {
    $dockerBuildArgs += '--no-cache'
}

Write-Host "============================================" -ForegroundColor Cyan
Write-Host "  Building GuideAnts Support Images" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan
Write-Host "Rebuild base:  $RebuildBase"
Write-Host ""

$scriptAgentPublish = Build-ScriptExecutionAgent -ServerPath $serverPath -RepoRoot $repoRoot

$plantumlContainerPath = Join-Path $PSScriptRoot "Sandboxes" "PlantUml"
$plantumlScriptAgentPath = Join-Path $plantumlContainerPath "ScriptExecutionAgent"
$plantumlDockerfilePath = Join-Path $plantumlContainerPath "dockerfile"
$plantumlImageTag = "plantuml-1.2025.2"
$plantumlHashFile = Join-Path $buildStateDir "plantuml.hash"

$plantumlInputFiles = @($plantumlDockerfilePath) + (Get-FilePathsRecursive -Root $scriptAgentPublish)
$plantumlHash = Get-CombinedHash -Paths $plantumlInputFiles -RelativeTo $repoRoot
$plantumlCanReuse =
    (-not $RebuildBase) -and
    (Test-DockerImageExists -ImageTag $plantumlImageTag) -and
    ((Get-HashFromFile -Path $plantumlHashFile) -eq $plantumlHash)

if ($plantumlCanReuse) {
    Write-Host "PlantUML unchanged; reusing existing image: $plantumlImageTag" -ForegroundColor Green
}
else {
    if ((Test-Path $scriptAgentPublish) -and (Test-Path $plantumlContainerPath)) {
        if (Test-Path $plantumlScriptAgentPath) {
            Remove-Item -Path $plantumlScriptAgentPath -Recurse -Force
        }
        Copy-Item -Path $scriptAgentPublish -Destination $plantumlScriptAgentPath -Recurse -Force
        Write-Host "Copied ScriptExecutionAgent to PlantUML container directory" -ForegroundColor Green
    }

    docker build @dockerBuildArgs -t $plantumlImageTag -f $plantumlDockerfilePath $plantumlContainerPath
    if ($LASTEXITCODE -ne 0) {
        Write-Error "PlantUML image build failed with exit code $LASTEXITCODE"
        exit 1
    }
    Set-Content -Path $plantumlHashFile -Value $plantumlHash -Encoding UTF8
}

$mssqlBuildContext = Join-Path $PSScriptRoot "mssql-fts"
$mssqlDockerfilePath = Join-Path $mssqlBuildContext "Dockerfile"
$mssqlImageTag = "mssql2025-express-fts"
$mssqlHashFile = Join-Path $buildStateDir "mssql-fts.hash"
if (-not (Test-Path $mssqlDockerfilePath)) {
    Write-Error "MSSQL Dockerfile not found at $mssqlDockerfilePath"
    exit 1
}
$mssqlHash = Get-CombinedHash -Paths (Get-FilePathsRecursive -Root $mssqlBuildContext) -RelativeTo $repoRoot
$mssqlCanReuse =
    (-not $RebuildBase) -and
    (Test-DockerImageExists -ImageTag $mssqlImageTag) -and
    ((Get-HashFromFile -Path $mssqlHashFile) -eq $mssqlHash)
if ($mssqlCanReuse) {
    Write-Host "MSSQL unchanged; reusing existing image: $mssqlImageTag" -ForegroundColor Green
}
else {
    Write-Host "Building mssql image: $mssqlImageTag"
    docker build @dockerBuildArgs -t $mssqlImageTag -f $mssqlDockerfilePath --build-arg MSSQL_PID=Express $mssqlBuildContext
    if ($LASTEXITCODE -ne 0) {
        Write-Error "MSSQL image build failed with exit code $LASTEXITCODE"
        exit 1
    }
    Set-Content -Path $mssqlHashFile -Value $mssqlHash -Encoding UTF8
}

$searxngDockerfilePath = Join-Path $PSScriptRoot "searxng\Dockerfile"
$searxngBuildContext = $repoRoot
$searxngImageTag = "guideants-searxng:latest"
$searxngHashFile = Join-Path $buildStateDir "searxng.hash"
if (-not (Test-Path $searxngDockerfilePath)) {
    Write-Error "SearXNG Dockerfile not found at $searxngDockerfilePath"
    exit 1
}
$searxngInputFiles = @($searxngDockerfilePath) + (Get-FilePathsRecursive -Root (Join-Path $PSScriptRoot "searxng"))
$searxngHash = Get-CombinedHash -Paths $searxngInputFiles -RelativeTo $repoRoot
$searxngCanReuse =
    (-not $RebuildBase) -and
    (Test-DockerImageExists -ImageTag $searxngImageTag) -and
    ((Get-HashFromFile -Path $searxngHashFile) -eq $searxngHash)
if ($searxngCanReuse) {
    Write-Host "SearXNG unchanged; reusing existing image: $searxngImageTag" -ForegroundColor Green
}
else {
    Write-Host "Building searxng image: $searxngImageTag"
    docker build @dockerBuildArgs -t $searxngImageTag -f $searxngDockerfilePath $searxngBuildContext
    if ($LASTEXITCODE -ne 0) {
        Write-Error "SearXNG image build failed with exit code $LASTEXITCODE"
        exit 1
    }
    Set-Content -Path $searxngHashFile -Value $searxngHash -Encoding UTF8
}

$webApiUiBuildScript = Join-Path $PSScriptRoot "build_webapi_ui.ps1"
$webApiUiHashFile = Join-Path $buildStateDir "webapi-ui.hash"
$envFile = Join-Path $dockerRoot '.env'
if (-not (Test-Path $webApiUiBuildScript)) {
    Write-Error "WebAPI+UI build script not found at $webApiUiBuildScript"
    exit 1
}

$webApiUiInputs = @(
    $webApiUiBuildScript,
    (Join-Path $PSScriptRoot "webapi-ui\Dockerfile")
) + (Get-FilePathsRecursive -Root (Join-Path $repoRoot "src\client")) + (Get-FilePathsRecursive -Root (Join-Path $repoRoot "src\server"))
$webApiUiHash = Get-CombinedHash -Paths $webApiUiInputs -RelativeTo $repoRoot

$existingWebApiUiImage = $null
if (Test-Path $envFile) {
    $line = Get-Content -Path $envFile | Where-Object { $_ -match '^GA_WEBAPI_UI_IMAGE=' } | Select-Object -First 1
    if ($line) {
        $existingWebApiUiImage = ($line -split '=', 2)[1].Trim()
    }
}
$webApiUiCanReuse =
    (-not $RebuildBase) -and
    (-not [string]::IsNullOrWhiteSpace($existingWebApiUiImage)) -and
    (Test-DockerImageExists -ImageTag $existingWebApiUiImage) -and
    ((Get-HashFromFile -Path $webApiUiHashFile) -eq $webApiUiHash)

if ($webApiUiCanReuse) {
    Write-Host "WebAPI+UI unchanged; reusing existing image: $existingWebApiUiImage" -ForegroundColor Green
}
else {
    Write-Host "Building WebAPI+UI image via build_webapi_ui.ps1" -ForegroundColor Cyan
    if ($RebuildBase) {
        & $webApiUiBuildScript -NoCache -NoRecreate
    }
    else {
        & $webApiUiBuildScript -NoRecreate
    }

    if ($LASTEXITCODE -ne 0) {
        Write-Error "WebAPI+UI image build failed with exit code $LASTEXITCODE"
        exit 1
    }

    Set-Content -Path $webApiUiHashFile -Value $webApiUiHash -Encoding UTF8
}

Write-Host ""
Write-Host "============================================" -ForegroundColor Cyan
Write-Host "  Support image build complete" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan
