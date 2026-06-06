# Task — Phase 5: Frontend

> Subagent brief. Execute top to bottom; return the Report-back contract verbatim.
> This is the largest phase. If schedule demands, it may be split into 5a (auth
> plumbing + routing) and 5b (screens + Settings), but the **same DoD** applies.

## Mission

Build the client auth surface: login/register/pending/change-password screens, the
auth context + guard + 401 handler, credential attachment, the admin Users settings
tab, and role-aware gating — all **reusing existing UI conventions** (no new design
primitives).

## Read first

- `../auth-system-plan.md` §4 → **Phase 5** (5.0 UI conventions, 5.1 experiences
  E1–E8, 5.2 plumbing, 5.3 routing/gating, 5.4 Settings admin-only).
- `./DECISIONS.md` → **D1 = App JWT Bearer (locked)**: `api.ts` attaches
  `Authorization: Bearer <token>`; `AuthContext` stores the token client-side;
  `logout()` just clears it (no server call). **D2 = UserRoles table**: role comes
  from `UserDto.role`.
- **UI canon (reuse, do not reinvent):**
  - `src/client/src/components/common/ConfirmationDialog.tsx` (confirm + form-modal
    portal/overlay/focus/Esc pattern)
  - `src/client/src/pages/settings/components/shared/ActionButtons.tsx`
    (`TextActionButton`/`IconActionButton` + tones)
  - `src/client/src/components/common/Toast.tsx` (`useToast`)
  - `src/client/src/components/LoadingSpinner.tsx`
  - `src/client/src/pages/settings/components/PersonalizationTab.tsx` (canonical
    form/card/field/error styling)
  - `src/client/src/pages/settings/utils.ts` (`getErrorMessage`); icons from
    `react-icons/fa` (+ `react-icons/fi`)
- Waterfall references to adapt (strip MSAL/billing): `pages/Login.tsx`,
  `components/ProtectedRoute.tsx`, `components/AuthExpiredHandler.tsx`,
  `services/authEvents.ts`.
- Existing shell: `src/client/src/App.tsx`, `components/AppContent.tsx`,
  `services/api.ts` (`callApi`), `services/authService.ts`,
  `services/userService.ts`, `pages/Settings.tsx`,
  `pages/settings/components/SettingsTabNavigation.tsx`, `pages/settings/types.ts`,
  `pages/Home.tsx`.
- `./codeql-gate.md` — run the local CodeQL diff (JS focus) before reporting (no
  GitHub parity).

## Preconditions

- Phases 2–4.5 gates green (all endpoints + guards exist). D1 finalized.

## Guardrails (hard) — the §5.0 acceptance gate

- **No new icon library, no bespoke modal/button markup, no one-off styles.** Reuse
  the canon components above. A new `<button className="bg-blue-600…">` for an action,
  a hand-rolled modal, or a new icon pack is an automatic FAIL.
- Full-screen auth pages (E1/E2/E3/E6/E8) use the centered-card layout
  (`min-h-screen … flex items-center justify-center px-4`, inner `w-full max-w-md`,
  `/guide.png` logo) mirroring Waterfall `Login.tsx`.
- Must work under **both** `HashRouter` (Electron) and `BrowserRouter` (web), and
  compose with `StartupGate` and the existing `/oauth/callback` special case.
- **No fallback / no swallowed 401.** On `401`, broadcast `AUTH_EXPIRED_EVENT` and
  redirect to `/login` (preserve `returnUrl`); do not silently retry or mask. Public
  routes (`/login`,`/register`,`/terms`,`/privacy`,`/public/:friendlyName`) are
  exempt from the redirect.
- UI hiding is **UX only** — every hidden/disabled affordance must already be
  enforced by a server policy (Phases 3/4). Do not rely on hiding for security.

## Tasks

**Plumbing (5.2):**
1. `contexts/AuthContext.tsx` — `AuthProvider` + `useAuth` holding `{ user, role,
   status, login, register, logout, isAuthenticated }`; replaces the `authService`
   stub. Store the **JWT client-side** (D1); `logout()` clears it (no server call).
2. `services/api.ts` `callApi()` — attach `Authorization: Bearer <token>` (D1); on
   `401` broadcast `AUTH_EXPIRED_EVENT`.
3. `services/authEvents.ts` + `components/AuthExpiredHandler.tsx` (adapt Waterfall).
4. `services/userService.ts` `getCurrentUser()` — include `role` +
   `mustChangePassword` (extend `UserDto`).

**Routing/shell (5.3):**
5. `components/ProtectedRoute.tsx` — unauth → `/login?returnUrl=…`; `Pending` →
   `/pending`; `MustChangePassword` → `/change-password`; else render.
6. Wire routes in `App.tsx`/`AppContent.tsx`: public `/login`,`/register`; gated
   `/pending`,`/change-password`; wrap feature routes in `ProtectedRoute`; keep
   `/terms`,`/privacy`,`/public/:friendlyName` public. Mount `AuthProvider` +
   `AuthExpiredHandler` inside the router.
7. User menu + sign-out in page headers (match `Home.tsx`’s
   `HomeButton`/`SettingsButton`/`HeaderIconLinkButton` pattern).
8. Role-aware affordances: hide/disable create+edit for `Reader`; hide Admin-only
   surfaces (guides/assistants management, setup wizard) from non-Admins.
8b. Notebook service header toolbar — **two-hook split** (pairs with the Phase 3
   endpoint split, DECISIONS D3). (a) Mount/fetch the full toolbar
   (`NotebookServiceToolbar` + `useNotebookHeaderToolbar`, backed by the now-`RequireAdmin`
   `GET .../header-toolbar`) **only for Admins** — non-admins must never call it.
   (b) Add `api.notebooks.chatReadiness(notebookId, conversationId?)` →
   `GET .../header-toolbar/chat-readiness` and a lean `useNotebookChatReadiness` hook;
   repoint `pages/NotebookDetails.tsx` (`chatModelMissing` from `effectiveModelId`, the
   no-model dialog `blockers`, the Contributor load-and-run state) to it, so
   run-readiness works for every runner without the Admin DTO. (c) The writes the
   toolbar triggers are already Admin (Phase 3:
   `/api/settings/services/.../active-provider`, llama `unload`/`restart`). (d) Update
   `NotebookServiceToolbar.test.tsx` if the mount condition changes.

**Screens (5.1):**
9. `pages/Login.tsx` (E1), `pages/Register.tsx` (E2, fix broken `/login` links from
   Phase 0), `pages/Pending.tsx` (E3), `pages/ChangePassword.tsx` (E8), first-run
   guidance to `/register` (E6).
10. `pages/settings/components/UsersTab.tsx` (E4) — admin-only Settings tab: card +
    `overflow-x-auto` users table; `IconActionButton` row actions; approve/assign,
    change role, deactivate/reactivate (via `ConfirmationDialog`), **set password**
    (form modal on the `ConfirmationDialog` portal pattern); toasts; backed by
    `/api/admin/users`.
11. Extend `PersonalizationTab.tsx` (E5): change-password, read-only current role,
    sign-out.

**Settings admin-gating (5.4):**
12. Filter `SettingsTabNavigation` tabs by role (non-Admin sees **only**
    Personalization); default non-Admin `activeTab` to `'personalization'`.
13. Guard tab content by role in `Settings.tsx` (non-Admin targeting an admin tab →
    not-authorized/fallback to Personalization — defense in depth, not just hiding).
14. Add `'users'` to the `SettingsTab` union (`pages/settings/types.ts`) + nav +
    `Settings.tsx` switch.
15. Gate the setup wizard (`AddAiServicesWizard` in `Home.tsx`, `AddModelWizard`) to
    Admins.
16. Clean up remaining dead auth UI (the `'oss-lite-token'` consumers, `VITE_MSAL_*`)
    now that `AuthProvider` exists.

## Files in scope

New: `pages/Login.tsx`, `pages/Register.tsx`, `pages/Pending.tsx`,
`pages/ChangePassword.tsx`, `pages/settings/components/UsersTab.tsx`,
`components/ProtectedRoute.tsx`, `components/AuthExpiredHandler.tsx`,
`contexts/AuthContext.tsx`, `services/authEvents.ts`.
Modified: `services/api.ts`, `services/authService.ts`, `services/userService.ts`,
`App.tsx`, `components/AppContent.tsx`, `pages/Settings.tsx`,
`pages/settings/components/SettingsTabNavigation.tsx`,
`pages/settings/components/PersonalizationTab.tsx`, `pages/settings/types.ts`,
`pages/Home.tsx`, `components/ErrorScreen.tsx`, `pages/Terms.tsx`,
`pages/Privacy.tsx`, `env.d.ts`.

**Out of scope:** server changes, tool-OAuth client (done in 4.5).

## Self-verification

```
cd src/client && npm run build
cd src/client && npm test -- --run
cd src/client && npm run find-orphans
```

Manual: load under web (`browser:dev`) and reason about Electron `HashRouter`;
verify the 4 redirect cases of `ProtectedRoute`; verify non-Admin Settings shows
only Personalization.

CodeQL (local, per `./codeql-gate.md`, JS focus; no GitHub parity): diff vs
`.codeql/baseline/`; expect **no new** `js/*` clear-text credential storage in
`localStorage` and the JWT is never logged.

## Definition of Done

- [ ] All E1–E8 experiences present; routes wired; works under both routers.
- [ ] `api.ts` attaches `Authorization: Bearer` (D1) and broadcasts 401 (no swallow).
- [ ] §5.0 convention gate: zero new icon libs / bespoke modals / one-off action
      buttons; canon components reused.
- [ ] Non-admins see only Personalization; admin tab content server-guarded too.
- [ ] Notebook service toolbar (full `header-toolbar`) mounted/fetched Admin-only;
      `useNotebookChatReadiness` (`/chat-readiness`) drives run-readiness for all runners.
- [ ] Build + tests (incl. new) green; orphans not increased.

## Report-back contract (return exactly this)

```
PHASE 5 REPORT
- Credential mechanism in api.ts (D1=Bearer): <Authorization: Bearer confirmed?>
- Screens added: E1=<file> E2=<file> E3=<file> E4=<file/tab> E5=<extended> E8=<file>
- ProtectedRoute redirects implemented: unauth/ pending/ mustChangePassword = yes/yes/yes
- 401 handling: broadcasts AUTH_EXPIRED + redirect, public-route exempt = yes/yes
- §5.0 convention check: new-icon-libs=<none?> bespoke-modals=<none?> raw-action-buttons=<none?>
- Settings role-gating: non-admin-only-personalization=<yes?> admin-content-guarded=<yes?>
- Notebook toolbar gating: full-toolbar-admin-only=<yes?> chat-readiness-hook-added=<yes?> NotebookDetails-repointed=<yes?>
- Both routers verified: web=<yes> electron-hashrouter-reasoned=<yes>
- Verification: build=<p/f> tests=<counts incl new> find-orphans=<delta>
- CodeQL (local, no GitHub parity): new-findings-vs-baseline=<count> -> <RuleId@file:line or "none"> js-localStorage-credential-storage=<none?>
- Files touched: <exhaustive list>
- Deviations / surprises: <list or "none">
```
