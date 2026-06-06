# Task — Phase 1: Data model & migration

> Subagent brief. Execute top to bottom; return the Report-back contract verbatim.

## Mission

Extend the data model for first-party auth — a **`UserRoles` table** (D2) plus new
`User` columns including a JWT-revocation `SecurityStamp` (D1) — and add the EF Core
migration that (a) creates them and (b) **deletes the OSS-lite seed user** so a
fresh install has **zero users**. No auth pipeline, endpoints, or UI here.

> **Decisions locked (read `DECISIONS.md` for full implications):** D1 = **App JWT
> Bearer**, D2 = **separate `UserRoles` table** (one row per user, unique on
> `UserId`; the flat single-role model still holds).

## Read first

- `../auth-system-plan.md` §2.2 (current `User` shape), §4 → **Phase 1**, §3.4–3.5.
- `./DECISIONS.md` → **D2 = separate `UserRoles` table (locked)** and **D3
  (invariants)**. Also D1 → add the `SecurityStamp` column (see below).
- `src/server/GuideAntsApi.DataModel/Models/User.cs`
- `src/server/GuideAntsApi.DataModel/ApplicationDbContext.cs`
- `src/server/GuideAntsApi.DataModel/EF_COMMANDS.md` (exact migration commands)
- The seed migration `src/server/GuideAntsApi.DataModel/Migrations/20260326164916_OssLiteSingleUserPrep.cs`
- `src/server/GuideAntsApi/Database/DevScripts/OssLiteSingleUserPrecheck.sql`

## Preconditions

- Phase 0 gate green. `dotnet ef --version` works. D2 finalized in DECISIONS.

## Guardrails (hard)

- Implement the **`UserRoles` table** (D2). **One row per user** — enforce a
  **unique index on `UserId`**. This is storage for the flat model, **not** RBAC:
  no multiple active roles, no per-project rows.
- Role enum is **exactly** `Pending = 0, Reader = 1, Contributor = 2, Admin = 3`.
- **No user `HasData` seed.** The migration must leave `Users` empty on a fresh DB.
  There is **no** bootstrap admin (that happens at runtime in Phase 2).
- Do **not** re-introduce any multitenant table (D3).
- Migration must be **idempotent-safe** with the repo's lite-baseline workflow
  (see EF_COMMANDS.md §"Lite baseline"). The seed user may or may not exist; the
  delete must not throw if it's already gone.

## Tasks

1. Add a `Role` enum (`Pending=0, Reader=1, Contributor=2, Admin=3`) in
   `GuideAntsApi.DataModel`.
2. Add a **`UserRole` entity / `UserRoles` table** (D2):
   - `UserId` (FK → `Users`, cascade delete) — **unique index** (one row per user)
   - `Role` (the enum above)
   - `AssignedAt`, `AssignedByUserId` (nullable FK → `Users`)
   - A `Pending` user is a `UserRoles` row with `Role = Pending` (created at
     registration in Phase 2); approval **updates** that row in Phase 4.
3. Extend `User` (`Models/User.cs`) with:
   - `PasswordHash` (single column; ASP.NET `PasswordHasher<T>` encodes the salt in
     the hash — add a separate salt/format column **only** if Phase 2 picks a hasher
     that needs it)
   - `SecurityStamp` (string/Guid, **required by D1/JWT**) — included as a JWT claim
     and validated against this row so Phase 4 set-password/deactivate can revoke
     outstanding tokens by bumping it. Initialize for new users.
   - `LastLoginAt` (nullable)
   - `ApprovedByUserId` (nullable FK → `Users`) + `ApprovedAt` (nullable)
   - `MustChangePassword` (bool, default false)
   - Do **not** add a `Role` column (it lives in `UserRoles`) and do **not** add a
     redundant `Status`/`IsApproved` column (covered by the `Pending` role row).
4. Wire `ApplicationDbContext`: `UserRoles` DbSet + unique index on `UserId`;
   self-ref FKs for `ApprovedByUserId` / `AssignedByUserId` with `OnDelete` set so an
   approver/assigner deletion does **not** cascade-delete other users' rows.
5. Add the migration via EF_COMMANDS.md exact command (name it
   `AddUserAuthAndRoles` or similar):
   ```powershell
   # from src/server
   dotnet ef migrations add AddUserAuthAndRoles --project GuideAntsApi.DataModel/GuideAntsApi.DataModel.csproj --startup-project GuideAntsApi/GuideAntsApi.csproj
   ```
6. In the **same or a follow-on migration**, add explicit SQL to **delete** seed
   user `fd787545-ffae-4ea9-81fa-700db2fffccd` (guard with existence check so it is
   safe whether or not it was applied). Do not delete by email match alone.
7. Verify auto-migrate path still works (`SqlServerDatabaseInitializer`) and the
   `DataModel.Tests` project compiles against the new model.

## Files in scope

- `src/server/GuideAntsApi.DataModel/Models/User.cs`
- `src/server/GuideAntsApi.DataModel/` new `Role` enum file + `UserRole` entity (D2)
- `src/server/GuideAntsApi.DataModel/ApplicationDbContext.cs`
- `src/server/GuideAntsApi.DataModel/Migrations/*` (generated + the delete-seed SQL)
- `src/server/GuideAntsApi.DataModel.Tests/*` (only if model change breaks them)

**Out of scope:** endpoints, `Program.cs`, services, client.

## Self-verification

```
cd src/server && dotnet build GuideAntsApi.sln
cd src/server && dotnet ef migrations list --project GuideAntsApi.DataModel/GuideAntsApi.DataModel.csproj --startup-project GuideAntsApi/GuideAntsApi.csproj
cd src/server && dotnet ef migrations script <previousHead> <newHead> --project GuideAntsApi.DataModel/GuideAntsApi.DataModel.csproj --startup-project GuideAntsApi/GuideAntsApi.csproj
# fresh-DB proof (use a scratch DB / localdb):
cd src/server && dotnet ef database drop --force --project GuideAntsApi.DataModel/GuideAntsApi.DataModel.csproj --startup-project GuideAntsApi/GuideAntsApi.csproj
cd src/server && dotnet ef database update --project GuideAntsApi.DataModel/GuideAntsApi.DataModel.csproj --startup-project GuideAntsApi/GuideAntsApi.csproj
cd src/server && dotnet test GuideAntsApi.DataModel.Tests/GuideAntsApi.DataModel.Tests.csproj
```

Then confirm `SELECT COUNT(*) FROM Users` = **0** on the fresh DB.

## Definition of Done

- [ ] `Role` enum + `UserRoles` table (unique on `UserId`) + `User` columns
      (incl. `PasswordHash`, `SecurityStamp`, `MustChangePassword`,
      `Approved*`) added — nothing extra, **no** `Role` column on `User`.
- [ ] Migration present at head; generated SQL adds the table/columns + deletes seed
      user; **no** user `HasData`.
- [ ] Fresh-DB apply succeeds, `Users` count = 0.
- [ ] `DataModel.Tests` green; solution builds.
- [ ] No multitenant tables reintroduced.

## Report-back contract (return exactly this)

```
PHASE 1 REPORT
- UserRoles table: created? <yes> unique-on-UserId? <yes>
- User columns added: <list with types/nullability incl. SecurityStamp>
- Migration name(s): <names>
- Seed-user delete: <how guarded; idempotent? yes/no>
- Fresh-DB Users count: <n> (must be 0)
- Designer snapshot updated: yes/no
- Verification: build=<pass/fail> migrations-list-head=<name> db-update=<pass/fail> datamodel-tests=<counts>
- Files touched: <exhaustive list>
- Deviations / surprises: <list or "none">
```
