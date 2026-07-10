# Task — Phase 1A: Llama catalog and quant discovery

> Subagent brief. Execute top to bottom and return the report contract verbatim.

## Mission

Ship the versioned 14-model llama manifest and a deterministic catalog/discovery
pipeline that resolves an exact Hugging Face commit and groups complete GGUF
artifact sets. Expose read-only llama catalog and quant endpoints through
llama-admin and GuideAnts API.

## Read first

- Proposal §§2.2–2.5, 3.2–3.4, 4.1–4.4, 4.10–4.16, 5.1, 9.
- `./DECISIONS.md` D1, D2, D6, D8, D10–D12.
- `docs/native-ai-migration/catalog/schema.model.json`
- Existing ASR/TTS/embedding `catalog/manifest.json` and `/admin/catalog` handlers.
- `docker/build/guideants-ai/lib/guideants_hf/*`
- `docker/build/guideants-ai/llama-admin-service/llama_admin_service.py`
- `src/server/GuideAntsApi/Endpoints/Settings/SettingsLlamaEndpoints.cs`
- HF token resolver/configuration used by model downloads.
- `./codeql-gate.md`

## Preconditions

- Phase 0 gate passed. D1 and contract fixtures are confirmed.

## Hard guardrails

- Manifest contains definitions and curator metadata, never discovered file arrays.
- Exactly 14 unique IDs from proposal §4.16 ship in one manifest version.
- Labels and recommendations are presentation metadata only.
- No preferred-quant heuristic and no selected/default quant response field.
- Repository response is pinned to a resolved commit for the whole query.
- No incomplete shard set, projector, unrelated GGUF, duplicate shard, or ambiguous
  group may appear as a selectable quant.
- HF token is server-resolved and never logged or returned.

## Tasks

1. Add the dedicated `schema.llama.json` locked by D11 for `task: "llama"`:
   repository/revision, display metadata, defaults, nullable/explicit projector,
   string-map router preset, quant metadata, hardware notes, versions. Validate the
   schema and instances with Python `jsonschema` Draft 2020-12; add the latest
   package through the project dependency mechanism if needed.
2. Add
   `docker/build/guideants-ai/llama-admin-service/catalog/manifest.json` with all
   14 proposal §4.16 definitions. Fill the full display/license/documentation/
   hardware metadata expected by the card UX, not only the abbreviated index.
3. Add schema tests that reject:
   file/variant arrays, executable capability booleans, duplicate IDs/aliases/model
   IDs, missing explicit `ctx-size`, vision rows without projector or
   `image-min-tokens`, and MTP rows with projector/vision settings. Enforce D12;
   do not copy the inconsistent MTP example from proposal §5.3.
4. Extend `guideants_hf` transport/catalog code to return resolved commit plus file
   path, size, and available integrity metadata at that commit.
5. Implement pure quant grouping:
   - normalize labels and stable IDs;
   - parse and order single/sharded GGUF groups;
   - require one complete consistent `1..N` set;
   - exclude projectors and unrelated files;
   - resolve the declared projector at the same commit or its explicit source;
   - enrich only matching discovered labels with authored guidance.
6. Add deterministic fixture tests for naming variants, nested paths, casing,
   duplicate/missing shards, mixed totals, projectors, separate projector repo,
   no projector, stable order, and repository drift.
7. Add llama-admin `GET /admin/catalog` and an internal definition/quant resolution
   endpoint/service without exposing HF credentials.
8. Add GuideAnts API:
   - `GET /api/settings/llama/catalog`
   - `GET /api/settings/llama/catalog/{catalogId}/quants`
   Use existing Admin settings authorization and return stable ProblemDetails codes
   for unknown definition/version, gated access, missing projector, drift, and
   incomplete groups.
9. Add C# DTO/client tests against Phase 0 fixtures.
10. Add one live repository-resolution case per manifest entry. Mark it as the
    explicit live manifest-drift suite, separate from deterministic unit tests, and
    make its release command fail on drift rather than quietly skip.
11. Verify the new catalog/schema/test assets are copied into every guideants-ai
    image variant. Update packaging only where the existing copy rules omit them.

## Files in scope

- `docs/native-ai-migration/catalog/schema.llama.json`.
- `docker/build/guideants-ai/llama-admin-service/catalog/*`
- `docker/build/guideants-ai/lib/guideants_hf/*`
- llama-admin catalog/discovery code and new Python tests.
- GuideAnts llama catalog client/service, DTOs, settings endpoints, registrations,
  and focused tests.

Out of scope: downloading artifacts, router-entry writes, EF schema, runtime
profiles, install orchestration, client UI.

## Self-verification

```text
python -m compileall docker/build/guideants-ai/lib/guideants_hf docker/build/guideants-ai/llama-admin-service
python -m unittest discover -s docker/build/guideants-ai/llama-admin-service/tests -p "test_*.py" -v
cd src/server && dotnet build GuideAntsApi.sln
cd src/server && dotnet test GuideAntsApi.sln
```

Run the live 14-entry suite and record its result separately. If infrastructure
blocks it, follow the explicit deferral rule in orchestration §6; do not report a
live pass. Run the Phase 1A CodeQL gate.

## Definition of Done

- [ ] Schema and exactly 14 complete definitions ship.
- [ ] Every definition has complete display/license/labels/documentation,
      quant-guidance, and hardware metadata required by proposal §§4.12–4.13.
- [ ] Deterministic grouping accepts complete singles/shards and rejects every
      invalid group class.
- [ ] Catalog/quants include resolved commit and exact metadata, no auto-selection.
- [ ] Projector policy matches each definition.
- [ ] Python and C# contract tests pass.
- [ ] Live suite has 14 named cases and a non-zero failure on drift; an environment
      block is recorded with command/evidence and mandatory Phase 8B dependency per
      orchestration §6.
- [ ] Security gate has zero new findings.

## Report-back contract

```text
PHASE 1A REPORT
- Manifest: path=<path> version=<v> definitions=<must be 14> schema=<path>
- IDs/repositories/profiles/presets audit: <pass/fail + discrepancies>
- Grouping tests: single=<p/f> complete-shards=<p/f> incomplete=<p/f> projector-exclusion=<p/f> stable-id/order=<p/f>
- Integrity metadata returned: <fields>
- Endpoints: llama-admin=<routes> GuideAnts=<routes>
- No quant auto-selection fields/logic: <confirmed?>
- Live drift suite: cases=<must be 14> command=<command> result=<pass/blocked with reason>
- Image packaging: CPU=<p/f> CUDA=<p/f> ROCm=<p/f> Vulkan=<p/f>
- Verification: python=<counts> server-build=<p/f> server-tests=<counts>
- CODEQL REPORT: <required block from codeql-gate.md>
- Files touched: <exhaustive list>
- Deviations / surprises: <list or none>
```
