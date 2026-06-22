# Task — Phase 9: New-notebook project-scope flow + reconciliation engine

> Subagent brief. Execute top to bottom; return the Report-back contract verbatim.

## Mission

Make project-scoped mappings automatically apply to **newly created notebooks**, and
implement the **reconciliation engine** that is the source of truth for symlink
state (plan §6 self-restart caveat → startup reconciliation is authoritative).

## Read first

- `../host-folder-notebook-mounts-plan.md` §17 (new notebook flow), §10
  (reconciliation method list + when to run), §16 (create flow steps 9–11), §6
  (callback is best-effort).
- `./DECISIONS.md` → Part B (compose change not needed for new symlinks; startup
  reconciliation authoritative; no fallback).
- Phase 4 service (the reconcile stubs you now implement), Phase 6 symlink/registry,
  Phase 7 guard.
- The notebook-creation code path (find where notebooks are created) and the API
  startup/hosted-service wiring.

## Preconditions

- Phase 7 gate green. (May run in parallel with Phase 8 after Phase 7.)

## Guardrails (hard)

- New notebook flow (plan §17): after a notebook is created, for each **active
  project-scoped** mount: create `HostFolderMountLink`, create the symlink **if the
  source exists**, write `mounts.json`. **No Compose change** (source already
  mounted).
- If the source is absent, link → `PendingRestart`/`LinkError` (explicit, surfaced)
  — **never** a silent skip that looks like success.
- Implement the three reconcile methods (plan §10) and run reconciliation:
  - on **API startup** (authoritative),
  - after the **helper command callback** (best-effort/redundant — must not be a
    dependency),
  - after **notebook creation**,
  - on **admin "Check mappings"** (the Phase-5 reconcile endpoint).
- Reconciliation must: create missing symlinks for active mounts, refresh
  `mounts.json`, update statuses, and (coordinating with Phase 11) flag/clean stale
  links — but **never** delete host content.
- **No fallback:** a mount whose source is missing stays `Missing source`/
  `PendingRestart`; do not coerce to `Active`/`Linked`.

## Tasks

1. Hook notebook creation to apply active project-scoped mounts per §17.
2. Implement `ReconcileNotebookMountsAsync`, `ReconcileProjectMountsAsync`,
   `ReconcileAllMountsAsync` filling the Phase-4 seams, using Phase-6 symlink/registry
   ops.
3. Wire startup reconciliation (a hosted service / startup task) calling
   `ReconcileAllMountsAsync`, and ensure the Phase-5 reconcile endpoint + callback
   path invoke the scoped reconcile.
4. Add integration tests:
   - create a project mount (active) → create a new notebook → it gets a link +
     symlink + `mounts.json` entry.
   - source absent → link `PendingRestart`/`LinkError`, surfaced.
   - startup reconciliation re-creates a missing symlink for an active mount.

## Files in scope

- `src/server/GuideAntsApi/Services/Components/HostFolderMountService.cs` (reconcile
  impl)
- The notebook-creation service/handler (hook point)
- Startup/hosted-service registration for `ReconcileAllMountsAsync`
- `src/server/GuideAntsApi.IntegrationTests/*`

**Out of scope:** remove flow + stale cleanup ownership (Phase 11 — but coordinate
the shared reconcile code), UI, guard, sync.

## Self-verification

```bash
cd src/server && dotnet build GuideAntsApi.sln
cd src/server && dotnet test GuideAntsApi.IntegrationTests/GuideAntsApi.IntegrationTests.csproj --filter "Reconcile|NewNotebook|Mount"
```

Plus global gate (orchestration §4.1).

## Definition of Done

- [ ] New notebook in a project with an active mount gets link + symlink +
      `mounts.json` (no Compose change) — proven by integration test.
- [ ] Source-absent → explicit `PendingRestart`/`LinkError` (no silent skip).
- [ ] All three reconcile methods implemented; reconciliation runs at startup (+
      after callback, after notebook creation, on Check mappings).
- [ ] Callback is best-effort, not a dependency; startup reconciliation authoritative.
- [ ] No host-content deletion in reconciliation.

## Report-back contract (return exactly this)

```
PHASE 9 REPORT
- New-notebook project-scope apply (test name): <name>
- Reconcile methods implemented: <list>
- Reconciliation triggers wired: startup=<y> callback=<y/best-effort> notebook-create=<y> check-mappings=<y>
- Source-absent handling: <PendingRestart/LinkError surfaced? yes>
- Startup-recreate-missing-symlink (test name): <name>
- Verification: build=<...> integration=<counts>
- Files touched: <list>
- Deviations / surprises: <list or "none">
```
