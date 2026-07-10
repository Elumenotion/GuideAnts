# Task — Phase 3: Runtime cleanup, fleet settings, and migration

> Subagent brief. Execute top to bottom and return the report contract verbatim.

## Mission

Establish the final runtime ownership model: minimal catalog routing JSON, alias-only
loads, runtime-profile tool fields, complete INI inventory, and persisted/UI-ready
fleet llama settings with desired/applied revisions. Deterministically migrate
legacy runtime fields and record every unresolved conflict.

## Read first

- Proposal §§2.3–2.6, 4.5–4.8, 4.10–4.11, 5.8, 6, 7.
- `./DECISIONS.md` D4–D7.
- Phase 1B migration/profile report and Phase 2 router contracts.
- `LocalRuntimeConfiguration.cs` and all parser consumers/tests.
- `src/server/AntRunner.Chat/AntRunner.Chat.LlamaCpp/LlamaCppChatClient.cs`
  and `LlamaCppChatClientFactory.cs`.
- `src/server/GuideAntsApi/Services/Conversations/RoutingChatCompletionClientFactory.cs`
  plus llama client/factory/walkthrough tests.
- `ILlamaServerRuntimeClient`, notebook/runtime/warmup load paths.
- `LlamaRouterIniSyncService`, runtime inventory and settings endpoints.
- `start-llama.sh`, `entrypoint.sh`, compose `GA_LLAMA_*` definitions.
- `./codeql-gate.md`

## Preconditions

- Phase 1B and Phase 2 gates passed.

## Hard guardrails

- `RuntimeConfigJson` ends with exactly router/profile identity.
- Alias loads contain no `loadParams`.
- Chat request fields come from profiles only and are added only when tools exist.
- Fleet SQL is authoritative; the projection file is revisioned derived state.
- Alias-scoped keys cannot enter fleet settings.
- Compose seeds only an absent fleet row.
- Unmapped legacy values or row/profile disagreement create durable issues. Do not
  change those models' behavior or claim migration complete.
- Existing INI extras are preserved.

## Tasks

1. Extend runtime profile resolution and `LlamaCppChatClient`/factory to carry
   `requestFieldsWhenToolsPresent`; merge exact fields into requests only when tools
   are present. Define collision behavior explicitly: profile-owned fields win over
   generic request defaults, and invalid profile JSON fails validation.
2. Remove constructor/config plumbing for row-level `parallelToolCalls`.
3. Shrink `LocalRuntimeConfiguration` parser/serializer to required
   `RouterModelId` + `RuntimeProfileId`; keep a migration reader separate from the
   final parser.
4. Remove `loadParams` from every load interface/call. Assert wire bodies contain
   only `{ "model": "<alias>" }`.
5. Extend inventory/detail DTOs to source `routerPreset` from INI and include fleet
   desired/applied summary and installation provenance when present.
6. Implement fleet settings:
   - typed schema for proposal §4.7.3 keys and ranges;
   - GET/PUT routes from D4;
   - reject alias keys (`ctx-size`, `cache-ram`, `image-min-tokens`, MTP keys);
   - persist a new desired revision;
   - call llama-admin to atomically materialize the revisioned projection and restart;
   - confirm applied revision/status or return an explicit apply error;
   - startup reconciliation for desired/applied mismatch.
7. Update `start-llama.sh` to read the projection safely into argument arrays on
   every spawn. Do not use `eval`. Keep fixed infrastructure flags separate.
8. Implement first-boot seeding: read compose environment only when no fleet SQL row
   exists; persist canonical settings and apply them. Prove later env changes do not
   override SQL.
9. Implement an idempotent migration service/command:
   - copy SQL context/cache into matching INI keys;
   - remove `loadParams.model` only when it equals the alias;
   - map other keys only through a reviewed mapping table;
   - migrate row-level tool policy only when all profile users agree;
   - preserve hand-edited extras and mark operator-managed;
   - write durable issues for every conflict/unmapped value;
   - rewrite runtime JSON only after that model's required moves succeed.
10. Expose D8 `GET /api/settings/llama/migration/status` and
    `GET /api/settings/llama/migration/issues` with committed fixtures for Phase 7.
11. Update `StubLlamaServerRuntimeClient`, chat factory tests, notebook runtime
    tests, routing tests, and integration walkthrough fixtures to alias-only loads
    and minimal runtime JSON. After this phase, no llama-cpp test fixture retains
    `loadParams`, row-level `parallelToolCalls`, or SQL context/cache.
12. Add fresh/legacy/conflict/idempotency tests and desired/applied failure/reconcile
    integration tests.

## Files in scope

- Runtime configuration/parser and all consumers/tests.
- Runtime profiles/types/chat client/factory and tests.
- Runtime inventory, settings DTO/services/endpoints/DI.
- Fleet settings model consumers created in Phase 1B.
- Migration service/command and tests.
- llama-admin fleet projection endpoint plus runtime shell scripts and tests.
- Compose files only to preserve/document bootstrap seed variables; no new
  operator-only compose setting.

Out of scope: curated add resolution, lifecycle actions, client UI, manifest edits.

## Self-verification

```text
cd src/server && dotnet build GuideAntsApi.sln
cd src/server && dotnet test GuideAntsApi.sln
python -m compileall docker/build/guideants-ai/llama-admin-service
python -m unittest discover -s docker/build/guideants-ai/llama-admin-service/tests -p "test_*.py" -v
bash -n docker/build/guideants-ai/start-llama.sh
bash -n docker/build/guideants-ai/entrypoint.sh
```

Search changed source and fixtures: final runtime JSON must contain none of
`loadParams`, `parallelToolCalls`, `routerContextSize`, `routerCacheRamMib`.
Exercise all migration fixtures from `STATUS.md`. Run Phase 3 CodeQL.

## Definition of Done

- [ ] Runtime JSON and load requests are minimal/alias-only.
- [ ] Profile tool fields have positive, absent-tools, collision, and invalid tests.
- [ ] Fleet desired/applied save, restart, error, and reconciliation are proven.
- [ ] Compose seeds only an absent row.
- [ ] Legacy migration is idempotent and all ambiguities become visible issues.
- [ ] Migration status/issues routes and integration stubs match committed contracts.
- [ ] Inventory reports authoritative INI/profile/provenance/fleet data.
- [ ] Builds/tests/security gate pass.

## Report-back contract

```text
PHASE 3 REPORT
- Final RuntimeConfigJson shape: <exact fields>
- Alias-only load paths updated: <files> wire-test=<p/f>
- Profile tool fields: tools-present=<p/f> tools-absent=<p/f> deepseek=false=<p/f> others=true=<p/f>
- Fleet API/schema: <routes/keys> desired-applied=<p/f> restart-error-visible=<p/f> reconcile=<p/f>
- Projection/start script: atomic=<p/f> revision=<p/f> no-eval=<confirmed>
- Compose seed: empty-row=<p/f> existing-row-not-overridden=<p/f>
- Migration fixtures: clean=<p/f> unmapped=<issue> disagreement=<issue> INI-extras=<preserved> rerun=<p/f>
- Migration routes: status=<p/f> issues=<p/f> fixtures=<p/f>
- Integration stubs/fixtures migrated: <files/results>
- Legacy field search: loadParams=<none?> parallelToolCalls=<none?> context/cache=<none in final runtime JSON?>
- Verification: server-build=<p/f> server-tests=<counts> python=<counts> shell=<p/f>
- CODEQL REPORT: <required block from codeql-gate.md>
- Files touched: <exhaustive list>
- Deviations / surprises: <list or none>
```
