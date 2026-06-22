# Task — Phase 2: Published-guide AppIdentity authentication

> Subagent brief. Execute top to bottom; return the Report-back contract verbatim.
> **Security-sensitive phase — CodeQL gate applies (see `codeql-gate.md`).**

## Mission

Make `AuthMode = AppIdentity` a real, in-process authentication mode for published
guides:

1. `PublishedGuideAuthService.ValidateAsync` gains an **`AppIdentity` branch** that
   validates the **app JWT** (same key/issuer/lifetime as `RequireApprovedUser`),
   resolves the user, and sets `AuthValidationResult.InternalUserId`.
2. Identity persists on published conversations (`UserId` + `ExternalUserIdentity`).
3. The publish/update API **rejects** any attempt to set/keep `AppIdentity` from the
   UI.
4. `GET /api/published/{pubId}` exposes `authMode` + `requiresAuth`.

## Read first

- `../guideants-guide-implementation-plan.md` §4.2 (validation), §4.3 (identity
  persistence), §4.4 (who may set it + public config), §4.7 (flow diagram).
- `./DECISIONS.md` → **D-GG-1 (same-host cookie auth)**, **D-GG-A** (seeder-only),
  **D-GG-C** (reuse validator, no anonymous fallback), **D-GG-D** (identity persist),
  **D-GG-G** (no fallback).
- `PublishedGuideAuthService.cs`, `IPublishedGuideAuthService.cs`
  (`AuthValidationResult`, current Webhook/ApiKey/Anonymous logic).
- The existing **app JWT validator** used by `RequireApprovedUser` (find it; reuse
  it — do not duplicate crypto) **and** how the `GuideAnts.Auth` HttpOnly cookie is
  read on the request. The `AppIdentity` branch reads the JWT from that **cookie**
  (same-host; D-GG-1). Optionally also accept an `X-Published-Auth` Bearer of the
  same signature for Swagger/manual testing.
- `PublishedConversationService.cs`, `PublishedConversationStreamPolicy.cs`
  (`ResolveUserIdentityAsync`, `CreateUserMessageRequest`).
- `GuidesPublishingEndpoints.cs` (publish + update), `PublishedGuidesEndpoints.cs`
  (public `GET /api/published/{pubId}` config), `PublishedNotebookConversationsEndpoints.cs`.

## Preconditions

- Phase 1 gate green (enum, columns, `InternalUserId` exist). D-GG-1 LOCKED
  (same-host cookie).

## Guardrails (hard)

- **D-GG-C**: reuse the existing JWT validation helper. Same signing key, issuer,
  audience, lifetime. **No duplicated crypto, no hard-coded key.**
- **D-GG-1**: the `AppIdentity` branch reads the app JWT from the **`GuideAnts.Auth`
  HttpOnly cookie** on the request and validates it. (May also accept an
  `X-Published-Auth` Bearer of the same signature for API testing.) No token is
  minted anywhere.
- **No anonymous fallback**: when `AuthMode == AppIdentity` and the token is
  missing/invalid/expired → **401**. Never resolve to anonymous, never swallow.
  Check `SecurityStamp` + approval status; a pending/deactivated user → 401/403 per
  existing convention.
- The `AppIdentity` branch runs **before** the anonymous path and is reached only
  when `AuthMode == AppIdentity`. Webhook/ApiKey/Anonymous behavior is **unchanged**.
- **D-GG-A**: publish + update endpoints reject `AuthMode = AppIdentity` (400) and
  reject changing an existing AppIdentity row's mode. No internal admin enable path
  in this phase.
- **Never log the token or raw claims** unsanitized (CodeQL `cs/log-forging`).
- Do not touch the seeder, client, or system-project code (later phases).

## Tasks

1. Switch `PublishedGuideAuthService` to branch on `guide.AuthMode` (the enum) for
   mode selection. Preserve existing Webhook/ApiKey/Anonymous outcomes.
2. Implement the `AppIdentity` branch: read the JWT from the `GuideAnts.Auth`
   cookie (fallback to `X-Published-Auth` Bearer for API testing only), validate via
   the shared app-JWT validator, resolve the `Users` row, verify `SecurityStamp` +
   approval. Populate `AuthValidationResult` with `UserIdentity = userId.ToString()`
   and `InternalUserId = userId`.
3. In `PublishedConversationService` (create + resume paths): when
   `authResult.InternalUserId` is set, write `CreateUserMessageRequest.UserId =
   InternalUserId` and `ExternalUserIdentity = UserIdentity`.
4. `PublishedConversationStreamPolicy.ResolveUserIdentityAsync`: return
   `StreamUserIdentity(InternalUserId, displayName, UserIdentity)` for AppIdentity
   instead of `(null, "User", external)`.
5. `GuidesPublishingEndpoints` publish + update: reject `AppIdentity` from DTO
   (`400 app_identity_auth_not_configurable_via_api`); reject mode-change away from
   AppIdentity on existing rows.
6. `PublishedGuidesEndpoints` public config (`GET /api/published/{pubId}`): expose
   `authMode` and `requiresAuth = authMode != Anonymous` (keep `requiresApiKey`).
7. Add unit + integration tests for all four modes + the AppIdentity matrix
   (valid / missing / expired token; identity persisted; publish/update reject;
   public config shape) — see plan §10.1.

## Files in scope

- `PublishedGuideAuthService.cs`, `IPublishedGuideAuthService.cs`
- `PublishedConversationService.cs`, `PublishedConversationStreamPolicy.cs`
- `GuidesPublishingEndpoints.cs`, `PublishedGuidesEndpoints.cs`
- `PublishedGuideDto` (server) — add `authMode` to the public config DTO
- Tests under `GuideAntsApi.Tests/Services/PublishedGuides/` (+ conversation tests)

**Out of scope:** seeder, system endpoints, client, Phase-1 schema.

## Self-verification

```powershell
cd src/server; dotnet build GuideAntsApi.sln
cd src/server; dotnet test GuideAntsApi.sln
```

Then run the CodeQL diff (`codeql-gate.md` §3–4) and confirm **0 new findings**,
watching `cs/log-forging`, clear-text token storage, hard-coded credentials.

## Definition of Done

- [ ] `ValidateAsync` branches on `AuthMode`; AppIdentity validates app JWT, sets
      `InternalUserId`; missing/invalid → 401 (no anonymous fallback).
- [ ] Webhook/ApiKey/Anonymous behavior unchanged (regression tests pass).
- [ ] Messages on AppIdentity guides persist `UserId` + `ExternalUserIdentity`;
      stream policy returns internal id.
- [ ] Publish/update API rejects AppIdentity (400) + rejects mode-change away.
- [ ] Public config returns `authMode` + `requiresAuth`.
- [ ] Tests for §10.1 matrix green. CodeQL diff = 0 new.

## Report-back contract (return exactly this)

```
PHASE 2 REPORT
- AuthMode branching in ValidateAsync: <yes>  AppIdentity branch reuses validator: <which helper>
- Token source for AppIdentity: <GuideAnts.Auth cookie; X-Published-Auth bearer for testing>
- Missing/invalid token on AppIdentity -> <status code> (no anonymous fallback: yes)
- Identity persisted: UserId set? <yes> ExternalUserIdentity set? <yes> stream policy internal id? <yes>
- Publish/update reject AppIdentity: <how + status>
- Public config authMode/requiresAuth exposed: <yes>
- Webhook/ApiKey/Anonymous regression: <pass/fail>
- Tests added: <names/counts>  server suite: <counts>
- Build: <pass/fail>
- Files touched: <exhaustive list>
- Deviations / surprises: <list or "none">

CODEQL (local, no GitHub parity):
- C# build-mode=none used: <yes>  suites=code-scanning: <yes>
- New findings vs baseline: <count> -> <RuleId @ file:line each, or "none">
- New findings fixed in-code (no suppression): <yes/n-a>
```
