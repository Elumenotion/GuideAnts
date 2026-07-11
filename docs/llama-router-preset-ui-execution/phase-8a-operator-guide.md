# Curated Local Llama — Phase 8A Operator Guide

## Authoritative stores

| Concern | Authoritative store | Notes |
| --- | --- | --- |
| Catalog definitions | `docker/build/guideants-ai/llama-admin-service/catalog/manifest.json` | Shipped in image; versioned by `catalogVersion` |
| Installation provenance | SQL `LocalModelInstallations` | Repository, revision, quant, artifacts, preset snapshot |
| Durable operations | SQL `LocalModelOperations` | Survives API and llama-admin restart |
| Fleet llama argv | SQL `FleetLlamaRuntimeSettings` | Desired/applied revisions; not compose edits |
| Per-alias router preset | `router-models.ini` via llama-admin | Inventory reads live INI |
| Runtime profile tool policy | SQL `RuntimeProfiles.requestFieldsWhenToolsPresent` | Not per-model row fields |
| Model identity | SQL `Models.runtimeConfigJson` | Minimal JSON: `routerModelId` + `runtimeProfileId` only |

## Curated install lifecycle

1. Operator selects catalog definition and quant only.
2. API resolves immutable operation input server-side (commit, files, preset, profile).
3. llama-admin downloads exact artifact list, registers alias INI, returns operation status.
4. API finalizes SQL catalog row and provenance only after artifacts + alias exist.
5. Interrupted installs resume from durable `LocalModelOperations` via `GET /api/settings/llama/operations/{id}`.

## Custom, attach, Customize, adoption

- **Custom**: explicit repository, revision, quant/shard selection, optional preset map.
- **Attach / existing alias**: links catalog row to an already-registered router alias without re-download.
- **Customize**: transitions curated installation to `operatorManaged`; retains provenance history.
- **Adopt**: compares operator-managed artifacts to a curated definition; never invents repository/revision/quant.

## Fleet desired vs applied

- `PUT /api/settings/llama/runtime/fleet-preset` writes desired preset + bumps `desiredRevision`.
- llama-admin applies fleet argv and reports `appliedRevision` / `applyStatus`.
- Mismatch (`pendingRestart`, `applyError`) is visible in Settings until restart confirms.

First-boot compose `GA_LLAMA_*` values seed SQL only when the fleet row is empty; they do not override an existing desired preset.

## Migration report

`GET /api/settings/llama/migration/issues` returns unresolved items. Issue codes:

| Code | Meaning | Resolution |
| --- | --- | --- |
| `unmapped-loadparams-key` | Legacy `loadParams` key without mapping | Remove key or map manually to router preset |
| `loadparams-model-mismatch` | `loadParams.model` ≠ router alias | Correct alias or remove key |
| `parallel-tool-calls-disagreement` | Models sharing a profile disagree on tool policy | Split profiles or remain operator-managed |
| `operator-managed-ini-extras` | Hand-edited INI extras present | Preserve; adopt/customize if curating |
| `invalid-runtime-json` | `runtimeConfigJson` not parseable | Fix JSON syntax |

Re-run migration is idempotent: identical issues are not duplicated.

## Repair and change-quant recovery

- **Repair** re-downloads recorded commit/files; poll operation status until `completed` or actionable `failed`.
- **Change quant** stages new artifacts, activates alias, removes obsolete files last; failures leave durable operation state for retry.
- **Finalization failure** (`CATALOG_FINALIZATION`): retry operation status endpoint; download is not repeated when side effects already completed.

## Troubleshooting

| Symptom | Check |
| --- | --- |
| Catalog empty / 502 | llama-admin health, manifest present in image |
| Quants 403 | HF token in server settings (never browser payload) |
| Fleet stuck pending | `GET /api/settings/llama/runtime/fleet-preset` applied vs desired |
| Load 409 on alias | Concurrent load/unload; wait for lock release |
| Migration pending count | Legacy `runtimeConfigJson` not yet minimal |

## Manifest authoring

- Schema: `docs/llama-router-preset-ui-execution/contracts/schema.llama.json`
- Live drift suite (requires HF token, Phase 8B): `python -m unittest discover -s docker/build/guideants-ai/llama-admin-service/tests -p test_live_manifest_drift.py`
