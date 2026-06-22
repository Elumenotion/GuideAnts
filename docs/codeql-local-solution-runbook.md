# CodeQL Local Runbook (Repository-Wide)

Date: June 4, 2026
Repository: `quality-alerts`

## Goal

Run the **same CodeQL extraction and query suites as GitHub Code Scanning** locally so you see path-injection, log-forging, and other alerts **before** merging to `main` — not on the GitHub dashboard afterward.

Local `triage.csv` must match GitHub at the current commit. The triage script **fails** if `scan-results.txt` open alerts are not reproduced in local SARIF.

## Pre-merge workflow

From repo root at the commit you intend to merge:

```powershell
# 1) Snapshot GitHub open alerts at this commit (requires GITHUB_TOKEN or git credential)
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/fetch-github-code-scanning.ps1

# 2) Full local scan + mandatory parity gate
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run-codeql-sln-triage.ps1 -CleanCodeqlOutputs
```

### One-liner (fetch + full rescan + regen `triage.csv`)

Close `triage.csv` in Excel first if it is open (~10–15 minutes).

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/fetch-github-code-scanning.ps1; if ($?) { powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run-codeql-sln-triage.ps1 -CleanCodeqlOutputs }
```

Scan only (skip fetch if `scan-results.txt` is already fresh):

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run-codeql-sln-triage.ps1 -CleanCodeqlOutputs
```

Step 2 **exits with an error** if any open `cs/*`, `py/*`, or `js/*` alert in `scan-results.txt` is missing from the matching local SARIF. If GitHub has not scanned your commit yet, the script falls back to all open alerts in the snapshot (with a warning). Refresh with `fetch-github-code-scanning.ps1` after pushing if you need strict SHA matching.

Use `-SkipGitHubParityCheck` only for debugging — not before merging to `main`.

## Canonical command

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run-codeql-sln-triage.ps1 -CleanCodeqlOutputs
```

`-Languages all` (default) runs **csharp + python + javascript**. Partial language runs are blocked unless `-AllowPartialLanguages` is set.

## GitHub parity (required)

| Setting | GitHub Code Scanning | Local (default) |
|---------|----------------------|-----------------|
| C# extract | `build-mode: none`, repo root | `--build-mode=none --source-root=.` |
| C# suite | `csharp-code-scanning.qls` | same |
| Python | build-mode none, repo root | same |
| JavaScript | build-mode none, `src/client` | same |

**Do not use `dotnet build GuideAntsApi.sln` for pre-merge triage.** That mode hides most `cs/path-injection` findings (GitHub would still report them after merge). Diagnostic rebuild only:

```powershell
... -CSharpBuildMode sln
```

## Outputs

| File | Purpose |
|------|---------|
| `.codeql/triage.csv` | Merged triage: every local finding (all languages) |
| `.codeql/parity-github-vs-local.csv` | Per-alert parity vs `scan-results.txt` |
| `.codeql/results-csharp.sarif` | C# code-scanning results |
| `.codeql/results-python.sarif` | Python code-scanning results |
| `.codeql/results-javascript.sarif` | JS/TS code-scanning results |
| `.codeql/run-manifest.json` | Commit, CodeQL version, build mode, counts |
| `scan-results.txt` | GitHub open-alert baseline (from fetch script) |

## Query suites

| Language | Suite | Extract / build |
|----------|--------|-----------------|
| C# | `csharp-code-scanning.qls` | **`build-mode=none`**, `--source-root=.` |
| Python | `python-code-scanning.qls` | Repo root, `build-mode=none` |
| JavaScript | `javascript-code-scanning.qls` | `src/client`, `build-mode=none` |

Do **not** use `csharp-security-and-quality.qls` for triage — hundreds of non-security quality rules.

## Expected scale (parity mode, `main`-like tree)

Rough totals when local matches GitHub:

- C#: ~15–25 code-scanning results (includes `cs/path-injection`, `cs/web/missing-x-frame-options`, occasional `cs/log-forging`); ~599 baseline `.cs` files
- Python: ~33 results
- JavaScript: ~6 results

**`triage.csv` rows ≈ sum of the three.** If C# shows only 3 `web.config` hits, you are in the wrong build mode.

## Sanity checks

```powershell
Get-Content .codeql/run-manifest.json | ConvertFrom-Json |
  Select-Object git_commit_short, csharp_build_mode, total_results, triage_csv_rows, parity_passed

Import-Csv .codeql/triage.csv | Group-Object Language, RuleId | Sort-Object Count -Descending

Import-Csv .codeql/parity-github-vs-local.csv | Group-Object LocalStatus | Format-Table Count, Name
```

Expect `parity_passed: true` and **zero** `missing_in_local_sarif` rows.

## Manual per-language fallback (GitHub-matched C#)

```powershell
$codeql = "C:\Users\dougl\tools\codeql\codeql.exe"

# C# (matches GitHub build-mode none)
& $codeql database create .codeql/db-csharp --language=csharp --build-mode=none --source-root=.
& $codeql database analyze .codeql/db-csharp codeql/csharp-queries:codeql-suites/csharp-code-scanning.qls `
  --download --format=sarifv2.1.0 --output=.codeql/results-csharp.sarif

# Python
& $codeql database create .codeql/db-python --language=python --build-mode=none --source-root=.
& $codeql database analyze .codeql/db-python codeql/python-queries:codeql-suites/python-code-scanning.qls `
  --download --format=sarifv2.1.0 --output=.codeql/results-python.sarif

# JavaScript
& $codeql database create .codeql/db-javascript --language=javascript --build-mode=none --source-root=src/client
& $codeql database analyze .codeql/db-javascript codeql/javascript-queries:codeql-suites/javascript-code-scanning.qls `
  --download --format=sarifv2.1.0 --output=.codeql/results-javascript.sarif
```

Then run parity only:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/compare-codeql-github-parity.ps1 `
  -ExpectedCommitSha (git rev-parse HEAD) -FailOnMismatch -ExportCsv .codeql/parity-github-vs-local.csv
```

## Known failure modes

1. **`codeql` not on PATH** — use `-CodeqlPath` or install under `C:\Users\dougl\tools\codeql\`.
2. **`scan-results.txt` missing** — run `fetch-github-code-scanning.ps1` before triage.
3. **Parity fails after fetch** — wrong commit: fetch and scan must both be at `HEAD`.
4. **C# only 3 results** — you used `-CSharpBuildMode sln` or an old SARIF; rerun with default `none`.
5. **~1000 C# rows** — wrong suite (`csharp-security-and-quality.qls`).
6. **Do not use CodeQL barrier/model packs** to suppress alerts — fix code.
7. **C# extraction crashes on Windows (`RecreateMe`, invalid path syntax)** — host-mount
   integration tests leave symlinks targeting `/app/HostMounts/...`. CodeQL walks the
   repo root and fails before finalize. Run `scripts/clean-codeql-blocking-artifacts.ps1`
   (also invoked automatically by `run-codeql-sln-triage.ps1` before the C# database).
   Integration test assembly cleanup removes `GuideAntsApi.IntegrationTests/docker/volumes`
   after each run.
8. **C# create exits non-zero after finalize (`Error while recursively deleting ...\db-csharp\src`)** —
   this can happen on Windows when file handles linger in CodeQL output folders. The
   wrapper now treats "already finalized" as recoverable and continues to analyze. If it
   still fails, rerun with `-CleanCodeqlOutputs` after closing tools that may hold file handles.

## Log forging (`cs/log-forging`)

Wrap user-controlled log arguments with `LogValueSanitizer.Sanitize(...)` from `GuideAnts.Logging`. Verify on a **parity** scan, not sln-rebuild mode.

## Path injection (`cs/path-injection`)

GitHub flags `Path.Combine` / file APIs when paths include user-controlled segments (e.g. `NotebookFileService`, published upload endpoints). Local parity mode must show the same alerts in `triage.csv` before merge. Fix with strict notebook-root containment (see `PathGuard` in `ScriptExecutionAgent`).

## Policy

- **Pre-merge:** refresh `scan-results.txt` at `HEAD`, then `run-codeql-sln-triage.ps1` (parity enforced).
- The triage script **fails** if `scan-results.txt` has no alerts at the current commit (stale snapshot).
- Parity is **rule/instance reproduction** at the same SHA: local SARIF must match every GitHub open alert for `cs/*`, `py/*`, `js/*` at that commit (within line tolerance).
- **Day-to-day triage:** `triage.csv`, `parity-github-vs-local.csv`, `run-manifest.json`.
- Treat green local scans in **sln** mode as a false negative for C# path rules.
