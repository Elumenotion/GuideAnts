# GuideAnts Authentication & Authorization Plan

Last updated: 2026-06-05

> Feature-branch design doc.
>
> This document describes the **goal** of the new GuideAnts login system, the
> **differences** between GuideAnts (the fork) and Waterfall (the upstream
> multitenant app), and an **initial task list** to implement DB-backed
> authentication with a flat `Admin / Contributor / Reader / Pending` role model.

---

## 1. Goal

GuideAnts was forked from Waterfall and had its authentication, login flow, and
multitenant entitlement system **removed** to make development easier. The fork
currently runs as an **OSS-lite single-user app**: every API endpoint is
anonymous, and "the current user" is simply the first row in the `Users` table
(seeded as `admin@localhost`).

On this feature branch we add a **first-party login system** that:

- **Uses our own database and API for identity** — _not_ Microsoft Entra ID,
  _not_ OAuth/MSAL. Credentials, sessions/tokens, and role assignment are owned
  by GuideAnts.
- Introduces three **flat, application-wide roles** plus a **Pending state** (per
  the requirement: "the user roles will be Admin, Contributor, Reader and a
  Pending state for new users yet to be approved by an Admin user"). The
  capability of each role is **derived from the Waterfall reference**, not
  invented:
  - **Admin** — the management role. It assumes everything Waterfall gated to a
    **Team Owner** (`EnsureTeamOwnerAsync`, `TeamRoleType.Owner`: Guide/Assistant
    management, guide usage reports, guide export/import — see §2.4), the
    Owner-or-Admin team actions (member removal, invitations), and the **project
    `Owner`** management surface (`IsUserOwner`: managing project membership/roles).
    In GuideAnts this also means approving Pending users and assigning roles.
  - **Contributor** — read **and** write of content, mirroring Waterfall's
    `IsUserContributor` (`ProjectRoleType.Owner` or `Contributor`): can
    create/edit projects, notebooks, conversations, files. It does **not** include
    the Admin-only management surfaces above (e.g. Guide/Assistant management).
  - **Reader** — read-only, mirroring `ProjectRoleType.Reader`: can view content
    via the project-access checks but cannot create or edit.
  - **Pending** — a **state**, not a capability tier: a newly self-registered user
    who has not yet been approved by an Admin. This is the GuideAnts analogue of
    Waterfall's `TeamInvitation.Status = Pending`. Pending users can authenticate
    but have no content access until an Admin approves them and assigns one of the
    three roles above. **Exception:** the very first user to register is **not**
    Pending — see bootstrap below.

  > The per-endpoint Contributor-vs-Reader split is **resolved** in
  > `docs/auth-execution/DECISIONS.md` (D3) and enumerated in **Appendix A**; the
  > bullets above are the Waterfall-derived intent behind that matrix.
- **Re-establishes API protection**: endpoints that are currently wide open must
  enforce authentication and role-based authorization.
- **Bootstraps via first registration (no seeded admin):** a fresh install starts
  with **zero users** — there is **no** automatic/seeded admin account. The
  **first user to register is granted `Admin` automatically and is active (not
  `Pending`)**; every subsequent registrant is `Pending` until an Admin approves
  them. This also changes the **initial database**: the existing OSS-lite seed
  user (`admin@localhost`) must be removed so a clean install has no users.
- **Hardens the secondary (tool) OAuth:** now that we have real users and a
  server-side identity, move the notebook/guide tool-OAuth tokens **out of browser
  `localStorage`** and onto the server, bound to the authenticated user, via a new
  endpoint and new data-model tables (see §2.5 and Phase 4.5).

### Non-goals (for this branch)

- No teams, team memberships, team invitations, or team-scoped billing.
- No Microsoft/Entra/OAuth identity provider (the option to add external IdP
  later via `User.IdentityIssuer` / `User.IdentitySubject` is preserved, but not
  built now).
- No per-project role grid. Roles are **global** to the application. (A future
  per-project ACL could be layered on later, but is out of scope here.)

---

## 2. The two systems compared

### 2.1 Waterfall (upstream) — Entra/OAuth + team entitlements

Waterfall is a **multitenant** app. Identity is delegated to **Microsoft Entra
ID** and authorization is layered across teams, projects, and billing.

- **Authentication**: `Microsoft.Identity.Web` + **JWT Bearer**. The frontend
  uses **MSAL Browser** (OAuth2 auth-code + PKCE via redirect) and stores tokens
  in `localStorage`. The API validates tokens via
  `AddMicrosoftIdentityWebApi(...)` against the `AzureAd` config section
  (`Instance`, `TenantId`, `ClientId`, `Audience`).
  - Backend: `WaterfallApi/Configuration/StartupConfiguration.cs`
    (`ConfigureAuthentication`), `Program.cs` (`UseAuthentication`/`UseAuthorization`).
  - Frontend: `client/src/config/authConfig.ts`, `services/authService.ts`,
    `pages/Login.tsx`, `components/ProtectedRoute.tsx`.
- **Login / provisioning**: There is no `/login` or `/token` on the backend —
  Microsoft issues tokens. The app **provisions** the DB user just-in-time via
  `POST /api/auth/initialize` (`WaterfallApi/Endpoints/AuthEndpoints.cs`,
  `Services/Core/AuthUserService.cs`): it upserts the `User`, auto-claims pending
  invitations, creates a **personal team**, and sets up a billing plan + credit
  wallet. `GET /api/auth/me` returns the profile.
- **Authorization** is **three+ layers**, _not_ a single role table:
  1. ASP.NET `[Authorize]` / `.RequireAuthorization()` on nearly every group.
  2. A `RequireProjectCredits` policy (billing gate) on mutating operations.
  3. Service-level checks via `IProjectAccessService`
     (`HasProjectAccess`, `IsUserContributor`, `IsUserOwner`, `IsUserInTeamAsync`).
  4. Team-level checks inline in services (e.g. `EnsureTeamOwnerAsync`).
- **Roles** are two separate seeded enums/tables:
  - `TeamRoleType`: `Owner (1)`, `Admin (2)`, `Member (3)`
  - `ProjectRoleType`: `Owner (1)`, `Contributor (2)`, `Reader (3)`
  - "Pending" is **not a role** — it is `TeamInvitation.Status` (`Pending`,
    `Accepted`, `Revoked`).
- **Current user resolution**: `ProjectAccessService.GetExistingUserOrThrow`
  reads claims (`email`/`upn`/`preferred_username`, `iss`, `sub`) and matches a
  DB user by `(IdentityIssuer, IdentitySubject)` then email.
- **Data model** (`WaterfallApi.DataModel`): `Users`, `Teams`, `TeamRoles`,
  `TeamMemberships`, `TeamInvitations`, `ProjectRoles`, `ProjectUserRoles`,
  `Plans`, `TeamPlanSubscriptions`, `TeamCreditWallets`, `AccessCodes`.
- **New-user approval**: handled by the **invitation** flow — an inviter (team
  Owner/Admin or project Owner) creates a `TeamInvitation` (`Status=Pending`),
  the invitee signs in and the invite is auto-claimed by email match (or an
  access code), creating a `TeamMembership` + `ProjectUserRole`.

### 2.2 GuideAnts (fork, current state) — auth stripped out

- **Authentication**: **none wired**. `Program.cs` / `StartupConfiguration.cs`
  have no `AddAuthentication`/`AddAuthorization`/`UseAuthentication`/
  `UseAuthorization`. The JWT/Identity.Web NuGet packages are still referenced in
  `GuideAntsApi.csproj` but unused.
- **Endpoint protection**: **none**. Zero `[Authorize]` attributes and zero
  `.RequireAuthorization()` calls. Minimal-API groups in `Endpoints/` are open.
  Many operations still _document_ `401`/`403` in OpenAPI metadata, but nothing
  enforces them at runtime. Published endpoints explicitly `.AllowAnonymous()`.
- **Current user**: there is no `IUserContext`/`ClaimsPrincipal`-based
  resolution. The "current user" is the **first row** in `Users`
  (`ORDER BY Created, Id`) — see `Endpoints/UserEndpoints.cs` (`/api/users/current`)
  and `ContextOptionsService.ResolveCurrentUserAsync`. Many handlers still take a
  `ClaimsPrincipal user` parameter that is never read. New conversation messages
  set `UserId = null`.
- **Login endpoints**: none (`/api/login`, `/api/auth`, `/api/account`,
  `/api/token`, `/api/signin` do not exist). `/api/users/current`,
  `/api/users/{userId}` read/update the single DB user. Remaining OAuth code
  (`ProjectExternalAuthEndpoints`, `PublishedGuideAuthService`, notebook OAuth)
  is for **external tools / published guides**, not app login.
- **Data model** (`GuideAntsApi.DataModel`): `User` has `Id`, `Name`, `Email`,
  `IdentityIssuer`, `IdentitySubject`, `PreferencesJson`, `TimeZone`, `Locale`,
  `Created` — **no password/hash column and no role column**. The multitenant
  tables (`Teams`, `TeamMemberships`, `TeamInvitations`, `ProjectUserRoles`,
  `ProjectRoles`, `TeamRoles`, `AccessCodes`, `Projects.OwnerUserId`) were
  **removed** by the OSS-lite migrations — they are _not_ available to restore.
- **Seed user**: migration `20260326164916_OssLiteSingleUserPrep` inserts a fixed
  user — GUID `fd787545-ffae-4ea9-81fa-700db2fffccd`, email `admin@localhost`,
  name `Admin`.
- **Frontend**: no `Login.tsx`, no route guard, no MSAL dependency.
  `services/authService.ts` is a **stub** returning a fake `'oss-lite-token'`.
  `services/api.ts` `callApi()` sends **no `Authorization` header**. There are
  broken leftover links to `/login` in `ErrorScreen.tsx`, `Terms.tsx`,
  `Privacy.tsx`, and unused `VITE_MSAL_*` env stubs.
- **Swagger** (`guideants-swagger.json`): no `securitySchemes` and no `security`
  requirements anywhere.

### 2.3 Difference summary

| Concern | Waterfall (upstream) | GuideAnts (current) | GuideAnts (target, this branch) |
|---|---|---|---|
| Identity provider | Microsoft Entra ID (OAuth/MSAL) | none | **First-party (our DB + API)** |
| Token / session | Entra-issued JWT Bearer | none (`'oss-lite-token'` stub) | **App-issued JWT Bearer** (decided, DECISIONS D1) |
| Login UI | MSAL redirect (`Login.tsx`) | removed (broken `/login` links) | **DB-backed login + register page** |
| API auth pipeline | `UseAuthentication`/`UseAuthorization` | none | **Wired, all endpoints protected** |
| Endpoint guards | `[Authorize]`/`RequireAuthorization` everywhere | none (open) | **Role-based authorization** |
| Tenancy | Multitenant (teams) | single-user | **Single-tenant, multi-user (no teams)** |
| Roles | Team `Owner/Admin/Member` + Project `Owner/Contributor/Reader` | none | **Global `Admin/Contributor/Reader`** |
| New-user state | `TeamInvitation.Status = Pending` | n/a | **`Pending` state until Admin-approved** |
| Current user | claims → DB match | first `Users` row | **authenticated principal → DB user** |
| Billing/credits | per-team plans + wallets | removed | **out of scope** |

### 2.4 Role mapping (Waterfall → GuideAnts)

GuideAnts collapses Waterfall's two role tables (team roles + project roles) into
one flat, application-wide role. **Anything Waterfall gated to a Team Owner is
gated to `Admin` in GuideAnts** — there is no separate "owner" concept once teams
are gone.

| Waterfall role / check | GuideAnts role |
|---|---|
| Team `Owner` (`TeamRoleType.Owner`, `EnsureTeamOwnerAsync`) | **Admin** |
| Team `Admin` (`TeamRoleType.Admin` — invite/revoke, remove members) | **Admin** |
| Team `Member` | Contributor or Reader (per assigned role) |
| Project `Owner` (`ProjectRoleType.Owner`; `IsUserOwner` == `HasRole(..,"Owner")`) | **Admin** |
| Project `Contributor` (`ProjectRoleType.Contributor`; included in `IsUserContributor`, which is Owner **or** Contributor) | **Contributor** |
| Project `Reader` (`ProjectRoleType.Reader`; read-only) | **Reader** |
| `TeamInvitation.Status = Pending` | **Pending** (state) |

> Note on `IProjectAccessService` checks: `HasProjectAccess` is true for **any**
> `ProjectUserRole` on a non-deleted project (Owner/Contributor/Reader) — it is
> "has access", **not** "is Reader". `IsUserContributor` is true for Owner **or**
> Contributor (the write tier). `IsUserOwner`/`HasRole(..,"Owner")` is Owner only.

The features Waterfall gates **strictly to a Team Owner** are the three services
that call a private `EnsureTeamOwnerAsync` (which checks
`TeamMembership.TeamRoleId == (int)TeamRoleType.Owner` and throws
`UnauthorizedAccessException` otherwise). These become **Admin**-gated in
GuideAnts:

| Waterfall service (`EnsureTeamOwnerAsync`) | Exception message | Operations covered |
|---|---|---|
| `WaterfallApi/Services/TeamGuides/TeamGuidesService.cs` | "Only team owners can manage guides and assistants" | All Guide & Assistant CRUD (`Get/Create/Update/Delete`, avatars) and OpenAPI operation get/update |
| `WaterfallApi/Services/TeamGuides/GuideUsageService.cs` | "Only team owners can access guide usage data" | Guide usage summaries/reports |
| `WaterfallApi/Services/TeamGuides/GuideExportImportService.cs` | "Only team owners can export/import guides" | Export/import of guides and assistants |

Other Waterfall management actions are gated to **Team Owner _or_ Team Admin**
(not owner-only) and also map to **Admin** in GuideAnts:

- Removing team members — `TeamService.RemoveMemberAsync` allows `TeamRole.Name`
  of `"Owner"` or `"Admin"` and forbids removing the last Owner.
- Inviting/revoking members — `TeamInvitationService` (Owner/Admin).

> Not a team-owner gate (kept separate, do **not** lump under this rule):
> `ProjectTeamService` member management (`AddTeamMember`, `UpdateTeamMemberRole`,
> `RemoveTeamMember`, `UpdateProjectUserRoles`) uses `IProjectAccessService.IsUserOwner`,
> which is the **project** `Owner` role, not the team Owner. The project-role
> rows in the table above reflect a GuideAnts **design choice** (collapsing the
> per-project role grid into the flat global role) rather than a team-owner gate.

### 2.5 Secondary (tool) OAuth and the `localStorage` problem

Separate from app login, GuideAnts has a **second OAuth flow** for notebook/guide
**tools** (e.g. Microsoft Graph). Today it runs the **entire PKCE authorization-code
flow in the browser** and persists everything in `localStorage`:

- **PKCE session** (`codeVerifier`, `state`, `clientId`, `scopes`, `projectId`,
  `providerId`, `returnUrl`, `tenant`) is written to `localStorage` under
  `oauth_pkce_*`, then read back in `pages/OAuthCallback.tsx` by **scanning all
  `localStorage` keys** to match `state`. (App-level redirect handling in
  `App.tsx` — and Waterfall's `ProtectedRoute.tsx` — also scans `oauth_pkce_*`
  to tell tool-OAuth callbacks apart from MSAL login.)
- **Token exchange happens in the browser**: `OAuthCallback.tsx` POSTs directly to
  `https://login.microsoftonline.com/.../oauth2/v2.0/token` and stores the
  resulting **access _and_ refresh tokens** in `localStorage` under
  `oauth_tokens_<projectId>_<providerId>` (`utils/notebookAuth.ts`).
- **Refresh also happens in the browser** against Microsoft's token endpoint
  (`refreshOAuthTokens` in `notebookAuth.ts`).
- **At runtime**, `contexts/conversation/useConversationActions.ts` reads tokens
  from `localStorage` (`collectOAuthTokensForTemplate`) and **sends them up to the
  server with every notebook message** (`sendMessageStream(..., oauthTokens)`).

Server-side, `ProjectExternalAuth` (`Endpoints/ProjectExternalAuthEndpoints.cs`,
`DataModel/Models/ProjectExternalAuth.cs`) stores only the per-project provider
**config** (`clientId`, `tenant`, or a `service_http` header) — **not** tokens.

**The problem (the issue Waterfall has with `localStorage`):**

1. **Security**: long-lived **refresh tokens** and access tokens sit in plaintext
   `localStorage`, fully exposed to any XSS; they are also re-transmitted on each
   request. (Contrast the server's encrypted `ApplicationSettings`, which protects
   secrets via ASP.NET Data Protection / `ApplicationSettingsJson.EncryptSecrets`.)
2. **Not user-bound / not portable**: tokens live per-browser, keyed only by
   `projectId`+`providerId`, with no link to a user; they don't follow the user
   across browsers/devices and vanish when storage is cleared.
3. **Coupling**: distinguishing a tool-OAuth callback from app login requires
   scanning `localStorage` for `oauth_pkce_*` — fragile glue we can delete once
   the flow is server-side.

Now that the new login system gives us an authenticated server-side user, we can
fix this with **one new endpoint group + data-model extensions** (Phase 4.5):
keep the PKCE verifier and the exchanged tokens **on the server**, encrypted and
**bound to the `User`**, and inject tokens into tool calls server-side so the
browser never holds or transmits them.

---

## 3. Design decisions (resolved)

These choices shape the task list. **All are now locked** in
`docs/auth-execution/DECISIONS.md` (D1–D4); the annotations below mirror that file,
which is the single source of truth if anything here drifts.

1. **Credential type**: email + password (proposed) with salted hashing
   (`PBKDF2`/`bcrypt`/ASP.NET `PasswordHasher<T>`). Magic-link / external IdP are
   future work. **No email in this version** — therefore **no self-service
   "forgot password"**; password recovery is **admin-driven** (an Admin sets the
   user's password; see Phase 4 / E7).
2. **Session mechanism** _(decided: **app-issued JWT Bearer** — see
   `docs/auth-execution/DECISIONS.md` D1)_: mirrors the existing client `fetch`
   model; no `logout` endpoint (token discarded client-side); a `SecurityStamp`
   claim enables server-side revocation. Note: GuideAnts' streaming endpoints use
   **POST +
   `Accept: text/event-stream`** (`NotebookConversationsEndpoints.cs`,
   `PublishedNotebookConversationsEndpoints.cs`), so they carry the normal
   `Authorization: Bearer` header — there is **no** GET `EventSource` needing a
   `?access_token=` query-string workaround like Waterfall has.
3. **Self-registration** _(decided)_: users self-register. The **first registrant
   becomes `Admin` and is active**; every subsequent registrant lands in
   **`Pending`** until an Admin approves and assigns `Admin`/`Contributor`/`Reader`.
4. **Role storage** _(decided: **separate `UserRoles` table** — see
   `docs/auth-execution/DECISIONS.md` D2)_: one row per user (unique index on
   `UserId`), storing the flat single role; **no** `Role` column on `User`. The JWT
   revocation `SecurityStamp` is added to `User` in the same Phase 1 migration.
5. **Bootstrap admin** _(decided)_: **no seeded/automatic admin account.** A fresh
   install ships with **zero users**; the first successful registration is granted
   `Admin` (active, not `Pending`). The existing OSS-lite seed user
   (`admin@localhost`, GUID `fd787545-ffae-4ea9-81fa-700db2fffccd`) must be removed
   from the initial database.
6. **First-user race**: how to guarantee exactly one auto-Admin under concurrent
   first registrations — do the "any users exist?" check and the insert inside a
   single transaction, or enforce it with a DB constraint/serialized check.
7. **Secondary tool-OAuth (Phase 4.5)** _(decided — see DECISIONS D4)_: tokens move
   server-side, encrypted, bound to the `User` (no more `localStorage`). (a) In-flight
   PKCE state is stored in an **`OAuthAuthorizationState` table** (not a cache);
   (b) token scope is **per (`User`, `Provider`)** — one grant per provider, shared
   across the user's projects (no `ProjectId` in the key).

---

## 4. Initial task list

### Phase 0 — Cleanup of stripped scaffolding

- [ ] Inventory and remove/replace dead auth scaffolding: unused `ClaimsPrincipal`
      parameters in endpoint handlers, stale "RequireAuthorization()"/JWT
      comments, broken `/login` links (`ErrorScreen.tsx`, `Terms.tsx`,
      `Privacy.tsx`), unused `VITE_MSAL_*` env stubs, and the
      `'oss-lite-token'` stub in `client/src/services/authService.ts`.
- [ ] Decide whether to reuse the existing `Microsoft.Identity.Web` /
      `JwtBearer` NuGet references for our **own** JWT validation or remove them.

### Phase 1 — Data model & migration

- [ ] Add a `Role` enum (`Pending = 0`, `Reader = 1`, `Contributor = 2`,
      `Admin = 3`) in `GuideAntsApi.DataModel`.
- [ ] Add a **`UserRoles` table** (D2): one row per user (**unique index on
      `UserId`**) holding the flat role + audit (`AssignedAt`, `AssignedByUserId?`).
      **No `Role` column on `User`.**
- [ ] Extend `User` (`GuideAntsApi.DataModel/Models/User.cs`) with:
      `PasswordHash` (+ salt/format if not using `PasswordHasher`), `SecurityStamp`
      (JWT revocation, D1), `LastLoginAt`, `ApprovedByUserId`/`ApprovedAt`
      (nullable), and `MustChangePassword` (set when an Admin sets a recovery
      password — see Phase 4 / E8). **Do not** add a `Role` or `Status`/`IsApproved`
      column (role + approval state live in the `UserRoles` row).
- [ ] Add EF Core migration (see `GuideAntsApi.DataModel/EF_COMMANDS.md`) and
      verify auto-migrate on startup (`SqlServerDatabaseInitializer`).
- [ ] **Remove the OSS-lite seed user from the initial database** so a fresh
      install starts with **zero users**: the `admin@localhost` insert lives in
      migration `20260326164916_OssLiteSingleUserPrep.cs` (and the dev script
      `Database/DevScripts/OssLiteSingleUserPrecheck.sql`). Since that migration
      may already be applied, add a **new** migration that deletes the seed user
      (`fd787545-ffae-4ea9-81fa-700db2fffccd`) and seed **no** users; do not add a
      `HasData` seed for any user. (There is **no** automatic/bootstrap admin —
      the first registrant becomes Admin at runtime, see Phase 2.)
- [ ] Audit code that assumed a single seeded user now that the DB can be empty:
      the "first `Users` row" reads in `UserEndpoints` (`/api/users/current`) and
      `ContextOptionsService.ResolveCurrentUserAsync` must tolerate zero users and
      be replaced by authenticated-principal resolution (Phase 2).

### Phase 2 — Backend authentication

- [ ] Add `AddAuthentication`/`AddAuthorization` + `UseAuthentication`/
      `UseAuthorization` to `StartupConfiguration.cs` / `Program.cs` using
      **app-issued tokens** (no Entra).
- [ ] Implement password hashing/verification and **app-issued JWT** issuance +
      validation (role + `SecurityStamp` claims; D1). No cookie path.
- [ ] Create auth endpoints (`Endpoints/AuthEndpoints.cs`):
      `POST /api/auth/register`, `POST /api/auth/login`, `GET /api/auth/me`.
      **No `logout` endpoint** — JWT is discarded client-side (D1).
- [ ] **First-registrant-is-Admin** logic in `register`: if no users exist yet,
      create the account as **`Admin` and active**; otherwise create it as
      **`Pending`**. Perform the "any users exist?" check and the insert in a
      single transaction (or a serialized/constraint-guarded path) so concurrent
      first registrations cannot both become Admin.
- [ ] Implement a `ICurrentUserService`/`IUserContext` that resolves the DB
      `User` from the authenticated principal (replace the "first `Users` row"
      logic in `UserEndpoints` and `ContextOptionsService`).

### Phase 3 — Authorization (roles)

- [ ] Define authorization policies: `RequireApprovedUser` (any non-`Pending`
      role), `RequireContributor` (Contributor or Admin), `RequireAdmin`.
- [ ] Apply `.RequireAuthorization(...)` to all `Map*Endpoints` groups; map
      read vs. write operations to `Reader`/`Contributor`/`Admin` appropriately
      (mutating routes → `RequireContributor`; user management → `RequireAdmin`).
- [ ] **Re-gate Waterfall Team-Owner features to `Admin`** (see §2.4): the three
      services that call `EnsureTeamOwnerAsync` (`TeamGuidesService`,
      `GuideUsageService`, `GuideExportImportService`) must require the
      `RequireAdmin` policy in GuideAnts — i.e. Guide/Assistant CRUD + OpenAPI
      operations, guide usage reports, and guide/assistant export/import.
- [ ] Also gate the Owner-or-Admin team actions (`TeamService.RemoveMemberAsync`,
      `TeamInvitationService` invite/revoke) — i.e. GuideAnts user
      administration — to `RequireAdmin`. Note: Waterfall's project-member
      management (`ProjectTeamService`, `IsUserOwner`) is a **project** role, not
      a team-owner gate; map it per the design choice in §2.4, not as a
      team-owner feature.
- [ ] Ensure `Pending` users are blocked from feature endpoints but can reach
      `GET /api/auth/me` (to see their pending status).
- [ ] Replace null message attribution with the authenticated user: conversation
      messages set `UserId = null` (`ConversationService.cs`), and edit history
      sets `FirstEditedByUserId = null` / `LastEditedByUserId = null`. (Note:
      `canCreateContent` exists only as a client DTO field in
      `client/src/services/api.ts` and `client/src/types/project.ts`; there is no
      server-side `canCreateContent` and no hard-coded `true` to replace — wire it
      to the real role/permission once roles exist.)

### Phase 4 — Admin user management

- [ ] Add admin endpoints: `GET /api/admin/users` (list, incl. pending),
      `POST /api/admin/users/{id}/approve` (assign role, clear `Pending`),
      `PUT /api/admin/users/{id}/role`, deactivate/disable user.
- [ ] **Admin password recovery** (this version has **no email**, so there is no
      self-service reset): add `POST /api/admin/users/{id}/set-password` so an
      Admin can set a user's password directly. It hashes the new password via the
      same hasher as Phase 2, and should invalidate the target user's existing
      sessions/tokens. Consider a `MustChangePassword` flag on `User` so the user
      is prompted to change it after the admin-set password.
- [ ] Guard all of the above with the `RequireAdmin` policy. Add safeguards for
      destructive self-targeting (an Admin must not lock out the last Admin via
      deactivate/role-change).

### Phase 4.5 — Secondary (tool) OAuth: server-side token storage

Solve the `localStorage` problem from §2.5 by moving the tool-OAuth PKCE flow and
token storage onto the server, **bound to the authenticated `User`**. The browser
should never hold the `codeVerifier`, the refresh token, or the access token.

**Data-model extensions (`GuideAntsApi.DataModel`):**

- [ ] Add **`ExternalOAuthToken`** — the per-user token grant. Keyed by
      (`UserId` → `Users`, `ProjectId` → `Projects`, `ProviderId`); columns:
      `AccessTokenEncrypted`, `RefreshTokenEncrypted`, `ExpiresAt`, `Scope`,
      `Created`, `Updated`. **Encrypt** the token columns with the existing
      mechanism (ASP.NET Data Protection / `ApplicationSettingsJson.EncryptSecrets`
      EncV2) — **never** store them plaintext. Unique index on
      (`UserId`,`ProjectId`,`ProviderId`).
- [ ] Add **`OAuthAuthorizationState`** — short-lived in-flight PKCE state so the
      verifier stays server-side. Columns: `State` (PK/unique), `UserId`,
      `ProjectId`, `ProviderId`, `CodeVerifier`, `Scopes`, `RedirectUri`,
      `ReturnUrl`, `Created`, `ExpiresAt`. (A distributed cache entry is an
      acceptable alternative to a table; pick one.)
- [ ] Migration for both (per `EF_COMMANDS.md`); FKs cascade-delete with `User`
      and `Project`.

**New endpoints** (extend `Endpoints/ProjectExternalAuthEndpoints.cs`, under the
existing `/api/projects/{projectId}/external-auth` group; require auth + project
access; the group must move from anonymous to `RequireAuthorization`/role-gated
per Phase 3):

- [ ] `POST .../{providerId}/oauth/authorize-url` — server generates PKCE
      (`codeVerifier`+`challenge`+`state`), persists `OAuthAuthorizationState` for
      the current user, and returns the provider **authorize URL**. The browser
      only redirects to it.
- [ ] `POST .../{providerId}/oauth/callback` — body `{ code, state }`. Server looks
      up the `OAuthAuthorizationState`, verifies it belongs to the **current user**
      and isn't expired, exchanges `code`+`codeVerifier` at the provider token
      endpoint **server-side**, encrypts and upserts `ExternalOAuthToken`, deletes
      the state, and returns **status only** (no tokens).
- [ ] `GET .../{providerId}/oauth/status` — returns `{ connected, expiresAt,
      scopes }` for the current user; **never** returns the token itself.
- [ ] `DELETE .../{providerId}/oauth` — deletes the current user's stored tokens
      for that provider (disconnect).
- [ ] **Server-side refresh**: a service refreshes a near-expiry token using the
      stored refresh token at request time (no browser involvement). On refresh
      failure, surface a re-connect requirement (no silent fallback).

**Runtime + client changes:**

- [ ] **Inject tokens server-side**: when running a notebook conversation, the
      server resolves the current user's `ExternalOAuthToken` for each required
      provider (refreshing if needed) and attaches it to the tool call. **Remove**
      the client-supplied `oauthTokens` path: `collectOAuthTokensForTemplate(...)`
      and the `oauthTokens` argument to `sendMessageStream`
      (`contexts/conversation/useConversationActions.ts`).
- [ ] **Client OAuth flow becomes server-mediated**: `pages/OAuthCallback.tsx`
      POSTs `{ code, state }` to the new callback endpoint instead of exchanging at
      Microsoft and writing `localStorage`; the "start" path calls
      `authorize-url`. Replace `utils/notebookAuth.ts` `localStorage` helpers
      (`storeOAuthTokens`/`getOAuthTokens`/`refreshOAuthTokens`/…) with calls to
      `oauth/status`; update `NotebookAuthInterstitial.tsx` and
      `ProjectGuideAuthContent.tsx` to drive the server flow and read connection
      status from the server.
- [ ] **Delete the `localStorage` coupling**: remove the `oauth_pkce_*` scanning
      in `App.tsx` (no longer needed to disambiguate from the now-removed MSAL
      login), and stop persisting `oauth_pkce_*` / `oauth_tokens_*` entirely.

### Phase 5 — Frontend

The current client (`src/client`) has **no** auth surface at all: routes in
`components/AppContent.tsx` go straight to `Home`/`Projects`/`Settings`/etc.,
`App.tsx` wraps everything in `StartupGate` (server-readiness only, browser mode)
and a `HashRouter` (Electron) / `BrowserRouter` (web), `services/authService.ts`
is a stub, and `services/api.ts` `callApi()` sends no credentials. `/login` is
referenced (`ErrorScreen.tsx`, `Terms.tsx`, `Privacy.tsx`) but **no such route
exists**. We therefore need several **new experiences**, plus cross-cutting auth
plumbing. Waterfall's `pages/Login.tsx`, `components/ProtectedRoute.tsx`,
`components/AuthExpiredHandler.tsx`, and `services/authEvents` are the reference
patterns to adapt (replacing MSAL/Entra with our DB+API calls).

#### 5.0 UI conventions (reuse, don't reinvent)

A recurring problem when extending this UI is new one-off styles, ad-hoc dialogs,
mismatched icons, and missed responsiveness. **Every new page/component below must
follow the existing conventions** (verified from `PersonalizationTab.tsx`,
`ConfirmationDialog.tsx`, `shared/ActionButtons.tsx`, `Toast.tsx`,
`LoadingSpinner.tsx`, `Settings.tsx`, `Home.tsx`). Do **not** introduce new design
primitives, icon libraries, or bespoke modal markup.

**Reuse these canonical components/utilities (don't hand-roll):**

- **Buttons** — `TextActionButton` / `IconActionButton` from
  `pages/settings/components/shared/ActionButtons.tsx` with a `tone`
  (`primary | neutral | accent | info | success | danger`). Don't write raw
  `<button className="bg-blue-600…">` for actions.
- **Confirm dialogs** — `components/common/ConfirmationDialog.tsx` for any
  destructive/confirming action (e.g. deactivate user). For **form** modals
  (e.g. admin set-password), follow its pattern exactly: `createPortal(…,
  document.body)`, overlay `fixed inset-0 bg-black bg-opacity-50 flex items-center
  justify-center z-50`, panel `bg-white rounded-lg shadow-xl w-full max-w-md mx-4`,
  `role="dialog" aria-modal="true"`, Esc-to-close, focus the primary button,
  footer `border-t border-gray-200 flex justify-end space-x-3`.
- **Transient feedback** — `useToast().showToast({ type, title, message })` from
  `components/common/Toast.tsx` (`success | error | info | warning`). Use toasts
  for action results; reserve inline banners for persistent form/validation state.
- **Loading** — `components/LoadingSpinner.tsx` (`message` prop) for page/section
  loads; `FaSpinner` + `animate-spin` for inline button-busy state.
- **API error text** — `getErrorMessage(err, fallback)` (`pages/settings/utils.ts`).
- **Icons** — default to **`react-icons/fa`** (e.g. `FaSave`, `FaTrash`, `FaPlus`,
  `FaSpinner`, `FaTimes`, `FaRedo`, `FaUserCog`); `react-icons/fi` is used for some
  close/alert glyphs (`FiX`, `FiAlertTriangle`). **Don't** add new icon packs or
  inline SVGs.

**Form & card styling (copy from `PersonalizationTab.tsx`):**

- Card: `rounded border border-gray-200 bg-white p-5 shadow-sm`; section wrapper
  `space-y-4`. Heading `text-lg font-semibold text-gray-900`, subtext
  `text-sm text-gray-600`.
- Field: `<label className="block"><span className="mb-1 block text-sm font-medium
  text-gray-700">…</span><input className="w-full rounded border border-gray-300
  px-3 py-2 text-sm text-gray-900 shadow-sm focus:border-blue-500 focus:outline-none
  focus:ring-1 focus:ring-blue-500" /></label>`.
- Inline error: `rounded border border-red-200 bg-red-50 px-3 py-2 text-sm
  text-red-700` with `role="alert"`. Trim inputs; disable Save when invalid/unchanged.

**Responsiveness:**

- App pages center content with `mx-auto max-w-7xl`; headers use
  `flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between`; multi-field
  forms use `grid gap-4 md:grid-cols-2`.
- **Full-screen auth pages** (E1/E2/E3/E6/E8) use a centered card:
  `min-h-screen … flex items-center justify-center px-4` with an inner
  `w-full max-w-md` card and the `/guide.png` logo (mirror Waterfall's `Login.tsx`
  layout).
- The **Users table** (E4) sits in a card and must scroll on small screens
  (`overflow-x-auto`); row actions use `IconActionButton`.
- Verify every screen under **both** Electron `HashRouter` and web `BrowserRouter`.

**Accessibility (match `ConfirmationDialog`):** `role="dialog" aria-modal` on
modals, `aria-label`/`sr-only` on icon-only buttons, `role="alert"` on error
banners, keyboard support (Esc/Enter), and focus management.

> Acceptance gate for each new page: it reuses the components above, adds **no**
> new icon library or bespoke modal/button markup, is responsive at `sm`/`md`,
> and matches the form/card classes from `PersonalizationTab.tsx`.

#### 5.1 New experiences (screens & flows)

| # | Experience | Route | Who sees it | Notes / reference |
|---|---|---|---|---|
| E1 | **Login form** (email + password) | `/login` (public) | Signed-out users | Replaces Waterfall's "Sign in with Microsoft" button (`pages/Login.tsx`) with email/password fields + submit; reuse the full-screen card layout, logo, and the existing Terms/Privacy links. Honor `returnUrl`. Fixes the broken `/login` links in `ErrorScreen`/`Terms`/`Privacy`. |
| E2 | **Register / sign-up form** | `/register` (public) | New users | Name + email + password (+ confirm) → `POST /api/auth/register`. The **first-ever registrant** becomes **Admin (active)** and goes straight into the app; **every later registrant** is **Pending**, so route them to E3. Link to/from E1. |
| E3 | **Pending-approval screen** | `/pending` | Authenticated users whose role is `Pending` | "Your account is awaiting Admin approval." No app access; offers sign-out and a refresh/re-check. GuideAnts analogue of Waterfall's provisioning gate (`AccessCode`/`PlanSelection`). |
| E4 | **User management** (a **Settings tab**, not a separate route) | `/settings` → **Users** tab (Admin only) | Admins | New admin-only tab in the existing Settings tab set. Card-wrapped, `overflow-x-auto` table of users (filter Pending vs active); row actions via `IconActionButton`; approve + assign role, change role (`Admin`/`Contributor`/`Reader`), deactivate/reactivate (confirm via `ConfirmationDialog`), and **set a user's password** via a form modal built on the `ConfirmationDialog` portal/overlay pattern (recovery — no email reset). Toasts for results. Backed by the Phase 4 `/api/admin/users` endpoints. Code under `pages/settings/` (see §5.0 & §5.4). |
| E5 | **Account / security** (Settings → **Personalization** tab) | `/settings` → Personalization | All signed-in users | Personalization is the **only** Settings tab visible to non-admins (see §5.4). Extend the existing `PersonalizationTab` (`/users/current/personalization`, name/email) with **change password** and a read-only **current role** display; add **Sign out**. |
| E6 | **First-run experience** | `/register` | First user | A fresh install has **no users** and **no seeded admin**. The first person to register is auto-granted **Admin (active)** and lands directly in the app. First-run visitors should be guided to `/register` (e.g. login page links to it / empty-state messaging that the first account becomes the administrator). |
| E7 | **Admin-set password recovery** | within E4 + E8 | Admins (set) / affected user (change) | **No email in this version**, so there is no self-service "forgot password" flow. Recovery is **admin-driven**: an Admin sets a new password from E4 (`POST /api/admin/users/{id}/set-password`) and communicates it out-of-band. |
| E8 | **Forced password change** | `/change-password` (or modal) | Users with `MustChangePassword` | After an admin sets their password, the user is prompted to choose a new one on next login before reaching the app. Reuses the change-password form from E5. |

#### 5.2 Cross-cutting auth plumbing (shared by all experiences)

- [ ] **Auth context** (`AuthProvider` + `useAuth`) holding `{ user, role,
      status, login, register, logout, isAuthenticated }`; replaces the
      `authService.ts` stub. Stores the **app-issued JWT** client-side (D1);
      `logout()` clears that client state (no server call).
- [ ] **Attach credentials in `services/api.ts`**: add the
      `Authorization: Bearer <token>` header in `callApi()` (D1), which currently
      sends only `Content-Type`.
- [ ] **`ProtectedRoute` guard** (model on Waterfall's `components/ProtectedRoute.tsx`,
      minus MSAL/billing): unauthenticated → redirect to `/login?returnUrl=…`;
      authenticated but `Pending` → redirect to `/pending`; authenticated with
      `MustChangePassword` → redirect to `/change-password` (E8); otherwise render.
- [ ] **Global `AuthExpiredHandler` + auth events** (model on Waterfall's
      `components/AuthExpiredHandler.tsx` + `services/authEvents.ts`): on `401`,
      `callApi` broadcasts an `AUTH_EXPIRED_EVENT`; the handler toasts and
      redirects to `/login` (preserving `returnUrl`), except on public routes
      (`/login`, `/register`, `/terms`, `/privacy`, `/public/:friendlyName`).
      **No fallback masking**: a `401` must surface as re-auth, not be swallowed.
- [ ] **`userService.getCurrentUser()` returns role + state**: `/users/current` is
      already consumed by `contexts/ConversationContext.tsx` and
      `contexts/conversation/useConversationActions.ts`; extend `UserDto` with
      `role` (and `mustChangePassword`) so the UI can gate from one source and the
      guard can route Pending / forced-change users.

#### 5.3 Routing, shell, and role-aware gating

- [ ] **Wire routes in `components/AppContent.tsx` / `App.tsx`**: add public
      `/login`, `/register`; add gated `/pending` and `/change-password` (E8);
      wrap the existing feature routes in `ProtectedRoute`; keep `/terms`,
      `/privacy`, `/public/:friendlyName` public. (User management is **not** a new
      route — it is a Settings tab, see §5.4.) Mount `AuthProvider` and
      `AuthExpiredHandler` inside the router. Verify behavior under **both**
      `HashRouter` (Electron) and `BrowserRouter` (web), and that it composes with
      `StartupGate` and the existing `/oauth/callback` special-case in `App.tsx`.
- [ ] **User menu + sign-out in page headers**: pages render their own headers
      (e.g. `Home.tsx` uses `HomeButton`/`SettingsButton` + `HeaderIconLinkButton`).
      Add a user/account control (display name, role, sign-out) consistent with
      that pattern.
- [ ] **Role-aware affordances**: hide/disable create & edit actions for
      `Reader` (e.g. New Project on `Home.tsx`, project/notebook editors, the
      guides/assistants management UI which maps to **Admin** per §2.4), and hide
      Admin-only surfaces from non-Admins. Pair every UI gate with the
      corresponding server policy from Phase 3 (UI hiding is UX, not enforcement).
- [ ] **Notebook service header toolbar is Admin-only; run-readiness is split out**:
      `components/notebook/header-toolbar/NotebookServiceToolbar.tsx` (rendered by
      `pages/NotebookDetails.tsx`) lets the user switch the active provider/model
      per service and unload the runtime — i.e. it **changes service config**, which
      is Admin per §2.4/§5.4. Mount/fetch the full toolbar (`useNotebookHeaderToolbar`,
      backed by the Admin `GET .../header-toolbar`) **only for Admins**. Move the
      run-readiness `NotebookDetails.tsx` needs (`chat.effectiveModelId`,
      `chat.blockers`, and the Contributor load-and-run state) to a new lean
      `useNotebookChatReadiness` hook backed by the `RequireApprovedUser`
      `GET .../header-toolbar/chat-readiness` endpoint (Phase 3). So every runner gets
      readiness without the Admin config DTO.
- [ ] **Clean up dead auth UI** (also tracked in Phase 0): the unused
      `VITE_MSAL_*` declarations in `env.d.ts`, MSAL test mocks, and the
      `'oss-lite-token'` stub once `AuthProvider` exists.

#### 5.4 Settings & setup-wizard are Admin-only (except Personalization)

The entire Settings surface is administrative **except** the Personalization tab.
Today `pages/Settings.tsx` renders `SettingsTabNavigation` with tabs
`overview, personalization, connections, models-runtime, services,
infrastructure, telemetry` and unconditionally defaults `activeTab` to
`'overview'`. The setup/onboarding wizard (`components/home/AddAiServicesWizard`,
launched first-run from `Home.tsx`) and the in-Settings `AddModelWizard` are also
administrative.

- [ ] **Hide non-Personalization Settings tabs from non-Admins**: filter the
      `tabs` array in `pages/settings/components/SettingsTabNavigation.tsx` by role
      so non-Admins see **only** Personalization. For non-Admins, default
      `activeTab` to `'personalization'` in `pages/Settings.tsx` instead of
      `'overview'`.
- [ ] **Guard Settings tab content by role** (defense-in-depth, not just hiding):
      in `Settings.tsx`, if a non-Admin somehow targets an admin tab
      (`overview`/`connections`/`models-runtime`/`services`/`infrastructure`/
      `telemetry`/`users`), render nothing/“not authorized” and fall back to
      Personalization. Don't rely on tab hiding alone.
- [ ] **Add the admin-only `Users` tab** to the `SettingsTab` union
      (`pages/settings/types.ts`), `SettingsTabNavigation` tab list, and the
      `activeTabContent` switch in `Settings.tsx` (new `UsersTab` component — E4).
- [ ] **Gate the setup wizard to Admins**: the first-launch
      `AddAiServicesWizard` in `Home.tsx` (and its dismiss-key auto-open) and the
      `AddModelWizard` opened from Settings must not be reachable by non-Admins.
- [ ] Pair all of the above with the corresponding server `RequireAdmin` policy
      on the underlying settings/models/services/admin endpoints (Phase 3/4) —
      hiding the UI is UX; the API must enforce it.

### Phase 6 — OpenAPI, tests, docs

- [ ] Add `securitySchemes` + per-operation `security` to Swagger; regenerate
      `guideants-swagger.json`.
- [ ] Update integration tests: replace the no-op `Bearer test_token`
      (`BaseIntegrationTest.SetupAuthentication`) with a real test-auth handler
      that can impersonate each role; add tests for register→pending→approve and
      role-gated endpoints.
- [ ] Add client tests for the new experiences (the repo already uses Vitest):
      Login/Register submit + error states, `ProtectedRoute` redirects
      (signed-out → `/login`, `Pending` → `/pending`, `MustChangePassword` →
      `/change-password`), `AuthExpiredHandler` 401 redirect, Settings tab
      role-filtering (non-Admin sees only Personalization), setup-wizard gating,
      and role-gated affordance hiding. Update existing MSAL-based test mocks that
      no longer apply.
- [ ] Update `appsettings` / `appsettings.Development.json` with token signing
      config (key/issuer/audience/lifetime).
- [ ] Document the final auth flow and bootstrap-admin procedure in `./docs`.

---

## 5. Key file index

**GuideAnts (to modify)**

- `src/server/GuideAntsApi/Program.cs` — pipeline
- `src/server/GuideAntsApi/Configuration/StartupConfiguration.cs` — auth registration
- `src/server/GuideAntsApi/Endpoints/UserEndpoints.cs` — current-user logic
- `src/server/GuideAntsApi/Endpoints/*Endpoints.cs` — apply authorization
- `src/server/GuideAntsApi/Endpoints/ProjectExternalAuthEndpoints.cs` — add server-side tool-OAuth endpoints (Phase 4.5)
- `src/server/GuideAntsApi/Settings/ApplicationSettingsJson.cs` — reuse `EncryptSecrets`/Data Protection for token encryption
- `src/server/GuideAntsApi.DataModel/Models/User.cs` — add Role/PasswordHash/MustChangePassword
- `src/server/GuideAntsApi.DataModel/Models/ProjectExternalAuth.cs` — existing provider config (tokens go in new tables)
- `src/server/GuideAntsApi.DataModel/ApplicationDbContext.cs` — remove seed user; add `ExternalOAuthToken` + `OAuthAuthorizationState` DbSets
- `src/server/GuideAntsApi.DataModel/EF_COMMANDS.md` — migration commands
- `src/client/src/services/api.ts` — attach auth + broadcast 401
- `src/client/src/services/authService.ts` — replace stub with `AuthProvider`/`useAuth`
- `src/client/src/services/userService.ts` — `getCurrentUser()` to include role
- `src/client/src/App.tsx`, `src/client/src/components/AppContent.tsx` — routes + providers + guards
- `src/client/src/components/ErrorScreen.tsx`, `src/client/src/pages/Terms.tsx`, `src/client/src/pages/Privacy.tsx` — existing broken `/login` links
- `src/client/src/pages/Home.tsx` — header user menu / sign-out + role-gated actions + gate `AddAiServicesWizard` to Admin
- `src/client/src/pages/Settings.tsx` — default non-Admins to Personalization + guard admin tab content + add `Users` tab
- `src/client/src/pages/settings/components/SettingsTabNavigation.tsx` — role-filter tabs (non-Admin sees only Personalization)
- `src/client/src/pages/settings/components/PersonalizationTab.tsx` — add change-password, current-role, sign-out (E5)
- `src/client/src/pages/settings/types.ts` — add `'users'` to the `SettingsTab` union
- `src/client/src/pages/OAuthCallback.tsx` — POST `{code,state}` to server callback instead of exchanging at Microsoft + writing `localStorage` (Phase 4.5)
- `src/client/src/utils/notebookAuth.ts` — replace `localStorage` token helpers with server `oauth/status` calls (Phase 4.5)
- `src/client/src/components/notebook/auth/NotebookAuthInterstitial.tsx`, `src/client/src/components/project/content/ProjectGuideAuthContent.tsx` — drive server-mediated tool-OAuth flow
- `src/client/src/contexts/conversation/useConversationActions.ts` — remove client-supplied `oauthTokens` from `sendMessageStream`
- `src/client/src/env.d.ts` — remove unused `VITE_MSAL_*`
- New client files: `pages/Login.tsx`, `pages/Register.tsx`, `pages/Pending.tsx`, `pages/ChangePassword.tsx`, `pages/settings/components/UsersTab.tsx` (admin user management — part of Settings), `components/ProtectedRoute.tsx`, `components/AuthExpiredHandler.tsx`, `contexts/AuthContext.tsx`, `services/authEvents.ts`
- New server files: `DataModel/Models/ExternalOAuthToken.cs`, `DataModel/Models/OAuthAuthorizationState.cs`, a tool-OAuth service (PKCE start / code exchange / refresh / inject)
- `guideants-swagger.json` — security schemes

**GuideAnts UI conventions to reuse (do not reinvent — see §5.0)**

- `src/client/src/components/common/ConfirmationDialog.tsx` — confirm + form-modal pattern (portal/overlay/focus/Esc)
- `src/client/src/pages/settings/components/shared/ActionButtons.tsx` — `TextActionButton`/`IconActionButton` + tones
- `src/client/src/components/common/Toast.tsx` — `useToast` for action feedback
- `src/client/src/components/LoadingSpinner.tsx` — page/section loading; `FaSpinner` + `animate-spin` for buttons
- `src/client/src/pages/settings/components/PersonalizationTab.tsx` — canonical form/card/field/error styling
- `src/client/src/pages/settings/utils.ts` — `getErrorMessage`; icons from `react-icons/fa` (and `react-icons/fi`)

**Waterfall (reference patterns to adapt, not copy wholesale)**

- `src/server/WaterfallApi/Configuration/StartupConfiguration.cs` — `ConfigureAuthentication`
- `src/server/WaterfallApi/Endpoints/AuthEndpoints.cs` — `initialize`/`me`
- `src/server/WaterfallApi/Services/Core/AuthUserService.cs` — user provisioning
- `src/server/WaterfallApi/Services/Core/ProjectAccessService.cs` — claim→user resolution & role checks
- `src/client/src/services/authService.ts`, `services/api.ts`, `services/authEvents.ts`, `components/ProtectedRoute.tsx`, `components/AuthExpiredHandler.tsx`, `pages/Login.tsx`

---

## 6. Appendix A — Endpoint authorization matrix

This appendix enumerates the **current** API surface plus the **new** endpoints
introduced by this plan, and states the **required auth guard** for each. The guard
column maps to the Phase 3 policies.

> **Source of truth: the live route table, not the committed `guideants-swagger.json`
> snapshot** (which is stale — e.g. it still shows the old `/api/onlyoffice/**` routes
> that are now `/api/documentserver/**`). This matrix was re-derived from every
> `Map*`/`MapGroup` call in `src/server/GuideAntsApi/Endpoints/*.cs`, the inline routes
> in `Program.cs`, and the SPA host in `Configuration/UiApplicationBuilderExtensions.cs`.
> Phase 6 regenerates the snapshot; Phase 3 must gate against this source-derived list.
>
> **Status:** today **none** of these are enforced — every group is anonymous (§2.2).
> The "Required guard" column is the **target** state for this branch.
>
> **The Reader vs. Contributor split is resolved** (DECISIONS D3); the rows below are
> the finalized matrix. The default rule behind them: **reads → `RequireApprovedUser`**,
> **content mutations →
> `RequireContributor`**, **management surfaces → `RequireAdmin`** (per Phase 3 and
> the Waterfall mapping in §2.4).

### Guard legend

| Guard | Who passes | Meaning |
|---|---|---|
| **Public** | anyone (no session) | `.AllowAnonymous()`. Login/registration, published-guide consumer endpoints (per-guide **API key**), document-server callbacks/proxy (doc-server token), the `/api/startup` probe, and the SPA shell. |
| **Authenticated** | any valid session, **including `Pending`** | Signed in but not necessarily approved. `/api/auth/me` only (no `logout` endpoint — JWT, D1). |
| **`RequireApprovedUser`** | `Reader`, `Contributor`, `Admin` | Any non-`Pending` user. Read/view operations. |
| **`RequireContributor`** | `Contributor`, `Admin` | Create / edit / delete **content** (the write tier; Waterfall `IsUserContributor`). |
| **`RequireAdmin`** | `Admin` only | Management surfaces: user admin, settings, guide/assistant management, usage/telemetry, publishing (Waterfall Team-Owner features, §2.4). |

### A.1 Authentication & account (new — Phase 2/4/E5)

| Method & route | Required guard | Source | Notes |
|---|---|---|---|
| `POST /api/auth/register` | **Public** | `AuthEndpoints.cs` (new) | First-ever registrant → `Admin` (active); all others → `Pending` (§3.3). |
| `POST /api/auth/login` | **Public** | `AuthEndpoints.cs` (new) | Issues the app JWT (role + `SecurityStamp` claims; D1). |
| ~~`POST /api/auth/logout`~~ | — | — | **Not built**: D1 = JWT Bearer, so logout is client-side token discard (DECISIONS D1). |
| `GET /api/auth/me` | **Authenticated** | `AuthEndpoints.cs` (new) | Must be reachable by `Pending` users so they can see their status (Phase 3). |

### A.2 Admin user management (new — Phase 4)

| Method & route | Required guard | Source | Notes |
|---|---|---|---|
| `GET /api/admin/users` | **`RequireAdmin`** | `AdminUsersEndpoints.cs` (new) | List incl. `Pending`. |
| `POST /api/admin/users/{id}/approve` | **`RequireAdmin`** | new | Clears `Pending`, assigns a role. |
| `PUT /api/admin/users/{id}/role` | **`RequireAdmin`** | new | Last-Admin safeguard (Phase 4). |
| `POST /api/admin/users/{id}/set-password` | **`RequireAdmin`** | new | Recovery (no email); sets `MustChangePassword`, invalidates sessions. |
| `POST /api/admin/users/{id}/deactivate` · `/reactivate` | **`RequireAdmin`** | new | Last-Admin safeguard. |

### A.3 Current user / profile — `UserEndpoints.cs` (`/api/users`)

| Method & route | Required guard | Notes |
|---|---|---|
| `GET /api/users/current` | **`RequireApprovedUser`** | Replace "first `Users` row" with authenticated principal (Phase 2). Extend DTO with `role` + `mustChangePassword` (§5.2). |
| `PUT /api/users/current/personalization` | **`RequireApprovedUser`** | Self-edit of own name/email; all approved roles (incl. `Reader`) may edit their own profile. |
| `GET /api/users/{userId}` | **`RequireAdmin`** | Reads an **arbitrary** user. _Open question:_ if needed for author display, expose a slimmer non-admin lookup instead. |

### A.4 Projects, folders, files, links (content)

Default split: **GET → `RequireApprovedUser`**, **mutations → `RequireContributor`**.

| Method & route | Required guard | Source |
|---|---|---|
| `GET /api/projects` · `/{projectId}` · `/{projectId}/details` | **`RequireApprovedUser`** | `ProjectEndpoints.cs` |
| `POST /api/projects` · `PUT /{projectId}` · `POST /{projectId}/copy` · `POST/DELETE /{projectId}/homepage[/{fileId}]` | **`RequireContributor`** | `ProjectEndpoints.cs` |
| `DELETE /api/projects/{projectId}` | **`RequireAdmin`** | `ProjectEndpoints.cs` — resolved (DECISIONS D3): project deletion is Admin-only. |
| `GET /api/projects/{projectId}/folders/**` (`/tree`, `/`, `/{folderId}`) | **`RequireApprovedUser`** | `ProjectFolderEndpoints.cs` |
| `POST/PUT/DELETE` + `PATCH /{folderId}/move` (folders) | **`RequireContributor`** | `ProjectFolderEndpoints.cs` |
| `GET /api/projects/{projectId}/files/**` (list, `{fileId}`, `/content`, `/history`, versions) | **`RequireApprovedUser`** | `ProjectContentFileEndpoints.cs` |
| `POST` · `PATCH /{fileId}/move` · `PATCH /{fileId}/rename` · `DELETE /{fileId}` (files) | **`RequireContributor`** | `ProjectContentFileEndpoints.cs` |
| `GET .../files/{fileId}/markdown[/content]` + version markdown | **`RequireApprovedUser`** | `ProjectContentFileMarkdownEndpoints.cs` |
| `POST .../files/{fileId}/markdown/retry` | **`RequireContributor`** | `ProjectContentFileMarkdownEndpoints.cs` |
| `GET /api/projects/{projectId}/links` · `/{linkId}` | **`RequireApprovedUser`** | `LinkEndpoints.cs` |
| `POST/PUT/DELETE` (links) | **`RequireContributor`** | `LinkEndpoints.cs` |

### A.5 Notebooks & notebook files

| Method & route | Required guard | Source |
|---|---|---|
| `GET /api/projects/{projectId}/notebooks` · `/{notebookId}` | **`RequireApprovedUser`** | `NotebookEndpoints.cs` |
| `POST` · `PUT/DELETE /{notebookId}` · `POST/DELETE /{notebookId}/homepage` · `POST /copy` · `POST /create-from-file` | **`RequireContributor`** | `NotebookEndpoints.cs` |
| `GET .../notebooks/{notebookId}/files/**` (`/`, `/tree`, `/content`, `/origin-info`) | **`RequireApprovedUser`** | `NotebookEndpoints.cs` (fileGroup) |
| `POST .../files/{sync,upload,copy-from-project,create-folder,publish-to-project}` · `DELETE /{fileId}` · `PATCH /{fileId}/{rename,move}` | **`RequireContributor`** | `NotebookEndpoints.cs` (fileGroup) |
| `GET .../notebooks/{notebookId}/files/{fileId}/markdown[/content]` | **`RequireApprovedUser`** | `NotebookFileMarkdownEndpoints.cs` |
| `POST .../files/{fileId}/markdown[/retry]` | **`RequireContributor`** | `NotebookFileMarkdownEndpoints.cs` |
| `GET /api/notebooks/{notebookId}/header-toolbar` | **`RequireAdmin`** | `NotebookHeaderToolbarEndpoints.cs` — full config DTO (provider/model option lists, per-service runtime switches). Only feeds the admin service toolbar, which **changes service config**; locked Admin. |
| `GET /api/notebooks/{notebookId}/header-toolbar/chat-readiness` _(new, Phase 3)_ | **`RequireApprovedUser`** | `NotebookHeaderToolbarEndpoints.cs` — new lean read split out for every runner: `effectiveModelId` + `chat.blockers` (drives `chatModelMissing`/the no-model dialog) and the runtime-load state the Contributor load-and-run flow needs. No config-switch data. |
| `GET /api/notebook-templates` · `/{templateId}` · `/{templateId}/assistants` | **`RequireApprovedUser`** | `NotebookEndpoints.cs` (templatesGroup) |
| `GET /api/notebook-templates/avatar/{templateName}` | **Public** | `NotebookEndpoints.cs` — resolved (DECISIONS D3): rendered as raw `<img src>`, which cannot carry the Bearer header under D1, so stays `.AllowAnonymous()` (non-sensitive asset). |
| `GET /api/assistants/avatar/{assistantName}` | **Public** | `NotebookEndpoints.cs` (line 524) — same `<img src>` reason; stays `.AllowAnonymous()`. |
| `GET /api/assistants/conversation-starters/{assistantName}` | **`RequireApprovedUser`** | `NotebookEndpoints.cs` (line 547) — fetched via `callApi` (carries Bearer), so it is gated. |

### A.6 Conversations (chat)

| Method & route | Required guard | Source |
|---|---|---|
| `GET /api/conversations` (current user's list) | **`RequireApprovedUser`** | `UserConversationsEndpoints.cs` |
| `GET .../notebooks/{notebookId}/conversations` · `/{convoId}` | **`RequireApprovedUser`** | `NotebookConversationsEndpoints.cs` |
| `POST` · `PUT /{convoId}` · `POST /{convoId}/title/generate` · `POST /{convoId}/messages` · `PATCH/DELETE messages[...]` · `POST /{convoId}/save-as` · `DELETE /{convoId}` | **`RequireContributor`** | `NotebookConversationsEndpoints.cs` — these create/edit content; also where `UserId = null` must become the authenticated user (Phase 3). |

### A.7 Guides, assistants & operations — **Admin-gated (§2.4)**

Per §2.4 the Waterfall Team-Owner features (Guide/Assistant CRUD **incl. reads & avatars**, OpenAPI operation get/update, export/import) map to **`Admin`** in GuideAnts.

| Method & route | Required guard | Source |
|---|---|---|
| `GET /api/guides` · `/{guideId}` · `/{guideId}/avatar` | **`RequireAdmin`** | `GuidesEndpoints.cs` — Admin (§2.4). Guide *consumers* use the Public `/api/published/**` surface, not `/api/guides`, so no `RequireApprovedUser` listing is needed. (`{guideId}/avatar` is blob-fetched via `getAuthenticatedUrl`, so Admin works.) |
| `POST /api/guides` · `PUT/DELETE /{guideId}` · `POST /{guideId}/duplicate` · `POST /runtime/validate` | **`RequireAdmin`** | `GuidesEndpoints.cs` |
| `GET /api/guides/{guideId}/export` · `POST /api/guides/import` | **`RequireAdmin`** | `GuidesEndpoints.cs` (export/import, §2.4). |
| `GET /api/operations/{operationId}` · `PUT /{operationId}` · `POST /preview` | **`RequireAdmin`** | `GuidesEndpoints.cs` (operationsGroup) — OpenAPI tool operations. |
| `GET /api/assistants` · `/{assistantId}` · `/{assistantId}/avatar` | **`RequireAdmin`** | `AssistantsEndpoints.cs` |
| `POST /api/assistants` · `PUT/DELETE /{assistantId}` · `POST /{assistantId}/duplicate` | **`RequireAdmin`** | `AssistantsEndpoints.cs` |
| `GET /api/assistants/{assistantId}/export` · `POST /api/assistants/import` | **`RequireAdmin`** | `AssistantsEndpoints.cs` |
| `GET /api/assistants/{assistantId}/files/{fileId}/download` · `.../markdown[/content]` · `POST .../markdown/retry` | **`RequireAdmin`** | `GuidesMarkdownEndpoints.cs` (assistant content). |

### A.8 Guide publishing & published (consumer) endpoints

Publishing **management** is Admin; the **published** runtime is a public, per-guide **API-key** surface (not user-session auth) and stays anonymous.

| Method & route | Required guard | Source |
|---|---|---|
| `GET /api/guides/{guideId}/publish/validate-friendly-name/{friendlyName}` · `GET /in-project/{projectId}` | **`RequireAdmin`** | `GuidesPublishingEndpoints.cs` |
| `POST /api/guides/{guideId}/publish` · `PUT /{pubId}` · `POST /{pubId}/{deactivate,reactivate}` · `POST/DELETE /{pubId}/api-key` | **`RequireAdmin`** | `GuidesPublishingEndpoints.cs` |
| `GET /api/published/guides/{pubId}` · `/by-name/{friendlyName}` · `/{pubId}/avatar` | **Public** | `PublishedGuidesEndpoints.cs` — `.AllowAnonymous()`; per-guide API key when configured (`ApiKeyHash`). |
| `POST /api/published/guides/{pubId}/invoke` | **Public** (API key) | `PublishedGuidesEndpoints.cs` — `PublishedGuideAuthService` validates `X-Api-Key`. |
| `POST/GET/DELETE /api/published/projects/{projectId}/notebooks/{notebookId}/conversations/**` | **Public** (API key) | `PublishedNotebookConversationsEndpoints.cs` — `.AllowAnonymous()` + API-key check. |
| `POST /api/published/speech/transcribe` | **Public** (API key) | `PublishedSpeechEndpoints.cs` — `.AllowAnonymous()` + API-key check. |

### A.9 Usage, catalogs, lineage, misc

| Method & route | Required guard | Source | Notes |
|---|---|---|---|
| `GET /api/projects/{projectId}/guides/{guideId}/usage/**` · `.../assistants/{assistantId}/usage/**` | **`RequireAdmin`** | `GuideUsageEndpoints.cs` | "Guide usage data" → Admin (§2.4). |
| `GET /api/invocations/{invocationId}` · `/api/conversations/{conversationId}/invocations` · `/api/conversations/{conversationId}/turns/{turnIndex}/{invocations,messages}` | **`RequireAdmin`** | `GuideUsageEndpoints.cs` | Usage/observability detail. |
| `GET /api/usage/**` (`summary`, `by-project`, `details`, `breakdown`, project-scoped) | **`RequireAdmin`** | `UsageEndpoints.cs` | Reporting/telemetry surface (§5.4). Resolved (DECISIONS D3): Admin-only, no self-view exception. |
| `GET /api/catalogs/{models,tools,global-assistants[/{id}[/avatar]]}` | **`RequireApprovedUser`** | `CatalogEndpoints.cs` | Catalog browse. |
| `GET /api/lineage/{eventId}` · `/{eventId}/download` | **`RequireApprovedUser`** | `FileLineageEndpoints.cs` | Read-only lineage. |
| `POST /api/speech/transcribe` | **`RequireContributor`** | `SpeechEndpoints.cs` | Resolved (DECISIONS D3): content-authoring aid (dictation via `useAudioRecorder`). Published-guide voice input uses the Public `/api/published/speech/transcribe` instead. |
| `POST /api/quick-start` | **`RequireContributor`** | `QuickStartEndpoints.cs` | Creates sample project/content. |

### A.10 Document server (OnlyOffice editor integration) — `DocumentServerEndpoints.cs`

_Source-corrected: routes are `/api/documentserver/**` (the snapshot's `/api/onlyoffice/**`
and `OnlyOfficeEndpoints.cs` are stale and do not exist)._

| Method & route | Required guard | Source | Notes |
|---|---|---|---|
| `GET /api/documentserver/capabilities` | **`RequireApprovedUser`** | `DocumentServerEndpoints.cs` | |
| `POST /api/documentserver/editor-config` | **`RequireApprovedUser`** | `DocumentServerEndpoints.cs` | Returns editor config; `Reader` opens **view-only**, `Contributor`+ in edit mode. |
| `GET /api/documentserver/download` · `POST /api/documentserver/callback` | **Public** (doc-server token) | `DocumentServerEndpoints.cs` | **Server-to-server** from the document server; authenticated by the `?token=` (JWT) query param, **not** a user session — must stay anonymous to the user-auth pipeline. |
| `POST /api/documentserver/diagnostics/probe` | **`RequireAdmin`** | `DocumentServerEndpoints.cs` | Diagnostics. |
| `(all verbs) /api/documentserver/ds/{**path}` | **Public** (reverse proxy) | `DocumentServerEndpoints.cs` (`MapMethods`, `ExcludeFromDescription`) | YARP reverse proxy for the **browser-loaded editor** (scripts/assets/XHR/WebSocket to the configured `DocumentServer:InternalUrl`). These are iframe/asset requests that **cannot carry the Bearer header** (same constraint as the `<img src>` rule), so it must stay `.AllowAnonymous()`. It only forwards to the single configured internal URL (no open-redirect/SSRF surface). Not in OpenAPI (`ExcludeFromDescription`); Phase 3 must mark it anonymous **explicitly**. |

### A.10b Infrastructure / non-group routes (must be explicitly anonymous in Phase 3)

These are **not** in an `Endpoints/*.cs` group, so Phase 3's group sweep will miss them
unless called out. Each must get an explicit `.AllowAnonymous()`.

| Method & route | Required guard | Source | Notes |
|---|---|---|---|
| `GET /api/startup` | **Public** | `Program.cs` (inline `MapGet`, ~L244) | Readiness probe returning `{ status: "ready" }`; polled by Electron/host before auth exists. No data exposure. |
| `MapFallback` → SPA shell | **Public** | `Configuration/UiApplicationBuilderExtensions.cs` | Serves the client HTML/asset shell for non-API requests; the React app handles its own auth UI. The shell itself carries no protected data. |

### A.11 Settings & runtime — **Admin-gated (§5.4)**

The **entire** `/api/settings` surface (and its sub-groups) is administrative; pair
with the UI gating in §5.4 (non-Admins see only Personalization).

| Route group | Required guard | Source |
|---|---|---|
| `/api/settings/**` — sections, schema, readiness, chat-defaults, `embeddings/rebuild`, models, runtime-profiles, overview, connections usage, `infrastructure/**` | **`RequireAdmin`** | `SettingsEndpoints.cs` |
| `/api/settings/services/**` — service editors, providers, local-models download/load/unload/select, operations | **`RequireAdmin`** | `SettingsEndpoints.cs` (serviceEditorsGroup) |
| `/api/settings/routing/**` — `chat-targets`, preflight, readiness | **`RequireAdmin`** | `SettingsEndpoints.cs` (routingGroup) |
| `/api/settings/llama/**` — runtime inventory/load/unload/status, downloads, router entries | **`RequireAdmin`** | `SettingsEndpoints.cs` (llamaGroup) |
| `/api/settings/huggingface/**` — repository file browse | **`RequireAdmin`** | `SettingsEndpoints.cs` (huggingFaceGroup) |
| `GET /api/notebooks/{notebookId}/llama-runtime` · `/operations/{id}` | **`RequireApprovedUser`** | `NotebookLlamaRuntimeEndpoints.cs` — read status/inventory. |
| `POST /api/notebooks/{notebookId}/llama-runtime/load` | **`RequireContributor`** | `NotebookLlamaRuntimeEndpoints.cs` — resolved (DECISIONS D3): **only** to load the chat-configured model if needed to run a conversation (`llama-runtime-requires-load`). Part of running, not management. |
| `POST /api/notebooks/{notebookId}/llama-runtime/{unload,restart}` | **`RequireAdmin`** | `NotebookLlamaRuntimeEndpoints.cs` — resolved (DECISIONS D3): runtime **management**, Admin only. Config + global model mgmt at `/api/settings/llama/**` also `RequireAdmin`. |

### A.12 Project external tool-OAuth — `ProjectExternalAuthEndpoints.cs` (Phase 4.5)

This group must move from anonymous to authorized (§2.5 / Phase 4.5). Provider
**config** management is a project-setup action; the new per-user **token** flow is
bound to the current authenticated user.

| Method & route | Required guard | Notes |
|---|---|---|
| `GET /api/projects/{projectId}/external-auth` | **`RequireApprovedUser`** | List provider config for the project. |
| `PUT /api/projects/{projectId}/external-auth/{providerId}` · `DELETE /{providerId}` | **`RequireAdmin`** | Provider **config** edit. Resolved (DECISIONS D3): Admin-only (it is part of the guides/admin surface). |
| `POST .../{providerId}/oauth/authorize-url` _(new)_ | **`RequireApprovedUser`** | Per-user PKCE start; state bound to current user. |
| `POST .../{providerId}/oauth/callback` _(new)_ | **`RequireApprovedUser`** | Server-side code exchange; verifies state belongs to current user. |
| `GET .../{providerId}/oauth/status` _(new)_ | **`RequireApprovedUser`** | `{ connected, expiresAt, scopes }` — never returns the token. |
| `DELETE .../{providerId}/oauth` _(new)_ | **`RequireApprovedUser`** | Disconnect (delete current user's stored tokens). |

### A.13 Summary by guard

- **Public (anonymous):** `auth/register`, `auth/login`; all `/api/published/**`
  (per-guide API key); `documentserver` `download`/`callback` + the
  `documentserver/ds/{**path}` reverse proxy (doc-server token / browser-loaded
  editor); `GET /api/startup` (readiness probe); the **SPA shell** (`MapFallback`);
  the **name-based avatar GETs** (`/api/assistants/avatar/{name}`,
  `/api/notebook-templates/avatar/{name}`) because they render as raw `<img src>`
  and cannot carry the Bearer header under JWT (DECISIONS D3).
- **Authenticated (incl. `Pending`):** `auth/me` only. (No `logout` endpoint — JWT
  is discarded client-side, DECISIONS D1.)
- **`RequireApprovedUser` (Reader+):** project/notebook/file/folder/link **reads**,
  conversation **reads**, current-user profile, catalogs, lineage,
  `assistants/conversation-starters`, notebook llama-runtime **status reads**,
  `documentserver` capabilities/editor-config, external-auth read + per-user OAuth flow.
- **`RequireContributor` (write tier):** all content create/edit/delete across
  projects (except **delete project** → Admin), notebooks, files, folders, links,
  **conversations/messages**, quick-start, notebook llama-runtime **`load`** (the
  chat-configured model, to run a conversation), **speech transcribe** (dictation).
- **`RequireAdmin`:** `/api/admin/users/**`, **all `/api/settings/**`**, guides /
  assistants / operations management (§2.4), guide **usage** + invocations,
  `/api/usage/**`, **`DELETE /api/projects/{id}`**, notebook llama-runtime
  **`unload`/`restart`**, **external-auth provider config**, guide **publishing**,
  assistant file content, `documentserver` diagnostics, and arbitrary-user lookup.
