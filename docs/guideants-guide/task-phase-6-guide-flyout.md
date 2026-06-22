# Task — Phase 6: Guide flyout (button, provider, chat, bridge stub)

> Subagent brief. Execute top to bottom; return the Report-back contract verbatim.
> **Security-sensitive phase — CodeQL gate applies (JS focus, see `codeql-gate.md`).**

## Mission

Add the in-app GuideAnts Guide flyout:

1. `GuideAntsGuideProvider` (state + session fetch + bridge lifecycle), mounted in
   `App.tsx` inside auth context.
2. `GuideAntsGuideButton` — the **leftmost** action in **every** `HeaderActionsBar`
   call site; hidden for unauthenticated/Pending users.
3. `GuideAntsGuideFlyout` — panel hosting `guideants-chat`, authed by the same-host
   session cookie (per **D-GG-1**, no token wiring), with context provider, ASR
   enabled (**D-GG-5**), and the `guideantsAppBridge` stub (`AppEcho`).

## Read first

- `../guideants-guide-implementation-plan.md` §8 (all subsections — components,
  header integration with the **full call-site list in §8.2**, flyout UX, chat
  wiring, bridge stub).
- `./DECISIONS.md` → **D-GG-1** (same-host cookie auth — flyout does **not** wire a
  token), **D-GG-H** (no client token storage), **D-GG-F** (button visibility by
  role), **D-GG-J** (stub `AppEcho` only), **D-GG-5** (panel + admin badge + **ASR
  enabled**).
- `src/client/src/pages/PublicGuide.tsx` (lazy `import('guideants')` + `createElement('guideants-chat')` pattern).
- `WormCommander/Game/src/voice-snakes/speech.ts` (`registerTool` + `setContextProvider` reference).
- `HeaderActionsBar.tsx` (child ordering with `align="end"`).
- `App.tsx`, `AuthContext`/`authService` (role + auth status). Note: auth to the
  published-guide endpoints rides the **same-host `GuideAnts.Auth` cookie**
  automatically (D-GG-1) — there is no JS token to read or set.
- `services/api.ts` (add the system-guide session API call; uses `withAuthFetchInit`
  → `credentials:'include'`).

## Preconditions

- Phase 4 gate green (`GET /api/system-guide/session` returns pub-id config).
  D-GG-1 locked (same-host cookie auth).

## Guardrails (hard)

- **D-GG-1**: the published-guide requests `guideants-chat` makes are authed by the
  **same-host `GuideAnts.Auth` cookie** automatically. Do **not** call
  `chat.setAuthToken(...)` for auth and do **not** call a non-existent
  `authService.getAccessToken()`. **Verification checkpoint:** confirm the
  component actually transmits the cookie to `api-base-url` (same-origin fetch sends
  cookies by default; cross-port needs `credentials:'include'` + CORS, which the app
  already allows). If the component omits credentials and there is no supported way
  to enable them, **stop and escalate** — do **not** invent a token fallback.
- **D-GG-H**: store **no** token anywhere. No `localStorage`/`sessionStorage`
  writes; never log a token/cookie value.
- **D-GG-F**: hide `GuideAntsGuideButton` when `status !== 'authenticated'` or
  `role === 'Pending'`.
- Button is the **first child** (leftmost with `align="end"`) in **every** call site
  listed in plan §8.2 — do not miss one.
- **D-GG-J**: bridge registers only `AppEcho` (and only ops present in both OpenAPI
  and bridge). No real UI actions. Admin-only tool registration is gated on
  `isAdminGuide` from the session (none to register in phase 1).
- `setContextProvider` is **supplementary** (route/role/name) — not security
  (D-GG-G). Don't render chat without a valid `pub-id`.
- Reuse existing popover/flyout/toast/spinner primitives (UI-convention gate). No
  new icon library.

## Tasks

1. `services/api.ts`: add `getSystemGuideSession()` calling
   `GET /api/system-guide/session` (cookie auth via existing `withAuthFetchInit`).
2. `features/guideantsGuide/`: `types.ts`, `GuideAntsGuideProvider.tsx`,
   `GuideAntsGuideButton.tsx`, `GuideAntsGuideFlyout.tsx`, `guideantsAppBridge.ts`.
3. Mount `GuideAntsGuideProvider` in `App.tsx` inside auth context.
4. Flyout open flow (plan §8.4): fetch session → lazy `import('guideants')` →
   mount `guideants-chat` (`pub-id`, `api-base-url`, `command-mode="true"`,
   `speech-to-text-enabled="true"` per D-GG-5) → on ready:
   `setContextProvider(buildAppContext)`, `registerGuideAntsAppBridge(chat)`. **No
   `setAuthToken`** — auth is the same-host cookie (D-GG-1).
5. `buildAppContext()` returns `{ route, role, userId, displayName }`; refresh on
   route change while open (`useLocation`).
6. `guideantsAppBridge.ts`: register `AppEcho` →
   `{ status:'ok', echo: args, context: buildAppContext() }`.
7. Integrate `GuideAntsGuideButton` as first child in **all** §8.2 call sites.
8. Component tests: button visibility by role; flyout open/close; session mock →
   `setAuthToken` called + `pub-id` set.

## Files in scope

- `src/client/src/features/guideantsGuide/*` (provider, button, flyout, bridge, types, `__tests__`)
- `src/client/src/App.tsx`
- `src/client/src/services/api.ts`
- All `HeaderActionsBar` call sites in plan §8.2 (Home, Settings, Projects,
  Conversations, Usage, NewProject, ProjectLayout, NotebookLayout, EditorHeader,
  LexicalToolbar)

**Out of scope:** server, seeder, Settings system-guides route (Phase 7), publish UI.

## Self-verification

```powershell
cd src/client; npm run build
cd src/client; npm test -- --run
```

Then run the CodeQL diff (`codeql-gate.md`, **JS focus**) — confirm **0 new**
`js/clear-text-storage`; the token is not in web storage and not logged.

## Definition of Done

- [ ] Provider mounted; button leftmost in **every** §8.2 call site; hidden for
      unauthenticated/Pending.
- [ ] Flyout fetches session, mounts `guideants-chat` (with `speech-to-text-enabled`),
      relies on same-host cookie (no `setAuthToken`), wires context provider + `AppEcho`.
- [ ] No token written to `localStorage`/`sessionStorage`; nothing logs a token.
- [ ] Cookie transmission to `api-base-url` verified (or escalated, not faked).
- [ ] Context refreshes on route change.
- [ ] Component tests green; build + client tests green. CodeQL JS diff = 0 new.

## Report-back contract (return exactly this)

```
PHASE 6 REPORT
- Provider mounted in App.tsx (auth context): <yes>
- Button call sites updated (leftmost): <count> -> <list>; hidden for unauth/Pending: <yes>
- Auth: same-host cookie (D-GG-1)  setAuthToken called: <no>  token stored: <none>
- Cookie reaches api-base-url: <verified how / escalated>
- ASR (speech-to-text-enabled) set: <yes>
- Context provider fields: <route,role,userId,displayName>  refresh on route change: <yes>
- Bridge tools registered: <AppEcho>  real actions: <none>
- Component tests: <names>  build: <pass/fail>  client tests: <counts>
- Files touched: <exhaustive list>
- Deviations / surprises: <list or "none">

CODEQL (local, no GitHub parity):
- JS build-mode=none used: <yes>  suites=code-scanning: <yes>
- New js/clear-text-storage vs baseline: <count> -> <file:line each, or "none">
- New findings fixed in-code (no suppression): <yes/n-a>
```
