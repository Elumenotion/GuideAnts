# Record per-flavor image baseline (venv size, torch tree, process count).
# Usage: .\scripts\native-ai-migration\capture-baseline.ps1 -Flavor cuda13
param(
    [ValidateSet("cpu", "cuda13", "rocm", "vulkan")]
    [string]$Flavor = "cuda13",
    [string]$ContainerName = "guideants-ai-baseline"
)

$ErrorActionPreference = "Stop"
$out = "docs/native-ai-migration/baselines/$Flavor-$(Get-Date -Format 'yyyyMMdd-HHmmss').txt"
New-Item -ItemType Directory -Force -Path (Split-Path $out) | Out-Null

@"
# guideants-ai baseline — $Flavor
# Container: $ContainerName
# Captured: $(Get-Date -Format o)

## Process count
"@ | Set-Content $out

docker exec $ContainerName sh -c "ps aux | wc -l" 2>&1 | Add-Content $out
"`n## /opt/venv size`n" | Add-Content $out
docker exec $ContainerName du -sh /opt/venv 2>&1 | Add-Content $out
"`n## pip show torch`n" | Add-Content $out
docker exec $ContainerName /opt/venv/bin/pip show torch torchaudio torchvision 2>&1 | Add-Content $out
"`n## pipdeptree -r -p torch`n" | Add-Content $out
docker exec $ContainerName /opt/venv/bin/pipdeptree -r -p torch 2>&1 | Add-Content $out

Write-Host "Baseline written to $out"
