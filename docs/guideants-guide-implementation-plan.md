# GuideAnts Guide — Implementation Plan (Phase 1)

Last updated: 2026-06-21 (App Identity auth extension)  
Branch: `feature/guideants-guide`

> **This is the master spec.** Execution is orchestrated via
> [`guideants-guide/00-orchestration.md`](./guideants-guide/00-orchestration.md),
> which splits this plan into 8 subagent task briefs with build/test
> ([`guideants-guide/test-gate.md`](./guideants-guide/test-gate.md)) and CodeQL
> ([`guideants-guide/codeql-gate.md`](./guideants-guide/codeql-gate.md)) gates.
> Cross-cutting choices are tracked in
> [`guideants-guide/DECISIONS.md`](./guideants-guide/DECISIONS.md). The task briefs
> cite this document by section, so keep section numbers stable.

## 1. Goal

Add an in-app **GuideAnts Guide** — a system-owned guide with **client tools** that
will eventually perform UI actions on behalf of the signed-in user. Phase 1 delivers
**all plumbing** so chat works end-to-end; tool behaviors and guide instructions are
minimal placeholders until phase 1 is accepted.

Phase 1 success criteria:

- A hidden **GuideAnts System** project exists with two seeded guides (user + admin).
- The system project workspace is reachable **only** from Settings (not from Home,
  Projects, or direct URL guessing by non-admins).
- Every page that uses `HeaderActionsBar` shows a **leftmost Guide button** that opens
  a flyout with `guideants-chat` connected to the correct system guide for the caller's
  role.
- A user can send a message and receive a streamed assistant reply.
- Client-tool infrastructure is wired (`client://guideants-app`, `registerTool`,
  `setContextProvider`, tool-result resume loop) with stub handlers only.
- System published guides use **`AuthMode = AppIdentity`** (§4); chat runs under the
  signed-in user's identity (`UserId` on messages).
- Admin callers receive the admin guide; all other approved roles receive the user guide.

Explicitly deferred to **Phase 2** (post-acceptance):

- Real UI actions (navigation, settings changes, notebook operations, etc.).
- Rich guide instructions and multi-step tool orchestration.
- Admin-only tools beyond what the user guide already has.

## 2. Background & Reference Patterns

### 2.1 Client tools (existing platform capability)

Client actions are OpenAPI tools whose server URL uses the `client://` scheme. At
runtime `ToolCaller.ActionType` resolves to `ClientHandled`. The server emits
`external_tool_call`, pauses the LLM run, and resumes after the client posts tool
results to `POST …/tool-calls/results?resume=true`.

Reference implementation: **Worm Commander** (`WormCommander/Game/src/voice-snakes/speech.ts`)
registers handlers on `<guideants-chat>` via `registerTool()` and injects live state
via `setContextProvider()`.

### 2.2 GuideAnts Chat web component (existing client dependency)

The Electron/web client already depends on `guideants@^0.6.13` and uses it on
`PublicGuide.tsx`. The flyout reuses the same component:

```tsx
createElement('guideants-chat', {
  'pub-id': publishedGuideId,
  'api-base-url': apiBaseUrl,
  commandMode: true, // stateless command UX fits in-app assistant
});
```

Lazy-load `import('guideants')` on first flyout open (same pattern as `PublicGuide.tsx`).

### 2.3 Relevant existing code

| Area | Location |
|------|----------|
| Header action bar | `src/client/src/components/common/HeaderActionsBar.tsx` |
| Settings shell | `src/client/src/pages/Settings.tsx`, `SettingsTabNavigation.tsx` |
| Route guards | `src/client/src/components/ProtectedRoute.tsx`, `AppContent.tsx` |
| Bootstrap guides | `src/server/GuideAntsApi/Resources/bootstrap/guides/` |
| Guide seeding | `RequiredGuidesAssistantsSeeder.cs` |
| Published guide auth | `PublishedGuideAuthService.cs` |
| Publish guide UI | `PublishGuideDialog.tsx`, `configTabs/AuthTab.tsx` |
| Client-handled tool pause/resume | `ThreadRun.cs`, `PublishedNotebookConversationsEndpoints.cs` |
| Public chat embedding | `src/client/src/pages/PublicGuide.tsx` |

## 3. Architecture Overview

```text
┌─────────────────────────────────────────────────────────────────────────┐
│ GuideAnts Client (Electron / web)                                       │
│                                                                         │
│  HeaderActionsBar ──► GuideAntsGuideButton (leftmost)                   │
│         │                                                               │
│         ▼                                                               │
│  GuideAntsGuideFlyout                                                   │
│    ├─ guideants-chat (pub-id from /api/system-guide/session)            │
│    ├─ setAuthToken(app JWT)                                             │
│    ├─ setContextProvider(app context: route, role, userId)              │
│    └─ registerTool(...) ──► guideantsAppBridge.ts (stub tools, phase 1)   │
└───────────────────────────────┬─────────────────────────────────────────┘
                                │ published guide SSE + tool results
                                ▼
┌─────────────────────────────────────────────────────────────────────────┐
│ GuideAnts API                                                           │
│                                                                         │
│  GuideAntsSystemSeeder (startup)                                        │
│    ├─ Project: "GuideAnts System" (IsSystemProject=true)                │
│    ├─ Guides: "GuideAnts Guide" + "GuideAnts Guide Admin"               │
│    ├─ Notebooks + PublishedGuide rows (internal, no public friendlyName)│
│    └─ ApplicationSettings section: GuideAntsSystem (IDs + pub IDs)        │
│                                                                         │
│  GET /api/system-guide/session  ──► pub-id + bridge config by role       │
│  Published guide auth ──► App Identity mode (see §4)                     │
│  ClientHandled tools ──► external_tool_call ──► flyout bridge           │
└─────────────────────────────────────────────────────────────────────────┘
```

## 4. Published Guide Extension — App Identity Authentication

This feature depends on a **platform extension** to published guides: a new
authentication mode for embedded first-party clients (GuideAnts Notebooks app)
that validates the same app-issued JWT used by the main client, resolves the
caller to a real `Users` row, and persists identity on published conversations.

This is **not** a special case hard-coded to system guide pub-IDs. System guides
are the first consumer; the auth mode is a general published-guide capability
set only through internal/server paths.

### 4.1 Auth modes (published guide)

Introduce an explicit auth mode on `PublishedGuide` (replacing implicit inference
from nullable webhook/API-key fields):

| Mode | Set via publish UI? | Behavior |
|------|---------------------|----------|
| `Anonymous` | Yes (default) | No auth required on published endpoints |
| `Webhook` | Yes | `X-Published-Auth` token POSTed to configured webhook |
| `ApiKey` | Yes | `x-guideants-apikey` header required |
| **`AppIdentity`** | **No** | `X-Published-Auth` must be a valid **GuideAnts app JWT**; user resolved from token |

**Mutual exclusivity:** exactly one mode per published guide. Migration maps existing
rows: `ApiKeyHash` set → `ApiKey`; `AuthValidationWebhookUrl` set → `Webhook`;
otherwise → `Anonymous`. System seeder creates rows with `AppIdentity`.

Suggested storage:

```csharp
public enum PublishedGuideAuthMode
{
    Anonymous = 0,
    Webhook = 1,
    ApiKey = 2,
    AppIdentity = 3,
}

// PublishedGuide.cs
public PublishedGuideAuthMode AuthMode { get; set; } = PublishedGuideAuthMode.Anonymous;
```

Legacy columns (`AuthValidationWebhookUrl`, `ApiKeyHash`) remain for `Webhook` /
`ApiKey` modes. They are ignored when `AuthMode == AppIdentity`.

### 4.2 Server — validation (`PublishedGuideAuthService`)

Extend `ValidateAsync` with an `AppIdentity` branch **before** the anonymous fallback:

1. Read the app JWT from the **`GuideAnts.Auth` HttpOnly cookie** (same-host;
   D-GG-1). Optionally also accept an `X-Published-Auth` Bearer of the same
   signature for Swagger / manual API testing.
2. Validate JWT with the **same signing key, issuer, and lifetime rules** as
   `RequireApprovedUser` (reuse existing token validation helper; do not duplicate
   crypto logic).
3. Resolve user from claims (`sub` / user id) and verify `SecurityStamp` / account
   status (approved, not pending-only where applicable).
4. Return `AuthValidationResult`:
   - `UserIdentity` = `userId.ToString()` (stable GUID string)
   - `InternalUserId` = `Guid` (new property on result type)

**Reject** anonymous access when `AuthMode == AppIdentity` and token is missing or
invalid.

Do **not** use a webhook URL for `AppIdentity` guides — validation is in-process.

### 4.3 Server — identity on published conversations

Today published chat stores `UserId = null` and only `ExternalUserIdentity` (string).
For `AppIdentity` mode, also persist the internal user:

In `PublishedConversationService` (and resume path), when
`authResult.InternalUserId` is present:

- `CreateUserMessageRequest.UserId` = `InternalUserId`
- `ExternalUserIdentity` = `UserIdentity` (GUID string, for compatibility)
- `PublishedConversationStreamPolicy.ResolveUserIdentityAsync` returns
  `StreamUserIdentity(InternalUserId, displayName, UserIdentity)` instead of
  `(null, "User", externalUserIdentity)`

This aligns published system-guide chat with private notebook chat for usage,
auditing, and phase-2 client tools that act on behalf of the signed-in user.

### 4.4 Server — who may set `AppIdentity`

| Path | May set `AuthMode = AppIdentity`? |
|------|-----------------------------------|
| `GuideAntsSystemSeeder` / internal publish helper | Yes |
| `POST /api/guides/{id}/publish` (admin UI) | **No** — reject if requested |
| `PUT` published guide update (admin UI) | **No** — cannot switch to/from `AppIdentity` |
| Ad-hoc SQL / migration / seeder repair | Yes (operational) |

Implement guard in `GuidesPublishingEndpoints` publish and update handlers:

```csharp
if (dto.AuthMode == PublishedGuideAuthMode.AppIdentity)
    return Results.BadRequest(new { error = "app_identity_auth_not_configurable_via_api" });
```

Also reject updates that attempt to change `AuthMode` away from `AppIdentity` on
rows already in that mode (system guides must stay on this mode). Optionally allow
**Admin-only internal endpoint** later; not required for phase 1.

Public config endpoint (`GET /api/published/{pubId}`) must expose auth mode so
clients know auth is required:

```json
{
  "authMode": "AppIdentity",
  "requiresAuth": true,
  "requiresApiKey": false
}
```

Update `requiresAuth` computation: `requiresAuth = authMode != Anonymous`.

### 4.5 Publish guide UI — read-only indication (no configuration)

**Requirement:** operators must **not** enable or disable App Identity auth from
the publish dialog. The UI **must** show when it is in effect.

Changes to `PublishGuideDialog` / `AuthTab`:

- Add `authMode` to `PublishedGuideDto` (and client `types/guides.ts`).
- When `authMode === 'AppIdentity'`:
  - Show a **read-only info panel** at the top of the Auth tab, e.g.  
    *“Authentication: GuideAnts app identity — callers must present a signed-in
    GuideAnts user token. This mode is managed by the system and cannot be changed
    here.”*
  - **Hide or disable** webhook URL, webhook timeout, API key generate/remove, and
    any controls that imply a different auth mode.
  - Do not include `authMode` in `UpdatePublishedGuideDto` payloads from the form.
- When `authMode` is `Webhook`, `ApiKey`, or `Anonymous`, existing Auth tab behavior
  is unchanged.

Optional secondary indicators (phase 1 nice-to-have):

- `GuideCard` published badge tooltip mentions app-identity auth when applicable.
- System Guides workspace shows the same read-only label on published guide summary.

**Out of scope:** no new Auth tab radio/toggle for App Identity; no documentation
link that implies admins can self-enable this for arbitrary guides in phase 1.

### 4.6 Client — flyout token wiring

> **Correction (auth source) — D-GG-1 LOCKED: same-host session cookie.** The app
> session JWT lives in an **HttpOnly cookie** (`GuideAnts.Auth`), so client JS
> **cannot** read it — there is no `authService.getAccessToken()`. Because the API
> and UI are **same-host**, the browser attaches that cookie to the same-origin
> published-guide requests `guideants-chat` makes. The `AppIdentity` validator
> (§4.2) reads/validates the cookie. **No token is minted, read, or stored in JS,
> and `setAuthToken()` is not used for auth.** See
> [`guideants-guide/DECISIONS.md`](./guideants-guide/DECISIONS.md) D-GG-1. Phase 6
> verifies the component transmits the cookie (same-origin default; cross-port needs
> `credentials:'include'` + CORS, which the app already allows).

`setContextProvider()` remains **supplementary** (route, role, display name for
the model). It is not the security boundary — JWT validation in §4.2 is.

### 4.7 Auth mode summary diagram

```text
guideants-chat.setAuthToken(appJwt)
        │
        ▼
POST /api/published/.../messages?pubId=...
  Header: X-Published-Auth: <appJwt>
        │
        ▼
PublishedGuideAuthService
  AuthMode == AppIdentity?
    ├─ yes → validate JWT → InternalUserId + UserIdentity
    └─ no  → existing Webhook / ApiKey / Anonymous paths
        │
        ▼
PublishedConversationService
  UserId + ExternalUserIdentity on messages
        │
        ▼
Client tools (phase 2) act with same user context
```

## 5. Data Model & Bootstrap

### 5.1 Project flag

Add to `Project`:

```csharp
public bool IsSystemProject { get; set; }
```

Migration adds column default `false`. The system project is the only row with
`IsSystemProject = true` (enforced in seeder + validation).

Well-known slug: **`guideants-system`** (stable across installs; IDs stored in settings).

### 5.2 Application settings section

New settings section **`GuideAntsSystem`** (JSON in `ApplicationSettings` table):

```json
{
  "projectId": "<guid>",
  "userGuideId": "<guid>",
  "adminGuideId": "<guid>",
  "userNotebookId": "<guid>",
  "adminNotebookId": "<guid>",
  "userPublishedGuideId": "<guid>",
  "adminPublishedGuideId": "<guid>",
  "clientBridgeId": "guideants-app"
}
```

The seeder writes this section once; runtime reads it for session config and access
checks (avoid hard-coding GUIDs in the client).

### 5.3 New seeder: `GuideAntsSystemSeeder`

Runs after `RequiredGuidesAssistantsSeeder` in `Program.cs` startup. Idempotent.

Steps:

1. If `GuideAntsSystem.projectId` already set and project exists → skip creation, verify
   guides/notebooks/published rows still present (repair if missing).
2. Create project **GuideAnts System** (`IsSystemProject = true`, slug `guideants-system`).
3. Import bootstrap guides (folder-based export layout under
   `Resources/bootstrap/guides/`):
   - `guideants-guide/` → **GuideAnts Guide** (user)
   - `guideants-guide-admin/` → **GuideAnts Guide Admin**
4. For each guide, create a notebook in the system project and bind the guide.
5. Publish each guide **internally** via seeder-only helper (not the admin publish UI):
   - `Active = true`
   - `FriendlyName = null` (no `/public/{friendlyName}` surface)
   - `CommandMode = true`
   - `AuthMode = AppIdentity` (see §4)
   - `MaxTurns` reasonable default (e.g. 50) for in-app use
6. Persist IDs into `GuideAntsSystem` settings section.

### 5.4 Bootstrap guide contents (phase 1 minimal)

Each guide folder contains at minimum:

| File | Phase 1 content |
|------|-----------------|
| `manifest.json` | Name, description, default assistant |
| `instructions.md` | Short system prompt: in-app assistant; tools are stubs in phase 1 |
| `OpenAPI/Web Connector.json` | `client://guideants-app` with one stub tool `AppEcho` (returns context echo) |

Admin guide includes the same stub plus placeholder paths for future admin-only tools
(e.g. `AppOpenSettings`, `AppListUsers`) marked in OpenAPI but **not registered** in
phase 1 client bridge until phase 2.

Instructions must use **actual `operationId` values** (`AppEcho`, not prefixed names).

### 5.5 Published guide auth mode migration

Add migration for `PublishedGuide.AuthMode` (§4.1). Backfill existing rows from
`ApiKeyHash` / `AuthValidationWebhookUrl`. New system guide rows created by seeder
with `AuthMode = AppIdentity` only.

## 6. Backend API & Authorization (system project)

### 6.1 Hide system project from normal listings

Update `ProjectService.GetProjectsAsync()` (and any home/recent project aggregations):

```csharp
where !p.Deleted && !p.IsSystemProject
```

Also exclude from project search, quick-start suggestions, and usage dashboards unless
explicitly filtered for admin system views.

### 6.2 System project access enforcement

**Rule:** Non-admin users must not read or mutate the system project even if they know
the UUID.

Apply consistently on:

- `GET /api/projects/{projectId}`
- `GET /api/projects/{projectId}/details`
- All `/api/projects/{projectId}/…` notebook, file, and guide routes

Implementation: shared helper `SystemProjectAccessGuard`:

```csharp
// Returns 404 (not 403) to avoid leaking existence of system project IDs.
if (project.IsSystemProject && !user.IsAdmin) return NotFound();
```

Admin users may access the system project workspace (for guide editing in Settings flow).

Additional hardening:

- `PUT`/`DELETE` on system project itself → Admin only; disallow delete while
  `IsSystemProject` (or soft-block with clear error).
- Prevent publishing system guides to a **public** `FriendlyName` via normal publish UI.
- Prevent changing `AuthMode` on system published guides via publish UI (§4.4).

### 6.3 Session config endpoint

New authenticated endpoint for the flyout:

```
GET /api/system-guide/session
Authorization: Bearer <app-jwt>
```

Response (example):

```json
{
  "publishedGuideId": "<guid>",
  "projectId": "<guid>",
  "notebookId": "<guid>",
  "guideId": "<guid>",
  "guideName": "GuideAnts Guide",
  "clientBridgeId": "guideants-app",
  "isAdminGuide": false,
  "commandMode": true
}
```

Selection logic:

| Caller role | Published guide |
|-------------|-----------------|
| `Admin` | Admin system guide |
| `Contributor`, `Reader`, etc. (approved, non-admin) | User system guide |

Pending/unapproved users: endpoint returns `403` (flyout button hidden or disabled).

Register in `SystemGuideEndpoints.cs` with `RequireAuthorization("RequireApprovedUser")`.

### 6.4 System project settings link endpoint (optional convenience)

```
GET /api/system-guide/workspace
```

Returns `{ projectId, projectSlug }` for Settings nav link target. Admin only (`RequireAdmin`).
Non-admin → `404`.

## 7. Frontend — Settings Access

### 7.1 Settings top nav link

Add an **admin-only** entry to the Settings header area (the bar above tab navigation,
alongside Home / Settings / User menu — **not** mixed into personalization tabs).

Proposed label: **System Guides**  
Target: `/projects/{systemProjectId}` (standard project workspace for editing guides)

Implementation:

1. On Settings mount (admin only), fetch `GET /api/system-guide/workspace`.
2. Render `HeaderIconLinkButton` in the Settings header row (left of `HomeButton` or
   adjacent per design — distinct from flyout button placement).
3. Do **not** add this link to `Home`, `Projects`, or global nav.

### 7.2 Route protection

Add to `AppContent.tsx`:

```tsx
<Route
  path="/projects/:projectId/*"
  element={withSystemProjectGuard(withProtection(withProjectProvider(...)))}
/>
```

`withSystemProjectGuard`:

- If `projectId === systemProjectId` (from session/workspace API or cached settings
  snapshot) and `role !== 'Admin'` → `<Navigate to="/settings" replace />`.
- Server still enforces; this is UX-only.

Alternatively, dedicated route `/settings/system-guides` that loads project details via
admin API and renders `ProjectDetails` — avoids exposing raw UUID in the URL bar.
**Recommendation:** use `/settings/system-guides` as the Settings link target; it
internally resolves the system project ID from the API so bookmarks do not embed a
stable UUID. Direct `/projects/{uuid}` for the system project still returns 404/403
for non-admins.

## 8. Frontend — Guide Flyout

### 8.1 Global shell components

| Component | Responsibility |
|-----------|----------------|
| `GuideAntsGuideProvider` | Flyout open state, session config fetch, bridge lifecycle |
| `GuideAntsGuideButton` | Leftmost header icon; toggles flyout |
| `GuideAntsGuideFlyout` | Panel UI + `guideants-chat` host |
| `guideantsAppBridge.ts` | `registerTool` / `setContextProvider` for `client://guideants-app` |

Mount `GuideAntsGuideProvider` in `App.tsx` (inside `AuthProvider`, sibling to routes).

### 8.2 Header integration

Create a small wrapper used everywhere `HeaderActionsBar` appears:

```tsx
<HeaderActionsBar align="end">
  <GuideAntsGuideButton />   {/* always first child = leftmost when align=end… */}
  …existing actions…
</HeaderActionsBar>
```

**Note:** `HeaderActionsBar` with `align="end"` renders children in order left-to-right
before the overflow menu; placing `GuideAntsGuideButton` as the **first child** puts it
leftmost in the action group.

Files to update (complete list from codebase audit):

- `src/client/src/pages/Home.tsx`
- `src/client/src/pages/Settings.tsx`
- `src/client/src/pages/Projects.tsx`
- `src/client/src/pages/Conversations.tsx`
- `src/client/src/pages/Usage.tsx`
- `src/client/src/pages/NewProject.tsx`
- `src/client/src/components/layouts/ProjectLayout.tsx`
- `src/client/src/components/layouts/NotebookLayout.tsx`
- `src/client/src/components/guides/editor/EditorHeader.tsx`
- `src/client/src/components/notebook/conversations/LexicalToolbar.tsx` (toolbar variant)

Hide `GuideAntsGuideButton` when `status !== 'authenticated'` or `role === 'Pending'`.

### 8.3 Flyout UX (phase 1)

- **Trigger:** icon button (suggested: chat/sparkle icon, tooltip "GuideAnts Guide").
- **Surface:** anchored flyout panel (~400px wide, max-height viewport, right-aligned below
  header) or slide-over from the right — match existing popover z-index patterns.
- **Close:** Escape, outside click, toggle button.
- **Loading:** spinner while session config loads.
- **Error:** toast if session fetch fails; do not render chat without valid `pub-id`.

### 8.4 Chat wiring

On flyout open:

1. `GET /api/system-guide/session` → `publishedGuideId`, etc.
2. Lazy `import('guideants')`.
3. Render `guideants-chat` with:
   - `pub-id={publishedGuideId}`
   - `api-base-url={API_BASE_URL without /api suffix}`
   - `command-mode="true"` (attribute)
   - `speech-to-text-enabled="true"` (attribute — ASR enabled, D-GG-5)
4. On custom element ready (auth is the same-host cookie — **no `setAuthToken`**,
   see §4.6 correction + D-GG-1):
   - `chat.setContextProvider(() => JSON.stringify(buildAppContext()))`
   - `registerGuideAntsAppBridge(chat)` — stub tools

`buildAppContext()` (phase 1):

```json
{
  "route": "/projects/…/notebooks/…",
  "role": "Contributor",
  "userId": "…",
  "displayName": "…"
}
```

Refresh context provider on route change (`useLocation`) while flyout is open.

### 8.5 Client bridge stub (phase 1)

File: `src/client/src/features/guideantsGuide/guideantsAppBridge.ts`

Register minimal handlers matching OpenAPI `operationId`:

| Tool | Phase 1 behavior |
|------|------------------|
| `AppEcho` | Return `{ status: "ok", echo: args, context: buildAppContext() }` |

Verify end-to-end loop: user message → model calls `AppEcho` → handler runs → result
posted → assistant continues. If model does not call tools unprompted, phase 1 manual
test can use instruction "call AppEcho with message hello".

Admin guide OpenAPI lists future tools; client registers only tools present in both
OpenAPI **and** bridge (admin-only registration gated on `isAdminGuide` from session).

## 9. Delivery Workstreams

### Workstream A — Published guide App Identity auth (platform)

1. Migration: `PublishedGuide.AuthMode` + backfill
2. `PublishedGuideAuthMode` enum + `AuthValidationResult.InternalUserId`
3. `PublishedGuideAuthService` App Identity branch + shared JWT validator
4. Identity persistence in `PublishedConversationService` / stream policy
5. API guards: reject `AppIdentity` from publish/update DTOs
6. Expose `authMode` on `PublishedGuideDto` and `GET /api/published/{pubId}`
7. Unit + integration tests for all four auth modes

### Workstream B — Publish UI read-only indication

1. `authMode` on client `PublishedGuideDto` / API types
2. `AuthTab` read-only panel when `authMode === 'AppIdentity'`
3. Disable webhook/API-key controls in that state
4. Component test: App Identity guide shows indicator, controls disabled
5. Optional: `GuideCard` tooltip / badge

### Workstream C — Data model & seeder (system project)

1. Migration: `Project.IsSystemProject`
2. `GuideAntsSystemSeeder` + interface registration
3. Bootstrap guide folders (user + admin)
4. Settings section read/write helpers
5. Unit tests: seeder idempotency, settings round-trip

### Workstream D — API & authorization (system project)

1. Filter system project from listings
2. `SystemProjectAccessGuard` on project/notebook routes
3. `GET /api/system-guide/session`
4. `GET /api/system-guide/workspace` (admin)
5. Integration tests: non-admin cannot fetch system project; session returns correct pub-id

### Workstream E — Flyout UI (frontend)

1. `GuideAntsGuideProvider` + flyout + button
2. Integrate button into all `HeaderActionsBar` call sites
3. Lazy guideants load + chat mount
4. `guideantsAppBridge.ts` stub
5. Component tests: button visibility by role; flyout open/close

### Workstream F — Settings entry (frontend)

1. Admin link in Settings header
2. `/settings/system-guides` route (or guarded project route)
3. `ProtectedRoute requireAdmin` + server-side guard tests

### Workstream G — Documentation & acceptance

1. Update `docs/developer-config-guide.md` with system guide overview (brief)
2. Manual test script (§10.3)

## 10. Testing Plan

### 10.1 Published guide auth (platform)

| Test | Expectation |
|------|-------------|
| `AuthMode = AppIdentity` + valid app JWT | Auth succeeds; `InternalUserId` set |
| `AuthMode = AppIdentity` + missing token | 401 |
| `AuthMode = AppIdentity` + expired JWT | 401 |
| Publish UI POST with `AppIdentity` | 400 rejected |
| Update published guide to `AppIdentity` via UI | 400 rejected |
| Message persist | `UserId` and `ExternalUserIdentity` set on user message |
| `GET /api/published/{pubId}` | Returns `authMode: "AppIdentity"`, `requiresAuth: true` |
| Auth tab (UI) | Read-only panel; webhook/API key controls disabled |

### 10.2 System project & flyout (backend + frontend)

| Test | Expectation |
|------|-------------|
| Seeder first run | System project + 2 guides + 2 `AppIdentity` published guides + settings |
| Seeder second run | No duplicates |
| `GET /api/projects` | Excludes system project |
| `GET /api/projects/{systemId}` as Reader | 404 |
| `GET /api/projects/{systemId}` as Admin | 200 |
| `GET /api/system-guide/session` as Admin | Admin published guide ID |
| `GET /api/system-guide/session` as Contributor | User published guide ID |
| Guide button visible | Approved user on Home |
| Guide button hidden | Pending / logged out |
| Flyout opens chat | Mock session API; `setAuthToken` called; pub-id set |
| Settings System Guides link | Admin only |
| Direct navigate to system project UUID as Reader | Redirect or not-found |

### 10.3 Manual acceptance (phase 1)

1. Log in as **Contributor** → open flyout → send "Hello" → receive streamed reply.
2. Log in as **Admin** → same flyout → confirm admin published guide (different guide name
   in session API / optional badge in flyout header).
3. Log in as **Contributor** → confirm system project absent from Home/Projects.
4. Paste system project URL as **Contributor** → blocked.
5. Log in as **Admin** → Settings → System Guides → open publish dialog on system guide
   → Auth tab shows **App Identity** read-only indicator (no webhook/API key toggles).
6. Log in as **Admin** → Settings → System Guides → open workspace → edit user guide
   instructions → save.
7. (Optional) Prompt that triggers `AppEcho` → verify tool result in stream/workflow UI.

## 11. Risks & Mitigations

| Risk | Mitigation |
|------|------------|
| UUID leakage via client bundle | Use `/settings/system-guides` + API-resolved IDs; 404 for unauthorized API |
| Published guide cost limits | Exempt system published guides from daily/billing limits (or set high internal limits) |
| `guideants-chat` token refresh | Refresh `setAuthToken` on flyout open and on auth refresh events |
| Header bar clutter | Single icon; flyout hides when closed |
| Model doesn't invoke stub tool | Phase 1 acceptance is chat-only; tool loop verified via manual prompt or test hook |
| Operators confuse App Identity with webhook auth | Publish UI read-only panel; no enable control in dialog |

## 12. Phase 2 Preview (out of scope)

After phase 1 acceptance:

- User tools: navigate, open notebook, toggle theme, etc.
- Admin tools: user management shortcuts, service status, settings navigation.
- Rich instructions with senses-like app snapshot in context provider.
- Tool registration split: `registerUserTools` / `registerAdminTools`.
- Possibly unify with notebook conversation UI patterns for tool activity display.

## 13. Open Decisions (for review)

1. **Settings link URL:** `/settings/system-guides` (recommended) vs raw `/projects/{uuid}`.
2. **Flyout placement:** right-aligned panel vs modal drawer.
3. **Admin flyout indicator:** show "Admin" badge in flyout header?
4. **STT in flyout:** enable `speech-to-text-enabled` in phase 1 or defer?
5. **Cost limits:** explicit exemption for system published guides vs internal billing cap.

**Resolved:**

- **App Identity auth:** platform `PublishedGuide.AuthMode`; seeder-only assignment; publish UI read-only indication only.

> These open decisions are now **LOCKED** in
> [`guideants-guide/DECISIONS.md`](./guideants-guide/DECISIONS.md): §13.1 settings
> link = **D-GG-2** (`/settings/system-guides`); §13.2 placement + §13.3 admin badge
> + §13.4 STT = **D-GG-5** (right panel, admin badge, **ASR enabled**); §13.5 cost
> limits = **D-GG-4** (no default limits, editable via Project UI). A new **D-GG-1**
> (flyout auth) was added: the HttpOnly cookie is **same-host**, so the
> `AppIdentity` validator reads the `GuideAnts.Auth` cookie — no token minted/stored.

## 14. File Inventory (planned)

### New backend

- `GuideAntsApi.DataModel/Migrations/*_AddPublishedGuideAuthMode.cs`
- `GuideAntsApi.DataModel/Migrations/*_AddProjectIsSystemProject.cs`
- `GuideAntsApi.DataModel/Models/PublishedGuideAuthMode.cs` (or inline enum)
- `GuideAntsApi/Services/Auth/IAppJwtValidator.cs` (or reuse existing validator)
- `GuideAntsApi/Services/Bootstrap/GuideAntsSystemSeeder.cs`
- `GuideAntsApi/Services/Bootstrap/IGuideAntsSystemSeeder.cs`
- `GuideAntsApi/Services/Bootstrap/InternalPublishedGuideFactory.cs` (seeder publish helper)
- `GuideAntsApi/Services/SystemGuide/SystemProjectAccessGuard.cs`
- `GuideAntsApi/Endpoints/SystemGuideEndpoints.cs`
- `GuideAntsApi/Resources/bootstrap/guides/guideants-guide/**`
- `GuideAntsApi/Resources/bootstrap/guides/guideants-guide-admin/**`
- Tests under `GuideAntsApi.Tests/Services/PublishedGuides/` (auth mode)
- Tests under `GuideAntsApi.Tests/Services/SystemGuide/`

### New frontend

- `src/client/src/features/guideantsGuide/GuideAntsGuideProvider.tsx`
- `src/client/src/features/guideantsGuide/GuideAntsGuideButton.tsx`
- `src/client/src/features/guideantsGuide/GuideAntsGuideFlyout.tsx`
- `src/client/src/features/guideantsGuide/guideantsAppBridge.ts`
- `src/client/src/features/guideantsGuide/types.ts`
- `src/client/src/pages/SystemGuidesWorkspace.tsx` (if using settings sub-route)
- `src/client/src/features/guideantsGuide/__tests__/…`

### Modified

- `Program.cs` (register seeder)
- `PublishedGuide.cs`, `PublishedGuideDto.cs`
- `PublishedGuideAuthService.cs`, `IPublishedGuideAuthService.cs` (`AuthValidationResult`)
- `GuidesPublishingEndpoints.cs` (reject App Identity from UI API)
- `PublishedGuidesEndpoints.cs` (expose `authMode` on public config)
- `PublishedConversationService.cs`, `PublishedConversationStreamPolicy.cs`
- `Project.cs`, `ProjectService.cs`, `ProjectEndpoints.cs`
- `AppContent.tsx`, `App.tsx`
- `PublishGuideDialog.tsx`, `configTabs/AuthTab.tsx`, `types/guides.ts`
- All `HeaderActionsBar` call sites (§8.2)
- `Settings.tsx` (admin link)
- `src/client/src/services/api.ts` (system-guide session API)

---

**Review ask:** Confirm open decisions in §13 (Settings route, flyout UX, phase 1
acceptance scope).
