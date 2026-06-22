# CodeQL Security Gate (local, baseline-vs-current)

Companion to [`00-orchestration.md`](./00-orchestration.md). Full tool reference:
[`../codeql-local-solution-runbook.md`](../codeql-local-solution-runbook.md).

This feature touches the exact areas CodeQL flags hardest — **token validation and
issuance, logging of user-controlled claims, and client-side credential handling**
— so a CodeQL pass is a **first-class verification gate**, not an afterthought.

---

## 1. Local-only adaptation (READ THIS)

The runbook is written around **GitHub Code Scanning parity** (it fetches GitHub's
open alerts and fails unless the local SARIF reproduces them). **That entire GitHub
half does not apply here** — this feature branch is not on GitHub, so there is no
remote baseline. Therefore:

- **Do NOT run** `scripts/fetch-github-code-scanning.ps1`.
- **Do NOT enforce GitHub parity** (`compare-codeql-github-parity.ps1`,
  `scan-results.txt`, `parity_passed`). With no remote alerts, parity is undefined.
- We substitute **local baseline-vs-current**: snapshot CodeQL findings at the
  pre-flight commit, re-scan after each security-sensitive phase, and **diff**. The
  gate is **"no NEW findings vs the baseline"** (plus: fix anything pre-existing the
  feature code now touches).

Everything else in the runbook **still applies** — extraction modes, suites, and
"don't suppress, fix" policy below.

---

## 2. Non-negotiables carried over from the runbook

- **C# extraction MUST be `--build-mode=none --source-root=.`** Never use
  `dotnet build GuideAntsApi.sln` for the security scan — sln mode hides most
  `cs/path-injection` findings. A C# scan returning only ~3 `web.config` hits means
  you are in the wrong mode.
- **Code-scanning suites only**: `csharp-code-scanning.qls`,
  `python-code-scanning.qls`, `javascript-code-scanning.qls`. **Never**
  `csharp-security-and-quality.qls` (~1000 noise rows).
- **All three languages** (csharp + python + javascript). JS extracts from `src/client`.
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

`-SkipGitHubParityCheck` is the documented escape hatch; the runbook's warning
about it is about GitHub parity, which does not exist for this branch. If the
wrapper still hard-requires `scan-results.txt`, use the manual path below.

### Manual per-language (no GitHub dependency at all)

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

At the starting commit (before Phase 1), run a full scan and **save the SARIFs**:

```powershell
New-Item -ItemType Directory -Force .codeql/baseline | Out-Null
Copy-Item .codeql/results-*.sarif .codeql/baseline/
```

Record per-language and per-rule counts in [`STATUS.md`](./STATUS.md). The baseline
is **informational**, not a pass bar — but any pre-existing finding the feature code
**touches/extends** must be fixed, and **no new finding may be added**.

### 4.2 Diff (per gate)

Re-scan, then compare current findings to `.codeql/baseline/`. A finding is **NEW**
if its (RuleId, file, ~region) is present now but not in the baseline:

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

**Gate passes when the NEW-findings set is empty.** Any new row blocks the phase
(orchestration §5 → `fallback/masking` if it's a swallowed risk, else `missing DoD`).

---

## 5. When to run it

| Point | Scan | Why |
|---|---|---|
| Pre-flight | full baseline | establishes the comparison set |
| **Phase 2** gate | full diff | AppIdentity **JWT validation** (reads `GuideAnts.Auth` cookie); logging of user/claims (`cs/log-forging`); no clear-text token storage; no hard-coded signing key |
| **Phase 4** gate | full diff | new system endpoints (session = config only, no token); no token logging, no path/redirect regressions |
| **Phase 6** gate | full diff (**JS focus**) | flyout relies on the session cookie (D-GG-1) — confirm **no** `localStorage`/`sessionStorage` token write (`js/clear-text-storage`) and nothing logs a token |
| Final acceptance | full diff | clean close-out; attach final counts to `STATUS.md` |

(Phases 1, 3, 5, 7 are not security-sensitive; the build/test gate covers them. Run
CodeQL there too if a phase unexpectedly touches auth/token/file code.)

---

## 6. Rules to watch for this feature

- `cs/log-forging` — the `AppIdentity` validator / session endpoint logging
  user-controlled claims (user id, email, name, raw token). Fix by wrapping with
  `LogValueSanitizer.Sanitize(...)` from `GuideAnts.Logging`; **never log the
  token/cookie value**.
- **Clear-text storage/transmission of sensitive info** — published-guide secrets
  must not be stored in clear text or logged. The JWT **signing key** must come
  from config, never hard-coded (`cs/hardcoded-credentials`).
- `js/clear-text-storage` (client) — auth is the same-host cookie (D-GG-1); the
  flyout stores **no** token. Any `localStorage`/`sessionStorage` write of a token
  is a hard FAIL (D-GG-H).
- `cs/path-injection` — only relevant if the seeder's bootstrap-guide import builds
  file paths from user/config input; keep imports rooted under
  `Resources/bootstrap/guides/` with strict containment.

---

## 7. Report-back addition for security-sensitive phases

Each subagent on Phases **2, 4, 6** appends to its report:

```
CODEQL (local, no GitHub parity):
- C# build-mode=none used: <yes>  suites=code-scanning: <yes>
- New findings vs baseline: <count> -> <RuleId @ file:line each, or "none">
- New findings fixed in-code (no suppression): <yes/n-a>
```
