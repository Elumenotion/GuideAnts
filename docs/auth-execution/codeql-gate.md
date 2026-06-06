# CodeQL Security Gate (local, baseline-vs-current)

Companion to [`00-orchestration.md`](./00-orchestration.md). Full tool reference:
[`../codeql-local-solution-runbook.md`](../codeql-local-solution-runbook.md).

The auth work touches the exact areas CodeQL flags hardest — credential handling,
logging of user-controlled input, file/path operations, and OAuth token exchange —
so a CodeQL pass is a **first-class verification gate**, not an afterthought.

---

## 1. Local-only adaptation (READ THIS)

The runbook is written around **GitHub Code Scanning parity**: it fetches GitHub's
open alerts (`fetch-github-code-scanning.ps1`) and **fails** unless the local SARIF
reproduces every GitHub alert at the current SHA.

**That entire GitHub half does not apply here.** This feature branch is **not on
GitHub** and will not be until the job is done, so there is no remote baseline to
fetch or match. Therefore:

- **Do NOT run** `scripts/fetch-github-code-scanning.ps1`.
- **Do NOT enforce GitHub parity** (`compare-codeql-github-parity.ps1`,
  `scan-results.txt`, `parity_passed`). With no remote alerts, parity is undefined,
  not "passing" — ignore it.
- We substitute **local baseline-vs-current**: snapshot CodeQL findings at the
  pre-flight commit, then re-scan after each security-sensitive phase and **diff**.
  The gate is **"no NEW findings vs the baseline"** (plus: fix anything pre-existing
  the auth code now touches).

Everything else in the runbook **still applies** — especially the extraction modes,
suites, and "don't suppress, fix" policy below.

---

## 2. Non-negotiables carried over from the runbook

- **C# extraction MUST be `--build-mode=none --source-root=.`** Never use
  `dotnet build GuideAntsApi.sln` for the security scan — sln mode **hides most
  `cs/path-injection` findings** (runbook §"GitHub parity" + failure mode #4). A C#
  scan returning only ~3 `web.config` hits means you are in the wrong mode.
- **Code-scanning suites only**: `csharp-code-scanning.qls`,
  `python-code-scanning.qls`, `javascript-code-scanning.qls`. **Never**
  `csharp-security-and-quality.qls` (~1000 noise rows, failure mode #5).
- **All three languages** (csharp + python + javascript). JS extracts from
  `src/client`.
- **No suppression.** Do **not** use CodeQL barrier/model packs to silence alerts
  (runbook failure mode #6) — **fix the code**. This is the same spirit as the
  project's "no fallback" rule.
- CodeQL exe: `C:\Users\dougl\tools\codeql\codeql.exe` (or `-CodeqlPath`).

---

## 3. Commands (GitHub-free)

### Preferred: wrapper, parity skipped

```powershell
# from repo root
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run-codeql-sln-triage.ps1 -CleanCodeqlOutputs -SkipGitHubParityCheck
```

`-SkipGitHubParityCheck` is the documented escape hatch. The runbook labels it
"debugging only — not before merging to `main`"; **that warning is about GitHub
parity, which does not exist for this branch**, so it is the correct switch here.
If the wrapper still hard-requires `scan-results.txt`, use the manual path below
instead (it has **zero** GitHub dependency by construction).

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

At the starting commit (before Phase 0), run a full scan and **save the SARIFs and a
findings snapshot** out of the way of later overwrites:

```powershell
# after a clean scan (section 3)
New-Item -ItemType Directory -Force .codeql/baseline | Out-Null
Copy-Item .codeql/results-*.sarif .codeql/baseline/
# normalized findings list = RuleId + file + region, per language
```

Record per-language and per-rule counts in [`STATUS.md`](./STATUS.md). Expected
order-of-magnitude on a `main`-like tree (runbook §"Expected scale"): **C# ~15–25,
Python ~33, JS ~6**. If C# is ~3, you are in sln mode — redo with `build-mode=none`.

> The baseline is **informational**, not a pass bar. Pre-existing findings are not
> this project's job to fix wholesale — but any pre-existing finding that the auth
> code now **touches/extends** must be fixed, and **no new finding may be added**.

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
# NEW findings introduced by our work:
Compare-Object $base $now -Property RuleId,File -PassThru | Where-Object SideIndicator -eq '=>'
```

(Use the wrapper's `.codeql/triage.csv` instead if it produced one — same idea via
`Import-Csv` + `Compare-Object`.)

**Gate passes when the NEW-findings set is empty.** Any new row blocks the phase
(orchestration §5 → classification `fallback/masking` if it's a swallowed risk,
otherwise `missing DoD`).

---

## 5. When to run it

| Point | Scan | Why |
|---|---|---|
| Pre-flight | full baseline | establishes the comparison set |
| Phase 2 gate | full diff | password hashing, token issuance, **logging of email/username** (`cs/log-forging`), no clear-text secret storage |
| Phase 3 gate | full diff | new endpoint wiring; ensure no path/redirect/`cs/web/*` regressions |
| Phase 4 gate | full diff | admin set-password (clear-text password storage/logging), user enumeration |
| Phase 4.5 gate | full diff | **OAuth token exchange + encryption** (`cs/path-injection` on any file work, clear-text token storage); JS clear-text storage should **decrease** as `localStorage` tokens are removed |
| Phase 5 gate | full diff (JS focus) | client token handling; `localStorage` of credentials (`js/*` clear-text storage) |
| Final acceptance | full diff | clean close-out; attach final counts to `STATUS.md` |

## 6. Rules to watch for the auth work

- `cs/log-forging` — auth handlers logging user-controlled `email`/`name`. Fix by
  wrapping with `LogValueSanitizer.Sanitize(...)` from `GuideAnts.Logging` (runbook
  §"Log forging").
- `cs/path-injection` — any file/path use with user-controlled segments (relevant if
  Phase 4.5 or admin work touches file APIs). Fix with strict root containment
  (`PathGuard` in `ScriptExecutionAgent`), not string checks (runbook §"Path
  injection").
- Clear-text storage/transmission of sensitive info — **passwords** (Phase 2/4) and
  **OAuth tokens** (Phase 4.5) must be hashed/encrypted; never logged. JWT secret
  must come from config, never hard-coded (`cs/hardcoded-credentials`).
- `js/*` clear-text storage in `localStorage` — Phase 4.5 **removes** the
  `oauth_tokens_*` localStorage writes, so any such JS finding should disappear; if
  one remains after 4.5, the removal is incomplete.

## 7. Report-back addition for security-sensitive phases

Each subagent on Phases 2/3/4/4.5/5 appends to its report:

```
CODEQL (local, no GitHub parity):
- C# build-mode=none used: <yes>  suites=code-scanning: <yes>
- New findings vs baseline: <count> -> <RuleId @ file:line each, or "none">
- New findings fixed in-code (no suppression): <yes/n-a>
```
