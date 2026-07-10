# Task — Phase 4: Curated install orchestration

> Subagent brief. Execute top to bottom and return the report contract verbatim.

## Mission

Implement the authoritative curated install path behind
`POST /api/settings/models:add`: identity-only input, exact version/commit
re-resolution, durable immutable operation input, complete download/router
registration, and transactional catalog/provenance finalization.

## Read first

- Proposal §§3.1–3.5, 4.1–4.9, 5.1–5.4, 9.
- `./DECISIONS.md` D1–D3, D6, D8, D11–D12.
- Phase 1A catalog/quant contracts, Phase 1B entities, Phase 2 admin transport,
  Phase 3 final runtime/profile contracts.
- `src/server/GuideAntsApi/Models/Settings/SettingsDtos.cs`
- `src/server/GuideAntsApi/Endpoints/Settings/SettingsModelsEndpoints.cs`
  and `SettingsLlamaEndpoints.cs`.
- `LocalModelOnboardingCommand`, validator, orchestrator, download service.
- Existing `ConcurrentDictionary` pending registration and operation polling code.
- `ApplicationSettingsService.Models.cs` and model creation tests.
- `./codeql-gate.md`

## Preconditions

- Phases 1A, 1B, 2, and 3 gates passed.

## Hard guardrails

- Curated client input contains identities only.
- Definition ID and version must match shipped catalog content exactly.
- The selected quant must still be complete at the supplied resolved commit.
- The server derives all executable/configuration values.
- Model ID, alias, target directory, and existing installation conflicts are errors;
  do not rename or reuse another operation unless immutable input hashes match.
- Remove process-memory-only pending registration as an authority.
- Catalog/provenance finalization occurs after verified artifacts and INI commit.
- Partial completion remains durable and visible with a specific remediation.

## Tasks

1. Extend `AddModelInstallDto` with a discriminated curated request:
   `source`, `catalogId`, `catalogVersion`, `quantId`, `resolvedRevision`.
   Reject curated payloads containing repository/path/preset/profile/alias fields.
2. Update validation to resolve the exact definition/version, profile, quant group,
   projector, identities, target directory, preset, and artifact metadata.
3. Re-query the exact resolved commit, not mutable repository head. Return stable
   error codes for version unavailable, commit changed/unavailable, quant missing,
   incomplete shards, projector missing, profile missing, identity conflict,
   gated access, and invalid preset.
4. Build the immutable operation input from authoritative data and persist it before
   starting llama-admin. Store an input hash for idempotent duplicate submission.
5. Submit exact files/preset to llama-admin and correlate its journal/state with the
   durable SQL operation.
6. Replace the static in-memory pending-registration map with durable orchestration.
   Poll/status and startup reconciliation must resume finalization safely after API
   or llama-admin restart.
7. At `registeringAlias`, require complete artifact and alias/preset evidence.
8. Finalize catalog `Model` with minimal runtime JSON and
   `LocalModelInstallation` provenance in one database transaction. Mark operation
   completed only after that transaction commits.
9. If final database work fails, retain exact side-effect state and return
   `catalogFinalization` with an idempotent retry action; do not start a second
   download.
10. Add canonical `GET /api/settings/llama/operations/{operationId}` while retaining
    any required temporary read compatibility from D8.
11. Extend status DTOs with stage, immutable summary, completed side effects,
    structured error/remediation, and final model/provenance link.
12. Add unit/integration tests for request rejection, exact resolution, every
    conflict, duplicate idempotency, each partial boundary, API restart,
    llama-admin restart, finalization retry, and successful single/sharded installs.

## Files in scope

- Settings add/install DTOs, endpoints, validator/command/orchestrator/services/DI.
- Catalog resolver/admin client consumers needed to compose immutable input.
- Phase 1B operation/installation persistence services.
- Operation status route/DTO and focused server/integration tests.

Out of scope: change quant, repair, adoption, custom/attach redesign, client UI,
manifest/profile/router/fleet schema changes.

## Self-verification

```text
cd src/server && dotnet build GuideAntsApi.sln
cd src/server && dotnet test GuideAntsApi.sln
```

Run end-to-end fixture installs for single GGUF and two shards. Kill/recreate the API
between download completion and catalog finalization; prove one model/provenance row
and completed operation result. Run Phase 4 CodeQL.

## Definition of Done

- [ ] Curated add accepts identities only and resolves exact authoritative input.
- [ ] Immutable operation exists before side effects and survives restarts.
- [ ] No process-memory collection is the finalization authority.
- [ ] Model/provenance commit only after artifact+INI success.
- [ ] Every partial state and retry is explicit/idempotent.
- [ ] Single and sharded integration tests, builds, and security gate pass.

## Report-back contract

```text
PHASE 4 REPORT
- Curated request fields: <exact list> forbidden-field tests=<p/f>
- Resolution: definition-version=<p/f> commit=<p/f> quant=<p/f> projector/profile/preset=<p/f>
- Immutable operation: entity=<name> input-hash=<yes> persisted-before-side-effects=<yes>
- In-memory pending authority removed: <file/result>
- Finalization order: <stages> DB transaction=<model+provenance>
- Restart tests: API=<p/f> llama-admin=<p/f> finalization-retry=<p/f>
- Conflict/partial error codes: <list + test result>
- Fixture installs: single=<p/f> sharded=<p/f> runtime-json-minimal=<p/f> provenance=<p/f>
- Verification: build=<p/f> tests=<counts>
- CODEQL REPORT: <required block from codeql-gate.md>
- Files touched: <exhaustive list>
- Deviations / surprises: <list or none>
```
