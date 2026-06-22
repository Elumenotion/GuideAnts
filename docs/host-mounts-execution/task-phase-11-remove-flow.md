# Task — Phase 11: Remove flow + stale-symlink reconciliation

> Subagent brief. Execute top to bottom; return the Report-back contract verbatim.
> **Docker gate required (remove rewrites the override + restarts affected only).**

## Mission

Complete the removal lifecycle: admin-initiated mapping removal that unlinks symlinks
everywhere, drives the host remove command, and reconciles to `Removed` — plus stale
symlink cleanup — all without ever deleting host content, and with normal folder
delete on a mount root blocked server-side.

## Read first

- `../host-folder-notebook-mounts-plan.md` §18 (remove flow steps + symlink
  behavior), §19 (deletion semantics — block normal delete on a mount root), §10
  (reconciliation removes stale symlinks).
- `./DECISIONS.md` → Part B (no host-content deletion; no fallback; startup
  reconciliation authoritative).
- `./docker-gate.md` §3.4–3.5 (scoped restart, idempotent override after remove).
- Phase 6 symlink/registry ops, Phase 9 reconciliation engine, Phase 2 helper-script
  `remove`.

## Preconditions

- Phase 10 gate green.

## Guardrails (hard)

- Remove flow (plan §18), admin-only: mark mount `PendingRemoval` → remove symlinks
  from **all** affected notebooks → update each `mounts.json` → mark links
  `Unlinked` → return remove command. **Symlinks are removed before the compose
  restart.**
- **Never delete host contents.** Removing symlinks/mapping only.
- After the host command confirms `/app/HostMounts/{mountKey}` is gone (startup/
  callback reconciliation), mark mount `Removed`.
- **Stale cleanup**: reconciliation removes symlinks for removed mappings.
- **Failure handling (no fallback):** if the API cannot remove a symlink, keep the
  mapping in `Error`/`PendingRemoval` with admin-facing remediation — **never** mark
  `Removed`/`Unlinked` optimistically.
- **Block normal folder delete on a mount root server-side** (plan §19) — not just
  hidden in the UI. Deleting files *inside* the mount remains a real host operation.
- If the host command is never run, `/app/HostMounts/{mountKey}` stays mounted but
  unreachable from notebooks — handle that state explicitly (do not crash/loop).

## Tasks

1. Implement the remove flow in the service + the Phase-5 remove endpoint wiring:
   status transitions, unlink-all, registry update, remove command text.
2. Implement stale-symlink reconciliation (extend Phase-9 reconcile): detect links
   for `PendingRemoval`/`Removed` mounts and remove them; confirm source gone →
   `Removed`.
3. Add the server-side block for normal folder delete on a mount root (coordinate
   with the folder/delete endpoint + sync rules from Phase 8).
4. Add tests (unit + integration):
   - remove unlinks all notebooks, updates registries, returns the command, never
     deletes host content.
   - symlink-removal failure → mapping stays `Error`/`PendingRemoval` with
     remediation (not `Removed`).
   - stale reconciliation removes orphaned links and marks `Removed` after source
     gone.
   - normal folder delete on a mount root is blocked (server returns a guarded
     error).
5. Confirm the Phase-2 `remove` helper script + this flow agree (override rewritten
   without the source; affected-only restart) — run the docker gate.

## Files in scope

- `src/server/GuideAntsApi/Services/Components/HostFolderMountService.cs` (remove +
  stale reconcile)
- The Phase-5 remove endpoint wiring (if not fully done there)
- The folder/delete endpoint (mount-root delete block)
- `src/server/GuideAntsApi.Tests/*`, `GuideAntsApi.IntegrationTests/*`

**Out of scope:** UI (done Phase 10 — only adjust if a contract changed), docker
launcher logic (Phase 1), data model.

## Self-verification

```bash
cd src/server && dotnet build GuideAntsApi.sln
cd src/server && dotnet test GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "Remove|Reconcile|Mount"
cd src/server && dotnet test GuideAntsApi.IntegrationTests/GuideAntsApi.IntegrationTests.csproj --filter "Remove|Mount"
# docker gate after a remove rewrite:
docker compose -f docker/docker-compose.ghcr-cpu.yml -f docker/docker-compose.host-mounts.generated.yml config > /dev/null && echo CONFIG_OK
```

Plus global gate (orchestration §4.1) + docker gate.

## Definition of Done

- [ ] Remove flow per §18; symlinks removed before restart; host content never
      deleted.
- [ ] Stale reconciliation removes orphaned links; mount → `Removed` only after
      source confirmed gone.
- [ ] Symlink-removal failure → `Error`/`PendingRemoval` + remediation (no optimistic
      `Removed`).
- [ ] Normal folder delete on a mount root blocked **server-side**.
- [ ] Docker gate green (override rewritten without source; affected-only restart).

## Report-back contract (return exactly this)

```
PHASE 11 REPORT
- Remove flow status transitions: <PendingRemoval -> Unlinked -> Removed path>
- Symlinks removed before restart: <yes>; host content preserved (test): <name>
- Stale reconciliation (test name): <name>
- Removal-failure stays Error/PendingRemoval (test name): <name>
- Mount-root delete blocked server-side (test name): <name>
- DOCKER GATE: config-after-remove=<ok> scoped-restart=<yes> idempotent=<yes>
- Verification: build=<...> unit=<counts> integration=<counts>
- Files touched: <list>
- Deviations / surprises: <list or "none">
```
