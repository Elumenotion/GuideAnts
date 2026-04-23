# Settings Page: Providers, Models, Local Llama Runtime

Last updated: 2026-04-21

## Purpose

Documents the Settings UI and the routing / readiness backend it runs on,
as shipped. Binding requirements live in
[`settings-and-llama-completion-requirements.md`](settings-and-llama-completion-requirements.md);
this document describes the implementation that satisfies them. For
behavior disputes the requirements doc wins.

Non-chat **service → provider** editing (the five routed services) is specified in
[`settings-service-provider-model-requirements.md`](settings-service-provider-model-requirements.md)
and implemented on the **Services** tab (`ServicesTab.tsx`, `ServiceEditorBase.tsx`).

## Information architecture (five top-level tabs)

Order matches `SettingsTabNavigation.tsx`:

1. **Overview**
2. **Services**
3. **Connections**
4. **Models & Runtime**
5. **Infrastructure**

Each tab has a single responsibility:

| Tab | Responsibility | Key components |
|-----|----------------|----------------|
| Overview | **Default Chat Model** (global catalog default + optional hard override), chat providers in use (assistant-referenced), all five non-chat services, with deep links to Connections, Services, and Models & Runtime (R-5.2, R-9). | `OverviewTab.tsx`, `GET /api/settings/overview`, `GET/PUT /api/settings/chat-defaults`, `GET /api/settings/services/{serviceId}` (readiness per service). |
| Services | Bespoke editors for Embeddings, Image Generation, Document Intelligence, Speech Transcription, Speech Synthesis; provider selection + fields + local model ops probe. | `ServicesTab.tsx`, `ServiceEditorBase.tsx`, `/api/settings/services/*`. |
| Connections | Provider credentials grouped by ownership, with "Used by services" chips (R-5.5, R-5.6). | `ConnectionsTab.tsx`, `GET /api/settings/connections/{section}/usage`. |
| Models & Runtime | Catalog + runtime profiles + local llama runtime workspace (R-6.\*), including per-model chat-target readiness badges in Catalog. | `ModelsRuntimeWorkspace.tsx`, `ModelsTab.tsx`, `LocalLlamaRuntimeTab.tsx`, `/api/settings/routing/chat-targets/{modelId}/readiness`. |
| Infrastructure | Runtime-owned dependency keys with source + probes (R-5.7). | `InfrastructureTab.tsx`, `InfrastructureProbeService`. |

Deep-links are one-way action-based references (e.g. Connections "Used by
services" chips navigate to Services or Models & Runtime with the relevant
target pre-selected). Each target tab owns its query-param deserialization so
link shapes can evolve without a global redirect table.

## Two-resolver routing model

Chat and non-chat services use the same `RoutingException` contract but
different resolution paths. This is the split that ended `ActiveProviderId`:

- `IChatModelResolver` (`src/server/GuideAntsApi/Services/Routing/IChatModelResolver.cs`) —
  runs **before** catalog lookup for chat turns: merges assistant/guide `modelId` with
  `ChatDefaults` (global default + optional override-all). See [default-chat-models.md](default-chat-models.md).
- `IChatTargetResolver` (`src/server/GuideAntsApi/Services/Routing/IChatTargetResolver.cs`) —
  resolves the `(catalog model, provider section)` pair for a single
  chat turn from the assistant's `modelId`. This is the R-1.6 seam where a
  future global-default-chat-model feature plugs in; no other code path
  should ever decide "what modelId is this turn using?".
- `IServiceModeResolver` (`src/server/GuideAntsApi.DataModel/Routing/IServiceModeResolver.cs`) —
  resolves the `ServiceMode` record (`{modeId, providerSection, modelId?,
  requestPresetJson, enabled, isDefault}`) for one of the five non-chat
  services, honoring a per-request `modeId` override or the service
  default. No fallback — a missing mode surfaces as
  `ROUTING_MODE_NOT_FOUND`.

Both resolvers feed `IChatTargetValidator` (for chat) or the
service-specific ports (for non-chat) which perform the
fail-fast R-3.\* validation and throw `RoutingException` on the first
unmet prerequisite.

Chat explicitly has no entry in `ServiceModes` (R-1.5). Legacy CRUD APIs for ad-hoc non-chat “modes” rows were removed; non-chat routing is driven by the service editor + `ServiceModes` storage described in the service-provider requirements doc.

## ProblemDetails contract

`RoutingException` is the single error type for every routing failure
across chat and non-chat services (R-2.1). Every throw site supplies an
`action` string (R-2.2); the factory methods on `RoutingException`
(`ModeNotFound`, `ProviderNotReady`, `ModelNotReady`, `RuntimeNotReady`)
make it impossible to construct one without.

Wire shape (`RoutingProblemDetailsFactory` + `RoutingExceptionHandler`):

```json
{
  "type": "https://guideants.app/problems/routing/routing-mode-not-found",
  "title": "Routing mode not found",
  "status": 400,
  "detail": "Service 'Embeddings' has no mode 'unknown-mode' configured.",
  "code": "ROUTING_MODE_NOT_FOUND",
  "action": "Configure the Embeddings service with a valid active route in Settings -> Services, or request a different mode id.",
  "service": "Embeddings",
  "modeId": "unknown-mode"
}
```

Status mapping per R-2.4 (`RoutingProblemDetailsFactory.MapStatus`):

| Code | HTTP status | Rationale |
|------|-------------|-----------|
| `ROUTING_MODE_NOT_FOUND` | 400 | Caller supplied an unknown modeId. |
| `ROUTING_PROVIDER_NOT_READY` | 409 | Configuration state: provider section incomplete. |
| `ROUTING_MODEL_NOT_READY` | 409 | Configuration state: catalog row missing / inactive / misconfigured. |
| `ROUTING_RUNTIME_NOT_READY` | 503 | Transient runtime unavailability (unloaded alias, missing artifact). |

Never 500. Client code distinguishes errors by the stable `code`
extension alone; `message` is for humans only (R-2.5).

The settings-llama endpoints (`/runtime/load`, `/runtime/unload`) emit an
inline 409 problem+json with `code=ROUTING_RUNTIME_NOT_READY` when the
`ILlamaRuntimeCoordinator` alias lock is already held — this is the
"busy" flavor of runtime-not-ready distinguished by its `action` field
("Wait for the in-flight operation on this alias to complete, then
retry."). See the llama runtime doc for lock semantics.

## Readiness aggregation

`IRoutingReadinessService` (`RoutingReadinessService`) computes readiness
for Overview and Models & Runtime readiness surfaces. Three entry points:

- `ProbeModeAsync(service, modeId)` — per-mode readiness for the five
  non-chat services; returns `ModeReadinessDto { status, blockers,
  providerSection, modelId, runtimeState }`.
- `ProbeChatTargetAsync(modelId)` — readiness snapshot for a single
  chat-target catalog row. This is explicitly a **snapshot** used by UI
  surfaces (the aggregated `GET /api/settings/overview` payload, `/chat-targets/preflight`,
  `/chat-targets/{modelId}/readiness`). It is NOT called from the chat
  dispatch path; doing so would break R-12.5 by failing chat with
  `ROUTING_RUNTIME_NOT_READY` for an alias that the notebook
  orchestration is actively loading. The test
  `ChatDispatch_DuringInFlightLoad_DoesNotFailWithRoutingRuntimeNotReady`
  pins that boundary.
- Overview aggregation happens in `GET /api/settings/overview` and
  composes per-service mode rollups + chat-target readiness +
  provider-connection issues + llama runtime snapshot into one payload.
  The **Overview tab UI** consumes only `chatTargets` and `providerIssues`
  from that payload (plus parallel `GET /api/settings/services/*` calls for
  the five non-chat services). Mode rollups, per-target blocker strings, and
  the llama runtime snapshot remain available to other consumers (tests,
  diagnostic endpoints, future surfaces) without changing the wire shape.

Readiness blockers are structured strings of the form
`"<KEY>:<detail>"`. The chat-target readiness endpoint promotes
`MODEL_MISSING` to a 404 problem+json and, with `?strict=true`, promotes
a blocked state to `ROUTING_MODEL_NOT_READY` 409 — both with stable
`code` + `action` fields.

Forward compatibility (R-9.2): `ChatTargetReadinessDto` carries a
`referenceKind` field that today is always `"direct"`. Values like
`"defaultedTo"` are reserved for the future global-default-chat-model
feature; the field is additive so adding a value is not a breaking
change.

## Connection usage endpoint

`GET /api/settings/connections/{section}/usage` (R-5.5) returns the list
of services that reference a provider section, derived from the service
mode table for the five non-chat services and from the chat-model
catalog for chat. The Connections tab renders these as
"Used by services" chips and deep-links to Services (for non-chat
services) or Models & Runtime → Catalog (for chat). Deleting a
connection with non-zero usage is blocked at the UI with a clear
explanation; the API rejects the same operation for an operator using
curl.

## Infrastructure source tracking + probes

Runtime-owned keys (`LocalServiceHosts:*`, `ServiceRouting:Containers:*:BaseUrl`,
`LlamaModelManagement:ModelStorePath`, `LlamaModelManagement:RouterModelsConfigPath`)
are catalogued by `RuntimeDependencySourceResolver` with a source
indicator (R-5.3):

| Source | Meaning |
|--------|---------|
| `appsettings` | Value comes from `appsettings.json` / `.Development.json`. |
| `env` | Environment variable overrides appsettings. |
| `compose` | Value is provided by the compose stack (detected by known env-variable prefixes). |
| `user-secrets` | .NET user-secrets provider. |
| `unknown` | Value is present but the source could not be determined. |

Reachability is probed by `InfrastructureProbeService` via
`POST /api/settings/infrastructure/probes` — POST-with-body so a batch of
dozens of items never pushes past browser / proxy URL length limits and
so future probe kinds (DNS, socket, path-exists) can carry their own
parameter shapes.

Diagnostics baked in today (R-5.7):

- `LlamaCpp:BaseUrl` prefix check.
- HTTP reachability probe for each declared base URL.
- Path-existence probe for `ModelStorePath` and
  `RouterModelsConfigPath`.

## Settings persistence + concurrency

All settings sections (including `ServiceModes`) flow through
`ApplicationSettingsService`'s row-versioned optimistic concurrency
path. Updates return 409 on row-version mismatch (R-4.1). Secrets flow
through `SettingsSecretsOptions` encryption; plaintext never appears in
responses and `secretHasValue` semantics are preserved (R-4.2, R-10.3).

`ActiveProviderId` retirement (R-4.4):

- The five non-chat service sections no longer declare `ActiveProviderId`
  in their schema.
- Production routing/bootstrap paths do not read
  `{Service}:ActiveProviderId` keys. Service mode state is explicit and
  managed through `ServiceModes`.
- `ServiceRoutingStartupValidator` no longer reads the key.

## Interaction principles

1. Organize by ownership first. Services own non-chat provider state;
   Connections own credentials; Infrastructure owns runtime-owned keys. A
   given setting has exactly one home.
2. Fail-fast on unresolved targets. No silent provider / model / mode
   substitution, ever. Unresolved targets always surface as a
   `RoutingException` → problem+json (R-1.7).
3. Snapshots vs dispatch. UI readiness probes sample the world and can
   legitimately report `loading` as a blocker; chat dispatch validates
   static shape and lets notebook orchestration handle the load
   lifecycle.
4. Additive evolution. New readiness values, new probe kinds, and new
   routing codes all extend existing structures; no breaking wire
   changes.

## Components delivered

Backend (`src/server/GuideAntsApi`):

- `Services/Routing/IChatTargetResolver.cs`, `IChatTargetValidator.cs`,
  `IRoutingReadinessService.cs`, `ILlamaRuntimeCoordinator.cs`,
  `RoutingProblemDetailsFactory.cs`, `ServiceModeResolver.cs`,
  `RoutingReadinessService.cs`.
- `Services/LlamaCpp/HuggingFaceModelDownloadService.cs`,
  `LlamaRuntimeInventoryService.cs`, `RouterModelsConfigService.cs`,
  `LlamaRuntimeAdminClient.cs`, `LlamaModelStorePathResolver.cs`.
- `Services/Infrastructure/InfrastructureProbeService.cs`.
- `Settings/RuntimeDependencySourceResolver.cs`,
  `Settings/ApplicationSettingsService.cs` (service editors, modes persistence,
  connection-usage).
- `Endpoints/SettingsEndpoints.cs` — one place for every settings HTTP
  route.
- `DataModel/Routing/RoutingException.cs`, `ServiceMode.cs`.

Client (`src/client/src/pages/settings`):

- `components/SettingsTabNavigation.tsx` (five-tab IA).
- `components/OverviewTab.tsx`, `ServicesTab.tsx`,
  `ConnectionsTab.tsx`, `ModelsRuntimeWorkspace.tsx`, `InfrastructureTab.tsx`,
  `LocalLlamaRuntimeTab.tsx`, `ModelsTab.tsx`.
- `types.ts` + `src/types/settings.ts` carry the API contract.
- `services/api.ts` wires each endpoint (including `settings.services` and
  `settings.localModels.listOutcome` for local model capability probes).

Deleted:
`ProviderSectionsTab.tsx`, `RuntimeDependenciesTab.tsx`,
`ServiceRoutingTab.tsx`, `ChatReadinessTab.tsx` were folded into newer tabs and removed to
avoid two components owning the same surface.

## What changed during implementation

- **Two resolvers, not one.** The proposal spoke of "service routing" as
  one system; the shipped design isolates chat (assistant-driven, R-1.1)
  from the five non-chat services (mode-driven, R-1.3) on purpose so the
  future global-default-chat-model feature has a clean seam.
- **Problem type URIs.** The proposal did not define them; the shipped
  factory prefixes every problem type with
  `https://guideants.app/problems/routing/<kebab-case-code>` so
  problem+json consumers can enumerate them without a translation table.
- **`referenceKind` field.** Added to `ChatTargetReadinessDto` for R-9.2;
  the proposal described the global-default seam only at the resolver
  level.
- **Runtime coordinator is per-alias, not global.** The proposal
  discussed "serialization of load operations"; the coordinator ships as
  a per-alias semaphore with a non-blocking `TryAcquireAliasLock` so the
  UI can render a deterministic 409 instead of spinning (R-6.10,
  R-12.4).
- **Infrastructure probes moved from GET to POST.** Originally sketched
  as a query-string fan-out; batched probe lists push past URL length
  limits quickly, so the shipping endpoint is
  `POST /api/settings/infrastructure/probes` with a body.
- **Runtime management boundary moved into `guideants-ai`.** Router-file
  mutation and model download/write operations now execute in the AI
  container via the internal `/llama-admin/*` surface. The web API
  delegates through `LlamaRuntimeAdminClient` and no longer requires
  direct model-storage access.
- **Free-text provider is gone.** The Catalog editor enforces a select
  populated from the provider registry (R-6.2); free-text is removed,
  not merely deprecated.
- **`ActiveProviderId` is a full retirement.** Routing resolves through
  `ServiceModes` / `IServiceModeResolver`; production routing/bootstrap
  paths do not read `{Service}:ActiveProviderId`.

## Add-model refactor decisions

- **Phase 0 decision (resource group):** Option B is in effect. For
  llama-cpp add flows, `resourceGroupKey` is required on the wire and the
  only accepted value is `local`. Any other value is rejected with
  `RESOURCE_GROUP_UNKNOWN`.
- **Add / Edit / Attach / Delete are split intentionally:**
  - **Add:** `POST /api/settings/models:add` is the only onboarding path.
    It supports cloud providers synchronously and llama-cpp in both async
    (`huggingface`) and sync (`existingAlias`) modes.
  - **Edit:** per-row catalog edit updates the existing model row only.
    Identity fields (`provider`, `modelId`) are immutable.
  - **Attach:** llama-cpp attach-existing-alias validates inventory +
    artifacts and adopts only orphaned aliases.
  - **Delete (catalog-only):** `DELETE /api/settings/models/{id}` removes
    only the catalog row, preserving the alias/files.
  - **Delete alias + files (cascade):**
    `DELETE /api/settings/llama/router/entries/{routerModelId}` removes the
    alias/files and then deletes bound catalog rows only when notebook
    reference count is zero.
