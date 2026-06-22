# Task — Phase 10: Folder tree UI + deletion semantics

> Subagent brief. Execute top to bottom; return the Report-back contract verbatim.
> **Client phase — client build + tests gate.**

## Mission

Add the admin folder-tree affordances for host folder mounts: context-menu actions,
mount display states, the admin host-command display, non-admin behavior, and the
visible deletion-semantics distinction — reusing existing UI components.

## Read first

- `../host-folder-notebook-mounts-plan.md` §15 (folder tree UI: menu items, display
  states, non-admin rules), §19 (deletion semantics — visible distinction), §11
  (endpoint shapes the UI calls), §16/§18 (create/remove flows the UI drives).
- `./DECISIONS.md` → Part B (admin-only, no host-path leak to non-admins, no
  `External/`).
- `./codeql-gate.md` §6 (`js/*`: no host path/command in `localStorage`).
- `src/.cursor/rules/project-rules.mdc` (frontend standards).
- Existing folder-tree component(s) + context menu, and the shared dialog/button/
  toast/spinner components (reuse — do not introduce a new icon library or bespoke
  modal markup).
- The client API layer (`api.ts`) for how authenticated admin calls are made.

## Preconditions

- Phase 8 + Phase 9 gates green (server surface + states exist).

## Guardrails (hard)

- Menu actions (plan §15): `Map host folder here`, `Remove mapped folder`,
  `Show apply command`, `Show remove command`, `Check mapped folders`. Available on
  the **notebook root / notebook file section only**, not arbitrary nested folders.
- Display states (plan §15): `Pending restart`, `Linked`, `Missing source`,
  `Link error`, `Pending removal` — each visually distinct.
- **Non-admins**: can see/use linked mapped folders per normal permissions but
  **cannot** create/remove/repair or view host commands. Gate the menu items and the
  command display on admin — and rely on the server's admin gate too (do not trust
  the client alone).
- **Deletion semantics must be visible** (plan §19): the mount root offers
  `Remove mapped folder` instead of `Delete`; deleting files *inside* the mount is a
  real host operation and should be signposted. Do not present a normal recursive
  delete on a mount root.
- **No host path leak**: never render the original host path to non-admins; do not
  persist host commands/paths in `localStorage` (CodeQL `js/*`).
- **UI-convention gate**: reuse existing `ConfirmationDialog`/action buttons/toast/
  spinner styling; no new icon library; no bespoke modal/button markup.
- TypeScript strict; no `any`; interfaces for props (project rules).

## Tasks

1. Add the admin context-menu actions to the notebook-root/file-section menu, wired
   to the Phase-5 endpoints (create, remove-command, apply-command, reconcile/check).
2. Render mount entries with their display state + an admin host-command view
   (copyable), reusing existing dialog/toast components.
3. Implement the create dialog (collect host path, scope, optional leaf name) and the
   remove flow UI, surfacing the returned command for the admin to run.
4. Enforce non-admin restrictions in the UI (menu/command hidden) while relying on
   server gating for security.
5. Make the deletion-semantics distinction visible (mount-root → Remove mapped
   folder; inside-mount delete signposted as a real host operation).
6. Add component tests (vitest) for: admin sees actions / non-admin does not; display
   states render; mount-root delete is replaced by remove; no host path shown to
   non-admin.

## Files in scope

- The folder-tree component(s) + context menu (client)
- New dialog/state components for mounts (reusing shared primitives)
- Client API calls for the mount endpoints
- `src/client/src/**/*.test.tsx` (new tests)

**Out of scope:** server endpoints/services, docker, the guard.

## Self-verification

```bash
cd src/client && npm run typecheck
cd src/client && npm run build
cd src/client && npm test -- --run
cd src/client && npm run find-orphans   # not worse than baseline
```

Plus global gate (orchestration §4.1).

## Definition of Done

- [ ] All §15 menu actions present on notebook root/file section only; wired to
      Phase-5 endpoints.
- [ ] All five display states render distinctly; admin command view is copyable.
- [ ] Non-admins cannot create/remove/repair or view host commands; no host path
      shown to them; no host path/command in `localStorage`.
- [ ] Deletion-semantics distinction visible (mount root → Remove mapped folder;
      inside-mount delete signposted).
- [ ] UI-convention gate honored (reused components; no new icon lib / bespoke
      markup). `build` + `test -- --run` green; `find-orphans` not worse.

## Report-back contract (return exactly this)

```
PHASE 10 REPORT
- Menu actions added (scoped to root/file section): <list>
- Display states rendered: <list>
- Non-admin restrictions enforced (UI) + server-gated: <yes>
- Host path shown to non-admin: <no>; host command in localStorage: <no>
- Deletion-semantics distinction visible: <how>
- UI-convention gate (reused components, no new icon lib): <pass>
- Tests added: <names/counts>
- Verification: typecheck=<...> build=<...> test=<counts> find-orphans=<delta>
- Files touched: <list>
- Deviations / surprises: <list or "none">
```
