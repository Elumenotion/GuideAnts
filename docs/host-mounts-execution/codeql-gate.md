# CodeQL Security Gate (host folder mounts — local, baseline-vs-current)

Companion to [`00-orchestration.md`](./00-orchestration.md). Full tool reference:
[`../codeql-local-solution-runbook.md`](../codeql-local-solution-runbook.md). The
sibling auth gate [`../auth-execution/codeql-gate.md`](../auth-execution/codeql-gate.md)
established the local-only (no-GitHub) procedure this file reuses.

This feature touches the exact areas CodeQL flags hardest — **filesystem/path
operations** (symlink creation, registry writes, the script-agent path guard),
**shell command construction** (the displayed apply/remove command), and
**credential handling** (SMB follow-on) — so a CodeQL pass is a **first-class
verification gate**, not an afterthought.

---

## 1. Local-only adaptation (READ THIS)

This branch is **not on GitHub**, so the runbook's GitHub Code Scanning **parity**
half does not apply:

- **Do NOT run** `scripts/fetch-github-code-scanning.ps1`.
- **Do NOT enforce GitHub parity** (`compare-codeql-github-parity.ps1`,
  `scan-results.txt`, `parity_passed`). With no remote alerts, parity is undefined.
- We substitute **local baseline-vs-current**: snapshot CodeQL findings at the
  pre-flight commit, re-scan after each security-sensitive phase, and **diff**. The
  gate is **"no NEW findings vs the baseline"** (plus: fix anything pre-existing the
  new code now touches).

Everything else in the runbook still applies — extraction modes, suites, and the
"don't suppress, fix" policy below.

---

## 2. Non-negotiables carried over from the runbook

- **C# extraction MUST be `--build-mode=none --source-root=.`** Never use
  `dotnet build GuideAntsApi.sln` for the security scan — sln mode **hides most
  `cs/path-injection` findings**, which are the highest-risk rule for this feature.
  A C# scan returning only a handful of `web.config` hits means you are in the wrong
  mode.
- **Code-scanning suites only**: `csharp-code-scanning.qls`,
  `python-code-scanning.qls`, `javascript-code-scanning.qls`. **Never**
  `csharp-security-and-quality.qls` (noise).
- **All three languages** (csharp + python + javascript). JS extracts from
  `src/client`.
- **No suppression.** Do **not** use CodeQL barrier/model packs to silence alerts —
  **fix the code.** Same spirit as the project's "no fallback" rule.
- CodeQL exe: `C:\Users\dougl\tools\codeql\codeql.exe` (or `-CodeqlPath`).

---

## 3. Commands (GitHub-free)

### Preferred: wrapper, parity skipped

```powershell
# from repo root
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run-codeql-sln-triage.ps1 -CleanCodeqlOutputs -SkipGitHubParityCheck
```

### Reliable fallback: manual per-language (no GitHub dependency at all)

```powershell
$codeql = "C:\Users\dougl\tools\codeql\codeql.exe"

# C# — build-mode none (MANDATORY), repo root
& $codeql database create .codeql/db-csharp --language=csharp --build-mode=none --source-root=. --overwrite
& $codeql database analyze .codeql/db-csharp codeql/csharp-queries:codeql-suites/csharp-code-scanning.qls `
  --download --format=sarifv2.1.0 --output=.codeql/results-csharp.sarif

# Python — repo root
& $codeql database create .codeql/db-python --language=python --build-mode=none --source-root=. --overwrite
& $codeql database analyze .codeql/db-python codeql/python-queries:codeql-suites/python-code-scanning.qls `
  --download --format=sarifv2.1.0 --output=.codeql/results-python.sarif

# JavaScript/TypeScript — src/client
& $codeql database create .codeql/db-javascript --language=javascript --build-mode=none --source-root=src/client --overwrite
& $codeql database analyze .codeql/db-javascript codeql/javascript-queries:codeql-suites/javascript-code-scanning.qls `
  --download --format=sarifv2.1.0 --output=.codeql/results-javascript.sarif
```

Do **not** run `compare-codeql-github-parity.ps1` afterward.

---

## 4. Baseline + diff procedure

### 4.1 Baseline (Pre-flight, once)

At the starting commit (before Phase 1), run a full scan and save SARIFs + a findings
snapshot out of the way of later overwrites:

```powershell
New-Item -ItemType Directory -Force .codeql/baseline | Out-Null
Copy-Item .codeql/results-*.sarif .codeql/baseline/
```

Record per-language/per-rule counts in [`STATUS.md`](./STATUS.md). The baseline is
**informational**, not a pass bar — but any pre-existing finding the mount code
**touches/extends** must be fixed, and **no new finding may be added**.

### 4.2 Diff (per gate)

Re-scan, then compare current findings to `.codeql/baseline/`. A finding is **NEW**
if its (RuleId, file, ~region) is present now but not in the baseline (allow small
line drift):

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

**Gate passes when the NEW-findings set is empty.** Any new row blocks the phase.

---

## 5. When to run it

| Point | Scan | Why |
|---|---|---|
| Pre-flight | full baseline | establishes the comparison set |
| Phase 5 gate | full diff | new admin endpoint surface; **command-text construction** (shell injection), host-path/leaf **logging** (`cs/log-forging`) |
| Phase 6 gate | full diff | **symlink creation + `mounts.json` writes** with user-influenced leaf/path segments (`cs/path-injection`) |
| Phase 7 gate | full diff (path focus) | **path-guard rework** — widened authorized root + reparse crossing; the single highest-risk change. Confirm no traversal escape (`cs/path-injection`) |
| SMB follow-on | full diff | **credential handling**: no clear-text storage/logging of SMB creds; `CredentialRef` only |
| Final acceptance | full diff | clean close-out; attach final counts to `STATUS.md` |

---

## 6. Rules to watch for this feature

- `cs/path-injection` — **the top risk.** Any file/symlink/path use with a
  user-influenced segment (leaf name, host path, mount key, link relative path).
  Fix with strict root containment (resolve, then verify the canonical path is under
  the notebook root **or** a registered `containerSourcePath`) — not string checks.
  The Phase 7 guard widening must not become a traversal escape.
- **Command injection / shell construction** — the displayed apply/remove command
  (plan §6, §11, §20) must be built so no user input can break out of the argument.
  Sanitize/quote; prefer structured argument arrays over string concatenation.
- `cs/log-forging` — handlers logging the user-supplied host path / leaf name. Wrap
  with the project's `LogValueSanitizer.Sanitize(...)` (`GuideAnts.Logging`).
- **Clear-text storage/transmission of secrets** — SMB credentials (follow-on) must
  never be logged, returned in API responses, written to `mounts.json`, or inlined
  into the generated override. Reference a Docker secret / env var via
  `CredentialRef`.
- `js/*` — the folder-tree UI (Phase 10) must not store host paths/commands in
  `localStorage`; host commands are admin-only and fetched, not persisted.

---

## 7. Report-back addition for security-sensitive phases

Each subagent on Phases 5/6/7 (and any SMB work) appends to its report:

```
CODEQL (local, no GitHub parity):
- C# build-mode=none used: <yes>  suites=code-scanning: <yes>
- New findings vs baseline: <count> -> <RuleId @ file:line each, or "none">
- New findings fixed in-code (no suppression): <yes/n-a>
```
