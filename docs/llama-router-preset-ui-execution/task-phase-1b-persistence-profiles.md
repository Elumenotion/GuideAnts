# Task — Phase 1B: Persistence and runtime-profile contract

> Subagent brief. May run in parallel with Phase 1A. Return the report contract verbatim.

## Mission

Add the durable data required for installation provenance, restart-safe operations,
fleet desired/applied state, and deterministic migration issues. Extend runtime
profiles with concrete tool-request fields and seed the six profile changes required
by the proposal. Do not wire install or fleet behavior yet.

## Read first

- Proposal §§2.6, 4.5, 4.8–4.11, 4.17, 5.3, 7, 9.
- `./DECISIONS.md` D2–D7.
- `src/server/GuideAntsApi.DataModel/Models/Model.cs`
- `RuntimeProfile.cs`, `ApplicationDbContext.cs`, `EF_COMMANDS.md`, latest migration.
- `Resources/bootstrap/runtime-profiles/*`
- Runtime profile seeder/resolver/types/endpoints and their tests.
- Existing JSON-column and one-to-one entity conventions in the data model.
- `./codeql-gate.md`

## Preconditions

- Phase 0 gate passed and data contract fixtures are frozen.

## Hard guardrails

- Provenance is separate from transient progress/logs.
- Existing models receive no invented repository, commit, quant, curated ID, or digest.
- Operation input is immutable after creation; status updates are explicit fields.
- Fleet desired and applied revisions are distinct.
- Tool request policy is concrete JSON request data, not capability booleans.
- Do not remove legacy runtime fields in this phase; Phase 3 migrates them.
- Generated migrations must be reviewed, not hand-edited to conceal model mistakes.

## Tasks

1. Add `LocalModelInstallation` (or equivalently named dedicated entity) one-to-one
   with `Model`, including management mode, curated identity/version, repository,
   requested/resolved revision, quant identity, ordered artifact metadata JSON,
   router preset snapshot JSON, created/updated, and concurrency protection.
2. Add durable `LocalModelOperation` with operation kind, model/alias correlation,
   immutable input JSON, status/current step, completed-side-effects JSON, error
   code/message/remediation, desired/applied revisions where relevant, and times.
3. Add singleton `FleetLlamaRuntimeSettings` with canonical preset JSON, desired
   revision, applied revision, apply status/error, bootstrap source, and times.
4. Add `LocalModelMigrationIssue` (or equivalent durable report entity) with model,
   issue code, source field/value snapshot, required action, resolution state, and
   times. Enforce idempotent uniqueness per model+issue+source hash.
5. Extend `RuntimeProfile` and API DTOs with validated
   `RequestFieldsWhenToolsPresentJson`.
6. Extend seed loading, resolver types, create/update validation, and serializers.
   Require a JSON object; reject reserved transport fields or structurally invalid
   values rather than coercing them.
7. Add bootstrap profiles `deepseek_r1` and `qwen3_coder` exactly as proposal §4.11.
8. Add `parallel_tool_calls: true` to the fresh-install seed definitions for
   `qwen3_5`, `qwen3_6`, `gemma4`, and `gpt_oss`; retain `false` for
   `deepseek_r1`.
   Existing databases require special care because the current seeder skips an
   existing profile: add the schema now but do not overwrite existing built-in or
   operator-edited profiles in this phase. Phase 3 migrates row-level values only
   after proving all profile users agree.
9. Generate one EF migration for these structures. Its upgrade path must leave
   current models/profiles and their behavior intact and must not create provenance
   rows for them.
10. Add model, migration, seeder, resolver, DTO, invalid-JSON, concurrency, and
    round-trip tests.

## Files in scope

- `src/server/GuideAntsApi.DataModel/Models/*` new entities + `RuntimeProfile.cs`
- `ApplicationDbContext.cs`, migration and snapshot.
- Runtime-profile seed JSON, seeder/resolver/types/settings DTO/endpoints/services.
- Focused server and data-model tests.

Out of scope: llama-admin/Python, client, fleet apply behavior, runtime JSON cleanup,
install/lifecycle orchestration.

## Self-verification

```text
cd src/server
dotnet build GuideAntsApi.sln
dotnet test GuideAntsApi.sln
dotnet ef migrations list --project GuideAntsApi.DataModel/GuideAntsApi.DataModel.csproj --startup-project GuideAntsApi/GuideAntsApi.csproj
dotnet ef migrations script <previousHead> <newHead> --project GuideAntsApi.DataModel/GuideAntsApi.DataModel.csproj --startup-project GuideAntsApi/GuideAntsApi.csproj
```

Apply to a fresh database and a copy containing legacy local model/profile rows.
Prove no provenance was manufactured and seed re-run is idempotent. Run Phase 1B
CodeQL.

## Definition of Done

- [ ] Four durable concerns are modeled with required indexes/concurrency.
- [ ] Migration applies fresh and over legacy fixtures.
- [ ] No legacy model receives fabricated provenance.
- [ ] Runtime-profile request fields validate and round-trip.
- [ ] Fresh-install two new/four extended profile seeds match proposal §4.11;
      existing profile behavior remains unchanged pending Phase 3 analysis.
- [ ] Build and all tests pass.
- [ ] Security gate has zero new findings.

## Report-back contract

```text
PHASE 1B REPORT
- Entities/tables: installation=<name> operation=<name> fleet=<name> issues=<name>
- Key/index/concurrency design: <summary>
- RuntimeProfile field: <name/type/validation>
- Profiles (fresh seed): deepseek_r1=<p/f> qwen3_coder=<p/f> qwen3_5=<p/f> qwen3_6=<p/f> gemma4=<p/f> gpt_oss=<p/f>
- Existing profile upgrade behavior unchanged until Phase 3: <p/f + evidence>
- Migration: <name> fresh=<p/f> legacy=<p/f> invented-provenance=<must be none>
- Idempotency/concurrency tests: <counts/results>
- Verification: build=<p/f> tests=<counts> migration-script-review=<p/f>
- CODEQL REPORT: <required block from codeql-gate.md>
- Files touched: <exhaustive list>
- Deviations / surprises: <list or none>
```
