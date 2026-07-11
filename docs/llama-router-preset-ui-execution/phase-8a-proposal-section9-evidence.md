# Proposal §9 Evidence Map — Curated Local Llama

Maps each acceptance criterion in `docs/llama-router-preset-ui-proposal.md` §9 to Phase 8A evidence. Live hardware qualification slots reference Phase 8B.

## V1 catalog completeness

| Criterion | Evidence |
| --- | --- |
| 14 curated models in one release | `catalog/manifest.json` + `test_llama_catalog.py` (14 entries) |
| `runtimeProfileId`, `routerPreset`, `mmproj`, `quantMetadata` per entry | `test_llama_schema.py`, `validate-contracts.*` |
| Vision `image-min-tokens=1024`; MTP `spec-type`/`spec-draft-n-max` | Manifest entries + `LlamaCatalogContractTests` |
| Profiles `deepseek_r1`, `qwen3_coder` | Bootstrap JSON + `RuntimeProfileSeederPhase1BTests` |
| Parameter ownership §4.17 | `RouterPresetValidator`, `test_router_preset.py` fleet-key rejection |

## Curated UX

| Criterion | Evidence |
| --- | --- |
| Install by model + quant only | `CuratedInstallTests`, client `AddModelWizard.flow.test.tsx` |
| Quant rows from repository API | `LlamaCatalogContractTests`, `quant-group-response.fixture.json` |
| Sharded quants one row | `CuratedInstallTests.Resolver_ShardedQuant_*`, `test_quant_grouping.py` |
| No profile/projector/alias/preset selection | `CuratedInstallTests.ValidateAsync_CuratedForbiddenFields_*`, client parity tests |
| Review shows commit + artifacts | `immutable-operation-input.fixture.json`, operation status DTO tests |

## Definition behavior

| Criterion | Evidence |
| --- | --- |
| Definitions hold repository/defaults not quant file arrays | Manifest schema + `check-routes-and-manifest.mjs` D11 |
| Labels non-authoritative | Server resolver uses definition defaults only |
| Concrete profile + router keys | `RuntimeProfileResolverTests`, preset fixtures |
| Discovery does not mutate definitions | Admin catalog read-only proxy tests |
| Versioned definition changes | `catalogVersion` gate in `CuratedInstallResolver` |

## Runtime integrity

| Criterion | Evidence |
| --- | --- |
| Minimal `RuntimeConfigJson` | `LlamaFleetContractTests.LoadModelAsync_WireBodyContainsAliasOnly`, migration tests |
| Fleet argv editable in Settings | `FleetLlamaRuntimeSettingsServiceTests`, `fleet-preset-*.fixture.json` |
| Per-alias preset in Customize | `LocalModelLifecycleTests.Customize_*`, `router-entry-put-request.fixture.json` |
| Provenance recoverable | `LlamaCrossLayerContractTests.OperatorManagedInstall_*`, installation DTO fixtures |
| Repair uses recorded commit/files | `LocalModelLifecycleTests.ChangeQuant_*`, `repair-request.fixture.json` |
| Changed repo content → actionable failure | `LlamaNegativeContractTests` commit/quant incomplete |
| No silent substitution | Negative tests: `QuantMissing`, `CommitChanged`, `ProjectorMissing` |

## Extensibility

| Criterion | Evidence |
| --- | --- |
| Custom repository install | `custom-add-request.fixture.json`, `CustomInstallResolver` |
| Custom sharded GGUF | `test_exact_download.py` shard validation |
| Custom full preset map | `RouterPresetValidator`, client advanced UI tests Phase 7 |
| New curated models via manifest | Schema tests; no new UI columns required |
| New switches via fleet or alias preset | `FleetLlamaRuntimeSettingsServiceTests.PutAsync_RejectsAliasScopedKeys` |

## D8 lifecycle routes (proposal §§3.6–3.8, §7.2)

| Route | Evidence |
| --- | --- |
| `GET /api/settings/llama/catalog` | `LlamaCatalogContractTests`, OpenAPI export, auth matrix |
| `GET .../catalog/{id}/quants` | Quant fixtures + auth matrix |
| `POST /api/settings/models:add` (curated) | `CuratedInstallTests`, auth matrix |
| `GET .../operations/{id}` | `CuratedInstallTests` operation polling |
| `GET .../installations/{modelId}` | `LocalModelLifecycleTests`, cross-layer tests |
| `POST .../change-quant` | `LocalModelLifecycleTests.ChangeQuant_*` |
| `POST .../repair` | Lifecycle + negative alias lock tests |
| `POST .../customize` | `LocalModelLifecycleTests.Customize_*` |
| `POST .../adopt` | Adoption preview/blocker tests |
| `GET/PUT .../router/entries` | `LlamaRouterContractTests` |
| `GET/PUT .../runtime/fleet-preset` | `LlamaFleetContractTests`, fleet service tests |
| `GET .../migration/status` | Migration service + fixture deserialization |
| `GET .../migration/issues` | `migration-issues-response.fixture.json`, migration fixture tests |

## Phase 8B slots (not Phase 8A gates)

| Slot | Evidence path |
| --- | --- |
| Live 14-repo HF drift | `test_live_manifest_drift.py` |
| Representative runtime qualification | Phase 8B qualification matrix in `STATUS.md` |
