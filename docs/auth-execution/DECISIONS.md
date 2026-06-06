# Auth System — Locked Decisions (single source of truth)

Last updated: 2026-06-06 · Status: **ALL LOCKED** — D1 (JWT in HttpOnly cookie), D2 (UserRoles table), D3 (Appendix-A guards incl. header-toolbar split), D4(a) (OAuthAuthorizationState table), D4(b) (token per User+Provider). D1 changed 2026-06-06 from Bearer-in-JS to cookie transport (see deviation log in `STATUS.md`).

Every subagent reads this file. If a value here is `UNDECIDED`, the orchestrator
**must** resolve it with the user (see `00-orchestration.md` §1) before dispatching
the phase that depends on it. Changing a value after a phase has shipped requires a
revert + re-dispatch of that phase — so get these right first.

---

## D1. Session mechanism — **LOCKED: App-issued JWT in HttpOnly cookie** (Phase 2, revised 2026-06-06)

Chosen (plan §3.2, revised after Phase 5 hard-refresh defect):

- [ ] ~~App-issued JWT Bearer in client JS~~ — rejected: `localStorage`/`sessionStorage`
      triggers CodeQL `js/clear-text-storage`; in-memory-only JWT loses session on refresh.
- [x] **App-issued JWT in an HttpOnly secure cookie** — server sets `GuideAnts.Auth`
      on login/register; browser sends it automatically; JavaScript never reads the token.

**Implications now in force:**

- **`POST /api/auth/logout`** clears the auth cookie server-side; client `logout()`
  calls it then clears in-memory user state.
- Phase 5 `api.ts` uses `credentials: 'include'` on authenticated fetches (not
  `Authorization: Bearer` from JS). CORS already allows credentials on loopback and
  configured origins.
- Login/register JSON responses carry **user profile fields only** — no `token` in the
  body. Swagger/manual API testing may still send `Authorization: Bearer` (JWT bearer
  reads cookie **or** header).
- Phase 6 `appsettings` carries JWT signing config (issuer/audience/lifetime + key
  **source**); integration tests extract the JWT from `Set-Cookie` or use Bearer.
- **Token invalidation** (Phase 4 set-password / deactivate) still uses
  `SecurityStamp` validated against the `User` row; bumping it invalidates
  outstanding JWTs (cookie or header).
- Cookie flags: `HttpOnly`, `Secure` when HTTPS, `SameSite=Lax`, `Path=/`, expiry
  aligned with JWT lifetime.

> Note: GuideAnts streaming uses **POST + `Accept: text/event-stream`**. Same-origin
> `fetch(..., { credentials: 'include' })` sends the auth cookie on SSE POSTs — no
> `EventSource` query-string workaround and no Bearer header from JS.

**Rationale:** survives hard refresh/new tabs without storing credentials in
`localStorage` (CodeQL-clean); JWT stays out of JavaScript (XSS-resistant transport);
same signed JWT + `SecurityStamp` revocation model as before.

---

## D2. Role storage — **LOCKED: separate `UserRoles` table** (Phase 1)

Chosen (plan §3.4):

- [ ] ~~Single `Role` enum column on `User`~~
- [x] **Separate `UserRoles` table.**

**Implications now in force:**

- Phase 1 adds a `UserRoles` table, **not** a `Role` column on `User`. Suggested
  shape: `UserId` (FK → `Users`, cascade delete) + `Role` (the
  `Pending/Reader/Contributor/Admin` enum) + audit (`AssignedAt`,
  `AssignedByUserId?`). 
- **The flat model still holds: exactly one effective role per user** (D3). Enforce
  with a **unique index on `UserId`** (one row per user) — the table is the storage
  mechanism, **not** a license for multi-role/RBAC. Do not add per-project or
  multiple-active-role rows.
- Authorization (Phase 3/4) resolves the user's single role via the `UserRoles`
  row; policies read it through `ICurrentUserService`.
- Phase 5 `UserDto.role` is projected from the `UserRoles` row.
- **Approval flow:** a `Pending` user is represented by a `UserRoles` row with
  `Role = Pending` (created at registration); approval **updates that row** to the
  assigned role and stamps `AssignedByUserId`/`AssignedAt`.
- **JWT revocation column (from D1):** add `SecurityStamp` (or `TokenVersion`) to
  **`User`** in the same Phase 1 migration so Phase 4 can invalidate tokens.

**Downstream impact:** Phase 1 migration shape; every authz check joins/reads
`UserRoles`; `UserDto.role` projection in Phase 5; Phase 4 approve = role-row update.

---

## D3. Frozen invariants (NOT open for subagent reinterpretation)

These are decided by the plan and must hold in every phase:

- **Role set is exactly** `Pending = 0`, `Reader = 1`, `Contributor = 2`,
  `Admin = 3`. No other roles. `Pending` is a state, not a capability tier.
- **No automatic/seeded admin.** Fresh install = **zero users**. The **first
  registrant** becomes `Admin` + active; everyone after is `Pending`. The OSS-lite
  seed user `fd787545-ffae-4ea9-81fa-700db2fffccd` (`admin@localhost`) is
  **deleted** by migration; no `HasData` user seed is added.
- **No multitenancy.** Do **not** re-introduce `Teams`, `TeamMemberships`,
  `TeamInvitations`, `ProjectUserRoles`, `ProjectRoles`, `TeamRoles`,
  `AccessCodes`, or `Projects.OwnerUserId`. They were removed and are out of scope.
- **First-party identity only.** No Microsoft Entra / MSAL / OAuth IdP for **app
  login**. (`User.IdentityIssuer`/`IdentitySubject` stay as future-proofing, unused
  now.)
- **Endpoint guards follow Appendix A** of `../auth-system-plan.md`. The open
  questions are now **resolved** (and Appendix A updated to match):

  | Appendix A open question | **LOCKED decision** | Note |
  |---|---|---|
  | `DELETE /api/projects/{id}` | **`RequireAdmin`** | |
  | `/api/usage/**` | **`RequireAdmin`** | |
  | `POST /api/speech/transcribe` | **`RequireContributor`** | Voice input / dictation is a content-authoring aid (`useAudioRecorder` → notebook ASR toolbar). Published-guide voice input goes through the separate **Public** `/api/published/speech/transcribe` (API key), so no Admin gate is needed here. |
  | notebook `llama-runtime` `load` | **`RequireContributor`** | **Only** scenario: loading the **chat-configured** model **if necessary** to run a conversation (`llama-runtime-requires-load` in `useStreamingEventHandler`). It is part of *running* a notebook, not management. |
  | notebook `llama-runtime` `unload` / `restart` | **`RequireAdmin`** | Runtime **management**, not part of running — Admin only. (Config changes live in the `/api/settings/llama/**` group, also Admin.) |
  | notebook `llama-runtime` GET (status/inventory/operations) | **`RequireApprovedUser`** | Read-only status. |
  | `GET /api/notebooks/{id}/header-toolbar` (full config DTO) | **`RequireAdmin`** | The full DTO (provider/model option lists, per-service runtime switches, `activeProviderId`, …) only feeds the admin **service toolbar**, which **changes service config**. Split from run-readiness (below) so it can be Admin-only cleanly. |
  | `GET /api/notebooks/{id}/header-toolbar/chat-readiness` (new) | **`RequireApprovedUser`** | New **lean** read used by every runner: `effectiveModelId` + `chat.blockers` (drives `chatModelMissing` / the no-model dialog) plus the runtime-load state the Contributor load-and-run flow needs (`supportsLocalRuntimePower`, `localRuntimeOn`, `inProgressOperationId/State`). No config-switch data. |
  | notebook service **toolbar UI** (provider/model switch, unload, open-settings) | **Admin-only UI** (Phase 5) | Mounted/fetched **only for Admins**; non-admins never call the full Admin endpoint. The writes it triggers are already Admin (`PUT /api/settings/services/{id}/active-provider`, llama `unload`/`restart`). |
  | external-auth provider config (`PUT`/`DELETE` `/{providerId}`) | **`RequireAdmin`** | Per-user `oauth/*` flow endpoints stay `RequireApprovedUser` (they bind to the current user). |
  | name-based **avatar** GETs (`/api/assistants/avatar/{name}`, `/api/notebook-templates/avatar/{name}`) | **Public** (`AllowAnonymous`) | Verified: rendered as raw `<img src>` → see D3 `<img>` rule below. `GET /api/assistants/conversation-starters/{name}` is fetched via `callApi` (carries Bearer) → **`RequireApprovedUser`**. |

- **JWT `<img src>` / `<a href>` rule (consequence of D1).** A raw browser
  `<img src="/api/...">` or direct link **cannot** carry an auth cookie on cross-origin
  requests and cannot use JS-set Bearer headers, so any endpoint consumed that way
  **must be Public** *or* the client must load it via the authenticated blob pattern
  `api.utils.getAuthenticatedUrl(...)` (as `GuideCard.tsx`/`AssistantCard.tsx` already
  do for the `{id}/avatar` routes, which therefore can stay `RequireAdmin`). Phase 3
  must not gate an endpoint that Phase 5 renders as a bare `<img src>` without also
  reworking the client to blob-fetch it. The name-based avatars above stay Public for
  exactly this reason.

- **Password hashing** uses a salted KDF (ASP.NET `PasswordHasher<T>` / PBKDF2 /
  bcrypt). Pick **one** hasher in Phase 2 and reuse it everywhere (Phase 4
  set-password included).
- **No "fallback" identity/role logic.** Per user rule, never default a missing
  user/role to something permissive; a missing/invalid principal is `401`, an
  insufficient role is `403`. A `401` is never swallowed.
- **Token encryption** (Phase 4.5) reuses the existing Data Protection /
  `ApplicationSettingsJson.EncryptSecrets` (EncV2) path. No plaintext token columns.

---

## D4. Tool-OAuth storage shape (Phase 4.5)

**(a) In-flight PKCE state — LOCKED: `OAuthAuthorizationState` table** (plan §3.7a):

- [x] **`OAuthAuthorizationState` table** (persisted, queryable, survives restart).
- [ ] ~~distributed cache entry~~

Implication: Phase 4.5 adds the `OAuthAuthorizationState` table (columns per the
brief) with the `State` value unique/PK and a short `ExpiresAt`; a cleanup of
expired rows is expected. No cache dependency is introduced for this.

**(b) Token grant scope — LOCKED: per (`User`, `Provider`)** (plan §3.7b):

- [ ] ~~per (`User`, `Project`, `Provider`)~~
- [x] **per (`User`, `Provider`)** — one grant per provider, shared across all of the
      user's projects.

Implications: `ExternalOAuthToken` is keyed/unique on (`UserId`, `ProviderId`) — **no
`ProjectId` in the key**. The server-side token lookup when injecting into a tool
call resolves by (current user, provider), independent of project. `ProjectId` may
still be stored for auditing but is **not** part of the uniqueness/lookup key, and
`OAuthAuthorizationState` likewise binds to (`User`, `Provider`). Update the Phase
4.5 brief's "keyed per D4 scope" accordingly.
