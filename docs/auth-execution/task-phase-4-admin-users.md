# Task — Phase 4: Admin user management

> Subagent brief. Execute top to bottom; return the Report-back contract verbatim.

## Mission

Add the Admin-only user-management endpoints: list (incl. pending), approve + assign
role, change role, deactivate/reactivate, and **admin-set password recovery** (no
email in this version). Guard everything with `RequireAdmin` and add last-Admin
safeguards.

## Read first

- `../auth-system-plan.md` §4 → **Phase 4**, §1 (Admin = approval authority), §3.1
  (no email → admin-driven recovery), Appendix A.2.
- `./DECISIONS.md` (hasher from Phase 2; D3 invariants).
- `./codeql-gate.md` — run the local CodeQL diff before reporting (no GitHub parity).
- `src/server/GuideAntsApi/Endpoints/AuthEndpoints.cs` + the Phase 2 hasher/token
  services + `ICurrentUserService`.

## Preconditions

- Phase 3 gate green (`RequireAdmin` policy exists; auth live).

## Guardrails (hard)

- **Every** endpoint here is `RequireAdmin`. Non-admin → `403`.
- **Last-Admin safeguard**: an Admin cannot deactivate or demote the **final** active
  Admin (and cannot lock themselves out). Return a clear guarded error, never allow
  the zero-Admin state.
- `set-password` reuses the **Phase 2 hasher** (do not introduce a second one),
  sets `MustChangePassword = true`, and **invalidates the target user's outstanding
  JWTs by bumping `User.SecurityStamp`** (the column added in Phase 1; JWT carries it
  as a claim, validation rejects stale stamps). Deactivate must bump it too.
- Role changes update the user's **single `UserRoles` row** (D2) — never insert a
  second active role row.
- **No fallback / no silent success**: approving a non-existent user → `404`;
  assigning an invalid role → `400`. Do not coerce bad input to a default.
- No email sending, no self-service reset (out of scope, §3.1).

## Tasks

1. Add `Endpoints/AdminUsersEndpoints.cs`, group `/api/admin/users`, all
   `RequireAdmin`:
   - `GET /api/admin/users` — list incl. `Pending` (filterable).
   - `POST /api/admin/users/{id}/approve` — assign a role (Reader/Contributor/Admin),
     clear `Pending`, set `ApprovedByUserId`/`ApprovedAt`.
   - `PUT /api/admin/users/{id}/role` — change role (last-Admin safeguard).
   - `POST /api/admin/users/{id}/deactivate` and `/reactivate`.
   - `POST /api/admin/users/{id}/set-password` — admin recovery; hash via Phase 2
     hasher; set `MustChangePassword`; invalidate target sessions.
2. Implement the last-Admin invariant in the service layer (count active Admins;
   block the operation that would reach zero).
3. Map the group in `Program.cs`.

## Files in scope

- `src/server/GuideAntsApi/Endpoints/AdminUsersEndpoints.cs` (new)
- `src/server/GuideAntsApi/Program.cs` (map group)
- admin user service (new) + reuse of Phase 2 hasher
- `SecurityStamp` bump for revocation uses the column **already added in Phase 1** —
  no new migration here. If it is somehow missing, **stop and report** (do not add a
  migration in this phase).

## Self-verification

```
cd src/server && dotnet build GuideAntsApi.sln
cd src/server && dotnet test GuideAntsApi.sln
```

Checks (tests preferred):
- non-admin token → `403` on every `/api/admin/users` route.
- approve flips Pending→assigned role; sets approver fields.
- deactivate/demote the last Admin → guarded error; Admin count never 0.
- set-password: old password fails, new works, `MustChangePassword` set, old token
  rejected.

CodeQL (local, per `./codeql-gate.md` — **C# `build-mode=none`**, no GitHub parity):
diff vs `.codeql/baseline/`; expect **no new** clear-text password storage/logging in
set-password and no user-enumeration/`cs/log-forging`.

## Definition of Done

- [ ] All five admin operations exist and are `RequireAdmin`.
- [ ] Last-Admin safeguard enforced (test proves it).
- [ ] set-password reuses Phase 2 hasher, sets `MustChangePassword`, invalidates
      sessions.
- [ ] Bad input → `404`/`400`, never coerced.
- [ ] Build + tests green.

## Report-back contract (return exactly this)

```
PHASE 4 REPORT
- Endpoints added (all RequireAdmin?): <list> -> <yes/no>
- Last-Admin safeguard: <where enforced; test name>
- set-password: hasher=<same as Phase 2?> mustChangePassword=<set?> SecurityStamp-bumped=<yes?>
- Role change updates single UserRoles row (no second row)? <yes>
- Bad-input handling: approve-missing=<404?> invalid-role=<400?>
- Verification: build=<pass/fail> tests=<counts> nonadmin-403=<yes?> lastadmin-block=<yes?>
- CodeQL (local, no GitHub parity): C#-build-mode-none=<yes> new-findings-vs-baseline=<count> -> <RuleId@file:line or "none">
- Files touched: <exhaustive list>
- Deviations / surprises: <list or "none">
```
