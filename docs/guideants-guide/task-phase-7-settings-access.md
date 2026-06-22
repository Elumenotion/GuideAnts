# Task — Phase 7: Settings access entry & route guard

> Subagent brief. Execute top to bottom; return the Report-back contract verbatim.

## Mission

Give admins a way into the System Guides workspace from Settings — and **only**
admins. Non-admins must not see the link and must be blocked (client + server) from
the system project route.

## Read first

- `../guideants-guide-implementation-plan.md` §7 (Settings link + route protection;
  read the `/settings/system-guides` recommendation in §7.2).
- `./DECISIONS.md` → **D-GG-2** (link shape — `/settings/system-guides` proposed),
  **D-GG-E** (server 404s non-admins anyway), **D-GG-F** (roles).
- `Settings.tsx`, `SettingsTabNavigation.tsx` (header area vs tabs — the link goes
  in the **header row**, not personalization tabs).
- `AppContent.tsx`, `ProtectedRoute.tsx` (`requireAdmin` pattern), `HeaderIconLinkButton`.
- `GET /api/system-guide/workspace` (Phase 4) for resolving the project id.

## Preconditions

- Phase 4 gate green (`GET /api/system-guide/workspace` admin-only). **D-GG-2 locked.**

## Guardrails (hard)

- **Admin-only**: the link renders only for `role === 'Admin'`; the route is wrapped
  in `ProtectedRoute requireAdmin`. The server (Phase 4) is the real boundary — this
  is defense-in-depth, not the only check.
- **D-GG-2 (LOCKED `/settings/system-guides`)**: the route resolves the system
  project id from `GET /api/system-guide/workspace` (no raw UUID in the URL bar).
- Do **not** add the link to Home, Projects, or global nav.
- No fallback: non-admin hitting the route → redirect (UX) and the server returns
  404 anyway. Do not silently render an empty page.
- Frontend only (the workspace endpoint already exists from Phase 4).

## Tasks

1. Add the admin-only **System Guides** entry to the Settings header row
   (`HeaderIconLinkButton`), distinct from the flyout button placement.
2. Per D-GG-2: add `/settings/system-guides` route in `AppContent.tsx` wrapped in
   `ProtectedRoute requireAdmin`; on mount fetch `GET /api/system-guide/workspace`
   to resolve the project id and render the project workspace (new
   `SystemGuidesWorkspace.tsx` that reuses `ProjectDetails`, or navigate
   internally). Non-admin → `<Navigate to="/settings" replace />`.
3. Component test: link visible for Admin only; non-admin navigating the route is
   redirected.

## Files in scope

- `src/client/src/pages/Settings.tsx`
- `src/client/src/AppContent.tsx`
- `src/client/src/pages/SystemGuidesWorkspace.tsx` (if D-GG-2 = settings sub-route)
- `src/client/src/components/ProtectedRoute.tsx` (only if a new guard variant is needed)
- `src/client/src/...__tests__/…` (component test)

**Out of scope:** server, seeder, flyout, publish UI.

## Self-verification

```powershell
cd src/client; npm run build
cd src/client; npm test -- --run
```

## Definition of Done

- [ ] Admin-only System Guides link in Settings header (not in Home/Projects/global nav).
- [ ] Route guarded `requireAdmin`; resolves project id from workspace API (D-GG-2);
      non-admin redirected.
- [ ] Admin can reach the workspace and edit guide instructions.
- [ ] Component test green; build + client tests green.

## Report-back contract (return exactly this)

```
PHASE 7 REPORT
- Settings link: admin-only? <yes>  placement=<header row>  added elsewhere? <no>
- Route (D-GG-2): <path>  guard=<ProtectedRoute requireAdmin>  id resolved via=<workspace API>
- Non-admin route behavior: <redirect target>  (server still 404: yes)
- Admin reaches workspace + edits guide: <verified how>
- Component test: <name>  build: <pass/fail>  client tests: <counts>
- Files touched: <exhaustive list>
- Deviations / surprises: <list or "none">
```
