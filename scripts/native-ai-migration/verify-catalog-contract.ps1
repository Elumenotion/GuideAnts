# Drift guard for the native local-AI catalog contract.
#
# Fails (non-zero exit) if any of these drift apart:
#   1. Runtime manifest ids  !=  INVENTORY.md ids (per service: ASR / TTS / Emb)
#   2. Hardcoded voice lists reappear in .NET
#        (LocalTtsVoiceNames / LocalTtsVoiceLanguageCodes)
#   3. Hardcoded catalog arrays reappear in the client editors
#        (catalogEntries = [ ...)
#
# The manifests are the runtime source of truth; INVENTORY.md is the product
# source of truth. They must stay identical. See docs/native-ai-migration/RULES.md.
#
# Usage:  pwsh -File scripts/native-ai-migration/verify-catalog-contract.ps1

param(
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
)

$ErrorActionPreference = "Stop"
$failures = New-Object System.Collections.Generic.List[string]

function Read-ManifestIds([string]$path) {
    if (-not (Test-Path $path)) {
        throw "manifest not found: $path"
    }
    $json = Get-Content -Raw -Path $path | ConvertFrom-Json
    return @($json.entries | ForEach-Object { $_.id })
}

# Extract ids for one service from INVENTORY.md. ASR/TTS entries are `### `id``
# headings between the service header and the next `## ` section; Emb entries
# are the backticked first column of the embeddings table.
function Read-InventoryHeadingIds([string]$text, [string]$sectionHeader) {
    $lines = $text -split "`n"
    $ids = New-Object System.Collections.Generic.List[string]
    $inSection = $false
    foreach ($line in $lines) {
        if ($line -match '^##\s') {
            $inSection = ($line -match [regex]::Escape($sectionHeader))
            continue
        }
        if ($inSection -and $line -match '^###\s+`([^`]+)`') {
            $ids.Add($Matches[1])
        }
    }
    return $ids.ToArray()
}

function Read-InventoryEmbeddingIds([string]$text) {
    $lines = $text -split "`n"
    $ids = New-Object System.Collections.Generic.List[string]
    $inSection = $false
    foreach ($line in $lines) {
        if ($line -match '^##\s') {
            $inSection = ($line -match 'Embeddings')
            continue
        }
        # Table rows look like: | `qwen3_embedding_0_6b` | ... |
        if ($inSection -and $line -match '^\|\s*`([^`]+)`\s*\|') {
            $ids.Add($Matches[1])
        }
    }
    return $ids.ToArray()
}

function Compare-IdSet([string]$service, [string[]]$manifestIds, [string[]]$inventoryIds) {
    $m = @($manifestIds | Sort-Object)
    $i = @($inventoryIds | Sort-Object)
    $onlyManifest = @($m | Where-Object { $i -notcontains $_ })
    $onlyInventory = @($i | Where-Object { $m -notcontains $_ })
    if ($onlyManifest.Count -gt 0 -or $onlyInventory.Count -gt 0) {
        $failures.Add("[$service] manifest/INVENTORY id drift:")
        if ($onlyManifest.Count -gt 0) {
            $failures.Add("    only in manifest:  $($onlyManifest -join ', ')")
        }
        if ($onlyInventory.Count -gt 0) {
            $failures.Add("    only in INVENTORY: $($onlyInventory -join ', ')")
        }
    }
    else {
        Write-Host "[$service] OK - $($m.Count) ids match INVENTORY ($($m -join ', '))"
    }
}

$inventoryText = Get-Content -Raw -Path (Join-Path $RepoRoot "docs/native-ai-migration/INVENTORY.md")

# 1. Manifest <-> INVENTORY parity
$asrManifest = Read-ManifestIds (Join-Path $RepoRoot "docker/build/guideants-ai/asr-service/catalog/manifest.json")
$ttsManifest = Read-ManifestIds (Join-Path $RepoRoot "docker/build/guideants-ai/tts-service/catalog/manifest.json")
$embManifest = Read-ManifestIds (Join-Path $RepoRoot "docker/build/guideants-ai/emb-service/catalog/manifest.json")

Compare-IdSet "ASR" $asrManifest (Read-InventoryHeadingIds $inventoryText "ASR")
Compare-IdSet "TTS" $ttsManifest (Read-InventoryHeadingIds $inventoryText "5 shipped")
Compare-IdSet "Emb" $embManifest (Read-InventoryEmbeddingIds $inventoryText)

# 2. No hardcoded voice lists in .NET
$serverDir = Join-Path $RepoRoot "src/server"
foreach ($banned in @("LocalTtsVoiceNames", "LocalTtsVoiceLanguageCodes")) {
    $hits = Select-String -Path (Join-Path $serverDir "**\*.cs") -Pattern $banned -SimpleMatch -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -notmatch '\\obj\\' -and $_.Path -notmatch '\\bin\\' }
    if ($hits) {
        $failures.Add("[hardcoded-voice] '$banned' reappeared in src/server:")
        foreach ($h in $hits) { $failures.Add("    $($h.Path):$($h.LineNumber)") }
    }
    else {
        Write-Host "[hardcoded-voice] OK - no '$banned' in src/server"
    }
}

# 3. No hardcoded catalog arrays in the client editors
$editorsDir = Join-Path $RepoRoot "src/client/src/pages/settings/editors"
$catalogHits = Select-String -Path (Join-Path $editorsDir "**\*.tsx") -Pattern 'catalogEntries\s*=\s*\[' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -notmatch '__tests__' }
if ($catalogHits) {
    $failures.Add("[hardcoded-catalog] 'catalogEntries = [' reappeared in client editors:")
    foreach ($h in $catalogHits) { $failures.Add("    $($h.Path):$($h.LineNumber)") }
}
else {
    Write-Host "[hardcoded-catalog] OK - no inline catalogEntries arrays in client editors"
}

if ($failures.Count -gt 0) {
    Write-Host ""
    Write-Host "CATALOG CONTRACT DRIFT DETECTED:" -ForegroundColor Red
    foreach ($f in $failures) { Write-Host "  $f" -ForegroundColor Red }
    exit 1
}

Write-Host ""
Write-Host "Catalog contract verified: manifests match INVENTORY and no hardcoded lists reappeared." -ForegroundColor Green
exit 0
