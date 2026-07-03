param(
    [string]$ModelsDir,
    [string]$ModelRepository = "ResembleAI/chatterbox",
    [string]$ModelSubdirectory = "chatterbox",
    [string]$TokenizerRepository = "",
    [string]$TokenizerSubdirectory = ""
)

$ErrorActionPreference = "Stop"

function Get-RepoFiles {
    param([string]$Repo)

    $apiUrl = "https://huggingface.co/api/models/$Repo"
    $model = Invoke-RestMethod -Uri $apiUrl
    return @($model.siblings | ForEach-Object { $_.rfilename })
}

function Should-DownloadFile {
    param([string]$FileName)

    $leaf = Split-Path -Leaf $FileName
    if ($leaf -in @(".gitattributes", "README.md", "LICENSE", "LICENSE.txt")) {
        return $false
    }

    $extension = [System.IO.Path]::GetExtension($leaf).ToLowerInvariant()
    if ($extension -in @(".md", ".png", ".jpg", ".jpeg", ".gif", ".webp")) {
        return $false
    }

    return $true
}

function Download-FileIfMissing {
    param(
        [string]$Repository,
        [string]$FileName,
        [string]$Destination
    )

    if (Test-Path $Destination) {
        Write-Host "Already exists: $Destination"
        return
    }

    $escapedPath = (($FileName -split "/") | ForEach-Object { [Uri]::EscapeDataString($_) }) -join "/"
    $url = "https://huggingface.co/{0}/resolve/main/{1}?download=true" -f $Repository, $escapedPath
    Write-Host "Downloading $Repository/$FileName ..."

    $destinationDir = Split-Path -Parent $Destination
    if (-not (Test-Path $destinationDir)) {
        New-Item -ItemType Directory -Path $destinationDir -Force | Out-Null
    }

    $curlArgs = @("-fL", "--retry", "5", "--retry-delay", "2", "-o", $Destination, $url)
    if ($env:HF_TOKEN) {
        $curlArgs = @("-H", "Authorization: Bearer $($env:HF_TOKEN)") + $curlArgs
    }

    & curl.exe @curlArgs
    if ($LASTEXITCODE -ne 0) {
        throw "curl download failed with exit code $LASTEXITCODE for $url"
    }

    Write-Host "Saved to $Destination"
}

function Download-RepositorySnapshot {
    param(
        [string]$Repository,
        [string]$DestinationRoot
    )

    $files = Get-RepoFiles -Repo $Repository
    $downloadable = $files | Where-Object { Should-DownloadFile $_ }

    foreach ($file in $downloadable) {
        $destination = Join-Path $DestinationRoot ($file -replace "/", [System.IO.Path]::DirectorySeparatorChar)
        Download-FileIfMissing -Repository $Repository -FileName $file -Destination $destination
    }
}

$dockerRoot = Split-Path -Parent $PSScriptRoot
$repoDockerRoot = Split-Path -Parent $dockerRoot
$defaultModelsDir = Join-Path $repoDockerRoot "volumes\tts\models"
$modelRoot = if ([string]::IsNullOrWhiteSpace($ModelsDir)) { $defaultModelsDir } else { $ModelsDir }

if (-not (Test-Path $modelRoot)) {
    New-Item -ItemType Directory -Path $modelRoot -Force | Out-Null
}

$modelDestination = Join-Path $modelRoot $ModelSubdirectory

Download-RepositorySnapshot -Repository $ModelRepository -DestinationRoot $modelDestination
if (-not [string]::IsNullOrWhiteSpace($TokenizerRepository)) {
    $tokenizerDestination = Join-Path $modelRoot $TokenizerSubdirectory
    Download-RepositorySnapshot -Repository $TokenizerRepository -DestinationRoot $tokenizerDestination
}

Write-Host ""
Write-Host "TTS model assets ready at: $modelRoot" -ForegroundColor Green
