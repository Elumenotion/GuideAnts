# Task — Phase 6: OpenAPI, tests, docs

> Subagent brief. Execute top to bottom; return the Report-back contract verbatim.

## Mission

Make the security model **visible and verified**: add OpenAPI security schemes,
replace the no-op integration-test auth with a real per-role test handler, add
register→pending→approve + role-gated tests, add token signing config, and document
the final flow + bootstrap-admin procedure.

## Read first

- `../auth-system-plan.md` §4 → **Phase 6**, **Appendix A** (the guard matrix the
  tests must verify), §3.1–3.2.
- `./DECISIONS.md` → **D1 = App JWT Bearer (locked)**: swagger uses an
  `http`/`bearer` security scheme; `appsettings` carries JWT signing config; the
  test handler issues/accepts app JWTs per role.
- `src/server/GuideAntsApi/Program.cs` (~L185 `UseSwagger`/`UseSwaggerUI`,
  `/swagger/v1/swagger.json`).
- `src/server/GuideAntsApi.IntegrationTests/Infrastructure/BaseIntegrationTest.cs`
  (`SetupAuthentication` sets `Bearer test_token` no-op; `SetupAuthenticationWithClaims`
  currently ignores claims) and `TestWebApplicationFactory`.
- `scripts/find-unused-api-endpoints.mjs` (swagger vs client coverage).
- `guideants-swagger.json` (repo root) — the committed snapshot to regenerate.
- `appsettings.json` / `appsettings.Development.json`.

## Preconditions

- Phase 5 gate green (full stack implemented).

## Guardrails (hard)

- **No real secrets in source control.** Signing key comes from config/user-secrets/
  env; `appsettings*.json` holds only non-secret settings (issuer/audience/lifetime)
  and a placeholder/empty key with a comment on where the real one is supplied.
- The test auth handler is **test-only** (registered in `TestWebApplicationFactory`),
  must impersonate **each** role, and must **not** weaken production auth.
- **No fallback** in tests either: a test that asserts a `200` where Appendix A says
  `403` is wrong — fix the code or the expectation, do not relax the guard.
- Regenerated swagger must reflect the actual guards (don't hand-edit the JSON to
  fake security).

## Tasks

1. **OpenAPI security**: add a Bearer (`http`/`bearer`, `bearerFormat: JWT`)
   `securityScheme` (D1) and per-operation `security` requirements so Swagger UI
   shows auth. Then **regenerate**
   `guideants-swagger.json`:
   - run the API, fetch `GET /swagger/v1/swagger.json`, write it to the repo-root
     `guideants-swagger.json` (use the project's existing export step if one exists;
     otherwise document the fetch command you used).
   - _Expected diff (not a regression):_ the prior snapshot was **stale** — it still
     listed `/api/onlyoffice/**`. The regenerated file should show `/api/documentserver/**`
     instead (the `ds/{**path}` proxy stays excluded via `ExcludeFromDescription`).
2. **Endpoint coverage check**:
   `node scripts/find-unused-api-endpoints.mjs --swagger guideants-swagger.json
   --client src/client/src` — investigate every reported mismatch (a client call to a
   now-protected/renamed route, or a route the client never calls).
3. **Test auth handler**: replace the no-op `Bearer test_token` with a real
   test-authentication handler registered in `TestWebApplicationFactory` that issues
   a principal for a requested role; make `SetupAuthentication(role)` /
   `SetupAuthenticationWithClaims` actually apply it.
4. **New tests**:
   - `register → Pending → Admin approves → role assigned` happy path.
   - Role-gated matrix: for a representative endpoint of each tier, assert
     Reader/Contributor/Admin/Pending get the Appendix-A status (`200`/`403`/`401`).
     Include the toolbar split: `GET .../header-toolbar` → `403` for Contributor,
     `200` for Admin; `GET .../header-toolbar/chat-readiness` → `200` for
     Contributor/Reader, `401` for Pending.
   - Last-Admin safeguard (cross-check Phase 4).
   - Update/replace any MSAL-based mocks that no longer apply (server + client).
5. **Config**: formalize the production token-signing story (Phase 2 already added the
   **dev** `Jwt:*` keys). Add the committed `appsettings.json` `Jwt:*` placeholder
   (issuer/audience/lifetime, **no** real key value) and document the production key
   **source** (user-secrets / Key Vault). Reconcile with what Phase 2 left in
   `appsettings.Development.json`.
6. **Docs**: document the final auth flow and the **bootstrap-admin** procedure
   (fresh install → first register = Admin) under `./docs` (a new
   `docs/auth-flow.md` or an addendum to `auth-system-plan.md`).

## Files in scope

- `src/server/GuideAntsApi/Program.cs` / Swagger config (security schemes)
- `guideants-swagger.json` (regenerated)
- `src/server/GuideAntsApi.IntegrationTests/Infrastructure/*` (test auth handler,
  factory) + new test classes
- client test mocks that referenced MSAL
- `appsettings.json`, `appsettings.Development.json`
- `docs/auth-flow.md` (new) or `docs/auth-system-plan.md` addendum

**Out of scope:** changing runtime guards (those are fixed in Phases 2–4.5; if a
test reveals a wrong guard, **report it as a deviation** for the owning phase rather
than patching here).

## Self-verification

```
cd src/server && dotnet build GuideAntsApi.sln
cd src/server && dotnet test GuideAntsApi.sln
cd src/client && npm test -- --run
node scripts/find-unused-api-endpoints.mjs --swagger guideants-swagger.json --client src/client/src
```

Confirm `guideants-swagger.json` now contains `securitySchemes` and per-operation
`security` (grep the file).

## Definition of Done

- [ ] Swagger has security schemes + per-op security; `guideants-swagger.json`
      regenerated (not hand-faked).
- [ ] Endpoint coverage script run; mismatches explained/resolved.
- [ ] Real per-role test auth handler replaces the no-op; claims actually applied.
- [ ] register→pending→approve + role-gated matrix tests pass; MSAL mocks updated.
- [ ] Signing config added with **no** real secret committed.
- [ ] Final auth flow + bootstrap-admin documented.
- [ ] All builds + tests green.

## Report-back contract (return exactly this)

```
PHASE 6 REPORT
- Swagger security: scheme=<Bearer/JWT> per-op-security=<present?> regenerated=<how>
- Coverage script result: mismatches=<n> -> <each explained>
- Test auth handler: per-role impersonation=<yes> location=<file>
- New tests: register-pending-approve=<pass> role-matrix=<pass> last-admin=<pass>
- MSAL mocks updated/removed: <list>
- Signing config keys (no secret value committed?): <list> -> <yes>
- Docs: <path(s)>
- Guard bug found during testing? <none | reported to Phase N>
- Verification: build=<p/f> server-tests=<counts> client-tests=<counts> coverage-script=<clean?>
- Files touched: <exhaustive list>
- Deviations / surprises: <list or "none">
```
