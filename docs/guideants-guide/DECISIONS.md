# GuideAnts Guide — Decisions (single source of truth)

Last updated: 2026-06-21 · Status: **ALL LOCKED** — D-GG-1 (same-host session cookie), D-GG-2 (`/settings/system-guides`), D-GG-3 (chat-only acceptance), D-GG-4 (no limits by default, editable via Project UI), D-GG-5 (panel + admin badge + **ASR enabled**). Frozen invariants A–J in force.

Every subagent reads this file. If a value here is `PROPOSED` (not `LOCKED`), the
orchestrator **must** resolve it with the user (see `00-orchestration.md` §1)
before dispatching the phase that depends on it. Changing a value after a phase has
shipped requires a revert + re-dispatch of that phase — so get these right first.

There are two decision classes:

- **`GG-*` open decisions** — genuinely the user's to make; some are blocking.
- **Frozen invariants** — decided by the master plan; not open to subagent
  reinterpretation.

---

## Open decisions (confirm before dispatch)

### D-GG-1. Flyout auth — **LOCKED: same-host session cookie** (Phases 2, 4, 6)

**Context.** The app's auth was finalized as an **HttpOnly cookie**
(`GuideAnts.Auth`); client JS cannot read the token. **The API and UI are served
from the same host** (user-confirmed), so the browser attaches the session cookie
to same-origin/same-site requests automatically — including the published-guide
requests `guideants-chat` makes to the same `api-base-url`. No token needs to be
read by JS or minted.

- [x] **Same-host session cookie.** The `AppIdentity` branch of
      `PublishedGuideAuthService` reads + validates the **`GuideAnts.Auth` cookie**
      with the shared app-JWT validator (same key/issuer/lifetime as
      `RequireApprovedUser`). The flyout does **not** call `setAuthToken()` for auth
      and does **not** store any token. `setContextProvider` stays supplementary.
- [ ] ~~Mint a short-lived token~~ — unnecessary; same-host cookie already rides along.
- [ ] ~~Reissue the long-lived token to JS~~ — rejected (defeats HttpOnly).

**Implications now in force:**

- **Phase 2:** `AppIdentity` validation reads the JWT from the `GuideAnts.Auth`
  cookie (optionally also accept `X-Published-Auth` Bearer for Swagger/manual API
  testing). Missing/invalid → **401**, no anonymous fallback.
- **Phase 4:** `GET /api/system-guide/session` returns **config only** (pub-id,
  role flags) — **no token in the body**, nothing minted.
- **Phase 6:** the flyout mounts `guideants-chat` with `pub-id` + `api-base-url`
  and relies on the cookie. **No token storage** (so `js/clear-text-storage` is a
  non-issue by construction). **One verification checkpoint:** confirm
  `guideants-chat` transmits the cookie to the API (same-origin fetch default sends
  cookies; if `api-base-url` is a different port, the component must send
  `credentials:'include'` and CORS must allow credentials — the app already does
  this). If the component omits credentials, that is a **Phase 6 blocker to
  escalate**, not a silent fallback.

### D-GG-2. Settings link shape — **LOCKED: `/settings/system-guides`** (Phase 7)

- [x] **`/settings/system-guides`** dedicated route that resolves the system project
      id from the API and renders the project workspace. No stable system-project
      UUID in the URL bar / bookmarks / bundle. Server still 404s non-admins.

### D-GG-3. Phase-1 acceptance scope — **LOCKED: chat-only is sufficient** (Phases 6, 8)

- [x] **Chat round-trip is the acceptance bar.** Send a message, receive a streamed
      reply. The `AppEcho` tool loop is verified by a test hook / explicit prompt,
      not required to fire unprompted (model tool-invocation is non-deterministic).

### D-GG-4. System-guide cost/usage limits — **LOCKED: no limits by default, editable via Project UI** (Phases 3, 4)

- [x] **No special exemption code.** The seeder creates the system published guides
      with **no usage limits set** (default = unlimited). They are **editable like
      any other guide** through the normal usage-limits UI in the System Guides
      project workspace (admins reach it via D-GG-2). Do **not** hard-code an
      exemption branch; just don't apply default limits, and ensure the existing
      usage-limit editing UI works against system project guides.

### D-GG-5. Flyout UX — **LOCKED** (Phase 6)

- Flyout surface: **right-anchored panel** (~400px, max-height viewport), match
  existing popover z-index. (plan §8.3)
- Admin guide indicator: **show a small "Admin" badge** in the flyout header.
- **Speech-to-text: ENABLED** in the flyout in phase 1. `guideants-chat` supports
  ASR; set `speech-to-text-enabled` — no reason to gate it by phase.

---

## Frozen invariants (NOT open for subagent reinterpretation)

Decided by `../guideants-guide-implementation-plan.md`; must hold in every phase.

- **D-GG-A. AppIdentity is seeder-only.** `AuthMode = AppIdentity` is settable
  **only** by `GuideAntsSystemSeeder` / the internal publish helper. The publish
  API (`POST /api/guides/{id}/publish`) and update API **reject** it (400), and
  reject switching an AppIdentity row away from it. **No UI control** enables it;
  the publish dialog only **indicates** it read-only (plan §4.4, §4.5).
- **D-GG-B. Exactly one auth mode per published guide.** `Anonymous` / `Webhook` /
  `ApiKey` / `AppIdentity` are mutually exclusive. Migration backfills existing
  rows: `ApiKeyHash` set → `ApiKey`; `AuthValidationWebhookUrl` set → `Webhook`;
  else → `Anonymous`. (plan §4.1)
- **D-GG-C. Reuse the app JWT validator.** The `AppIdentity` branch validates with
  the **same signing key, issuer, and lifetime rules** as `RequireApprovedUser`.
  **Do not duplicate crypto logic.** A missing/invalid/expired token on an
  AppIdentity guide is **401** — never an anonymous fallback. (plan §4.2)
- **D-GG-D. Identity persists on AppIdentity chat.** User messages write **both**
  `UserId` (internal `Users.Id`) and `ExternalUserIdentity` (GUID string); the
  stream policy returns the internal id, not `null`. (plan §4.3)
- **D-GG-E. System project is hidden + guarded.** One row with
  `IsSystemProject=true` (slug `guideants-system`). Excluded from all listings.
  Non-admin access to it (or any of its child routes) returns **404** (not 403 —
  do not leak existence). Admin-only for read/edit. Not deletable while
  `IsSystemProject`. (plan §6.1, §6.2)
- **D-GG-F. Guide selection by role.** Caller `Admin` → admin system guide; any
  other **approved** role → user system guide. Pending/unapproved → 403; flyout
  button hidden/disabled. (plan §6.3, §8.2)
- **D-GG-G. No fallback identity/role.** Per user rule. Never default a missing
  user/role to something permissive; never swallow a `401`/`403`/`404`. The
  `context provider` is **supplementary** (route/role/name for the model) and is
  **not** the security boundary — JWT validation is. (plan §4.6)
- **D-GG-H. No client token storage.** Auth is the same-host session cookie
  (D-GG-1); the flyout neither reads nor stores any token. No
  `localStorage`/`sessionStorage` token writes (CodeQL `js/clear-text-storage`),
  and no token is logged anywhere.
- **D-GG-I. IDs come from settings, not hard-coded.** The seeder writes the
  `GuideAntsSystem` settings section; runtime reads project/guide/notebook/pub IDs
  from it. No GUIDs hard-coded in client or server logic. (plan §5.2)
- **D-GG-J. Stub tools only in phase 1.** Client bridge registers only `AppEcho`
  (and only tools present in **both** OpenAPI and bridge). Real UI actions and
  admin-only tools are **phase 2**. (plan §1, §8.5)

---

## Downstream impact map

| Decision | Phases affected | If changed after ship |
|---|---|---|
| D-GG-1 same-host cookie auth | 2 (validate cookie), 4 (config only), 6 (rely on cookie) | revert + re-dispatch 2/4/6 |
| D-GG-2 link shape (`/settings/system-guides`) | 7 (+ workspace page component) | revert + re-dispatch 7 |
| D-GG-3 acceptance scope | 6, 8 (test bar) | re-run acceptance only |
| D-GG-4 no default limits, editable via UI | 3 (seeder), 4 (UI works on system project) | adjust seeder + tests |
| D-GG-5 UX (panel, admin badge, ASR on) | 6 | cosmetic; low cost |
