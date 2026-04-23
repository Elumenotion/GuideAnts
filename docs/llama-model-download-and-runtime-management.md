# Llama Model Download + Runtime Management

Last updated: 2026-04-22

## Purpose

Documents the local llama model lifecycle as it actually ships:
download/register from Hugging Face, inspect inventory, load/unload aliases,
and serialize contention so concurrent operations on a single alias can never
interleave.

Binding requirements live in
[`settings-and-llama-completion-requirements.md`](settings-and-llama-completion-requirements.md)
(R-6.\*, R-7.\*, R-8.\*, R-12.\*). This document describes the implementation
that satisfies them; for behavior disputes the requirements doc wins.

A `llama-cpp` catalog row may be selected as the **instance-wide default chat model**
in Settings → Overview (**Default Chat Model**). The same R-12 load-on-demand
orchestration applies: resolving a chat turn to that model still goes through
`NotebookModelRuntimeService` before validation. See [default-chat-models.md](default-chat-models.md).

## Architecture overview

The stack has eight backend collaborators split across the web API
control-plane and the `guideants-ai` runtime-plane. Each owns a single
responsibility and none of them silently substitute another provider, model,
or alias:

| Component | File | Role |
|----------|------|------|
| `ILlamaRuntimeCoordinator` | `src/server/GuideAntsApi/Services/Routing/ILlamaRuntimeCoordinator.cs` | Per-alias semaphore that serializes load / unload mutations (R-12.10). |
| `NotebookModelRuntimeService` | `src/server/GuideAntsApi/Services/LlamaCpp/NotebookModelRuntimeService.cs` | Notebook-scoped orchestration of the load lifecycle (`queued → unloading → loading → verifying → ready | failed`). |
| `ILlamaRuntimeAdminClient` | `src/server/GuideAntsApi/Services/LlamaCpp/LlamaRuntimeAdminClient.cs` | API-side HTTP adapter for `guideants-ai` admin operations (`/llama-admin/router/*`, `/llama-admin/downloads/*`). |
| `ILlamaRouterIniSyncService` | `src/server/GuideAntsApi/Services/LlamaCpp/LlamaRouterIniSyncService.cs` | Syncs per-alias router knobs from catalog `LocalRuntimeJson` into `router-models.ini` after llama-cpp model create/update. |
| `ILlamaRuntimeInventoryService` | `src/server/GuideAntsApi/Services/LlamaCpp/LlamaRuntimeInventoryService.cs` | Merges admin-reported router + artifact state, llama server runtime state, and catalog linkage into a single DTO list. |
| `IRouterModelsConfigService` | `src/server/GuideAntsApi/Services/LlamaCpp/RouterModelsConfigService.cs` | Delegates router entry reads/writes to the admin service; no direct filesystem mutation in the API process. |
| `IHuggingFaceModelDownloadService` | `src/server/GuideAntsApi/Services/LlamaCpp/HuggingFaceModelDownloadService.cs` | Resolves HF token precedence (R-7.4), then delegates download + registration work to the admin service. |
| `llama-admin service` | `docker/build/guideants-ai/llama-admin-service/llama_admin_service.py` | Runtime-owned model download, artifact checks, and atomic updates to the router preset file (`/models-local/router-models.ini` on the `ai_local_models` volume; `GA_LLAMA_MODELS_PRESET` in compose). |

Options:

```5:11:src/server/GuideAntsApi/Configuration/LlamaModelManagementOptions.cs
public sealed class LlamaModelManagementOptions
{
    public string ModelStorePath { get; set; } = "./models";
    public string RouterModelsConfigPath { get; set; } = "./docker/llama/router-models.ini";
    public string? HfToken { get; set; }
    public bool AllowOverwrite { get; set; }
}
```

The default `RouterModelsConfigPath` string is a **convenience for local
diagnostics** (aligned with the repo template). It is not the path
`llama-admin` writes at runtime in Docker; that path is
`GA_LLAMA_MODELS_PRESET` inside `guideants-ai`.

These bind to the `LlamaModelManagement` section (see `appsettings.json`) and
satisfy R-7.3. In the current architecture, `AllowOverwrite` and `HfToken`
are consumed directly by the API download adapter, while
`ModelStorePath` / `RouterModelsConfigPath` are **operator diagnostic**
paths in Settings (R-6.12, R-5.7): they do not need to match the in-container
path of the live router file. The running `guideants-ai` container resolves
`GA_LLAMA_MODELS_PRESET` (default `/models-local/router-models.ini`); the
web API never opens that file directly — it calls llama-admin over HTTP.

## `ILlamaRuntimeCoordinator` — per-alias lock semantics

Every caller that mutates a router alias — settings-UI load / unload,
notebook-scoped `NotebookModelRuntimeService.LoadAsync`, the HF download
finalizer — goes through the coordinator. Two operating modes:

- `AcquireAliasLockAsync(alias, ct)` — awaits the alias-scoped
  `SemaphoreSlim` and returns an `IAsyncDisposable` releaser. Used by
  background orchestration (notebook load) where "wait your turn" is the
  right behavior.
- `TryAcquireAliasLock(alias)` — non-blocking; returns `null` if another
  caller holds the lock. Used by the settings HTTP endpoints
  (`POST /api/settings/llama/runtime/load` and `.../unload`) so a second
  click returns a deterministic `409 application/problem+json` with
  `code=ROUTING_RUNTIME_NOT_READY` instead of stalling behind the in-flight
  request (R-6.10 + R-12.4).
- `IsAliasLocked(alias)` — pure diagnostic read. The status endpoint
  (`GET /api/settings/llama/runtime/status`) samples this without mutating
  state so the UI can show an `InProgress` pill without blocking anyone.

There is no global "runtime lock" — contention is per-alias so different
aliases load in parallel. `RuntimeConcurrencyTests.Load_DifferentAliases_RunInParallel`
is the binding proof.

## `NotebookModelRuntimeService` — the load lifecycle

This service is the authority on "what models does this chat need, and are
they loaded?" (R-12.1). It is unchanged from the pre-refactor behavior and
remains the single path chat uses for on-demand load-before-turn:

1. Resolves required router aliases from the notebook's Guide → crew →
   optional assistant selection.
2. For each alias, examines llama server state and transitions through
   `queued → unloading → loading → verifying → ready | failed`.
3. Serializes load operations per notebook (in-process lock) **and** per
   alias (coordinator lock). The alias lock is what prevents the settings
   UI from tearing down a model that a notebook is currently loading
   (R-12.10).
4. Emits `ModelLoadOperationDto` for polling at
   `GET /api/notebooks/{id}/llama-runtime/operations/{opId}`.

The new validator (`IChatTargetValidator`) does **not** re-derive required
model sets from catalog alone. It validates a single `modelId` per turn
and defers set-level readiness to this orchestration (R-3.1, R-12.5,
R-12.6). A transient unloaded-while-loading state therefore never trips
`ROUTING_RUNTIME_NOT_READY` spuriously during chat dispatch.

Readiness probing via `IRoutingReadinessService.ProbeChatTargetAsync` is a
snapshot used by the settings / overview UI only; it can legitimately report
`loading` as a blocker because a UI snapshot is not a dispatch decision. The
regression test
[`ChatDispatch_DuringInFlightLoad_DoesNotFailWithRoutingRuntimeNotReady`](../src/server/GuideAntsApi.IntegrationTests/Services/LlamaCpp/RuntimeConcurrencyTests.cs)
pins this boundary.

## Runtime ownership boundary (`llama-admin`)

Model artifacts and router-file mutation are now runtime-owned operations:

- `guideants-ai` is authoritative for `/models-local/llama` (the llama subtree of the single `ai_local_models` named volume) and the router preset at **`/models-local/router-models.ini`** on that volume (same filesystem as GGUFs; not a host bind). On first boot of an empty volume, `entrypoint.sh` may seed that file from `/opt/seed/router-models.ini` in the image.
- `guideants-webapi-ui` consumes those capabilities through the internal
  `llama-admin` surface and does not need model-volume access.
- `ILlamaRuntimeInventoryService` reads `hasModelFile` / `hasMmprojFile`
  from admin-service DTOs instead of host-side `File.Exists` probes.

`LlamaModelStorePathResolver` remains in the codebase as a utility for older
call paths, but it is no longer authoritative for runtime inventory or
download registration.

`ModelStorePath` and `RouterModelsConfigPath` surface through the settings
UI per R-6.12:

- Models & Runtime → Local Llama Runtime tab shows the effective values on
  the header so operators can verify path resolution without opening
  appsettings.
- Infrastructure tab lists both keys in the dependency catalog with their
  source (`appsettings` / `env` / `compose`) and a reachability / existence
  probe.

## `RouterModelsConfigService`

Delegates router registration operations to `guideants-ai` admin endpoints:

- `GetEntriesAsync` calls `GET /llama-admin/router/entries`.
- `AddOrUpdateEntryAsync(alias, modelContainerPath, mmprojContainerPath)`
  calls `POST /llama-admin/router/entries`.

Atomic file updates and file-scoped locking (R-7.6) are enforced in
`llama_admin_service.py` where the writable router file actually lives.

The router mapping preview in Models & Runtime → Local Llama Runtime →
Router Mapping is fed from `GetEntriesAsync`. Duplicate aliases and
references to missing artifacts are flagged at render time from the
inventory service's view.

`GetEntriesAsync` now also carries optional per-alias tuning values surfaced by
the runtime admin service:

- `ContextSize` (router key family `c` / `ctx-size` / `ctx_size`)
- `CacheRamMib` (router key family `LLAMA_ARG_CACHE_RAM` / `cache-ram` / `cache_ram`)

These values are diagnostic/read-model data from the live router preset and are
included in runtime inventory responses for operator visibility.

## Per-alias router knobs (`routerContextSize`, `routerCacheRamMib`)

`llama-cpp` catalog models can now persist two optional alias-scoped runtime
knobs:

- `routerContextSize` — positive integer (1024..1,048,576); written to router
  preset key `c` for llama-server.
- `routerCacheRamMib` — non-negative integer (0..262,144); written to
  `LLAMA_ARG_CACHE_RAM`.

Persistence / flow:

1. Wizard/edit form captures the values under guided local-runtime config.
2. API stores them in `LocalRuntimeJson` (`LocalRuntimeConfiguration`) and, for
   HF installs, in `LlamaCatalogDownloadIntent` so async completion can register
   with the same settings.
3. On llama-cpp model create/update, `ApplicationSettingsService` invokes
   `ILlamaRouterIniSyncService` to reconcile the matching alias in
   `router-models.ini` (when alias paths already exist).

Sync semantics are explicit:

- Missing fields on router-entry upsert are treated as "preserve existing key".
- Explicit `null` clears the key (reverts to container defaults such as
  `GA_LLAMA_CTX_SIZE`).
- Non-null integers set/replace the key.

## `HuggingFaceModelDownloadService`

Accepts a `StartModelDownloadRequest { Repository, QuantIncludePattern,
MmprojIncludePattern, RouterModelId, TargetDirectory, HfTokenOverride?, CatalogRouterContextSize?, CatalogRouterCacheRamMib? }`,
returns a `ModelDownloadOperationDto` with an `operationId`, and delegates
transfer execution to `guideants-ai` (`POST /llama-admin/downloads`).
Key behaviors:

- **Token resolution** (R-7.4). Precedence: per-request
  `HfTokenOverride` → `LlamaCpp:HfToken` (DB-backed provider secret) →
  `HF_TOKEN` environment variable → `LlamaModelManagement:HfToken` in
  appsettings. `HF_TOKEN` is intentionally a host-only escape hatch for the
  shell-driven scripts under `docker/llama/run/*.ps1`; it deliberately does
  **not** appear under a managed section in `appsettings.json` and is
  whitelisted as a runtime-only environment key in
  `ComposeEnvironmentContractTests`.
- **Runtime-owned filesystem semantics** (R-7.5, R-7.6). Non-destructive
  writes, per-alias download serialization, and atomic router registration
  are implemented by the admin service inside `guideants-ai`.
- **Operation state machine.** `queued → resolvingFiles → downloading →
  registeringAlias → registeringCatalog → completed | failed`, produced by the admin service and
  polled via
  `GET /api/settings/llama/downloads/{operationId}`.

## HTTP surface

All under `/api/settings/llama`:

| Method | Route | Purpose |
|--------|-------|---------|
| `GET`  | `/runtime/inventory` | Merged inventory rows (R-7.1), including optional router `contextSize` / `cacheRamMib` when present in live router config. |
| `POST` | `/runtime/load` | Load an alias. 409 `ROUTING_RUNTIME_NOT_READY` if the alias lock is held. |
| `POST` | `/runtime/unload` | Unload an alias. Same 409 contention behavior as load. |
| `GET`  | `/runtime/status` | Non-blocking diagnostic snapshot: `{alias, loaded, inProgress, runtimeState}` per router entry. |
| `POST` | `/downloads` | Legacy direct download route (kept for internal compatibility). Primary onboarding route is `POST /api/settings/models:add`. |
| `GET`  | `/downloads/{operationId}` | Poll download status. |
| `GET`  | `/huggingface/repositories/{owner}/{repo}/files` | Wizard helper. Lists files in a public or gated HF repo (token injected server-side from the `HuggingFace:Token` application setting; never returned to the browser), classifying each as `gguf` / `mmproj` / `other` with size, quant label, and shard detection. Structured error codes: `REPO_INVALID`, `REPO_NOT_FOUND`, `REPO_TOKEN_MISSING`, `REPO_TOKEN_INSUFFICIENT`, `HF_UPSTREAM`. |

Notebook-scoped orchestration endpoints (`GET /api/notebooks/{id}/llama-runtime`,
`POST /api/notebooks/{id}/llama-runtime/load`, operation polling) are
unchanged; R-10.1 / R-12.8 explicitly forbid DTO breakage and the test
suite pins that contract.

Internal runtime-only admin routes (consumed by the web API, not user-facing):

| Method | Route | Purpose |
|--------|-------|---------|
| `GET` | `/llama-admin/health` | Runtime admin health and resolved model/router paths. |
| `GET` | `/llama-admin/router/entries` | Router entries with artifact existence as seen by `guideants-ai`. |
| `POST` | `/llama-admin/router/entries` | Atomic alias upsert into the router preset file (`GA_LLAMA_MODELS_PRESET`, default `/models-local/router-models.ini`), with optional `contextSize` / `cacheRamMib` reconciliation. |
| `POST` | `/llama-admin/downloads` | Start runtime-owned HF download + registration workflow. |
| `GET` | `/llama-admin/downloads/{operationId}` | Poll runtime-owned download operation state. |

## UI integration (Models & Runtime)

`Models & Runtime` is a composite workspace with three sub-tabs (R-6.1):

- **Catalog** — constrained-provider select, readiness badge per llama-cpp
  row (R-6.4), provider-specific wizard/edit shape.
- **Runtime Profiles** — list-first tab with an `Add Profile` dialog,
  row-level edit/export actions, top-level import, and templates for
  `qwen3_5 / qwen3_6 / gemma4` (R-6.6, R-6.7).
- **Local Llama Runtime** — pure operations:
  - **Runtime Inventory.** One row per router alias with runtime state,
    GGUF / mmproj presence, bound catalog model ids, and `Load / Unload /
    Delete alias + files` actions.
  - **Router Mapping.** Preview of the effective alias-to-path
    registration, with duplicate and missing-artifact flags.

New model onboarding path:

- **Catalog → Add Model wizard** (`provider=llama-cpp`):
  - Source `Install from Hugging Face` routes through
    `POST /api/settings/models:add` and returns an async operation id.
  - Source `Attach existing alias` routes through the same endpoint and
    returns a synchronous catalog row create when the alias is orphaned and
    artifacts are present.
  - Async status is polled from `GET /api/settings/llama/downloads/{operationId}`
    with the state machine segment `queued → resolvingFiles → downloading → registeringAlias → registeringCatalog → completed | failed`.

The `ModelStorePath` and `RouterModelsConfigPath` appear in the tab header
and also in Infrastructure → Runtime Dependencies (R-6.12 / R-5.7).

## Concurrency contract — R-12 tests

The per-alias serialization guarantees are pinned by
[`RuntimeConcurrencyTests`](../src/server/GuideAntsApi.IntegrationTests/Services/LlamaCpp/RuntimeConcurrencyTests.cs):

| Test | R-# pinned |
|------|------------|
| `Load_SameAlias_Concurrent_SecondCallReturns409RuntimeBusy` | R-12.1, R-12.4 |
| `Unload_WhileLoadInFlight_SameAlias_Returns409` | R-12.10 |
| `Status_DuringGatedLoad_IsNonBlocking_AndReportsInProgress` | R-6.10 |
| `Load_DifferentAliases_RunInParallel` | R-12.1 (cross-alias) |
| `ChatTargetReadiness_DuringInFlightLoad_ReportsLoadingStateBlocker` | readiness-snapshot boundary |
| `ChatDispatch_DuringInFlightLoad_DoesNotFailWithRoutingRuntimeNotReady` | R-12.5 |

The readiness-snapshot vs chat-dispatch split is the critical one: any
future change that tries to silently wait inside the readiness probe, or
fails chat dispatch just because a load is mid-flight, will be caught by
those last two tests.

## Qwen3.6 onboarding (worked example)

End-to-end:

1. **Runtime profile** — Models & Runtime → Runtime Profiles → `Add
   Profile` → `Insert qwen3_6` writes profile id `qwen3_6` with the
   sampling / thinking-control preset. Idempotent; no overwrite (R-8.1).
2. **Catalog model** — Models & Runtime → Catalog → `Insert Qwen3.6 model`
   writes `qwen3.6-35b-a3b-local` bound to router alias
   `Qwen3.6-35B-A3B-UD-Q4_K_XL` and profile `qwen3_6`. Idempotent (R-8.2).
3. **Download** — Catalog → Add Model wizard (`provider=llama-cpp`, source
   `Install from Hugging Face`) with the
   defaults (`repo=unsloth/Qwen3.6-35B-A3B-GGUF`, quant pattern
   `*UD-Q4_K_XL*`, mmproj pattern `mmproj*F16*`). Completion yields a
   Runtime Inventory row with `hasModelFile=true`, `hasMmprojFile=true`,
   `runtimeState=unloaded` (R-8.3).
4. **Load** — Local Llama Runtime → Runtime Inventory → Load; UI observes
   `loading → loaded` (R-8.4).
5. **Readiness** — notebook preflight for an assistant bound to
   `qwen3.6-35b-a3b-local` reports `ready` (R-8.5).
6. **Unload-then-chat** — unloading the alias and then invoking chat pinned
   to the same assistant fails with `ROUTING_RUNTIME_NOT_READY` and
   **never** substitutes another provider or model (R-8.6).

## What changed during implementation

- **Error contract.** The proposal described `ROUTING_*` codes informally;
  they ship as `RoutingErrorCodes` constants on a single `RoutingException`
  type with a required `action` remediation field (R-2.2) and a global
  `RoutingExceptionHandler` that maps them to RFC 7807 problem+json
  responses with the R-2.4 status codes (400 / 409 / 409 / 503; never 500).
- **Runtime Inventory is its own service.** The proposal folded inventory
  into "Settings llama management endpoints"; in the shipped code
  `ILlamaRuntimeInventoryService` is a standalone collaborator so the
  settings endpoints, overview endpoint, and chat readiness probe all
  share one computation of "what is on disk, what does the llama server
  report, what does the catalog bind?".
- **Storage ownership moved to `guideants-ai`.** Router mutation and model
  download work now execute inside `guideants-ai` through the internal
  `llama-admin` service. The web API delegates over HTTP and no longer
  requires direct model-volume access.
- **Router preset on the model volume.** The live router mapping file lives at
  `/models-local/router-models.ini` on `ai_local_models`, not as a bind-mounted
  repo file. That avoids host filesystem quirks (for example atomic replace on
  Docker Desktop for Windows) and keeps a single source of truth updated by the
  Settings UI flow.
- **`ActiveProviderId` retirement.** The five non-chat service sections no
  longer carry `ActiveProviderId`; routing runs through `ServiceModes`
  resolved by `IServiceModeResolver`. Production routing/bootstrap paths do
  not read `{Service}:ActiveProviderId`.
- **Chat has no mode matrix.** Chat routing stays assistant-driven
  (R-1.5); the `ServiceModes` registry has no entry for chat.
