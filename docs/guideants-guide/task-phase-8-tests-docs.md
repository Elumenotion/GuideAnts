# Task — Phase 8: Tests sweep, docs & final acceptance

> Subagent brief. Execute top to bottom; return the Report-back contract verbatim.

## Mission

Close out the feature: ensure the full test matrix is automated, document the
system guide, and execute the manual acceptance script. This phase **adds coverage
and docs only** — it does not change feature behavior. If a behavior gap is found,
report it (do **not** fix it here by smuggling logic into a later-phase file).

## Read first

- `../guideants-guide-implementation-plan.md` §10 (full testing plan —
  §10.1 platform auth, §10.2 system project + flyout, §10.3 manual acceptance),
  §1 (success criteria).
- `./DECISIONS.md` → all (especially D-GG-3 acceptance scope).
- `./00-orchestration.md` §6 (final acceptance checklist).
- Existing test conventions (server integration test base; client RTL setup).

## Preconditions

- Phases 1–7 gates all green in `STATUS.md`.

## Guardrails (hard)

- **No behavior changes.** Tests must not be weakened to pass; any red test is a
  real defect → report it, do not mask it (no `fallback`, no assertion-softening).
- Acceptance bar per **D-GG-3** (chat round-trip is sufficient; `AppEcho` verified
  via test hook / explicit prompt, not required unprompted).
- Docs are concise; **no secrets** in examples.

## Tasks

1. Audit §10.1 + §10.2 rows; add any missing automated test so **every** row is
   covered (server integration + client component).
2. Execute the §10.3 manual acceptance script (Contributor + Admin flyout chat,
   system-project hiding, blocked direct nav, Auth-tab AppIdentity indicator, admin
   guide edit, optional `AppEcho`). Record results in `STATUS.md`.
3. Add a brief System Guide section to `docs/developer-config-guide.md` (what it is,
   that it's seeded + hidden, AppIdentity auth, where admins edit it).
4. Run the **final CodeQL diff** (`codeql-gate.md`) and record final counts in
   `STATUS.md`.
5. Tick the `00-orchestration.md` §6 final-acceptance checklist in `STATUS.md`.

## Files in scope

- Test files under `GuideAntsApi.Tests/**` and `src/client/src/**/__tests__/**`
- `docs/developer-config-guide.md`
- `docs/guideants-guide/STATUS.md` (ledger + acceptance)

**Out of scope:** feature source files (report gaps; don't patch behavior here).

## Self-verification

```powershell
cd src/server; dotnet build GuideAntsApi.sln; dotnet test GuideAntsApi.sln
cd src/client; npm run build; npm test -- --run
# + final CodeQL diff per codeql-gate.md
```

## Definition of Done

- [ ] Every §10.1/§10.2 row has a passing automated test.
- [ ] §10.3 manual acceptance executed + recorded in `STATUS.md`.
- [ ] `developer-config-guide.md` System Guide section added.
- [ ] Final CodeQL diff = 0 new; counts in `STATUS.md`.
- [ ] §6 final-acceptance checklist all ticked.

## Report-back contract (return exactly this)

```
PHASE 8 REPORT
- §10.1 rows covered: <n/n>  §10.2 rows covered: <n/n>  new tests: <names/counts>
- §10.3 manual acceptance: <each step pass/fail>
- Behavior gaps found: <list or "none"> (NOT fixed here)
- Docs updated: developer-config-guide.md System Guide section: <yes>
- Final CodeQL: C#=<n> Python=<n> JS=<n>  new vs baseline=<0?>
- §6 final-acceptance checklist: <all ticked? yes/no>
- server tests: <counts>  client tests: <counts>  build: <pass/fail>
- Files touched: <exhaustive list>
- Deviations / surprises: <list or "none">
```
