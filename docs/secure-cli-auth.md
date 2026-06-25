# Secure CLI Authentication (browser device-code flow)

This document is the operator, admin, and developer guide for how the
command-line installer (`installer/guideants.sh`) authenticates to the GuideAnts
API when creating or removing host folder mounts. It complements the design plan
in [`../plans/secure-cli-auth-device-code-flow.md`](../plans/secure-cli-auth-device-code-flow.md)
and the mount operator guide in [`host-folder-mounts.md`](./host-folder-mounts.md).

## Overview

### What it does

The installer's `--mount` / `--unmount` operations call admin-only API endpoints
(`/api/projects`, `/api/projects/{id}/notebooks`,
`/api/projects/{id}/host-folder-mounts`). They need a bearer token to do so.

Instead of persisting a long-lived token on disk, the installer obtains a
**short-lived, single-use token held only in process memory**, released only
after the user explicitly approves the request in a browser where they are
already logged in. Nothing token-bearing is ever written to a file.

### Why it exists (the problem it replaces)

Previously, a successful `/api/auth/login` wrote the user's JWT to
`/app/ContentFiles/.cli-auth-token`. That path is bind-mounted **read-write into
the `guideants-ai` sandbox**, so the AI agent running in the sandbox could read a
~30-day, full-access bearer token (`Jwt:LifetimeMinutes = 43200`) and impersonate
the user against every API. The token also sat on disk in plaintext.

This feature removes that write entirely and replaces the installer's
file-based token retrieval with an interactive, approval-gated flow.

### Security properties

- **No token at rest.** Nothing token-bearing is written to `/app/ContentFiles`
  (or anywhere the sandbox can read). The issued token lives only in the
  installer's `AUTH_TOKEN` shell variable and is discarded when the script exits.
- **The browser never sees a token.** The browser only ever receives a
  `sessionId`. The token is released exclusively to the holder of the
  `deviceSecret` (the installer process). A malicious page that learns the
  `sessionId` cannot retrieve a token or even learn the approval state.
- **Single-use, short-lived.** The issued JWT is valid ~10 minutes and can be
  fetched exactly once; a second fetch returns `410 Gone`.
- **Human-gated.** Approval requires an interactive, cookie-authenticated click
  by an **admin** in the browser. The sandbox agent has no browser or cookie and
  cannot self-approve.

## How it works

### The flow

```
installer (bash, host)                       browser (cookie-auth)         API
 POST /api/cli/sessions  ───────────────────────────────────────────────►  create CliAuthSession
   ◄── { sessionId, deviceSecret, expiresAt }                              (Pending; store secret HASH only)
 open http://localhost:5107/cli/authorize?session=<sessionId>  ─────────►  /cli/authorize page (admin)
                                          user clicks Approve ───────────►  POST /api/cli/sessions/{id}/approve
                                                                            (cookie-auth) → Approved + userId
 poll GET /api/cli/sessions/{id}/token  (header X-Device-Secret) ───────►  if Approved & not consumed:
   ◄── 202 pending … then 200 { token (~10 min), expiresAt }                issue short-lived JWT, mark Consumed
 use token IN MEMORY for projects/notebooks/host-folder-mounts
 run scripts/guideants-host-mount.sh apply   (token discarded on exit)
```

### Components

| Layer | File | Responsibility |
|-------|------|----------------|
| Entity | [`CliAuthSession.cs`](../src/server/GuideAntsApi.DataModel/Models/CliAuthSession.cs) | Persisted session: `SessionId` (PK), `DeviceSecretHash`, `Status`, `UserId`, `CreatedAt`, `ExpiresAt` |
| Migration | `20260623200101_AddCliAuthSession` | Creates the `CliAuthSessions` table + `ExpiresAt`/`UserId` indexes |
| Service | [`CliAuthService.cs`](../src/server/GuideAntsApi/Services/Auth/CliAuthService.cs) | Create / approve / issue-token logic, hashing, single-use, cleanup |
| Token | [`JwtTokenService.cs`](../src/server/GuideAntsApi/Services/Auth/JwtTokenService.cs) | `IssueToken(user, role, TimeSpan? lifetimeOverride)` overload for short-lived tokens |
| Endpoints | [`CliAuthEndpoints.cs`](../src/server/GuideAntsApi/Endpoints/CliAuthEndpoints.cs) | `/api/cli/sessions` HTTP surface |
| Frontend | [`CliAuthorize.tsx`](../src/client/src/pages/CliAuthorize.tsx) | The `/cli/authorize` approval page |
| Installer | [`guideants.sh`](../installer/guideants.sh) (`acquire_token`) | Drives the flow from bash |

### Session lifecycle

A `CliAuthSession` moves strictly forward through three states:

```
Pending ──(admin approves)──► Approved ──(token fetched once)──► Consumed
```

- **Pending** → created by `POST /api/cli/sessions`. Carries only the SHA-256
  hash of the device secret (never the plaintext) and an expiry ~5 minutes out.
- **Approved** → set by `POST /api/cli/sessions/{id}/approve`; binds the
  approving admin's `UserId`. Already-approved is idempotent; a `Consumed`
  session can never be re-approved.
- **Consumed** → set in the same database write that issues the token, enforcing
  single use.

Expired rows are deleted opportunistically on each service call (the same
cleanup pattern used by the OAuth authorization-state service).

### Key security mechanisms

- **Hash-only secret.** The device secret is a 32-byte base64url random value
  (`CreateBase64Url(32)`). Only `base64url(SHA-256(secret))` is stored in
  `DeviceSecretHash`. The plaintext is returned by `CreateSession` exactly once
  and never persisted.
- **Verify before branch.** In `IssueTokenAsync`, the device secret is verified
  with `CryptographicOperations.FixedTimeEquals` (constant-time) **before** any
  branch on approval status. A caller who knows only the `sessionId` therefore
  cannot distinguish Pending / Approved / Consumed, and cannot obtain a token.
- **Single-use.** The token is issued and the session flipped to `Consumed` in
  one `SaveChanges`; a second fetch returns `410`.
- **Short-lived token.** Issued via
  `IssueToken(user, role, TimeSpan.FromMinutes(10))` — same claims, signing key,
  issuer, audience, and security stamp as a normal token, so it validates on all
  existing protected endpoints, but expires in ~10 minutes.

## Configuration

These are compile-time constants in
[`CliAuthService.cs`](../src/server/GuideAntsApi/Services/Auth/CliAuthService.cs):

| Constant | Value | Meaning |
|----------|-------|---------|
| `SessionLifetimeMinutes` | `5` | How long a Pending/Approved session is valid before it expires |
| `TokenLifetimeMinutes` | `10` | Lifetime of the issued short-lived JWT |

Related (not specific to this feature):

| Setting | Where | Default |
|---------|-------|---------|
| `Jwt:LifetimeMinutes` | `appsettings.json` | `43200` (30 days) — normal login tokens; **not** used by the CLI flow |

## API endpoints

Group: `/api/cli/sessions` (see
[`CliAuthEndpoints.cs`](../src/server/GuideAntsApi/Endpoints/CliAuthEndpoints.cs)).

### `POST /api/cli/sessions` — create a session

- **Auth:** anonymous.
- **Body:** none.
- **`200 OK`** → `{ "sessionId": "...", "deviceSecret": "...", "expiresAt": "..." }`.
  The `deviceSecret` is shown only here, once.

### `POST /api/cli/sessions/{sessionId}/approve` — approve

- **Auth:** `RequireAdmin` (HttpOnly login cookie).
- **`204 No Content`** → session marked Approved, bound to the calling admin.
- **`401 Unauthorized`** → not logged in.
- **`403 Forbidden`** → logged in but not an admin. (Mount creation is
  admin-only, so non-admin approval is rejected up front rather than failing
  later with a confusing 403 on mount-create.)
- **`404 Not Found`** → unknown session.
- **`410 Gone`** → session expired.

### `GET /api/cli/sessions/{sessionId}/token` — fetch the token

- **Auth:** anonymous, gated by the `X-Device-Secret` request header.
- **`202 Accepted`** → `{ "status": "pending" }` (not yet approved; keep polling).
- **`200 OK`** → `{ "token": "<jwt>", "expiresAt": "..." }` (issued once).
- **`401 Unauthorized`** → wrong or missing `X-Device-Secret`.
- **`404 Not Found`** → unknown session.
- **`410 Gone`** → already consumed, or expired.

## Frontend: the approval page

Route: `/cli/authorize?session=<sessionId>`, served by
[`CliAuthorize.tsx`](../src/client/src/pages/CliAuthorize.tsx) and wired in
[`AppContent.tsx`](../src/client/src/components/AppContent.tsx) as a
`withProtection(...)` route (the served browser build uses `BrowserRouter`, so
the path is plain — no `#`).

- If the visitor is logged out, the protected route redirects to
  `/login?returnUrl=/cli/authorize?...` and back after login (standard behavior).
- The page shows **"Authorize command-line mount access?"** with **Approve** and
  **Deny** buttons.
  - **Approve** → `POST /api/cli/sessions/{session}/approve` (the cookie is sent
    automatically) → "Approved — this window will close automatically."
  - **Deny** → no server call; "Request denied — this window will close
    automatically."
  - In both cases the browser tab auto-closes after 1.5 seconds.
- The page never requests, receives, or displays any token or device secret — it
  only ever handles the `sessionId`. The API method is
  `api.cli.approveSession(sessionId)` in
  [`api.ts`](../src/client/src/services/api.ts).

## How it is used

### Mounting a host folder

```bash
installer/guideants.sh --mount /path/to/folder
```

What happens:

1. The installer brings the stack up (idempotent if already running) and waits
   for health.
2. `acquire_token` creates a session and prints, e.g.:
   ```
   [guideants] Authorize this request in your browser:
   [guideants]   http://localhost:5107/cli/authorize?session=<sessionId>
   [guideants] Approve the command-line mount request in your browser, then return here...
   ```
   It also opens that URL in your default browser.
3. **In the browser (logged in as an admin), click Approve.** The terminal prints
   `[guideants] Authorized.` once the token is issued.
4. The installer proceeds through project/notebook selection, creates the mount,
   and applies it via `scripts/guideants-host-mount.sh apply`.

### Unmounting

```bash
installer/guideants.sh --unmount
```

`--unmount` uses the **same** approval flow: it creates a fresh session, opens
the browser for approval, then lists projects/mounts for interactive removal.

### Useful flags

| Flag | Effect |
|------|--------|
| `--mount <path>` | Mount a host folder (requires browser approval) |
| `--unmount` | Interactively remove a mount (requires browser approval) |
| `--yes` / `-y` | Auto-accept non-approval prompts (e.g. auto-select the only project, mount at project scope). **Does not** bypass the browser approval. |
| `--compose <ghcr\|local>` | Use GHCR images (default) or locally built images |

### One approval per run

There is **no cached or stored approval**. Every `--mount` and every `--unmount`
creates a new session and requires a fresh Approve click. The token is single-use
and ~10-minute-lived and is discarded when the script exits.

What *does* persist is your **browser login cookie** — so you click Approve, but
you do not have to re-enter your email/password each time.

## Migration of existing installs

- The committed `.cli-auth-token` is no longer written by the API and is no
  longer tracked by git. `.gitignore` now ignores
  `installer/docker/volumes/content-files/.cli-auth-token` and `*.cli-auth-token`.
- On every `--mount` / `--unmount`, `acquire_token` best-effort `rm -f`s any
  stale `volumes/content-files/.cli-auth-token` so older installs are scrubbed.

## Troubleshooting

| Symptom | Cause / fix |
|---------|-------------|
| Browser opens but Approve gives **403** | You are logged in as a non-admin. Only admins can approve (mount creation is admin-only). Log in as an admin. |
| Terminal stuck on "Approve the command-line mount request…" | The session has not been approved yet. Approve in the browser. The installer polls ~every 2s for up to ~5 minutes, then fails with guidance. |
| `Authorization request expired or was already used` | The session expired (TTL ~5 min) or its token was already issued. Re-run the command and approve promptly. |
| Browser did not open automatically | Open the printed `http://localhost:5107/cli/authorize?session=...` URL manually. |
| `Authorization session not found` (404) | The session was cleaned up/expired. Re-run the command. |
| Want to confirm nothing leaked to the sandbox | `docker exec guideants-ai sh -c 'find /app/ContentFiles -name "*.cli-auth-token"'` should print nothing. |

## Security model summary

- **No persisted credential.** The only artifact of a CLI auth is a row in
  `CliAuthSessions` holding a *hash* of a one-time device secret and a forward-only
  status — never a token, never a plaintext secret.
- **Admin-gated, interactive.** Token issuance requires an admin to click Approve
  in a cookie-authenticated browser; the sandboxed AI agent cannot do this.
- **Least exposure.** The issued token is short-lived, single-use, and exists only
  in the installer's memory for one operation.

## Related documents

- [`../plans/secure-cli-auth-device-code-flow.md`](../plans/secure-cli-auth-device-code-flow.md) — the design plan this feature implements.
- [`host-folder-mounts.md`](./host-folder-mounts.md) — the host folder mount operator/admin guide (what the installer authenticates *for*).
- [`../installer/README.md`](../installer/README.md) — installer usage.
