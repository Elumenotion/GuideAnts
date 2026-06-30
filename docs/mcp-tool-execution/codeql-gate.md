# CodeQL Security Gate (MCP Tool Execution — local baseline-vs-current)

Companion to `00-orchestration.md`.
Reference runbook: `../codeql-local-solution-runbook.md`.

This branch uses the local-only gate style: baseline once, then diff current findings
versus baseline after security-sensitive phases. No GitHub parity checks.

This feature is security-sensitive because it adds **server-side outbound MCP connections**
(SSRF surface), **secret resolution into headers/env** (leakage surface), and **subprocess
spawning of registry packages** (command/argument injection + untrusted-output surface).

---

## 1. Local-only adaptation

- Do not run GitHub fetch/parity scripts.
- Do use local baseline-vs-current SARIF diff.
- Pass criterion: zero NEW findings vs `.codeql/baseline/`.

---

## 2. Non-negotiables

- C# scan must use `--build-mode=none --source-root=.`.
- Use code-scanning suites:
  `csharp-code-scanning.qls`, `python-code-scanning.qls`, `javascript-code-scanning.qls`.
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

### 4.1 Baseline (once, pre-flight)

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
| After Phase 1 | Descriptor migration + scheme handling; secret-template plumbing |
| After Phase 2 | Outbound MCP HTTP connection + header secret resolution (SSRF + leakage) |
| After Phase 5 | Subprocess spawn of registry packages + env injection (command injection) |
| After Phase 6 | Registry import handling + publish-gate logic |
| Final acceptance (Phase 7) | Close-out security check |

---

## 6. Rules to watch for this feature

- **Secret leakage:** header `{{secret:VAR}}` and env templates must resolve only at
  call time; resolved values must never reach preview payloads, logs, exported JSON, or
  non-admin responses. No logging of resolved `Authorization` headers.
- **SSRF / outbound URL handling:** MCP `url` and `mcp+api://{bridgeId}` resolution must
  validate the target; do not follow attacker-controlled redirects to internal metadata
  endpoints. Note E6 (loopback) is *warned*, not silently rewritten.
- **Command/argument injection (stdio):** `package.command` + `args` and resolved env for
  `mcp+sandbox://` must be passed as an argv vector, never composed into a shell string;
  no untrusted interpolation into shell.
- **Untrusted subprocess output:** MCP tool results and `tools/list` output are untrusted;
  parse defensively, fail closed with explicit errors (no permissive execution).
- **Logging:** bridge ids, URLs, and descriptor fragments are sanitized in logs.
- **JSON handling:** descriptor fragments and MCP JSON-RPC parse must fail closed with
  explicit validation errors, not silent fallback.
- **Client rendering:** Guide Builder MCP panels (status, generated URL, headers) must
  avoid unsafe HTML rendering and must mask secret values.

---

## 7. Report-back addition (Phases 1, 2, 5, 6, 7)

```text
CODEQL (local baseline diff):
- C# build-mode=none used: <yes/no>
- Suites: code-scanning only: <yes/no>
- New findings vs baseline: <count and ids, or none>
- Any new finding fixed in code (no suppression): <yes/n-a>
```
