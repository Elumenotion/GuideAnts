# Task — Phase 4.5: Secondary (tool) OAuth — server-side token storage

> Subagent brief. Execute top to bottom; return the Report-back contract verbatim.

## Mission

Move the notebook/guide **tool-OAuth** PKCE flow and token storage **off the browser
`localStorage`** and onto the server, **encrypted and bound to the authenticated
`User`**. The browser must never hold the `codeVerifier`, access token, or refresh
token. Inject tokens into tool calls server-side.

## Read first

- `../auth-system-plan.md` §2.5 (the `localStorage` problem, in detail), §4 →
  **Phase 4.5**, Appendix A.12, §3.7.
- `./DECISIONS.md` → **D4 LOCKED**: (a) PKCE state in **`OAuthAuthorizationState`
  table**; (b) token grant scope **per (`User`, `Provider`)** — **no `ProjectId` in
  the key**.
- Server: `Endpoints/ProjectExternalAuthEndpoints.cs`,
  `DataModel/Models/ProjectExternalAuth.cs`,
  `Settings/ApplicationSettingsJson.cs` (`EncryptSecrets` / Data Protection EncV2).
- Client: `pages/OAuthCallback.tsx`, `utils/notebookAuth.ts`, `App.tsx`
  (`oauth_pkce_*` scanning), `contexts/conversation/useConversationActions.ts`
  (`collectOAuthTokensForTemplate`, `sendMessageStream(..., oauthTokens)`),
  `components/notebook/auth/NotebookAuthInterstitial.tsx`,
  `components/project/content/ProjectGuideAuthContent.tsx`.
- `./codeql-gate.md` — run the local CodeQL diff before reporting (no GitHub parity).

## Preconditions

- Phase 3 gate green (external-auth group already auth-gated). D4 finalized. Phase 1
  data-model migration tooling available (this phase adds tables).

## Guardrails (hard)

- **Tokens are encrypted at rest** via the existing Data Protection /
  `EncryptSecrets` (EncV2) path. **No plaintext token columns**, ever.
- Tokens are **bound to `User`** and scoped **per (`User`, `Provider`)** (D4b) —
  the unique key is (`UserId`, `ProviderId`), **not** including `ProjectId`. The
  `status` endpoint **never** returns a token.
- **Server-side refresh only.** On refresh failure, surface a **re-connect required**
  signal — **no silent fallback**, no swallowing the error, no reusing a stale token.
- The browser must not receive or transmit tokens after this phase. Remove the
  client token paths entirely (not feature-flagged off — removed).
- New endpoints go under the **existing** `/api/projects/{projectId}/external-auth`
  group and require auth + project access (the group is already gated from Phase 3).

## Tasks

**Data model (migration via EF_COMMANDS.md):**

1. `ExternalOAuthToken` — **unique key (`UserId`, `ProviderId`)** (D4b — **no
   `ProjectId` in the key**): `AccessTokenEncrypted`, `RefreshTokenEncrypted`,
   `ExpiresAt`, `Scope`, `Created`, `Updated`. FK cascade-delete with `User`.
   (`ProjectId` may be stored for audit only, never in the uniqueness/lookup key.)
2. `OAuthAuthorizationState` **table** (D4a — not a cache): `State` (PK/unique),
   `UserId`, `ProviderId`, `CodeVerifier`, `Scopes`, `RedirectUri`, `ReturnUrl`,
   `Created`, `ExpiresAt`. Add expired-row cleanup. Bind to (`User`, `Provider`).
3. Migration for both; update `ApplicationDbContext` DbSets.

**Endpoints (extend `ProjectExternalAuthEndpoints.cs`):**

4. `POST .../{providerId}/oauth/authorize-url` — server generates PKCE
   (`verifier`+`challenge`+`state`), persists state for the current user, returns the
   provider authorize URL.
5. `POST .../{providerId}/oauth/callback` — body `{ code, state }`; look up state,
   verify it belongs to the **current user** and isn't expired, exchange
   `code`+`verifier` server-side, encrypt + upsert `ExternalOAuthToken`, delete the
   state, return **status only**.
6. `GET .../{providerId}/oauth/status` — `{ connected, expiresAt, scopes }`; no token.
7. `DELETE .../{providerId}/oauth` — delete current user's tokens for that provider.
8. A service for PKCE start / exchange / **server-side refresh** / inject.

**Runtime + client:**

9. **Inject tokens server-side** when running a notebook conversation (resolve the
   user's `ExternalOAuthToken` per provider, refresh if near expiry, attach to the
   tool call). **Remove** `collectOAuthTokensForTemplate` and the `oauthTokens`
   argument from `sendMessageStream`.
10. **Client becomes server-mediated**: `OAuthCallback.tsx` POSTs `{code,state}` to
    the new callback (no Microsoft token exchange, no `localStorage` writes); the
    "start" path calls `authorize-url`. Replace `utils/notebookAuth.ts`
    `localStorage` helpers with `oauth/status` calls; update
    `NotebookAuthInterstitial.tsx` and `ProjectGuideAuthContent.tsx` to read
    connection status from the server.
11. **Delete the `localStorage` coupling**: remove `oauth_pkce_*` scanning in
    `App.tsx`; stop persisting `oauth_pkce_*` / `oauth_tokens_*` entirely.

## Files in scope

- `DataModel/Models/ExternalOAuthToken.cs`, `OAuthAuthorizationState.cs` (new)
- `DataModel/ApplicationDbContext.cs` + new migration
- `Endpoints/ProjectExternalAuthEndpoints.cs`
- new tool-OAuth service (start/exchange/refresh/inject)
- client: `OAuthCallback.tsx`, `utils/notebookAuth.ts`, `App.tsx`,
  `contexts/conversation/useConversationActions.ts`,
  `NotebookAuthInterstitial.tsx`, `ProjectGuideAuthContent.tsx`

**Out of scope:** app-login flow (done), unrelated endpoints.

## Self-verification

```
cd src/server && dotnet build GuideAntsApi.sln
cd src/server && dotnet test GuideAntsApi.sln
cd src/client && npm run build
cd src/client && npm test -- --run
```

Grep proofs (must return nothing in client `src/`):
- `oauth_tokens_`, `oauth_pkce_`, `collectOAuthTokensForTemplate`,
  `refreshOAuthTokens`, `storeOAuthTokens`, `getOAuthTokens`.
Server grep: no plaintext token column (only `*Encrypted`); `status` handler does
not serialize a token.

CodeQL (local, per `./codeql-gate.md` — **C# `build-mode=none`**, no GitHub parity):
diff vs `.codeql/baseline/`; expect **no new** clear-text token storage or
`cs/path-injection` from token/file work, and the **`js/*` clear-text `localStorage`
findings should DROP** now that tokens left the browser (if any remain, the removal
is incomplete).

## Definition of Done

- [ ] Both tables (or table+cache per D4) added + migration; tokens encrypted.
- [ ] 4 new endpoints exist, auth+project gated; `status` returns no token.
- [ ] Server-side refresh; refresh failure → re-connect signal (no fallback).
- [ ] Client stores/transmits **no** tokens; `localStorage` coupling deleted.
- [ ] Builds + tests green.

## Report-back contract (return exactly this)

```
PHASE 4.5 REPORT
- D4 implemented: state=<table?> token-key=<(UserId,ProviderId) with NO ProjectId?>
- Encryption path: <EncryptSecrets/Data Protection EncV2 confirmed?>
- Endpoints added: <list>
- Server-side refresh failure behavior: <re-connect signal; no fallback - describe>
- Client token paths removed (grep clean?): oauth_tokens_=<none?> oauth_pkce_=<none?> collectOAuthTokensForTemplate=<none?>
- sendMessageStream oauthTokens arg removed: <yes/no>
- Verification: build(server)=<p/f> test(server)=<counts> build(client)=<p/f> test(client)=<counts>
- CodeQL (local, no GitHub parity): C#-build-mode-none=<yes> new-findings-vs-baseline=<count> -> <RuleId@file:line or "none"> js-localStorage-findings-dropped=<yes?>
- Files touched: <exhaustive list>
- Deviations / surprises: <list or "none">
```
