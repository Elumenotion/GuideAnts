# =============================================================================
# dd-recover.ps1 — Docker Desktop (Windows) credential-server recovery
# =============================================================================
# Symptom signature (run this when you see ALL of these):
#   * docker pull fails for EVERY registry (GHCR, Docker Hub, GCR) — not one
#     registry, all of them
#   * error looks like:  "error from registry: denied"
#     or: "error getting credentials ... A specified logon session does not
#          exist / no credentials server URL"
#   * containers are otherwise running fine (engine is up, only auth is broken)
#
# Cause: a stale/duplicate com.docker.backend process is left over from an
# unclean Docker Desktop shutdown; docker-credential-desktop reads a dead
# credentials-server URL while the live engine keeps running. Reboot does NOT
# fix it (the broken state is on disk); tray "restart" usually does not fix
# it (it does not kill the stale backend). This script kills EVERY Docker
# process, starts one clean instance, and waits for the credentials server.
#
# Safety: touches processes only. No images, volumes, configs, or data.
# Idempotent: safe to run when things are already healthy.
#
# Usage (run in the user session that owns Docker Desktop, e.g. 'dougl'):
#   powershell -ExecutionPolicy Bypass -File .\dd-recover.ps1
#   powershell -ExecutionPolicy Bypass -File .\dd-recover.ps1 -PullTest ghcr.io/ggml-org/llama.cpp:server-cuda13-b10615
# =============================================================================
param(
    # Optional: pull this image at the end to prove end-to-end success
    [string]$PullTest
)
$ErrorActionPreference = 'Stop'

Write-Host "=== Docker Desktop recovery ===" -ForegroundColor Cyan

# 1) Kill ALL Docker Desktop processes (stale backends included).
$procs = Get-Process | Where-Object { $_.ProcessName -match 'Docker|com\.docker' }
if ($procs) {
    $backendCountBefore = ($procs | Where-Object { $_.ProcessName -eq 'com.docker.backend' } | Measure-Object).Count
    Write-Host ("Killing {0} Docker process(es) ({1} com.docker.backend — healthy count is 1)..." -f $procs.Count, $backendCountBefore)
    $procs | Stop-Process -Force
} else {
    Write-Host "No Docker processes running."
}
Start-Sleep -Seconds 5

# 2) Start one clean Docker Desktop instance.
$desktop = "C:\Program Files\Docker\Docker\Docker Desktop.exe"
if (-not (Test-Path $desktop)) {
    $desktop = (Get-ChildItem "C:\Program Files\Docker" -Filter "Docker Desktop.exe" -Recurse -ErrorAction SilentlyContinue |
        Select-Object -First 1).FullName
}
if (-not $desktop -or -not (Test-Path $desktop)) { throw "Docker Desktop.exe not found (adjust path in this script)." }
Write-Host "Starting: $desktop"
Start-Process $desktop

# 3) Wait for the credentials server (docker-credential-desktop) to answer.
$helper = Join-Path (Split-Path $desktop -Parent) "resources\bin\docker-credential-desktop.exe"
if (-not (Test-Path $helper)) { throw "Credential helper not found: $helper" }
$ok = $false
for ($i = 0; $i -lt 45; $i++) {
    Start-Sleep -Seconds 4
    & $helper list 2>$null | Out-Null
    if ($LASTEXITCODE -eq 0) { $ok = $true; break }
}
if (-not $ok) {
    Write-Error "Credentials server did not come back within ~3 min. Check the Docker Desktop UI itself, and %APPDATA%\Docker Desktop\log."
    exit 1
}
$backendCount = (Get-Process com.docker.backend -ErrorAction SilentlyContinue | Measure-Object).Count
Write-Host ("OK: credentials server up in ~{0}s. com.docker.backend count = {1} (expect 1)." -f (($i + 1) * 4), $backendCount) -ForegroundColor Green

# 4) Optional end-to-end proof.
if ($PullTest) {
    Write-Host "Pull test: $PullTest"
    docker pull $PullTest
    if ($LASTEXITCODE -ne 0) { Write-Error "Credential server is up but the pull still failed — different problem (registry auth, network)." }
    else { Write-Host "Pull test passed." -ForegroundColor Green }
}
