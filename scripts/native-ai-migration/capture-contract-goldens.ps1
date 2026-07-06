# Capture contract golden request/response pairs against a running guideants-ai container.
# Usage: .\scripts\native-ai-migration\capture-contract-goldens.ps1 -BaseUrl http://localhost
param(
    [string]$BaseUrl = "http://localhost",
    [string]$OutDir = "docs/native-ai-migration/goldens"
)

$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

function Save-Golden {
    param([string]$Name, [hashtable]$Record)
    $path = Join-Path $OutDir "$Name.json"
    $Record | ConvertTo-Json -Depth 20 | Set-Content -Path $path -Encoding utf8
    Write-Host "Wrote $path"
}

# Health endpoints
foreach ($svc in @("emb", "asr", "tts", "sd", "llama-admin")) {
    try {
        $r = Invoke-WebRequest -Uri "$BaseUrl/$svc/health" -UseBasicParsing
        Save-Golden "${svc}_health" @{ method = "GET"; path = "/$svc/health"; status = $r.StatusCode; body = $r.Content }
    } catch {
        Write-Warning "Failed $svc/health: $_"
    }
}

# Emb ready + models list
try {
    $r = Invoke-WebRequest -Uri "$BaseUrl/emb/ready" -UseBasicParsing -SkipHttpErrorCheck
    Save-Golden "emb_ready" @{ method = "GET"; path = "/emb/ready"; status = $r.StatusCode; body = $r.Content }
} catch { Write-Warning $_ }

try {
    $r = Invoke-WebRequest -Uri "$BaseUrl/emb/admin/models" -UseBasicParsing
    Save-Golden "emb_admin_models" @{ method = "GET"; path = "/emb/admin/models"; status = $r.StatusCode; body = $r.Content }
} catch { Write-Warning $_ }

Write-Host "Golden capture complete. Record results in docs/native-ai-migration/STATE.md"
