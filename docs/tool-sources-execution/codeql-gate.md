# CodeQL Security Gate (Tool Sources Guide Builder - local baseline-vs-current)

Companion to `00-orchestration.md`.
Reference runbook: `../codeql-local-solution-runbook.md`.

This branch uses the local-only gate style:
baseline once, then diff current findings versus baseline after security-sensitive
phases. No GitHub parity checks.

---

## 1. Local-only adaptation

- Do not run GitHub fetch/parity scripts.
- Do use local baseline-vs-current SARIF diff.
- Pass criterion: zero NEW findings vs `.codeql/baseline/`.

---

## 2. Non-negotiables

- C# scan must use `--build-mode=none --source-root=.`.
- Use code-scanning suites:
  `csharp-code-scanning.qls`, `python-code-scanning.qls`,
  `javascript-code-scanning.qls`.
- Run all three languages.
- No suppression shortcuts. Fix code.

---

## 3. Commands

```powershell
$codeql = "C:\Users\dougl\tools\codeql\codeql.exe"

# C#
& $codeql database create .codeql/db-csharp --language=csharp --build-mode=none --source-root=. --overwrite
& $codeql database analyze .codeql/db-csharp codeql/csharp-queries:codeql-suites/csharp-code-scanning.qls `
  --download --format=sarifv2.1.0 --output=.codeql/results-csharp.sarif

# Python
& $codeql database create .codeql/db-python --language=python --build-mode=none --source-root=. --overwrite
& $codeql database analyze .codeql/db-python codeql/python-queries:codeql-suites/python-code-scanning.qls `
  --download --format=sarifv2.1.0 --output=.codeql/results-python.sarif

# JavaScript/TypeScript (client source root)
& $codeql database create .codeql/db-javascript --language=javascript --build-mode=none --source-root=src/client --overwrite
& $codeql database analyze .codeql/db-javascript codeql/javascript-queries:codeql-suites/javascript-code-scanning.qls `
  --download --format=sarifv2.1.0 --output=.codeql/results-javascript.sarif
```

---

## 4. Baseline and diff procedure

### 4.1 Baseline (once pre-flight)

```powershell
New-Item -ItemType Directory -Force .codeql/baseline | Out-Null
Copy-Item .codeql/results-*.sarif .codeql/baseline/
```

Record counts in `STATUS.md`.

### 4.2 Diff (every required gate)

```powershell
function Read-Findings($sarifGlob) {
  Get-ChildItem $sarifGlob | ForEach-Object {
    (Get-Content $_.FullName -Raw | ConvertFrom-Json).runs.results | ForEach-Object {
      [pscustomobject]@{
        RuleId = $_.ruleId
        File   = $_.locations[0].physicalLocation.artifactLocation.uri
        Line   = $_.locations[0].physicalLocation.region.startLine
      }
    }
  }
}
$base = Read-Findings ".codeql/baseline/results-*.sarif"
$now  = Read-Findings ".codeql/results-*.sarif"
Compare-Object $base $now -Property RuleId,File -PassThru | Where-Object SideIndicator -eq '=>'
```

Pass = empty NEW findings set.

---

## 5. When to run this gate

| Point | Why |
|---|---|
| Pre-flight baseline | Establish comparison set |
| After Phase 3 | MCP metadata, remote connection handling, scheme dispatch updates |
| After Phase 4 | Validation/publish checks and preview endpoint expansion |
| After Phase 5 (if run) | Storage migration + serialization changes |
| Final acceptance (Phase 6) | Close-out security check |

---

## 6. Rules to watch for this feature

- Secret leakage risks:
  auth value templates, MCP headers/tokens, and preview payloads must not expose
  clear-text secrets.
- URL/connection handling risks:
  MCP connection test/discovery endpoints must validate transport/URL input and
  avoid unsafe unchecked outbound usage patterns.
- Logging risks:
  connector keys, URLs, and descriptor fragments should be sanitized in logs.
- JSON handling risks:
  advanced JSON and descriptor fragments must fail closed with explicit validation
  errors, not permissive execution paths.
- Client rendering risks:
  advanced JSON/preview displays should avoid unsafe HTML rendering patterns.

---

## 7. Report-back addition for security-sensitive phases

Subagents on Phases 3, 4, optional 5, and 6 append:

```text
CODEQL (local baseline diff):
- C# build-mode=none used: <yes/no>
- Suites: code-scanning only: <yes/no>
- New findings vs baseline: <count and ids, or none>
- Any new finding fixed in code (no suppression): <yes/n-a>
```
