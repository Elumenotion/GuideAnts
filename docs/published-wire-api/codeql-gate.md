# Published Wire APIs — CodeQL Gate (local baseline-vs-current)

Companion to [`00-orchestration.md`](./00-orchestration.md). Reference runbook:
[`../codeql-local-solution-runbook.md`](../codeql-local-solution-runbook.md).

## 1. Local-only mode

This execution uses local baseline-vs-current comparisons.

- Do not require GitHub Code Scanning parity for this branch.
- Use a local baseline captured during Phase 0.
- Gate standard: no new findings vs baseline.
- Use a two-tier workflow:
  - fast changed-scope scan for phase development gates
  - full all-language baseline-vs-current diff for pre-flight and final
    acceptance

## 2. Required extraction settings

- C#: `--build-mode=none --source-root=.`
- Python: code-scanning suite only
- JavaScript/TypeScript: code-scanning suite only, source root `src/client`
- No suppression to pass the gate; fix code.

## 3. Fast changed-scope scan (phases 2, 4, 6)

Run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run-codeql-changed.ps1 -BaseRef origin/main -IncludeWorkingTree
```

Outputs:

- `.codeql/triage-changed-languages.csv` (all findings for impacted languages)
- `.codeql/triage-changed-files.csv` (findings touching changed files)

Use this for fast iteration and phase gates.

## 4. Baseline capture (Phase 0, full)

Use the existing triage wrapper in full-language mode:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run-codeql-sln-triage.ps1 -Languages all -CleanCodeqlOutputs -SkipGitHubParityCheck
```

Manual equivalent (if needed):

```powershell
$codeql = "C:\Users\dougl\tools\codeql\codeql.exe"

# csharp
& $codeql database create .codeql/db-csharp --language=csharp --build-mode=none --source-root=. --overwrite
& $codeql database analyze .codeql/db-csharp codeql/csharp-queries:codeql-suites/csharp-code-scanning.qls `
  --download --format=sarifv2.1.0 --output=.codeql/results-csharp.sarif

# python
& $codeql database create .codeql/db-python --language=python --build-mode=none --source-root=. --overwrite
& $codeql database analyze .codeql/db-python codeql/python-queries:codeql-suites/python-code-scanning.qls `
  --download --format=sarifv2.1.0 --output=.codeql/results-python.sarif

# javascript/typescript
& $codeql database create .codeql/db-javascript --language=javascript --build-mode=none --source-root=src/client --overwrite
& $codeql database analyze .codeql/db-javascript codeql/javascript-queries:codeql-suites/javascript-code-scanning.qls `
  --download --format=sarifv2.1.0 --output=.codeql/results-javascript.sarif
```

Then snapshot baseline:

```powershell
New-Item -ItemType Directory -Force .codeql/baseline | Out-Null
Copy-Item .codeql/results-*.sarif .codeql/baseline/
```

Record counts in `STATUS.md`.

## 5. Full diff procedure (Phase 0 baseline vs final)

After each required phase scan, compare current results to baseline by
`RuleId + file` (line drift tolerated):

```powershell
function Read-Findings($sarifGlob) {
  Get-ChildItem $sarifGlob | ForEach-Object {
    (Get-Content $_.FullName -Raw | ConvertFrom-Json).runs.results | ForEach-Object {
      [pscustomobject]@{
        RuleId = $_.ruleId
        File = $_.locations[0].physicalLocation.artifactLocation.uri
      }
    }
  }
}

$base = Read-Findings ".codeql/baseline/results-*.sarif"
$now = Read-Findings ".codeql/results-*.sarif"
Compare-Object $base $now -Property RuleId,File -PassThru |
  Where-Object SideIndicator -eq '=>'
```

Pass = no new rows.

## 6. When this gate is required

- After Phase 2: changed-scope scan
- After Phase 4: changed-scope scan
- After Phase 6: changed-scope scan
- After Phase 8: full all-language diff vs baseline
- Also run whenever sensitive auth/header/token/cost logic changes.

## 7. Findings to watch closely for this feature

- Auth/header handling issues (improper token/key handling or logging)
- Provider/model routing path-injection style issues
- Usage-write error swallowing or ignored failures
- Clear-text storage/logging of keys, tokens, or external identity
- Input-size validation bypass issues

## 8. Report-back addendum for required phases

```text
CODEQL
- C# build-mode=none used: <yes/no>
- suites=code-scanning only: <yes/no>
- changed-scope scan run (phases 2/4/6): <yes/no>
- changed-file findings count: <n>
- changed-file finding list: <RuleId @ file or "none">
- full diff vs baseline (phase 8): <count + list or n/a>
- fixed in code (no suppression): <yes/n-a>
```
