# Settings Architecture and Extension Guide

Last updated: 2026-05-25

This document is the architecture reference for the Settings subsystem.

It describes the full path from UI surfaces (Settings page and Home onboarding wizard), through HTTP/API and server domain services, into SQL persistence, and back into runtime configuration consumption.

## 0) Database-first summary (read this first)

Settings is database-backed and database-primary.

- Every settings write from UI or wizard persists to SQL first.
- Runtime configuration is projected from SQL-backed rows (not directly from UI memory).
- Startup loads DB-backed settings into `IConfiguration` via the custom provider.

Primary SQL stores for Settings:

- `ApplicationSettings` table (row-per-section JSON payload; includes `ChatDefaults`, provider sections, `ServiceModes`, telemetry, runtime overrides)
- `Models` table (chat model catalog)
- `RuntimeProfiles` table (parameter/thinking profile contracts)

Critical consequence:

- If it is not in SQL, it is not authoritative Settings state.

## 1) What Settings is (and is not)

Settings is a configuration/control plane for GuideAnts.

Settings is responsible for:

- Connection/provider configuration (keys, endpoints, tokens)
- Chat defaults (default model, global override toggle, default parameter bag)
- Model catalog registration and lifecycle
- Runtime profile registration and lifecycle
- Non-chat service provider/mode selection and provider field editing
- Runtime dependency visibility and override controls
- Readiness and usage visibility

Settings is not:

- A separate execution engine
- A parallel runtime config system for the wizard
- A place where chat runtime policy should be improvised outside resolved policy contracts

## 2) Top-level architecture

## 2.1 Layers

1. Client surfaces
- Full Settings UI: `src/client/src/pages/Settings.tsx`
- Home onboarding wizard: `src/client/src/components/home/AddAiServicesWizard.tsx`

2. Client transport
- Typed API wrapper: `src/client/src/services/api.ts` (`api.settings.*`)

3. HTTP endpoints
- Minimal API mapping: `src/server/GuideAntsApi/Endpoints/Settings/`

4. Settings domain service
- Application settings domain logic: `src/server/GuideAntsApi/Settings/ApplicationSettingsService*.cs`

5. Section/schema contract
- Section/property registry: `src/server/GuideAntsApi/Settings/SettingsSectionRegistry.cs`

6. Persistence
- Section JSON rows: `src/server/GuideAntsApi.DataModel/Models/ApplicationSetting.cs`
- Model catalog: `src/server/GuideAntsApi.DataModel/Models/Model.cs`
- Runtime profiles: `src/server/GuideAntsApi.DataModel/Models/RuntimeProfile.cs`
- Assistant model refs: `src/server/GuideAntsApi.DataModel/Models/Assistant.cs`

7. Runtime projection
- DB-backed configuration provider: `src/server/GuideAntsApi/Settings/ApplicationSettingsConfigurationProvider.cs`
- Startup registration/bootstrap: `src/server/GuideAntsApi/Program.cs`

8. Runtime consumers
- Chat routing model policy: `src/server/GuideAntsApi/Services/Routing/ChatModelResolver.cs`
- Conversation runtime path: `src/server/GuideAntsApi/Services/Conversations/ConversationService.cs`

## 2.2 Canonical principle

There is one write plane for settings state: `/api/settings/*`.

Both UI surfaces (Settings page and Home wizard) call the same backend endpoints, writing the same data model.

## 2.3 End-to-end DB-centric flow

```text
Settings UI / Home Wizard
  -> /api/settings/* endpoints
    -> ApplicationSettingsService* domain methods
      -> SQL write/read (ApplicationSettings, Models, RuntimeProfiles)
        -> ReloadConfiguration (after successful mutating writes)
          -> ApplicationSettingsConfigurationProvider projection into IConfiguration
            -> Runtime consumers (routing, conversation execution, service components)
```

## 3) UI surface architecture

## 3.1 Full Settings page

Entry: `src/client/src/pages/Settings.tsx`

Responsibilities:

- Owns tab state and deep-linking (`overview`, `connections`, `models-runtime`, `services`, `infrastructure`, `telemetry`, `personalization`)
- Owns top-level loading and mutation state for models/profiles/inventory/confirmation flows
- Composes tab components and passes callbacks/refresh hooks

Primary tab components:

- Navigation: `src/client/src/pages/settings/components/SettingsTabNavigation.tsx`
- Overview: `src/client/src/pages/settings/components/OverviewTab.tsx`
- Connections: `src/client/src/pages/settings/components/ConnectionsTab.tsx`
- Models & Runtime workspace: `src/client/src/pages/settings/components/ModelsRuntimeWorkspace.tsx`
- Services: `src/client/src/pages/settings/components/ServicesTab.tsx`
- Infrastructure: `src/client/src/pages/settings/components/InfrastructureTab.tsx`
- Telemetry: `src/client/src/pages/settings/components/TelemetryTab.tsx`

Settings page data API is exclusively `api.settings.*` from `src/client/src/services/api.ts`.

## 3.2 Home Add AI Services wizard

Entry: `src/client/src/components/home/AddAiServicesWizard.tsx`

Responsibilities:

- Provider onboarding orchestration (`foundry`, `openai`, `google-gemini`, `local-ai`)
- Model-add/install orchestration including local model onboarding and operation polling
- Optional service configuration paths

Important detail:

- Wizard writes through the same `/api/settings/*` endpoints as full Settings
- Local AI flow state is isolated in: `src/client/src/components/home/addAiServicesWizard/useLocalAiWizardState.ts`

No wizard-only backend config path exists.

## 3.3 Client-to-endpoint mapping (high-value)

Client wrapper: `src/client/src/services/api.ts`.

Key groups:

- Sections/schema/readiness
  - `/settings/sections`
  - `/settings/schema`
  - `/settings/readiness`

- Chat defaults
  - `/settings/chat-defaults`

- Models + onboarding
  - `/settings/models`
  - `/settings/models:add`

- Runtime profiles
  - `/settings/runtime-profiles`

- Service editor
  - `/settings/services/{serviceId}`
  - `/settings/services/{serviceId}/active-provider`
  - `/settings/services/{serviceId}/providers/{providerId}`
  - local-model subroutes

- Routing/readiness probes
  - `/settings/routing/chat-targets*`
  - `/settings/routing/preflight`

- Connections usage
  - `/settings/connections/{section}/usage`

- Infrastructure
  - `/settings/infrastructure/dependencies`
  - `/settings/infrastructure/probes`

- Llama runtime
  - `/settings/llama/runtime/*`
  - `/settings/llama/downloads/*`

## 4) API and endpoint architecture

Endpoint registration is orchestrated from
`src/server/GuideAntsApi/Endpoints/Settings/SettingsEndpoints.cs`, which
delegates to domain-aligned modules under `Endpoints/Settings/`:

| Module | Routes |
|--------|--------|
| `SettingsCoreEndpoints.cs` | sections, schema, readiness, chat-defaults, embeddings rebuild |
| `SettingsModelsEndpoints.cs` | model catalog CRUD and `models:add` onboarding |
| `SettingsRuntimeProfilesEndpoints.cs` | runtime profile CRUD |
| `SettingsServiceEditorEndpoints.cs` | service editor state and provider fields |
| `SettingsServiceLocalModelsEndpoints.cs` | local-models proxy (download/load/unload/select) |
| `SettingsRoutingEndpoints.cs` | chat-target routing and readiness probes |
| `SettingsOverviewEndpoints.cs` | settings overview composite |
| `SettingsInfrastructureEndpoints.cs` | connections usage, infrastructure dependencies and probes |
| `SettingsLlamaEndpoints.cs` | llama runtime inventory/load/unload, downloads, router delete |
| `SettingsHuggingFaceEndpoints.cs` | Hugging Face repository file browse |

Shared non-route helpers live alongside the modules (`SettingsChatDefaultsMapper`,
`SettingsModelOnboardingSupport`, `SettingsLlamaRouterDeleteHandler`,
`SettingsRoutingProbeSupport`, `ServiceLocalModelDownloadValidator`,
`SettingsGroupFactory`). Existing cross-cutting helpers remain at
`Endpoints/LocalServiceAdminRouting.cs` and `Endpoints/HuggingFaceBrowseHandler.cs`.

Transport groups (unchanged URL prefixes):

- Core settings group: `/api/settings`
- Services editor subgroup: `/api/settings/services`
- Routing subgroup: `/api/settings/routing`
- Llama subgroup: `/api/settings/llama`
- Hugging Face utility subgroup: `/api/settings/huggingface`

Endpoint layer responsibilities are deliberately narrow:

- Parse request DTOs
- Convert transport result codes (200/201/202/400/404/409)
- Delegate to domain services
- Keep business rules centralized in domain services

## 5) Settings domain service architecture

Primary interface and implementation:

- Interface/constructor surface: `src/server/GuideAntsApi/Settings/ApplicationSettingsService.cs`
- Behavior split by concern in partial files:
  - `ApplicationSettingsService.Sections.cs`
  - `ApplicationSettingsService.Models.cs`
  - `ApplicationSettingsService.RuntimeProfiles.cs`
  - `ApplicationSettingsService.ServiceEditors.cs`
  - `ApplicationSettingsService.ServiceModes.cs`
  - `ApplicationSettingsService.Readiness.cs`
  - `ApplicationSettingsService.RuntimeDependencies.cs`
  - `ApplicationSettingsService.Contracts.cs`

## 5.1 Section CRUD pipeline

Section read/write methods in `ApplicationSettingsService.Sections.cs`:

- `GetSectionSummariesAsync`
- `GetSectionAsync`
- `UpdateSectionAsync`
- `GetSchemaAsync`

Update pipeline (`UpdateSectionAsync`) is architecture-critical:

1. Resolve section definition via `SettingsSectionRegistry`
2. Load row by `SectionName` from `ApplicationSettings`
3. Enforce optimistic concurrency via `RowVersion`
4. Decrypt current payload
5. Reject unsupported fields for section contract
6. Merge update payload with current payload (field-level merge semantics)
7. Validate:
- type/default validation from section definition
- section-specific validation hooks (for example `ChatDefaults` reasoning/model compatibility)
8. Encrypt secrets in outgoing payload
9. Persist row + schema version + updated timestamp
10. Reload runtime configuration

## 5.2 Model catalog domain

`ApplicationSettingsService.Models.cs`:

- `GetModelsAsync`, `CreateModelAsync`, `UpdateModelAsync`, `DeleteModelAsync`, `GetChatTargetsAsync`

Important behaviors:

- Normalization and validation of `ReasoningChoicesJson`
- Normalization/validation of `RuntimeConfigJson`
- Provider-aware reasoning choice validation
- Post-persist llama router sync when applicable

## 5.3 Runtime profile domain

`ApplicationSettingsService.RuntimeProfiles.cs`:

- CRUD for runtime profiles
- JSON validation for `SamplingParametersJson` and `ThinkingControlJson`
- Provider filtering metadata (`Providers` list)

## 5.4 Service editor and service mode domain

`ApplicationSettingsService.ServiceEditors.cs` + `ApplicationSettingsService.ServiceModes.cs` + `ApplicationSettingsService.Contracts.cs`:

- Service contracts define providers, required section fields, and required runtime keys
- Service editor state is composed from:
  - active/default mode state
  - provider section readiness
  - runtime dependency readiness
  - provider field metadata and values
- Active provider and provider fields flow through validated updates

## 5.5 Readiness and usage composition

`ApplicationSettingsService.Readiness.cs`:

- Global/readiness rollups
- Provider section readiness (`GetProviderSectionReadinessAsync`)
- Connection usage projection (`GetConnectionUsageAsync`)

This powers:

- Overview readiness cards
- Connections "used by" chips
- Preflight and chat-target readiness endpoints

## 5.6 Runtime dependency domain

`ApplicationSettingsService.RuntimeDependencies.cs`:

- Runtime dependency catalog (key list + read-only flags + provider usage)
- Dependency classification (`url`, `path`, `other`)
- Safe override updates with URL validation rules
- Config reload on writes

## 6) Section registry and schema contract

Registry: `src/server/GuideAntsApi/Settings/SettingsSectionRegistry.cs`.

The registry is the contract authority for:

- Which sections exist
- Which fields exist in each section
- Canonical configuration key mapping
- Field types (`string`, `int`, `bool`)
- Secret designation
- default values

Notable sections:

- `ChatDefaults`
  - `DefaultModelId`
  - `OverrideAllChatModels`
  - `Temperature`
  - `TopP`
  - `ReasoningEffort`
  - `SamplingParametersJson`

- `ServiceModes`
- Provider connection sections (`OpenAI`, `AzureOpenAI`, `GoogleGeminiApi`, etc.)
- Runtime host sections (`LlamaCpp`, `LocalServiceHosts`)
- Telemetry

## 7) Persistence architecture

## 7.1 `ApplicationSettings` table (section JSON)

Entity: `ApplicationSetting`.

Columns:

- `SectionName` (PK)
- `JsonValue` (section payload JSON)
- `SchemaVersion`
- `CreatedUtc`
- `UpdatedUtc`
- `RowVersion` (concurrency token)

Use cases:

- Settings sections and runtime overrides are stored as row-per-section JSON payloads
- Section schema evolution managed through `SchemaVersion` and bootstrap merge behavior

This table is the core Settings persistence layer. Most Settings UI edits end up here.

## 7.2 `Models` catalog table

Entity: `Model`.

Stores chat target catalog rows and metadata:

- `ModelId` PK
- `Provider`, `DisplayName`, `Description`
- `RuntimeConfigJson`
- `ReasoningChoicesJson`
- `IsActive`, `DisplayOrder`

This table is the authoritative source for selectable chat models in Settings and routing.

## 7.3 `RuntimeProfiles` table

Entity: `RuntimeProfile`.

Stores model-family request shaping contract and UI exposure metadata:

- `SamplingParametersJson`
- `ThinkingControlJson`
- `ProvidersJson`
- message normalization fields

This table is the authoritative source for model parameter/thinking profile definitions.

## 7.4 Assistant model reference fields

Entity: `Assistant`.

- `ModelId` references model catalog row or null ("Use default")
- Legacy typed fields remain on entity (`Temperature`, `TopP`, `ReasoningEffort`)
- Structured bag field `SamplingParametersJson` exists

## 8) Runtime projection architecture

## 8.1 DB-backed configuration provider

`ApplicationSettingsConfigurationProvider` is registered in `Program.cs` and becomes part of `IConfiguration`.

Behavior:

- Reads all rows from `ApplicationSettings`
- Decrypts secret fields using registry metadata
- Flattens section JSON into canonical `Section:Field` keys
- Rejects unknown sections (prevents unregistered DB rows from silently changing runtime config)
- Throws startup-fatal errors on load/decrypt failures

This is the bridge that makes SQL-backed Settings values become live runtime config.

## 8.2 Bootstrap and reload lifecycle

In `Program.cs`:

1. Add DB-backed settings source to configuration builder
2. Build app
3. Call `IApplicationSettingsService.BootstrapAsync(...)`
4. Call `ReloadConfiguration()` to activate DB-primary values
5. Run seeders (required guides/assistants, runtime profiles, local service auto-selector)

Every successful settings write path that changes config calls reload.

## 9) Chat runtime integration from Settings

## 9.1 Resolver contract

`ChatModelResolver` reads from `IConfiguration` (including DB-backed `ChatDefaults`) and returns:

- `ModelId`
- `ReferenceKind` (`Direct`, `DefaultedTo`, `OverriddenToDefault`)
- `ResolvedExecutionPolicy` (`Authority`, resolved parameter bag, provider)

Authority is binary:

- `GlobalOverride`
- `AssistantDefinition`

Parameter bag source:

- `GlobalOverride`: `ChatDefaults` bag
- `AssistantDefinition` + explicit model: model-defined path
- `AssistantDefinition` + "Use default": default-model bag from `ChatDefaults`

`ReferenceKind` is provenance metadata; runtime policy authority comes from `ExecutionPolicy`.

## 9.2 Conversation path

`ConversationService` resolves model/policy via `IChatModelResolver` and passes `ExecutionPolicy` to runtime execution path.

This is where settings decisions become runtime behavior.

## 10) Detailed write/read sequences

## 10.1 Section update sequence

```text
Settings UI/Wizard
  -> PUT /api/settings/sections/{section}
     -> SettingsEndpoints (orchestrator) / Settings*Endpoints modules
        -> ApplicationSettingsService.UpdateSectionAsync
           -> SettingsSectionRegistry (contract)
           -> ApplicationSettings row load + rowVersion check
           -> decrypt current payload
           -> merge + validate + section-specific validation
           -> encrypt secrets + save row
           -> ReloadConfiguration
        <- updated section DTO
  <- UI refreshes state
```

## 10.2 Chat defaults update sequence

```text
OverviewTab / ChatToolbarPanel
  -> PUT /api/settings/chat-defaults
     -> SettingsCoreEndpoints maps typed DTO -> section payload
        -> ApplicationSettingsService.UpdateSectionAsync("ChatDefaults", ...)
           -> validation includes reasoning vs default model checks
           -> persist + reload
        <- ChatDefaultsDto
  <- resolver sees updated ChatDefaults on next resolve
```

## 10.3 Model add/update sequence

```text
ModelsRuntimeWorkspace / Wizard
  -> POST /api/settings/models or /api/settings/models:add
     -> endpoint validation + orchestrator (for local onboarding path)
     -> ApplicationSettingsService model CRUD
        -> normalize runtime/reasoning JSON
        -> persist model row
        -> optional llama router sync
  <- model DTO or operation DTO
```

## 10.4 Service provider edit sequence

```text
ServicesTab / Wizard optional services
  -> PUT /api/settings/services/{serviceId}/providers/{providerId}
     -> ApplicationSettingsService.UpdateServiceProviderFieldsAsync
        -> service contract lookup
        -> provider field validation
        -> underlying section update(s)
        -> compose refreshed ServiceEditorState
  <- ServiceEditorStateDto
```

## 11) Ownership matrix

| Concern | Authority | Source of truth | Primary files |
|---|---|---|---|
| Section contract | Settings registry | code (`SettingsSectionRegistry`) | `SettingsSectionRegistry.cs` |
| Section values | DB row per section | `ApplicationSettings` | `ApplicationSetting.cs`, `ApplicationSettingsService.Sections.cs` |
| Model list | DB catalog | `Models` table | `Model.cs`, `ApplicationSettingsService.Models.cs` |
| Runtime profile list | DB catalog | `RuntimeProfiles` table | `RuntimeProfile.cs`, `ApplicationSettingsService.RuntimeProfiles.cs` |
| Runtime projection | config provider | flattened `IConfiguration` | `ApplicationSettingsConfigurationProvider.cs`, `Program.cs` |
| Chat model resolution | routing service | `IChatModelResolver` output | `ChatModelResolver.cs` |
| Chat turn execution | conversation runtime | `ExecutionPolicy` | `ConversationService.cs` (+ downstream chat runtime) |

## 12) Invariants and guardrails

- One write plane: `/api/settings/*`
- One section contract authority: `SettingsSectionRegistry`
- RowVersion concurrency on section writes
- Secrets encrypted at rest in section JSON
- Runtime config reload after successful mutating writes
- Unknown DB sections are not projected into runtime config
- Resolver authority is binary and explicit
- `ReferenceKind` is provenance, not policy authority
- Wizard and full Settings must remain behaviorally equivalent for persisted state

## 13) Extension playbook

When adding a new settings capability:

1. Add or update section/provider contract in `SettingsSectionRegistry`
2. Add validation and write/read logic in `ApplicationSettingsService` partials
3. Add/adjust endpoint mapping in the appropriate `Endpoints/Settings/*Endpoints.cs` module
4. Update `SettingsDtos` contracts if transport changes
5. Wire UI surface(s) via `api.settings.*`
6. Add service + endpoint tests (validation, concurrency, readiness, projection)
7. Confirm runtime projection and consumer behavior after reload

This sequence preserves architecture integrity: one contract, one persistence model, one runtime projection, one API.
