# Task — Phase 2: Backend authentication

> Subagent brief. Execute top to bottom; return the Report-back contract verbatim.

## Mission

Wire the ASP.NET auth pipeline with **app-issued** credentials (no Entra), add the
auth endpoints (`register`/`login`/`me`; **no `logout`** under JWT), implement
**first-registrant-is-Admin** bootstrap, and replace the "first `Users` row" current-
user logic with principal-based resolution.

## Read first

- `../auth-system-plan.md` §4 → **Phase 2**, §3.1–3.3, §3.6 (first-user race), §2.1
  (Waterfall `AuthEndpoints`/`AuthUserService` as a *pattern* to adapt — **not** copy;
  strip Entra/teams/billing).
- `./DECISIONS.md` → **D1 = App JWT Bearer (locked)**, **D2 = UserRoles table
  (locked)**. **D3** invariants (hasher choice, no fallback).
- `./codeql-gate.md` — run the local CodeQL diff before reporting (no GitHub parity).
- `src/server/GuideAntsApi/Program.cs` (pipeline; `UseSwagger` lives ~L185)
- `src/server/GuideAntsApi/Configuration/StartupConfiguration.cs`
- `src/server/GuideAntsApi/Endpoints/UserEndpoints.cs` (`/api/users/current`, first-row logic)
- The current-user resolver `ContextOptionsService.ResolveCurrentUserAsync`

## Preconditions

- Phase 1 gate green (Role + PasswordHash exist). D1 finalized.

## Guardrails (hard)

- **App-issued JWT Bearer only** (D1). Do **not** call `AddMicrosoftIdentityWebApi`
  or any Entra/MSAL path, and do **not** build a cookie/session path. Include the
  `User.SecurityStamp` (from Phase 1) as a JWT claim and validate it against the DB
  row on each request, so Phase 4 can revoke tokens.
- **Role lives in `UserRoles`** (D2), not on `User`. Registration creates the user's
  single `UserRoles` row; `ICurrentUserService` reads role from it. Do **not** add a
  `Role` column.
- **No `POST /api/auth/logout`** — JWT is stateless; the client discards the token
  (D1). Do not build a logout endpoint.
- **One hasher**, chosen here, reused by Phase 4 set-password. Salted KDF only.
- **First-registrant-is-Admin must be race-safe**: do the "any users exist?" check
  and the insert inside a **single transaction** (or a serialized/constraint-guarded
  path) so two concurrent first registrations cannot both become Admin (§3.6).
- **No fallback identity.** Missing/invalid principal → `401`. Never default the
  current user to "first row" or "admin". Delete the old first-row logic; do not
  leave it as a fallback.
- `Pending` users can authenticate and **must** be able to call `GET /api/auth/me`,
  but get no content access (enforcement detail is Phase 3; just don't block `me`).
- Do not apply per-endpoint role policies here — that's Phase 3. (You may add the
  bare `.RequireAuthorization()` to the new auth group's `me` only.)

## Tasks

1. Register auth in `StartupConfiguration.cs` + `Program.cs`:
   `AddAuthentication(...)` + `AddAuthorization()` and
   `UseAuthentication()`/`UseAuthorization()` in the correct pipeline order
   (after routing, before endpoint mapping).
2. Implement password hashing/verification (the chosen hasher) and **JWT** issuance
   and validation (signing key/issuer/audience/lifetime read from config — **no secret
   literals**). The JWT carries the role claim (from `UserRoles`) and the
   `SecurityStamp` claim; validation rejects a token whose stamp ≠ the DB row.
   - **Dev signing config must exist now** so this phase is self-verifiable: add the
     `Jwt:*` keys (issuer/audience/lifetime + a **dev-only** signing key) to
     `appsettings.Development.json` **and** ensure the integration-test host has a key
     (test config or `WebApplicationFactory` setting). Without a key the gate's
     register/login/`me` smoke cannot sign/validate a token. Production key **source**
     (user-secrets/KeyVault) and the committed `appsettings.json` placeholder +
     bootstrap docs remain Phase 6 — but do **not** defer the dev key, or this gate
     fails. The dev key is non-production and may live in `appsettings.Development.json`
     (which must not hold real secrets).
3. Create `Endpoints/AuthEndpoints.cs` with:
   - `POST /api/auth/register` — name+email+password; create the `User` (with a fresh
     `SecurityStamp`) **and** its single `UserRoles` row in the same transaction:
     **first user → `Admin` + active, else → `Pending`**; race-safe.
   - `POST /api/auth/login` — verify hash, issue JWT (incl. `SecurityStamp` claim),
     set `LastLoginAt`.
   - `GET /api/auth/me` — returns profile incl. `role` (from `UserRoles`) and
     `mustChangePassword` (reachable by Pending).
   - **No `logout` endpoint** (JWT; client discards token).
4. Implement `ICurrentUserService`/`IUserContext` resolving the DB `User` from the
   authenticated principal. Replace the first-row reads in `UserEndpoints`
   (`/api/users/current`) and `ContextOptionsService.ResolveCurrentUserAsync`. These
   must now tolerate "no matching user" → `401`, and an empty `Users` table.
5. Map the auth endpoints group in `Program.cs`.

## Files in scope

- `src/server/GuideAntsApi/Configuration/StartupConfiguration.cs`
- `src/server/GuideAntsApi/Program.cs`
- `src/server/GuideAntsApi/Endpoints/AuthEndpoints.cs` (new)
- `src/server/GuideAntsApi/Endpoints/UserEndpoints.cs` (replace first-row logic)
- new service files for hashing, token issuance, `ICurrentUserService`
- `ContextOptionsService` (current-user resolution only)
- `GuideAntsApi.csproj` (only to add/remove the JWT package per Phase 0 recommendation)
- `src/server/GuideAntsApi/appsettings.Development.json` (+ integration-test config)
  — **dev** `Jwt:*` keys only (no real secret)

**Out of scope:** per-endpoint role policies on feature groups (Phase 3), admin
endpoints (Phase 4), client. **Production** key source + committed `appsettings.json`
placeholder + the test auth handler swap are **Phase 6** (you only add the dev key).

## Self-verification

```
cd src/server && dotnet build GuideAntsApi.sln
cd src/server && dotnet test GuideAntsApi.sln
```

Smoke (integration test preferred, or run the API and curl):
- register #1 → role `Admin`, active; `me` returns Admin.
- register #2 → role `Pending`; `me` returns Pending.
- login with wrong password → `401`.
- request with no/!valid credential → `401` (no first-row fallback).

CodeQL (local, per `./codeql-gate.md` — **C# `build-mode=none`**, no GitHub parity):
diff vs `.codeql/baseline/`; expect **no new** `cs/log-forging` (sanitize any logged
`email`/`name` via `LogValueSanitizer`), clear-text password storage, or hard-coded
JWT secret.

## Definition of Done

- [ ] Pipeline wired with app-issued **JWT** (D1); no Entra calls; no logout endpoint.
- [ ] `register`/`login`/`me` work; register creates the `UserRoles` row; JWT carries
      role + `SecurityStamp` claims.
- [ ] First-registrant-is-Admin proven; concurrency guard in place (test or
      documented transaction/constraint).
- [ ] `ICurrentUserService` resolves from principal; first-row logic deleted (grep
      proves it).
- [ ] One hasher chosen; salted; reusable by Phase 4.
- [ ] Dev `Jwt:*` config present (appsettings.Development + test host) so JWTs sign/
      validate in this gate; **no** real secret committed.
- [ ] Build + tests green; no secret literals.

## Report-back contract (return exactly this)

```
PHASE 2 REPORT
- JWT issuance (matches D1): <yes> role+SecurityStamp claims present: <yes>
- Role row created at register (UserRoles, D2): <yes>
- Hasher chosen: <PasswordHasher<T> | PBKDF2 | bcrypt>
- First-user race guard: <transaction | unique constraint | serialized> + how proven
- Endpoints added: <list with methods>
- First-row logic removed from: <files:line>
- Config keys used (no secrets): <list>
- Verification: build=<pass/fail> tests=<counts> register1=<Admin?> register2=<Pending?> badlogin=<401?> noauth=<401?>
- CodeQL (local, no GitHub parity): C#-build-mode-none=<yes> new-findings-vs-baseline=<count> -> <RuleId@file:line or "none"> fixed-in-code=<yes/n-a>
- Files touched: <exhaustive list>
- Deviations / surprises: <list or "none">
```
