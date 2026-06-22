# Task — Phase 5: Admin API endpoints

> Subagent brief. Execute top to bottom; return the Report-back contract verbatim.
> **Security-sensitive phase — CodeQL gate required.**

## Mission

Expose the admin-only HTTP surface for host folder mounts (plan §11): list, create,
get, apply-command, remove-command, reconcile, delete. Wire them to the Phase 4
service. No symlink work here (Phase 6) — these endpoints orchestrate records,
plans, and command text.

## Read first

- `../host-folder-notebook-mounts-plan.md` §11 (endpoints + request/response
  shapes), §16 (create flow), §18 (remove flow), §20 (security).
- `./DECISIONS.md` → D3 (command-source model), Part B (admin-only, no host-path
  leak).
- `./codeql-gate.md` (run after this phase: command injection, `cs/log-forging`,
  path handling).
- `src/server/GuideAntsApi/Endpoints/ProjectEndpoints.cs` and
  `ProjectExternalAuthEndpoints.cs` (project-scoped, admin-gated endpoint patterns;
  how `RequireAdmin`/authorization is applied — mirror the auth-system convention).

## Preconditions

- Phase 4 gate green.

## Guardrails (hard)

- **Every** endpoint is **admin-only** (`RequireAdmin` policy from the auth system).
  Non-admin → `403`. No `AllowAnonymous`.
- Responses must **never** leak the original host path or any credential to a
  non-admin (and the get/list DTOs must omit/redact sensitive fields per the Phase 3
  flags). The create/remove responses carry the **command** (admin-only by virtue of
  the admin gate).
- The displayed command must be the **sanitized** text from Phase 4 — do not rebuild
  it inline with string concatenation.
- **No fallback:** validation errors → `400` with the precise reason; missing
  mount/project → `404`; unauthorized → `401/403`. Never coerce a bad request into a
  silent success.
- Do not log raw host paths/leaf names without `LogValueSanitizer.Sanitize(...)`.

## Tasks

1. Add `HostFolderMountEndpoints.cs` mapping exactly the plan §11 routes:
   - `GET    /api/projects/{projectId}/host-folder-mounts`
   - `POST   /api/projects/{projectId}/host-folder-mounts`
   - `GET    /api/projects/{projectId}/host-folder-mounts/{mountId}`
   - `POST   /api/projects/{projectId}/host-folder-mounts/{mountId}/commands/apply`
   - `POST   /api/projects/{projectId}/host-folder-mounts/{mountId}/commands/remove`
   - `POST   /api/projects/{projectId}/host-folder-mounts/{mountId}/reconcile`
   - `DELETE /api/projects/{projectId}/host-folder-mounts/{mountId}`
2. Bind the create request (plan §11) and return the create response shape
   (`mountId`, `status`, `leafName`, `containerSourcePath`, `command`). Remove
   returns its documented shape.
3. Apply `RequireAdmin` to the whole group; register the group with the app's
   endpoint mapping.
4. Wire to `IHostFolderMountService` (Phase 4) for validation, record creation,
   command text, and the reconcile call (reconcile may invoke the Phase-4 stub; full
   reconciliation lands in Phase 9 — that is acceptable as long as the endpoint is
   wired and returns sensible status).
5. Add integration tests covering: admin create (notebook + project scope),
   non-admin `403`, validation `400`, apply/remove command shapes, reconcile,
   delete.

## Files in scope

- `src/server/GuideAntsApi/Endpoints/HostFolderMountEndpoints.cs`
- Endpoint registration file (where `Map*Endpoints` are wired)
- Request/response DTOs (new)
- `src/server/GuideAntsApi.IntegrationTests/*` (mount endpoint coverage)
- `src/server/GuideAntsApi.Tests/*` (if endpoint helpers need unit coverage)

**Out of scope:** symlink/registry (Phase 6), guard (Phase 7), UI (Phase 10),
full reconciliation engine (Phase 9).

## Self-verification

```bash
cd src/server && dotnet build GuideAntsApi.sln
cd src/server && dotnet test GuideAntsApi.Tests/GuideAntsApi.Tests.csproj
cd src/server && dotnet test GuideAntsApi.IntegrationTests/GuideAntsApi.IntegrationTests.csproj --filter HostFolderMount
```

Plus global gate (orchestration §4.1) **and** CodeQL gate (`codeql-gate.md` §5
Phase-5 row).

## Definition of Done

- [ ] All §11 routes exist, admin-only, with the documented request/response shapes.
- [ ] No host-path/credential leak to non-admins; command text is the sanitized
      Phase-4 output.
- [ ] Integration tests: admin create (both scopes), non-admin 403, validation 400,
      apply/remove/reconcile/delete.
- [ ] **CodeQL diff clean** vs baseline (no new command-injection/log-forging/
      path-injection).

## Report-back contract (return exactly this)

```
PHASE 5 REPORT
- Endpoints added (route + admin-gated y/n): <list>
- Create/remove response shapes match §11: <yes>
- Non-admin 403 proven by test: <yes>; validation 400: <yes>
- Sensitive-field redaction in list/get DTOs: <how>
- Integration tests added: <names/counts>
- CODEQL: build-mode=none=<yes> new-vs-baseline=<count -> rules or none> fixed-in-code=<yes/n-a>
- Verification: build=<...> unit=<counts> integration=<counts>
- Files touched: <list>
- Deviations / surprises: <list or "none">
```
