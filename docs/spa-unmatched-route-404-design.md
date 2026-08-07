# SPA unmatched-route 404 — design (Defect A)

**Status:** Design only — no implementation until this document is accepted.  
**Scope:** First defect only — blank screen when React Router matches no route.  
**Out of scope for this change:** inventing `/guides/:id` smart redirects, `returnUrl` allowlisting, docs path cleanup (follow-ups).

---

## 1. Existing routing

### 1.1 Router selection (Electron vs browser)

| Runtime | Detection | Router | URL shape |
|---------|-----------|--------|-----------|
| Electron | `isElectron()` (`window.electron`, UA, `process.versions.electron`, `file:`) | `HashRouter` | `…/index.html#/projects/…` |
| Browser / Azure Container Apps | not Electron | `BrowserRouter` | `https://host/projects/…` |

Source: `src/client/src/utils/environment.ts` (`getRouterType`), `src/client/src/App.tsx`.

**Implication for any redirect:** always use React Router `navigate('/')` (or `<Link to="/">`). Do **not** use `window.location.href = '/'` for the happy path — that breaks HashRouter (drops the hash route and can leave Electron on a useless path).

### 1.2 Shell stack (order matters)

From `App.tsx`, inside the chosen `Router`:

1. `StartupGate` — **browser only** (`enabled={routerType === 'browser'}`); polls `GET ${API_BASE_URL}/startup` until `ready`. Electron skips.
2. `AuthProvider` — cookie session; `GET /api/auth/me` on mount.
3. `GuideAntsGuideProvider` — flyout; reads route via `matchPath` (does not own routing).
4. `AuthExpiredHandler` — on `auth-expired`, `navigate(/login?returnUrl=…)` for non-public paths.
5. `UrlCorrector` — hash malformation only (`##`, missing `#/` in hash mode). Does **not** rewrite unknown SPA paths.
6. `AppContent` — `<Routes>` / `<Route>` table (sole route owner).

Outside / beside:

- **ErrorBoundary** wraps the tree → `ErrorScreen` on thrown render errors (different from unmatched routes).
- **OAuth HashRouter special case** in `App.tsx`: if hash mode and pathname is `/oauth/callback` or `/redirect`, render `OAuthCallback` **before** mounting `HashRouter` (path lives on the document URL, not the hash).

### 1.3 Complete client route table (`AppContent.tsx`)

#### Public (no `ProtectedRoute`)

| Path | Page |
|------|------|
| `/login` | Login |
| `/register` | Register |
| `/oauth/callback` | OAuthCallback |
| `/redirect` | OAuthCallback |
| `/terms` | Terms |
| `/privacy` | Privacy |
| `/public/:friendlyName` | PublicGuide |

#### Authenticated (`ProtectedRoute`)

| Path | Extra guard | Page |
|------|-------------|------|
| `/pending` | role Pending only (others → `/`) | Pending |
| `/change-password` | `mustChangePassword` | ChangePassword |
| `/` | — | Home |
| `/projects` | — | Projects |
| `/conversations` | — | Conversations |
| `/usage` | — | Usage |
| `/cli/authorize` | — | CliAuthorize |
| `/settings` | — | Settings |
| `/settings/system-guides` | Admin; else `/settings` | SystemGuidesWorkspace |
| `/new-project` | Editor (blocks Reader → `/`) | NewProject |
| `/projects/:projectId` | + ProjectProvider | ProjectDetails |
| `/projects/:projectId/edit` | Editor + ProjectProvider | EditProject |
| `/projects/:projectId/notebooks/:notebookId` | + ProjectProvider | NotebookDetails |
| `/projects/:projectId/notebooks/:notebookId/edit` | Editor + ProjectProvider | EditNotebook |
| `/projects/:projectId/notebooks/:notebookId/files/preview` | + ProjectProvider | FilePreviewPage |
| `/projects/:projectId/guides` | Admin + ProjectProvider | GuidesDashboard |
| `/projects/:projectId/guides/guide/new` | Admin + ProjectProvider | GuideEditor |
| `/projects/:projectId/guides/guide/:guideId` | Admin + ProjectProvider | GuideEditor |
| `/projects/:projectId/guides/guide/:guideId/usage` | Admin + ProjectProvider | GuideUsagePage |
| `/projects/:projectId/guides/assistant/new` | Admin + ProjectProvider | AssistantEditor |
| `/projects/:projectId/guides/assistant/:assistantId` | Admin + ProjectProvider | AssistantEditor |
| `/projects/:projectId/guides/assistant/:assistantId/usage` | Admin + ProjectProvider | GuideUsagePage |

**There is no `path="*"` catch-all today.**

### 1.4 Special cases (must not regress)

| Case | Behavior today | Risk if 404 is wrong |
|------|----------------|----------------------|
| **Protected unknown path** | Unmatched → blank (no login redirect) | Catch-all must not sit *inside* a single catch-all ProtectedRoute that changes auth redirects for valid routes |
| **Auth loading** | Spinner on protected routes | Catch-all must not wrap the whole tree |
| **Pending / mustChangePassword** | Hard redirects to `/pending`, `/change-password` | Unchanged |
| **Admin / Editor guards** | `Navigate` to fallback — **matched** routes, not 404 | `/projects/:id/guides` for Reader is redirect-to-home, not NotFound |
| **Param routes with bad IDs** | Still **match** (e.g. `/projects/not-a-guid`) → page/API error UX | Must remain page-owned; **not** NotFound |
| **Auth expiry** | Preserve `pathname+search` as `returnUrl` | Unchanged by this fix |
| **Login `returnUrl`** | Any path starting with `/` accepted | Unchanged; bad returnUrl can still land on NotFound after login (acceptable for this change) |
| **OAuth callback (browser)** | Routes `/oauth/callback`, `/redirect` | Must still match before `*` |
| **OAuth callback (Electron hash)** | Pre-router branch on document pathname | Untouched |
| **UrlCorrector** | Hash-only fixes + reload | Untouched |
| **StartupGate** | Blocks Router until API ready (browser) | NotFound only reachable after gate + Router mount |
| **GuideAnts Guide bridge** | Navigates only known app paths | Untouched |
| **Server SPA fallback** | Non-`/api` GETs get `index.html` 200 | Unchanged; client NotFound is the user-visible fix |
| **API paths `/api/guides/...`** | Never hit React Router | Untouched |

### 1.5 Existing visual pattern for full-page messages

`ErrorScreen` (`src/client/src/components/ErrorScreen.tsx`) is the established full-page style:

- `min-h-screen bg-gray-50`, centered card
- GuideAnts mark (`/code-ants.png`) + “GuideAnts Notebooks” / “AI for people”
- White `rounded-lg shadow-lg` card, title, message, primary blue button, secondary gray home control
- Optional collapsible “Technical Details”

`ErrorBoundary` already uses this component. The 404 UI must reuse it (same layout/classes/branding), not invent a new visual system.

---

## 2. The defect (Defect A)

### 2.1 Symptom

Browser URL (example incident):

`https://…/guides/a71c69b4-69b5-4bb1-b80a-474e9e3b469d`

- Document title: `GuideAnts Notebooks`
- DOM: `#root` → `<div class="h-full"></div>` only
- Auth and `/api/startup` succeed
- No page component mounts; no error UI; no console route diagnostic

### 2.2 Cause

1. Server `MapFallback` serves SPA shell for any UI GET (including `/guides/...`) with HTTP 200.
2. Client route table has no match for `/guides/:id` (and never has).
3. `<Routes>` with no match and no `path="*"` renders **nothing**.

### 2.3 Correct URLs for the same guide entity

| Intent | Path |
|--------|------|
| Run notebook | `/projects/{projectId}/notebooks/{notebookId}` |
| Edit guide definition (Admin) | `/projects/{projectId}/guides/guide/{guideId}` |
| Public published guide | `/public/{friendlyName}` |

### 2.4 Why this is critical

Any bookmark, agent `goto`, markdown link, or typo to an unmatched path produces a **silent white screen** indistinguishable from a total client crash. Electron and browser are both affected once the router has no match.

---

## 3. Proposed changes (minimal)

### 3.1 Goals

1. Unmatched paths show a real NotFound screen in **existing `ErrorScreen` style**.
2. Auto-redirect to **home** (`/`) after **30 seconds**.
3. Zero intentional changes to: auth, OAuth, StartupGate, UrlCorrector, ProtectedRoute guards, param-route matching, Electron router selection.

### 3.2 Code changes (narrow)

| Change | File(s) | Notes |
|--------|---------|-------|
| Add `NotFoundPage` | `src/client/src/pages/NotFound.tsx` (new) | Thin page: render `ErrorScreen` with 404 copy; own 30s timer + `navigate('/')` |
| Register catch-all | `src/client/src/components/AppContent.tsx` | **Last** route only: `<Route path="*" element={<NotFoundPage />} />` — **public** (no `ProtectedRoute`) |
| Optional test id | on NotFound / ErrorScreen wrapper | e.g. `data-testid="not-found-page"` for Playwright |

**Do not** in this change:

- Add `/guides/:guideId` rewrite/recovery
- Change `Login` / `returnUrl` validation
- Change `ErrorBoundary` behavior
- Change server `MapFallback` / `IsUiPath`
- Touch Electron-only OAuth pre-router branch
- Use `window.location.href` for the auto-redirect or primary Home action

### 3.3 `NotFoundPage` behavior

| Element | Spec |
|---------|------|
| Visual | `ErrorScreen` with title like `Page not found`; message that the address is not a valid GuideAnts page; optional technical details = current pathname (+ search) |
| Primary action | “Go to Home” → `navigate('/')` (immediate); cancel pending timer |
| Secondary | Keep `ErrorScreen` home control if used, or rely on primary — avoid duplicate divergent behaviors |
| Auto-redirect | `useEffect` → `setTimeout(30_000)` → `navigate('/')`; clear timeout on unmount / on manual navigate |
| Countdown copy | Optional short line: “Redirecting to home in Ns…” (updates each second) — must still look like existing gray helper text under the card actions |
| Auth | Public route: unauthenticated users see NotFound; after redirect to `/`, existing `ProtectedRoute` sends them to login |
| Electron | Same component; `navigate('/')` updates hash route to `#/` |

### 3.4 Explicit non-goals / non-matches

These must **continue** to hit their existing matched routes (not NotFound):

- `/login`, `/register`, `/terms`, `/privacy`, `/public/anything`
- `/oauth/callback`, `/redirect`
- `/projects/:projectId/...` even with nonsense GUIDs
- `/settings`, `/settings/system-guides` (admin redirect stays admin redirect)
- `/` and all other listed authenticated routes

Only paths with **no** matching pattern (e.g. `/guides`, `/guides/{uuid}`, `/foo`, `/this/does/not/exist`) hit `*`.

### 3.5 Regression surface (why this is safe if kept minimal)

- Catch-all is lowest priority in React Router v6/v7 route ranking.
- No changes to guard components → role redirects unchanged.
- No `window.location` hard navigations → HashRouter safe.
- No server pipeline change → API and SPA fallback unchanged.
- NotFound is a leaf page inside existing providers (toast/auth available but unused for core UX).

---

## 4. Test plan (Playwright — real browser, no mocks)

### 4.1 Harness

| Item | Choice |
|------|--------|
| Runner | `@playwright/test` under `walkthroughs/` (existing headed Chrome + `baseURL`, default `http://localhost:5107`) **or** a sibling suite `walkthroughs/scenarios/routing/` using the same `playwright.config.ts` |
| App under test | Real local stack (`guideants-webapi-ui` on `:5107`) — same as walkthroughs |
| Auth | Real login via existing walkthrough auth helper (`walkthroughs/lib/auth.ts` / `signedIn` fixture) — **no** mocked `AuthContext` |
| Timers | Use Playwright clock (`page.clock.install` + `fastForward`) to advance the 30s redirect without wall-clock waits; do **not** mock `navigate` or stub React Router |
| Electron | See §4.4 — separate thin check; primary suite is BrowserRouter (web) |

Do **not** use Vitest/RTL mocks for this suite. Unit tests of timer math alone are insufficient.

### 4.2 Shared fixtures / helpers (real)

- `signedIn` — session cookie against real API
- `signedOut` — cleared cookies / fresh context
- Known good IDs from env or walkthrough defaults (project / notebook already used by toolbar tour)
- Admin user for guide-editor smoke; Reader user if available for guard smoke

### 4.3 Cases mapped to §1 special cases + defect

Each row is a Playwright test (or a clearly named `test.step` group). Assertions are concrete.

#### A. Defect A — unmatched routes show NotFound

| ID | Setup | Action | Checks |
|----|-------|--------|--------|
| A1 | signed in | `goto /guides/{any-guid}` (incident shape) | `data-testid=not-found-page` visible; GuideAnts branding visible; **not** blank `#root` only; URL path still `/guides/…` |
| A2 | signed in | `goto /this-route-does-not-exist` | NotFound visible |
| A3 | signed in | `goto /guides` | NotFound visible |
| A4 | signed out | `goto /guides/{guid}` | NotFound visible (public catch-all); **not** forced through login first |

#### B. Auto-redirect (30 seconds)

| ID | Setup | Action | Checks |
|----|-------|--------|--------|
| B1 | signed in; clock installed | open unmatched path; `clock.fastForward(29999)` | still on NotFound; URL unchanged |
| B2 | signed in; clock installed | `fastForward` past 30000 | URL is home `/` (or ends with `/`); Home shell visible (e.g. projects/home content or known home test id / heading — same signals walkthroughs use) |
| B3 | signed in | open NotFound; click “Go to Home” before 30s | immediate navigate to `/`; timer must not later fight navigation (stay on home) |

#### C. Matched public routes — must not become NotFound

| ID | Action | Checks |
|----|--------|--------|
| C1 | `goto /login` (signed out) | Login form / “Sign in” — **not** NotFound |
| C2 | `goto /register` | Register UI — not NotFound |
| C3 | `goto /terms` | Terms content — not NotFound |
| C4 | `goto /privacy` | Privacy content — not NotFound |
| C5 | `goto /public/{known-or-unknown-friendly}` | PublicGuide page chrome (or its own empty/error state) — **not** the NotFound page component |

#### D. Matched authenticated routes — smoke (no NotFound)

| ID | Setup | Action | Checks |
|----|-------|--------|--------|
| D1 | signed in | `goto /` | Home — not NotFound |
| D2 | signed in | `goto /projects` | Projects list — not NotFound |
| D3 | signed in | `goto /projects/{validProjectId}/notebooks/{validNotebookId}` | Notebook shell (toolbar / title) — not NotFound |
| D4 | Admin | `goto /projects/{id}/guides/guide/{validGuideId}` | Guide editor chrome — not NotFound |
| D5 | signed in | `goto /settings` | Settings — not NotFound |
| D6 | signed in | `goto /conversations` | Conversations — not NotFound |

#### E. Guards still redirect (matched routes — not NotFound)

| ID | Setup | Action | Checks |
|----|-------|--------|--------|
| E1 | Reader (or non-Admin) | `goto /projects/{id}/guides` | Ends on admin fallback (`/` or allowed page) — **NotFound must not appear** |
| E2 | Reader | `goto /new-project` | Redirected away (home) — not NotFound |
| E3 | signed out | `goto /projects/{id}` | Login with `returnUrl` containing that project path — not NotFound |

#### F. Param match vs catch-all

| ID | Action | Checks |
|----|--------|--------|
| F1 | signed in; `goto /projects/00000000-0000-0000-0000-000000000000` | **Not** NotFound (route matched). May show project error/spinner/empty — page-owned |
| F2 | signed in; `goto /projects/{valid}/notebooks/00000000-0000-0000-0000-000000000000` | **Not** NotFound; notebook page error handling |

#### G. Auth / OAuth / expiry paths untouched

| ID | Setup | Action | Checks |
|----|-------|--------|--------|
| G1 | signed out | `goto /oauth/callback` (no code) | OAuthCallback loading then redirect home/login path — **not** NotFound flash as final state |
| G2 | signed in on `/` | Trigger real 401 on an API call that broadcasts auth-expired **or** dispatch `auth-expired` via `page.evaluate` on the real event bus | Lands on `/login?returnUrl=…` — NotFound not involved |
| G3 | signed out; login with `returnUrl=/guides/dead` | Complete real login | Lands on NotFound for that path (or home if product later allowlists — **this change expects NotFound**), then B2 still applies |

#### H. Server + client contract

| ID | Action | Checks |
|----|--------|--------|
| H1 | `goto /guides/{guid}` | Document response is SPA (status 200 from server is OK); client shows NotFound |
| H2 | `request.get /api/guides/{validGuideId}` (API context with cookies) | JSON 200 — unrelated to NotFound; guards against accidental server route breakage in same PR |

#### I. Style conformance (light visual contract)

| ID | Checks |
|----|--------|
| I1 | NotFound shows `img[alt="GuideAnts"]` (or `/code-ants.png`), title “GuideAnts Notebooks”, card with 404 title |
| I2 | Primary button uses visible “Go to Home” (or equivalent) and is clickable |

### 4.4 Electron / HashRouter

Full Electron packaging in CI is heavy. For this change:

| ID | Approach | Checks |
|----|----------|--------|
| EL1 | Code review + static guarantee: NotFound uses only `navigate` / `<Link>`, never `window.location.href` for home redirect | PR checklist |
| EL2 | Optional follow-up: headed Electron smoke manually — open `#/guides/test`, confirm NotFound, wait/fast-forward, confirm `#/` home | Document as manual release check if no Electron Playwright job exists |

Do **not** mock `isElectron()` in the web Playwright suite; that would not prove HashRouter behavior and violates the “no mocks” intent for routing.

### 4.5 Pass / fail criteria

- All A–I automated cases green against local `:5107` with real auth.
- No existing walkthrough scenario (`notebook/toolbar-tour`, `settings/change-quant`) regressed.
- Manual EL1 checklist signed off in the PR.

### 4.6 Suggested commands (after implementation)

```powershell
# App stack already running on :5107
cd walkthroughs
npm test -- scenarios/routing/not-found.spec.ts
```

(Exact filename TBD at implementation; suite lives next to other walkthrough scenarios.)

---

## 5. Implementation order (when approved)

1. Land this doc (done).
2. Implement `NotFoundPage` + `path="*"` only.
3. Add Playwright suite §4.3.
4. Run walkthrough smoke + new routing suite.
5. Follow-ups (separate PRs): `/guides/:id` recovery, `returnUrl` allowlist, docs path fixes.

---

## 6. Acceptance checklist

- [ ] Unmatched browser paths show ErrorScreen-styled NotFound (not blank).
- [ ] Auto-redirect to `/` after 30s via React Router navigation.
- [ ] Manual Go Home works and cancels the timer.
- [ ] All documented matched routes and guards unchanged.
- [ ] Playwright cases A–I pass without mocking Auth/Router.
- [ ] Electron redirect uses `navigate`, not `window.location.href`.
