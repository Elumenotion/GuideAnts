# Task — Phase 3: System project seeder & bootstrap guides

> Subagent brief. Execute top to bottom; return the Report-back contract verbatim.

## Mission

Bootstrap the hidden **GuideAnts System** project at startup, idempotently:

1. `GuideAntsSystemSeeder` creates the system project (`IsSystemProject=true`,
   slug `guideants-system`), two guides + notebooks (user + admin), and two
   **internal** `PublishedGuide` rows with `AuthMode = AppIdentity`.
2. Bootstrap guide folders (user + admin) under `Resources/bootstrap/guides/`.
3. A `GuideAntsSystem` settings section storing all IDs.
4. An internal publish helper (the **only** path allowed to set `AppIdentity`).

## Read first

- `../guideants-guide-implementation-plan.md` §5.2 (settings section), §5.3 (seeder
  steps), §5.4 (bootstrap guide contents).
- `./DECISIONS.md` → **D-GG-A** (AppIdentity seeder-only), **D-GG-E** (system
  project), **D-GG-I** (IDs from settings), **D-GG-J** (stub tools only),
  **D-GG-4** (cost limits — exempt if locked).
- `RequiredGuidesAssistantsSeeder.cs` (pattern + startup ordering in `Program.cs`).
- Existing `Resources/bootstrap/guides/` layout (manifest/instructions/OpenAPI).
- How guides are published internally (the publish service the admin UI calls) —
  build a **seeder-only** helper that sets `AppIdentity` directly, bypassing the
  API guard from Phase 2.
- `WormCommander/Guide/OpenAPI/Web Connector.json` (reference for `client://` tools).
- `ApplicationSettings` read/write pattern for a JSON section.

## Preconditions

- Phase 2 gate green (AppIdentity validation + persistence working). D-GG-A/4 locked.

## Guardrails (hard)

- **Idempotent.** Second run creates **zero** duplicates; if settings IDs exist and
  rows are present, skip; if a row is missing, repair only that row.
- Exactly **one** `IsSystemProject=true` project, slug `guideants-system`.
- Published rows: `Active=true`, `FriendlyName=null` (no public surface),
  `CommandMode=true`, `AuthMode=AppIdentity`, sane `MaxTurns` (e.g. 50).
- The internal publish helper is the **only** code that sets `AppIdentity`. It must
  **not** route through the publish API (which rejects AppIdentity — Phase 2). Keep
  it `internal`/server-only.
- **D-GG-J**: OpenAPI declares `client://guideants-app` with a single stub op
  `AppEcho`. Admin guide may **list** future admin ops (e.g. `AppOpenSettings`,
  `AppListUsers`) but they are not wired anywhere. Instructions reference the
  literal `operationId` `AppEcho`.
- **No secrets** in bootstrap JSON. IDs are generated at seed time and stored in the
  `GuideAntsSystem` settings section (D-GG-I) — do not hard-code GUIDs.
- Per **D-GG-4**: create the system published guides with **no usage limits set**
  (default = unlimited). Do **not** add a special exemption branch in limit-checking
  code. The guides must remain **editable like any other** via the normal usage-limits
  UI in the System Guides project workspace (admins reach it in Phase 7). Note in the
  report that no default limits are applied and that editing works.
- Do not touch endpoints/authz (Phase 4) or client (Phase 6/7).

## Tasks

1. Create bootstrap guide folders:
   - `Resources/bootstrap/guides/guideants-guide/` → user guide
   - `Resources/bootstrap/guides/guideants-guide-admin/` → admin guide
   Each with `manifest.json`, `instructions.md`, `OpenAPI/Web Connector.json`
   (`client://guideants-app`, `AppEcho` stub; admin lists future ops).
2. Implement `IGuideAntsSystemSeeder` + `GuideAntsSystemSeeder`: create project →
   import guides → create notebooks + bind → internal-publish each (AppIdentity) →
   write `GuideAntsSystem` settings section.
3. Implement the seeder-only internal publish helper (`InternalPublishedGuideFactory`
   or similar) that sets `AppIdentity` directly.
4. Add settings read/write helpers for the `GuideAntsSystem` section (typed).
5. Register the seeder in `Program.cs` **after** `RequiredGuidesAssistantsSeeder`.
6. Unit tests: idempotency (1st vs 2nd run, no dupes, repair missing row) +
   settings round-trip.

## Files in scope

- `GuideAntsApi/Services/Bootstrap/IGuideAntsSystemSeeder.cs`
- `GuideAntsApi/Services/Bootstrap/GuideAntsSystemSeeder.cs`
- `GuideAntsApi/Services/Bootstrap/InternalPublishedGuideFactory.cs`
- `GuideAntsApi/Resources/bootstrap/guides/guideants-guide/**`
- `GuideAntsApi/Resources/bootstrap/guides/guideants-guide-admin/**`
- Settings section helper (new) + DI registration
- `Program.cs` (register seeder, ordering)
- Tests under `GuideAntsApi.Tests/Services/SystemGuide/` (or Bootstrap)

**Out of scope:** system endpoints/authz (Phase 4), client, Phase-1/2 code.

## Self-verification

```powershell
cd src/server; dotnet build GuideAntsApi.sln
cd src/server; dotnet test GuideAntsApi.sln
# manual: run the API twice against a scratch DB; confirm 1 system project,
# 2 guides, 2 AppIdentity published rows, populated GuideAntsSystem settings, no dupes.
```

## Definition of Done

- [ ] Seeder creates project + 2 guides + 2 notebooks + 2 AppIdentity published
      rows + settings section on first run; **no dupes** on second run; repairs
      missing rows.
- [ ] Published rows: `FriendlyName=null`, `CommandMode=true`, `AuthMode=AppIdentity`.
- [ ] Bootstrap folders present; OpenAPI uses `client://guideants-app` + `AppEcho`;
      admin lists (not wires) future ops; instructions use literal `AppEcho`.
- [ ] AppIdentity set **only** via the internal helper (not the API).
- [ ] No default usage limits set; no special exemption branch; guides editable via UI (D-GG-4).
- [ ] Seeder tests green; solution builds.

## Report-back contract (return exactly this)

```
PHASE 3 REPORT
- Seeder registered after RequiredGuidesAssistantsSeeder: <yes>
- First run creates: project(IsSystemProject,slug)=<...> guides=<2> notebooks=<2> published(AppIdentity)=<2> settingsSection=<yes>
- Second run dupes: <none>  repair-missing tested: <yes>
- Internal publish helper sets AppIdentity, bypasses API guard: <yes>
- Bootstrap OpenAPI: scheme=<client://guideants-app> ops=<AppEcho [+admin-listed]>
- Cost-limit handling (D-GG-4): <no default limits; no exemption branch; editable via UI>
- IDs hard-coded anywhere: <no>  settings round-trip test: <pass>
- Build: <pass/fail>  seeder tests: <counts>
- Files touched: <exhaustive list>
- Deviations / surprises: <list or "none">
```
