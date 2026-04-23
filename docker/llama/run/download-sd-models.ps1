param(
    [string]$ModelsDir,
    [string]$DiffusionRepository = "unsloth/FLUX.2-klein-4B-GGUF",
    [string]$DiffusionFile = "flux-2-klein-4b-Q4_K_S.gguf",
    [string]$VaeRepository = "black-forest-labs/FLUX.2-small-decoder",
    [string]$VaeFile = "full_encoder_small_decoder.safetensors",
    [string]$LlmRepository = "unsloth/Qwen3-4B-GGUF",
    [string]$LlmFile = "Qwen3-4B-Q4_K_M.gguf"
)

$ErrorActionPreference = "Stop"

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

    $url = "https://huggingface.co/{0}/resolve/main/{1}?download=true" -f $Repository, $FileName
    Write-Host "Downloading $Repository/$FileName ..."

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

$dockerRoot = Split-Path -Parent $PSScriptRoot
$repoDockerRoot = Split-Path -Parent $dockerRoot
$defaultModelsDir = Join-Path $repoDockerRoot "volumes\sd\models"
$modelRoot = if ([string]::IsNullOrWhiteSpace($ModelsDir)) { $defaultModelsDir } else { $ModelsDir }

if (-not (Test-Path $modelRoot)) {
    New-Item -ItemType Directory -Path $modelRoot -Force | Out-Null
}

Download-FileIfMissing -Repository $DiffusionRepository -FileName $DiffusionFile -Destination (Join-Path $modelRoot $DiffusionFile)
Download-FileIfMissing -Repository $VaeRepository -FileName $VaeFile -Destination (Join-Path $modelRoot $VaeFile)
Download-FileIfMissing -Repository $LlmRepository -FileName $LlmFile -Destination (Join-Path $modelRoot $LlmFile)

Write-Host ""
Write-Host "Stable Diffusion model assets ready at: $modelRoot" -ForegroundColor Green
