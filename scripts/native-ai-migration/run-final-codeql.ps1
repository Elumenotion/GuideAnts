# Run once after Phase 4 — deferred from per-phase gates.
# Usage: .\scripts\native-ai-migration\run-final-codeql.ps1 -BaseRef origin/main

param(
    [string]$BaseRef = "origin/main"
)

$ErrorActionPreference = "Stop"
Write-Host "CodeQL final gate: diff languages from $BaseRef...HEAD"
Write-Host "1. Checkout base and snapshot SARIFs (manual step if CodeQL CLI not installed)"
Write-Host "2. Run CodeQL on HEAD for changed languages (cpp, python, csharp)"
Write-Host "3. Diff new findings vs baseline per docs/native-ai-migration/codeql-gate.md"
Write-Host "Record results in docs/native-ai-migration/STATE.md"
