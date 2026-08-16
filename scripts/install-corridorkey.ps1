[CmdletBinding()]
param(
    [string]$Destination = "artifacts/tools/CorridorKey"
)

$ErrorActionPreference = "Stop"
$CorridorKeyCommit = "97e55a453060745bead1befd293f6e523c4b845c"
$CheckpointRevision = "f6386ddf042d8e92aeb5fd16cb9b101cff508195"
$CheckpointSha256 = "74d614f7d92fc559a118c30a7deadedc3cacd8ef83dcb85a030d0bed7af8b20b"
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

if (-not [IO.Path]::IsPathRooted($Destination)) {
    $Destination = Join-Path $RepoRoot $Destination
}
$Destination = [IO.Path]::GetFullPath($Destination)
$Parent = Split-Path -Parent $Destination
New-Item -ItemType Directory -Force -Path $Parent | Out-Null

if (-not (Test-Path -LiteralPath $Destination)) {
    & git clone --filter=blob:none --no-checkout `
        https://github.com/nikopueringer/CorridorKey.git $Destination
    if ($LASTEXITCODE -ne 0) { throw "CorridorKey clone failed" }
}
if (-not (Test-Path -LiteralPath (Join-Path $Destination ".git"))) {
    throw "CorridorKey destination is not a Git checkout: $Destination"
}

& git -C $Destination fetch --depth 1 origin $CorridorKeyCommit
if ($LASTEXITCODE -ne 0) { throw "CorridorKey fetch failed" }
& git -C $Destination checkout --detach $CorridorKeyCommit
if ($LASTEXITCODE -ne 0) { throw "CorridorKey checkout failed" }

$Revision = (& git -C $Destination rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $Revision -ne $CorridorKeyCommit) {
    throw "CorridorKey revision is $Revision; expected $CorridorKeyCommit"
}

Push-Location $Destination
try {
    & uv sync --extra cuda --frozen
    if ($LASTEXITCODE -ne 0) { throw "CorridorKey dependency installation failed" }
} finally {
    Pop-Location
}

$CorridorPython = Join-Path $Destination ".venv/Scripts/python.exe"
if (-not (Test-Path -LiteralPath $CorridorPython -PathType Leaf)) {
    throw "CorridorKey virtual-environment Python is missing: $CorridorPython"
}
$CheckpointPath = Join-Path $Destination "CorridorKeyModule/checkpoints/CorridorKey_v1.0.safetensors"
$env:GUIDEANTS_CORRIDORKEY_CHECKPOINT_REVISION = $CheckpointRevision
$env:GUIDEANTS_CORRIDORKEY_CHECKPOINT_PATH = $CheckpointPath
$downloadCheckpoint = @'
import os
import shutil
from pathlib import Path
from huggingface_hub import hf_hub_download

source = hf_hub_download(
    repo_id="nikopueringer/CorridorKey_v1.0",
    filename="CorridorKey_v1.0.safetensors",
    revision=os.environ["GUIDEANTS_CORRIDORKEY_CHECKPOINT_REVISION"],
)
destination = Path(os.environ["GUIDEANTS_CORRIDORKEY_CHECKPOINT_PATH"])
destination.parent.mkdir(parents=True, exist_ok=True)
shutil.copy2(source, destination)
'@
$downloadCheckpoint | & $CorridorPython -
if ($LASTEXITCODE -ne 0) { throw "CorridorKey checkpoint download failed" }
$ActualCheckpointSha256 = (Get-FileHash -LiteralPath $CheckpointPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($ActualCheckpointSha256 -ne $CheckpointSha256) {
    throw "CorridorKey checkpoint SHA-256 is $ActualCheckpointSha256; expected $CheckpointSha256"
}

Write-Host "CorridorKey ready at $Destination ($Revision)"
