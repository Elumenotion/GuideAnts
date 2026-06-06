# GuideAnts Auth Flow

This document describes the final authentication and authorization flow shipped in
Phases 2-6, including bootstrap-admin behavior on a fresh install.

## Role model

GuideAnts uses one application-wide role per user:

- `Pending`
- `Reader`
- `Contributor`
- `Admin`

`Pending` is a pre-approval state, not a capability tier. Authorization policies
map to roles as follows:

- `RequireApprovedUser`: `Reader`, `Contributor`, `Admin`
- `RequireContributor`: `Contributor`, `Admin`
- `RequireAdmin`: `Admin`

## Bootstrap-admin procedure (fresh install)

1. Start with an empty database (no rows in `Users` / `UserRoles`).
2. First user registers at `POST /api/auth/register`.
3. Registration transaction grants that first account:
   - role `Admin`
   - `ApprovedAt` set (active account)
4. Every later registration is created as:
   - role `Pending`
   - `ApprovedAt = null`
5. An Admin approves pending users through `POST /api/admin/users/{id}/approve`
   and assigns `Reader`, `Contributor`, or `Admin`.

## User journey

1. User registers (`/register`) or logs in (`/login`).
2. API issues an app JWT in an **HttpOnly** cookie (`GuideAnts.Auth`); the client
   never stores the token in JavaScript.
3. Authenticated API calls use `fetch(..., { credentials: 'include' })` so the
   browser sends the cookie; `GET /api/auth/me` hydrates auth state on load.
   The session is **sliding**: the server re-issues the cookie on authenticated
   requests once the token is older than `SlidingSessionRenewal.RenewalInterval`
   (1 day), so an active user is never logged out mid-session. With the default
   30-day (`Jwt:LifetimeMinutes = 43200`) lifetime, only a genuinely idle session
   (no requests for ~30 days) lapses. There is no absolute session cap.
4. Route behavior:
   - anonymous -> `/login`
   - authenticated `Pending` -> `/pending`
   - authenticated with `MustChangePassword` -> `/change-password`
   - approved user -> app routes
5. Logout calls `POST /api/auth/logout` to clear the cookie, then clears client state.
6. Admin-managed recovery:
   - Admin sets password via `POST /api/admin/users/{id}/set-password`
   - user receives `MustChangePassword = true` and rotates password at next sign-in

## Token signing configuration

`Jwt:*` settings live in `src/server/GuideAntsApi/appsettings*.json` for
issuer/audience/lifetime, with a non-secret `SigningKey` placeholder only.

Real signing keys must be supplied outside source control:

- local development: `dotnet user-secrets` or environment variable `Jwt__SigningKey`
- production: secret manager (for example Azure Key Vault) exposed as
  `Jwt__SigningKey`

No real signing key should be committed to git.
