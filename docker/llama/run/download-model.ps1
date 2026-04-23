param(
    [ValidateSet('q6', 'q8')]
    [string]$Quantization = 'q6',
    [string]$Repository = 'unsloth/Qwen3.5-27B-GGUF',
    [switch]$SkipMmproj
)

$ErrorActionPreference = 'Stop'

function Get-RepoFiles {
    param([string]$Repo)

    $apiUrl = "https://huggingface.co/api/models/$Repo"
    $model = Invoke-RestMethod -Uri $apiUrl
    return @($model.siblings | ForEach-Object { $_.rfilename })
}

function Get-PreferredModelFile {
    param(
        [string[]]$Files,
        [string]$Quant
    )

    if ($Quant -eq 'q6') {
        $candidates = @(
            'Qwen3.5-27B-Q6_K.gguf',
            'Qwen3.5-27B-UD-Q6_K_XL.gguf'
        )
    }
    else {
        $candidates = @(
            'Qwen3.5-27B-Q8_0.gguf',
            'Qwen3.5-27B-UD-Q8_K_XL.gguf'
        )
    }

    foreach ($candidate in $candidates) {
        if ($Files -contains $candidate) {
            return $candidate
        }
    }

    $hint = if ($Quant -eq 'q6') { 'Q6' } else { 'Q8' }
    $matching = $Files | Where-Object { $_ -match $hint -and $_ -match '\.gguf$' }
    throw "No $Quant model file found. Found: $($matching -join ', ')"
}

function Get-PreferredMmprojFile {
    param([string[]]$Files)

    $candidates = @(
        'mmproj-F16.gguf',
        'mmproj-BF16.gguf',
        'mmproj-F32.gguf'
    )

    foreach ($candidate in $candidates) {
        if ($Files -contains $candidate) {
            return $candidate
        }
    }

    $matching = $Files | Where-Object { $_ -like 'mmproj-*.gguf' }
    if ($matching.Count -gt 0) {
        return $matching[0]
    }

    throw "No mmproj file found. Repo files: $($Files -join ', ')"
}

function Download-FileIfMissing {
    param(
        [string]$Repo,
        [string]$FileName,
        [string]$Destination
    )

    if (Test-Path $Destination) {
        Write-Host "Already exists: $Destination"
        return
    }

    $url = 'https://huggingface.co/{0}/resolve/main/{1}?download=true' -f $Repo, $FileName
    Write-Host "Downloading $FileName ..."

    $curlArgs = @('-fL', '--retry', '5', '--retry-delay', '2', '-o', $Destination, $url)
    if ($env:HF_TOKEN) {
        $curlArgs = @('-H', "Authorization: Bearer $($env:HF_TOKEN)") + $curlArgs
    }

    & curl.exe @curlArgs
    if ($LASTEXITCODE -ne 0) {
        throw "curl download failed with exit code $LASTEXITCODE for $url"
    }

    Write-Host "Saved to $Destination"
}

$dockerRoot = Split-Path -Parent $PSScriptRoot
$repoDockerRoot = Split-Path -Parent $dockerRoot
$modelsRoot = Join-Path $repoDockerRoot 'volumes\llama\models'

$files = Get-RepoFiles -Repo $Repository
$modelFile = Get-PreferredModelFile -Files $files -Quant $Quantization

# For llama.cpp router mode to support multimodal, the model and mmproj must be in a subdirectory
$modelBaseName = [System.IO.Path]::GetFileNameWithoutExtension($modelFile)
$modelDir = Join-Path $modelsRoot $modelBaseName
New-Item -ItemType Directory -Force -Path $modelDir | Out-Null

$outFile = Join-Path $modelDir $modelFile

Download-FileIfMissing -Repo $Repository -FileName $modelFile -Destination $outFile

if (-not $SkipMmproj) {
    $mmprojFile = Get-PreferredMmprojFile -Files $files
    $mmprojOutFile = Join-Path $modelDir $mmprojFile
    Download-FileIfMissing -Repo $Repository -FileName $mmprojFile -Destination $mmprojOutFile
}
