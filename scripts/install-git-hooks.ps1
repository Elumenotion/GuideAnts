# Installs repo git hooks (pre-push guard against accidental main pushes).
# Run once per clone:  .\scripts\install-git-hooks.ps1

$ErrorActionPreference = "Stop"
$repoRoot = (git rev-parse --show-toplevel 2>$null)
if (-not $repoRoot) {
    Write-Error "Not inside a git repository."
}

Set-Location $repoRoot
$hooksPath = Join-Path $repoRoot ".githooks"
$prePush = Join-Path $hooksPath "pre-push"

if (-not (Test-Path $prePush)) {
    Write-Error "Missing hook: $prePush"
}

git config core.hooksPath .githooks
Write-Host "Installed git hooks from .githooks (core.hooksPath = .githooks)"
Write-Host "pre-push will block pushing feature branches to origin/main."
