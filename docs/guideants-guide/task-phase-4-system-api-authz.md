# Task — Phase 4: System project API & authorization

> Subagent brief. Execute top to bottom; return the Report-back contract verbatim.
> **Security-sensitive phase — CodeQL gate applies (see `codeql-gate.md`).**

## Mission

Make the system project invisible and inaccessible to non-admins, and add the two
endpoints the flyout + Settings need:

1. Exclude the system project from all listings.
2. `SystemProjectAccessGuard` → non-admin access to the system project (or its
   children) returns **404**.
3. `GET /api/system-guide/session` (`RequireApprovedUser`) → role-correct `pub-id`
   **config only** (no token — auth rides the same-host cookie, **D-GG-1**).
4. `GET /api/system-guide/workspace` (`RequireAdmin`) → `{ projectId, projectSlug }`.

## Read first

- `../guideants-guide-implementation-plan.md` §6.1 (hide listings), §6.2 (access
  guard), §6.3 (session endpoint + selection logic), §6.4 (workspace endpoint).
- `./DECISIONS.md` → **D-GG-E** (404 not 403, hidden), **D-GG-F** (role selection),
  **D-GG-1** (same-host cookie — session returns config only, no token), **D-GG-G**
  (no fallback), **D-GG-4** (no default limits).
- `ProjectService.GetProjectsAsync()` + any home/recent/search aggregations.
- `ProjectEndpoints.cs` (project + child routes), the auth policy names
  (`RequireApprovedUser`, `RequireAdmin`) and `ICurrentUserService`.
- `GuideAntsSystem` settings helper from Phase 3 (read IDs from it).

## Preconditions

- Phase 3 gate green (system project + settings exist). **D-GG-1 + D-GG-2 locked.**

## Guardrails (hard)

- **D-GG-E**: non-admin access to the system project or any
  `/api/projects/{systemId}/…` route returns **404** (not 403 — do not leak
  existence). Admin → normal access.
- Exclude system project from `GET /api/projects` and every aggregation
  (home/recent/search/usage) — grep all project queries.
- `PUT`/`DELETE` on the system project → Admin only; **block delete** while
  `IsSystemProject`.
- Session endpoint selection (**D-GG-F**): `Admin` → admin published guide; any
  other **approved** role → user published guide; Pending/unapproved → **403**.
- **D-GG-1**: the session endpoint returns **config only** (pub-id, role flags) —
  **no token minted or returned.** Auth for the published-guide calls rides the
  same-host `GuideAnts.Auth` cookie validated in Phase 2. Never log cookie/token
  values.
- **No fallback** (D-GG-G): a missing/invalid principal is 401; wrong role is
  403/404. Read IDs from settings (D-GG-I), never hard-code.
- Do not touch client (Phase 6/7) or the seeder (Phase 3).

## Tasks

1. Add `!IsSystemProject` filter to `ProjectService.GetProjectsAsync()` and all
   other project aggregations.
2. Implement `SystemProjectAccessGuard` (returns 404 for non-admin on a system
   project) and apply it on `GET /api/projects/{id}`, `/details`, and all
   `/api/projects/{id}/…` notebook/file/guide routes. Guard `PUT`/`DELETE` +
   block delete while `IsSystemProject`.
3. Create `SystemGuideEndpoints.cs`:
   - `GET /api/system-guide/session` (`RequireApprovedUser`): resolve role →
     pub-id from settings; return session JSON (plan §6.3) **config only, no token**;
     Pending → 403.
   - `GET /api/system-guide/workspace` (`RequireAdmin`): return
     `{ projectId, projectSlug }`; non-admin → 404.
4. Integration tests (plan §10.2): system project absent from `GET /api/projects`;
   Reader → 404 on system project; Admin → 200; session returns role-correct
   pub-id; session Pending → 403; workspace admin-only.

## Files in scope

- `GuideAntsApi/Services/SystemGuide/SystemProjectAccessGuard.cs`
- `GuideAntsApi/Endpoints/SystemGuideEndpoints.cs`
- `ProjectService.cs`, `ProjectEndpoints.cs` (filter + guard wiring)
- Any aggregation service that lists projects (home/recent/search/usage)
- Tests under `GuideAntsApi.Tests/Services/SystemGuide/` (integration)

**Out of scope:** client, seeder, Phase-1/2 schema/auth-service internals.

## Self-verification

```powershell
cd src/server; dotnet build GuideAntsApi.sln
cd src/server; dotnet test GuideAntsApi.sln
```

Then run the CodeQL diff (`codeql-gate.md`) — **0 new findings**; confirm no cookie/
token value is logged in the new endpoints.

## Definition of Done

- [ ] System project excluded from `GET /api/projects` + all aggregations.
- [ ] `SystemProjectAccessGuard`: non-admin → 404 on project + children; admin OK;
      delete blocked while `IsSystemProject`.
- [ ] `GET /api/system-guide/session` role-correct pub-id (config only, no token —
      D-GG-1); Pending → 403.
- [ ] `GET /api/system-guide/workspace` admin-only; non-admin → 404.
- [ ] Integration tests (§10.2 subset) green. CodeQL diff = 0 new.

## Report-back contract (return exactly this)

```
PHASE 4 REPORT
- Listing filter !IsSystemProject applied in: <files/queries>
- SystemProjectAccessGuard: non-admin status=<404> applied on=<routes>; delete blocked while system: <yes>
- session endpoint: policy=<RequireApprovedUser> Admin->adminGuide? <yes> other-approved->userGuide? <yes> Pending-><403>
- session returns token? <no — config only; auth via same-host cookie per D-GG-1>
- workspace endpoint: policy=<RequireAdmin> non-admin-><404>
- IDs read from settings (not hard-coded): <yes>
- Integration tests: <names/counts>  server suite: <counts>  build: <pass/fail>
- Files touched: <exhaustive list>
- Deviations / surprises: <list or "none">

CODEQL (local, no GitHub parity):
- C# build-mode=none used: <yes>  suites=code-scanning: <yes>
- New findings vs baseline: <count> -> <RuleId @ file:line each, or "none">
- New findings fixed in-code (no suppression): <yes/n-a>
```
