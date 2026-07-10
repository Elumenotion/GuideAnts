# Task — Phase 5: Installation lifecycle and operator-managed APIs

> Subagent brief. May run in parallel with Phase 6. Return the report contract verbatim.

## Mission

Implement backend operations for change quant, repair, Customize, curated adoption,
advanced custom HF installation, and attach existing alias. Preserve atomic runtime
activation and provenance truth across each transition.

## Read first

- Proposal §§3.6–3.8, 4.7.7, 4.9, 5.5–5.8, 7.2, 9.
- `./DECISIONS.md` D1–D3, D5, D7–D9.
- Phase 4 operation/install implementation and Phase 2 router/download contract.
- Existing model delete, inventory, runtime coordinator, load/unload, and settings
  model update handlers.
- Phase 1B installation/operation/migration-issue entities.
- `./codeql-gate.md`

## Preconditions

- Phase 4 gate passed. Phase 6 may execute concurrently against frozen Phase 4 APIs.

## Hard guardrails

- Repair uses the recorded commit and artifact set; it does not browse current head.
- Change quant is a staged operation. Prior active artifacts remain until the new
  set and alias section are verified.
- Obsolete artifacts are removed only after successful activation/provenance commit.
- Loaded state is observed before change and restored only when it was actually
  loaded. A load failure remains an operation error.
- Adoption never invents provenance; all unknowns remain visible.
- Attach does not alter the existing INI section.
- Custom input is explicit: revision, ordered artifact group, projector, alias,
  profile, preset, target, and catalog presentation.
- Customize is an explicit management-mode transition with confirmation semantics.
- Alias locks prevent concurrent install/change/repair/delete on the same alias.

## Tasks

1. Add installation detail endpoint and D8 lifecycle routes with stable DTOs/error
   codes and the existing Admin settings authorization. Add unauthenticated `401`,
   non-Admin `403`, and Admin behavior tests for every new route.
2. Change quant:
   - list current definition quant groups;
   - resolve selected group and commit;
   - create durable operation;
   - stage/download/verify complete set;
   - capture loaded state and unload under alias lock;
   - atomically activate artifacts + replacement preset;
   - update provenance in a transaction;
   - reload if previously loaded;
   - delete obsolete files last;
   - record exact remediation at each failure boundary.
3. Repair:
   - read recorded commit, artifact metadata, paths, and preset;
   - verify existing artifacts;
   - re-download missing/invalid exact files;
   - rewrite the recorded preset in replace mode;
   - prove alias load;
   - update verification timestamps without changing source identity.
4. Customize:
   - copy provenance preset into operator-managed state;
   - retain historical curated ID/version and artifacts;
   - set management mode explicitly;
   - stop future curated-version comparison until adoption.
5. Custom HF install:
   - require explicit revision and selected complete ordered artifacts;
   - support shards and optional explicit projector;
   - require alias/profile/full preset/target/catalog identity;
   - use the same durable staging/finalization pipeline;
   - write operator-managed provenance without curated identity.
6. Attach existing:
   - list only aliases with model artifacts and no catalog binding;
   - require catalog identity and profile;
   - create minimal runtime JSON and operator-managed record;
   - preserve the exact existing INI section.
7. Curated adoption:
   - select a definition and compare alias, artifacts, profile, preset, and any known
     source metadata;
   - return an exact diff;
   - require explicit confirmation;
   - allow adoption only when required provenance can be verified;
   - otherwise remain operator-managed with actionable differences.
8. Ensure model delete and lifecycle operations coordinate through the same alias
   lock and operation state. Test that a runtime/artifact deletion failure preserves
   the catalog model and provenance; database deletion occurs only after runtime
   cleanup succeeds.
9. Add integration tests for success, all stage failures, restart recovery,
   concurrency conflict, loaded/unloaded behavior, obsolete-file timing, unknown
   provenance, custom shards, attach preservation, and adoption differences.

## Files in scope

- Settings llama installation/lifecycle endpoints, DTOs, services, DI.
- Operation/provenance persistence services.
- Runtime coordinator/admin/download clients as needed for orchestration.
- Focused server/integration tests.

Out of scope: Python transport contract changes, manifest changes, client UI,
runtime/profile/fleet schema redesign.

## Self-verification

```text
cd src/server && dotnet build GuideAntsApi.sln
cd src/server && dotnet test GuideAntsApi.sln
```

Run staged filesystem/INI integration fixtures and restart/concurrency cases. Prove
attach leaves the INI byte-for-byte unchanged. Run Phase 5 CodeQL.

## Definition of Done

- [ ] All six lifecycle/operator-managed paths are implemented and authorized.
- [ ] Change quant and repair preserve provenance/activation rules.
- [ ] Custom and attach support complete sharded groups.
- [ ] Adoption reports exact differences and never invents unknown values.
- [ ] Alias concurrency and restart recovery are tested.
- [ ] Authorization matrix and delete/provenance ordering are tested.
- [ ] Builds/tests/security gate pass.

## Report-back contract

```text
PHASE 5 REPORT
- Routes added: <installation detail + actions>
- Change quant: staged=<p/f> loaded-state=<p/f> provenance-commit=<p/f> obsolete-files-last=<p/f>
- Repair: recorded-commit=<p/f> exact-files=<p/f> recorded-preset=<p/f> load-proof=<p/f>
- Customize: management-mode-transition=<p/f> history-retained=<p/f>
- Custom: explicit-input=<p/f> shards=<p/f> operator-provenance=<p/f>
- Attach: unbound-only=<p/f> INI-byte-preserved=<p/f>
- Adoption: diff=<p/f> no-invented-provenance=<p/f> confirmation=<p/f>
- Authorization: unauthenticated=<401> non-admin=<403> admin=<p/f>
- Delete ordering: runtime-failure-preserves-DB=<p/f> success-removes-provenance=<p/f>
- Concurrency/restart/failure-boundary tests: <counts/results>
- Verification: build=<p/f> tests=<counts>
- CODEQL REPORT: <required block from codeql-gate.md>
- Files touched: <exhaustive list>
- Deviations / surprises: <list or none>
```
