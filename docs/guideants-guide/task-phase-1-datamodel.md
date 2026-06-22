# Task — Phase 1: Data model & migrations

> Subagent brief. Execute top to bottom; return the Report-back contract verbatim.

## Mission

Add the **storage shape** for the GuideAnts Guide feature — nothing behavioral:

1. `PublishedGuideAuthMode` enum + `PublishedGuide.AuthMode` column, with a
   migration that **backfills existing rows** from their current auth fields.
2. `Project.IsSystemProject` boolean column (default `false`).
3. `AuthValidationResult.InternalUserId` (nullable `Guid`) property — declared only;
   no logic reads/writes it yet.

No validation, no seeder, no endpoints, no UI. Those are Phases 2–7.

## Read first

- `../guideants-guide-implementation-plan.md` §4.1 (auth modes + backfill mapping),
  §5.1 (project flag), §5.5 (auth-mode migration).
- `./DECISIONS.md` → **D-GG-B** (mutually-exclusive modes + backfill mapping),
  **D-GG-A** (AppIdentity is seeder-only — relevant to defaults).
- `src/server/GuideAntsApi.DataModel/Models/Project.cs`
- `PublishedGuide` model + `ApplicationDbContext` (find with grep).
- `src/server/GuideAntsApi.DataModel/EF_COMMANDS.md` (exact migration commands).
- `AuthValidationResult` definition (in `IPublishedGuideAuthService.cs` /
  `PublishedGuideAuthService.cs`).

## Preconditions

- Pre-flight baseline captured. `dotnet ef --version` works. DECISIONS D-GG-A/B locked.

## Guardrails (hard)

- Enum is **exactly** `Anonymous=0, Webhook=1, ApiKey=2, AppIdentity=3` (D-GG-B).
- `AuthMode` default is `Anonymous` (0). **Do not** create any row with
  `AppIdentity` in this phase (the seeder does that in Phase 3).
- Backfill in the migration via explicit SQL using the existing columns:
  `ApiKeyHash` non-null/non-empty → `2`; else `AuthValidationWebhookUrl`
  non-null/non-empty → `1`; else `0`. **Order matters** (ApiKey wins if both set —
  match current implicit inference; verify against `PublishedGuideAuthService`
  before choosing the precedence and note it in the report).
- Keep legacy columns (`ApiKeyHash`, `AuthValidationWebhookUrl`) — they still back
  `Webhook`/`ApiKey` modes. Do not drop them.
- `IsSystemProject` default `false`; no data seed.
- **No** behavior change: do not touch `PublishedGuideAuthService` logic,
  endpoints, services, or client. Only the model + migration + the one result
  property declaration.
- Migration must apply cleanly on a fresh DB **and** on an existing DB (backfill).

## Tasks

1. Add `PublishedGuideAuthMode` enum (own file `Models/PublishedGuideAuthMode.cs`
   or alongside `PublishedGuide`).
2. Add `public PublishedGuideAuthMode AuthMode { get; set; }` to `PublishedGuide`
   (default `Anonymous`). Configure in `ApplicationDbContext` if explicit config is
   the repo convention (int column, not null, default 0).
3. Add `public bool IsSystemProject { get; set; }` to `Project`; configure default
   `false`.
4. Add `public Guid? InternalUserId { get; set; }` to `AuthValidationResult`
   (declaration only).
5. Generate the migration(s) using the EF_COMMANDS.md exact command(s). One
   migration covering both columns is acceptable; name e.g.
   `AddGuideAntsGuideSchema` (or two: `AddPublishedGuideAuthMode`,
   `AddProjectIsSystemProject`).
6. Edit the generated migration `Up()` to add the **backfill SQL** for `AuthMode`
   (idempotent / guarded).
7. Verify auto-migrate path + `DataModel.Tests` compile against the new model.

## Files in scope

- `src/server/GuideAntsApi.DataModel/Models/Project.cs`
- `src/server/GuideAntsApi.DataModel/Models/PublishedGuide*.cs` + new enum file
- `src/server/GuideAntsApi.DataModel/ApplicationDbContext.cs`
- `src/server/GuideAntsApi.DataModel/Migrations/*` (generated + backfill SQL)
- `AuthValidationResult` declaration file (property only)
- `src/server/GuideAntsApi.DataModel.Tests/*` (only if model change breaks compile)

**Out of scope:** `PublishedGuideAuthService` logic, endpoints, seeder, client.

## Self-verification

```powershell
cd src/server; dotnet build GuideAntsApi.sln
cd src/server; dotnet ef migrations list --project GuideAntsApi.DataModel/GuideAntsApi.DataModel.csproj --startup-project GuideAntsApi/GuideAntsApi.csproj
cd src/server; dotnet ef migrations script <prevHead> <newHead> --project GuideAntsApi.DataModel/GuideAntsApi.DataModel.csproj --startup-project GuideAntsApi/GuideAntsApi.csproj
# fresh-DB + backfill proof on a scratch DB:
cd src/server; dotnet ef database drop --force --project GuideAntsApi.DataModel/GuideAntsApi.DataModel.csproj --startup-project GuideAntsApi/GuideAntsApi.csproj
cd src/server; dotnet ef database update --project GuideAntsApi.DataModel/GuideAntsApi.DataModel.csproj --startup-project GuideAntsApi/GuideAntsApi.csproj
cd src/server; dotnet test GuideAntsApi.DataModel.Tests/GuideAntsApi.DataModel.Tests.csproj
```

## Definition of Done

- [ ] `PublishedGuideAuthMode` enum (0–3) + `PublishedGuide.AuthMode` (default 0).
- [ ] `Project.IsSystemProject` (default false).
- [ ] `AuthValidationResult.InternalUserId` (nullable Guid) declared.
- [ ] Migration at head; `Up()` backfills `AuthMode` from legacy columns with the
      verified precedence; **no** `AppIdentity` rows created; **no** `HasData`.
- [ ] Fresh-DB apply succeeds; existing-row backfill correct.
- [ ] `DataModel.Tests` green; solution builds; **no behavior change**.

## Report-back contract (return exactly this)

```
PHASE 1 REPORT
- Enum: PublishedGuideAuthMode values = <list>
- PublishedGuide.AuthMode: default=<Anonymous> column type=<int>
- Project.IsSystemProject: default=<false>
- AuthValidationResult.InternalUserId: added? <yes> type=<Guid?>
- Migration name(s): <names>
- Backfill SQL precedence (ApiKey vs Webhook): <which wins; matches existing inference? yes/no>
- Fresh-DB apply: <pass/fail>  existing-row backfill verified: <how>
- Designer snapshot updated: <yes/no>
- Verification: build=<pass/fail> migrations-list-head=<name> db-update=<pass/fail> datamodel-tests=<counts>
- Files touched: <exhaustive list>
- Deviations / surprises: <list or "none">
```
