# CodeQL Local Runbook (Repository-Wide)

Date: June 4, 2026
Repository: `quality-alerts`

## Goal

Reassess **all CodeQL languages in this repo** locally (C#, Python, JavaScript/TypeScript) without calling GitHub. Results are reproducible for a git commit when you use the same commit, CodeQL version, query suites, and build/extract settings.

GitHub’s open-alert list is a **snapshot of `main` at scan time**; your branch will differ after fixes. Compare at the same SHA only when validating setup.

## Canonical command

From repo root:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run-codeql-sln-triage.ps1 -CleanCodeqlOutputs
```

`-Languages all` (default) runs **csharp + python + javascript**. The triage script **rejects** `-Languages csharp` (etc.) unless you pass `-AllowPartialLanguages` — partial runs must not overwrite the merged `triage.csv` by mistake.

First C# run also benefits from `-CleanBuildOutputs`.

## Outputs

| File | Purpose |
|------|---------|
| `.codeql/triage.csv` | Merged triage: every finding from all languages (one row each) |
| `.codeql/results-csharp.sarif` | C# code-scanning results |
| `.codeql/results-python.sarif` | Python code-scanning results |
| `.codeql/results-javascript.sarif` | JS/TS code-scanning results |
| `.codeql/run-manifest.json` | Commit, CodeQL version, per-language counts and coverage |
| `.codeql/db-csharp`, `db-python`, `db-javascript` | CodeQL databases |

## Query suites (match GitHub Code Scanning families)

| Language | Suite | Extract / build |
|----------|--------|-----------------|
| C# | `csharp-code-scanning.qls` | `GuideAntsApi.sln` rebuild, `UseSharedCompilation=false` |
| Python | `python-code-scanning.qls` | Repo root, build-mode none (`echo build`) |
| JavaScript | `javascript-code-scanning.qls` | `src/client`, build-mode none |

Do **not** use `csharp-security-and-quality.qls` for `triage.csv` — that adds hundreds of non-security quality rules.

## Expected scale (full `all` run on a healthy tree)

Rough totals align with GitHub’s code-scanning rule families (not identical line-by-line after local fixes):

- C#: ~70–90 security results; ~597/598 `.cs` files scanned
- Python: ~33 results; ~19 project Python files (plus Actions metadata)
- JavaScript: ~6 results; ~483 JS/TS files under `src/client`

**`triage.csv` rows ≈ sum of the three** (e.g. ~115 on `main` before log-forging fixes; ~42 after fixing ~70 `cs/log-forging` alerts on this branch).

## Sanity checks

```powershell
Get-Content .codeql/run-manifest.json | ConvertFrom-Json | Select-Object git_commit_short, total_results, triage_csv_rows, languages

Import-Csv .codeql/triage.csv | Group-Object Language, RuleId | Sort-Object Count -Descending

((Import-Csv .codeql/triage.csv).Count)
```

## Manual per-language fallback

```powershell
$codeql = "C:\Users\dougl\tools\codeql\codeql.exe"

# C#
& $codeql database create .codeql/db-csharp --language=csharp `
  --command "dotnet build src/server/GuideAntsApi.sln -c Debug -v minimal -t:Rebuild -p:UseSharedCompilation=false"
& $codeql database analyze .codeql/db-csharp codeql/csharp-queries:codeql-suites/csharp-code-scanning.qls `
  --format=sarifv2.1.0 --output=.codeql/results-csharp.sarif

# Python (repo root, no build)
& $codeql database create .codeql/db-python --language=python --source-root=. --command "cmd /c echo build"
& $codeql database analyze .codeql/db-python codeql/python-queries:codeql-suites/python-code-scanning.qls `
  --download --format=sarifv2.1.0 --output=.codeql/results-python.sarif

# JavaScript (client tree)
& $codeql database create .codeql/db-javascript --language=javascript --source-root=src/client --command "cmd /c echo build"
& $codeql database analyze .codeql/db-javascript codeql/javascript-queries:codeql-suites/javascript-code-scanning.qls `
  --download --format=sarifv2.1.0 --output=.codeql/results-javascript.sarif
```

Then merge with `scripts/run-codeql-sln-triage.ps1 -Languages all` after databases exist, or re-run the wrapper.

## Known failure modes

1. **`codeql` not on PATH** — use `C:\Users\dougl\tools\codeql\codeql.exe` or `-CodeqlPath`.
2. **C#-only `triage.csv`** — rerun with default `-Languages all`.
3. **~1000 C# rows** — wrong suite (`csharp-security-and-quality.qls`).
4. **Low C# coverage** — must build `GuideAntsApi.sln`; check `build-tracer.log`. The triage script **fails** if analyze reports fewer than ~550/590 C# files scanned.
5. **Do not use CodeQL barrier/model packs** to suppress `cs/log-forging` — that hides alerts; fix log arguments instead.

## Optional: GitHub validation at one commit

```powershell
git checkout <sha>
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run-codeql-sln-triage.ps1 -CleanCodeqlOutputs -CleanBuildOutputs

# optional API snapshot for that era
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/fetch-github-code-scanning.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/compare-codeql-github-parity.ps1 -ExportCsv .codeql/parity-github-vs-local.csv
```

## Log forging (`cs/log-forging`)

Wrap user-controlled log arguments with `LogValueSanitizer.Sanitize(...)` from `GuideAnts.Logging` (uses `ReplaceLineEndings(" ")`). Do **not** use CodeQL barrier/model packs to suppress this rule — fixes must clear on a full scan (~597/598 `.cs` files).

## Policy

- Day-to-day triage: `triage.csv` + `run-manifest.json` only.
- `scan-results.txt` / `fetch-github-code-scanning.ps1` are optional validators, not inputs to `triage.csv`.
