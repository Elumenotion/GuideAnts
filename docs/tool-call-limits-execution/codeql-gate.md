# CodeQL Gate

Last updated: 2026-07-12

**Run only at Phase 7 close-out.** Do not run CodeQL diffs after Phases 1–6.

## Procedure

1. **Pre-flight (before Phase 1):** capture baseline SARIF on clean `main` (or the branch
   base) → `.codeql/baseline/tool-call-limits/`. Store only; no diff yet.
2. **Phase 7 (after all implementation phases):** run CodeQL on the final tree and diff vs
   baseline.
3. Pass when **new** findings vs baseline are zero on **feature-changed source files**
   (`.cs`/`.ts`/`.tsx` from `git diff origin/main` + untracked). Full-tree diffs may include
   environmental noise (`.codeql/db-*` copies, `.build/`, `publish/web.config`) on dirty
   working trees — ignore those for this gate.

**CLI path (this machine):** `C:\Users\dougl\tools\codeql\codeql.exe` — pass
`-CodeqlPath` to `scripts/run-codeql-sln-triage.ps1` if `codeql` is not on PATH.

## Focus areas

- Limit message injection (no user-controlled content in synthetic tool results beyond
  configured integers)
- No secret leakage in limit trace / SSE events
- `Agent.Invoke` nested budget cannot be bypassed via depth manipulation

## Fail modes

- Any new high/critical finding vs baseline → hard reject; fix in code and re-run CodeQL once
  at close-out (not per-phase).
