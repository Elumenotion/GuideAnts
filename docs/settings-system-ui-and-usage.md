# Settings System: Architecture, UI, and Usage

Last updated: 2026-04-21

> Status: historical/superseded. This document reflects a pre-`ServiceModes`
> design that routed non-chat services via `ActiveProviderId`.
> Do not treat it as current behavior or as new-install guidance.
> For current behavior, use:
> - `docs/setup-guide.md`
> - `docs/settings-service-provider-model-requirements.md` (non-chat service editors, §3.5 UI contract)
> - `docs/settings-page-provider-model-llama-redesign.md`
> - `docs/llama-model-download-and-runtime-management.md`

## 1) What This Covers

This document captures an earlier GuideAnts settings design as it existed at
the time of writing:

- DB-backed settings architecture
- Settings API contract
- Settings UI behavior
- Secrets handling (`encv2` + masking)
- Provider routing with `ActiveProviderId`
- Runtime-owned host routing (`LocalServiceHosts`)
- Common operational workflows (including switching Markdown Extraction from Azure to Docling)

## 2) Mental Model

The system is **DB-first for settings sections** and **file/env-owned for runtime transport roots**.

- DB-backed sections (editable in UI/API) hold service/provider configuration.
- Runtime-owned sections (for example `LocalServiceHosts`) come from appsettings/env and are not DB-editable.

## 3) Source of Truth and Load Order

At startup:

1. Appsettings + environment variables are loaded.
2. `SettingsSecrets` configuration is validated.
3. DB settings provider is added and overlays DB values on top of file/env values.
4. Missing registry-defined settings rows are bootstrapped into DB from current config.
5. Configuration is reloaded and startup validation runs.

Practical effect:

- Existing DB rows override appsettings defaults.
- If appsettings says Docling but DB row says Azure, runtime uses Azure until DB row is changed.

## 4) Data Model

### `ApplicationSettings` table

Current model (post-migration):

- Primary key: `SectionName` (single-row-per-section)
- `JsonValue` (section payload)
- `SchemaVersion`
- `CreatedUtc`
- `UpdatedUtc`
- `RowVersion` (`[Timestamp]` for optimistic concurrency)

`ConfigMode` is removed from runtime schema and logic.

### Logical model: services and service providers

This settings system models routing in four layers:

- Service section (DB-backed): one row per service capability (`SpeechTranscription`, `SpeechSynthesis`, `ImageGeneration`, `Embeddings`, `DocumentIntelligence`) with service-level execution fields and an `ActiveProviderId`.
- Provider section (DB-backed): one row per provider/config family (`AzureSpeechService`, `AzureOpenAI`, `AzureOpenAiImages`, `AzureOpenAiEmbedding`, `AzureDocumentIntelligence`, `OpenAI`, `Anthropic`, `LlamaCpp`, `ServiceRouting`).
- Provider ID catalog (registry + validator): allowed provider IDs per service (for example `SpeechTranscription.AzureSpeech.Batch`, `SpeechTranscription.LocalAsr.Http`).
- Runtime transport roots (file/env-owned): `LocalServiceHosts:*BaseUrl` keys used only when selected provider is non-cloud.

Relationship rules:

- A service section has exactly one active provider at runtime (`ActiveProviderId`).
- A service section can allow many provider IDs, but only one is selected at a time.
- A provider ID resolves to either:
  - a cloud provider flow that requires a provider section, or
  - a local provider flow that requires a `LocalServiceHosts` base URL plus a fixed endpoint suffix.
- Provider sections are reusable configuration nodes; multiple services can depend on different sections in parallel.
- Runtime host roots are not persisted in DB and do not participate in rowVersion concurrency for settings sections.

Design invariants enforced at startup:

- unknown `ActiveProviderId` fails validation,
- missing required provider-section values for selected cloud providers fails validation,
- missing required `LocalServiceHosts:*BaseUrl` values for selected local providers fails validation.

## 5) Registry-Driven Sections

All DB-backed sections come from `SettingsSectionRegistry` and include:

- `AzureDocumentIntelligence`
- `DocumentIntelligence`
- `AzureSpeechService`
- `SpeechTranscription`
- `SpeechSynthesis`
- `AzureOpenAI`
- `AzureOpenAiImages`
- `ImageGeneration`
- `Embeddings`
- `AzureOpenAiEmbedding`
- `OpenAI`
- `Anthropic`
- `LlamaCpp`

Each section defines:

- display metadata
- property schema and types
- defaults
- secret fields
- canonical key + optional legacy alias keys

Tab mapping in current UI:

- `Services` tab: service sections only (`SpeechTranscription`, `SpeechSynthesis`, `ImageGeneration`, `Embeddings`, `DocumentIntelligence`) with provider selection + readiness/dependency guidance.
- `Providers` tab: all non-service DB-backed sections (for example `AzureSpeechService`, `AzureOpenAI`, `AzureOpenAiImages`, `AzureOpenAiEmbedding`, `AzureDocumentIntelligence`, `OpenAI`, `Anthropic`, `LlamaCpp`).

## 6) Runtime-Owned Configuration

`LocalServiceHosts` is runtime-owned and must come from appsettings/env:

- `LocalServiceHosts:SpeechTranscriptionBaseUrl`
- `LocalServiceHosts:SpeechSynthesisBaseUrl`
- `LocalServiceHosts:ImageGenerationBaseUrl`
- `LocalServiceHosts:EmbeddingsBaseUrl`
- `LocalServiceHosts:DocumentIntelligenceBaseUrl`

DB guardrail:

- If a DB row named `LocalServiceHosts` exists, startup fails.

## 7) Provider Routing Model

Service sections now route via `ActiveProviderId` (not binary `Provider` flags).

Examples:

- `SpeechTranscription.AzureSpeech.Batch` / `SpeechTranscription.LocalAsr.Http`
- `SpeechSynthesis.AzureSpeech.Ssml` / `SpeechSynthesis.LocalTts.Http`
- `ImageGeneration.AzureOpenAI.Images` / `ImageGeneration.LocalSd.Http`
- `Embeddings.AzureOpenAI.Embedding` / `Embeddings.LocalEmb.Http`
- `DocumentIntelligence.Azure.DocumentIntelligence` / `DocumentIntelligence.LocalDocling.Http`

Non-cloud providers build endpoints from `LocalServiceHosts` + fixed suffixes.

## 8) Secrets Lifecycle

### At rest

- New writes use `encv2::` (AES-GCM with key-id envelope).
- Legacy `enc::` reads are still supported for migration compatibility.

### Configuration required

- `SettingsSecrets:ActiveKeyId`
- `SettingsSecrets:Keys:{keyId}` (base64 AES key, 16/24/32 bytes)

### In API/UI

- Secret values are masked on read.
- Payload returns `********` and `secretHasValue` metadata.
- On update, sending `********` preserves existing secret.

Startup fails fast if secrets remain encrypted after DB load/decrypt, indicating key/decrypt mismatch.

## 9) Settings API Contract

Base route: `/api/settings`

### Services/sections

- `GET /sections`
- `GET /schema`
- `GET /readiness`
- `GET /sections/{sectionName}`
- `PUT /sections/{sectionName}`

`PUT` request body:

```json
{
  "rowVersion": "base64-rowversion",
  "payload": {
    "SomeKey": "SomeValue"
  }
}
```

Concurrency behavior:

- `409 Conflict` when row version is stale.
- Client reloads latest section values and preserves the user draft for explicit reapply/discard before retry.

Validation behavior:

- `400 Bad Request` with validation errors when payload fails section rules.

### Schema and readiness contract

- `GET /schema` returns:
  - section schema metadata (typed properties, secrets, display order)
  - service-to-provider graph
  - provider dependency requirements
  - runtime-owned dependency keys (`LocalServiceHosts:*`) as read-only with change guidance
- `GET /readiness` returns:
  - per-service status (`ready` or `blocked`)
  - per-service blocker list
  - global blocker list not specific to one service

### Other settings endpoints

- `POST /embeddings/rebuild`
- `GET|POST|PUT|DELETE /models`
- `GET|POST|PUT|DELETE /runtime-profiles`

## 10) Settings UI Behavior

Route: `/settings`

Tabs:

- `Services`
- `Providers`
- `Runtime`
- `Models`
- `Profiles`

Services tab behavior:

- Service-first cards drive `ActiveProviderId` via constrained provider selection (no free-text provider IDs).
- Each service shows readiness (`ready`/`blocked`) and a dependency checklist for selected provider requirements.
- Provider credential sections are intentionally excluded from Services to preserve service-first flow.
- Saves remain section-based with rowVersion concurrency (`Refresh`, `Reset`, `Save Service`).
- On concurrency conflicts, latest server values are loaded and a preserved local draft can be reapplied before saving again.
- After save, readiness is refreshed to guide staged completion.

Providers tab behavior:

- Provider sections are edited as provider-owned configuration nodes.
- Includes all non-service sections, including chat/model providers (`AzureOpenAI`, `OpenAI`, `Anthropic`, `LlamaCpp`) and cloud service provider sections (`AzureSpeechService`, `AzureOpenAiImages`, `AzureOpenAiEmbedding`, `AzureDocumentIntelligence`).
- Provider sections are subdivided into subordinate service-aligned tabs so each tab mirrors the provider choices exposed on the Services screen.
- Local providers are not treated as config-less; when they currently rely on runtime-owned host routing, the Providers tab shows the `LocalServiceHosts` config surface and the exact runtime dependency keys in use.
- Sections are grouped by provider dependencies and shown with typed field editors.

Runtime tab behavior:

- `LocalServiceHosts:*` keys are visible as read-only runtime dependencies.
- UI includes concise guidance on where to change runtime values (appsettings/env/compose).

UI construction standards (baseline patterns reused from stable Guide Builder screens):

- Confirmation UX: destructive actions use shared `ConfirmationDialog` (no `window.confirm`).
- Toast UX: mutations and critical failures emit `useToast().showToast` messages.
- Scrolling UX: page shell uses fixed-height layout with a single primary `overflow-auto` content region.
- Wide tabular content is wrapped in `overflow-x-auto`.

Stable implementation references:

- `src/client/src/components/common/ConfirmationDialog.tsx`
- `src/client/src/components/common/Toast.tsx`
- `src/client/src/components/guides/editor/BaseEntityEditor.tsx` (page shell + overflow + unsaved dialog pattern)
- `src/client/src/pages/GuidesDashboard.tsx` (toast usage + list overflow pattern)
- `src/client/src/components/guides/PublishGuideDialog.tsx` (portal dialog with bounded height and inner scroll)

## 11) Startup Validation Guardrails

`ServiceRoutingStartupValidator` enforces:

- valid `SettingsSecrets` configuration
- no encrypted secret values left after load
- valid provider IDs per service
- required cloud keys when cloud providers selected
- required `LocalServiceHosts` values when local providers selected
- required route prefixes for key service URLs (`/sandbox`, `/llama-cpp`)

## 12) Common Operations

### A) Inspect current Markdown Extraction provider

```powershell
Invoke-RestMethod -Uri "http://localhost:5107/api/settings/sections/DocumentIntelligence" -Method Get
```

### B) Switch Markdown Extraction to Docling

```powershell
$current = Invoke-RestMethod -Uri "http://localhost:5107/api/settings/sections/DocumentIntelligence" -Method Get
$request = @{
  rowVersion = $current.rowVersion
  payload = @{
    ActiveProviderId = "DocumentIntelligence.LocalDocling.Http"
    TimeoutSeconds = [int]$current.payload.TimeoutSeconds
  }
}

Invoke-RestMethod `
  -Uri "http://localhost:5107/api/settings/sections/DocumentIntelligence" `
  -Method Put `
  -ContentType "application/json" `
  -Body ($request | ConvertTo-Json -Depth 8)
```

### C) Verify DB row really changed

```sql
SELECT SectionName, JsonValue
FROM dbo.ApplicationSettings
WHERE SectionName = 'DocumentIntelligence';
```

### D) Verify Docling endpoint health

```powershell
curl.exe -s -o NUL -w "HTTP=%{http_code}" "http://localhost:5001/health"
```

### E) Confirm log path now uses Docling

Look for:

- `via docling-serve`

instead of:

- `via Azure Document Intelligence`

### F) Configure chat/model provider credentials

Use Providers tab for chat/model provider sections:

- `AzureOpenAI` (resource/api key/deployment/api version)
- `OpenAI` (endpoint/api key/deployment)
- `Anthropic` (base URL, keys/tokens, defaults, thinking budgets)
- `LlamaCpp` (base URL/api key/timeout)

## 13) Troubleshooting Notes

### Symptom: appsettings shows one provider, runtime uses another

Cause:

- DB row overrides file default.

Fix:

- Update section via `/api/settings/sections/{sectionName}` with current rowVersion.

### Symptom: startup fails on secret validation/decryption

Cause:

- `SettingsSecrets` missing/invalid, or ciphertext cannot be decrypted in current runtime.

Fix:

- Ensure identical key material for host/compose where shared data is expected.

### Symptom: local provider returns `model_not_loaded` even though container is up

Cause:

- Service process healthy, model not autoloaded yet.

Fix:

- Ensure relevant autoload env is set (`GA_ASR_AUTO_LOAD_ON_STARTUP=1` for ASR case), or load via admin endpoint.
- Use service health/admin endpoints to verify model-loaded state.

## 14) Related Files

- `src/server/GuideAntsApi/Program.cs`
- `src/server/GuideAntsApi/Endpoints/SettingsEndpoints.cs`
- `src/server/GuideAntsApi/Settings/ApplicationSettingsConfigurationProvider.cs`
- `src/server/GuideAntsApi/Settings/ApplicationSettingsService.cs`
- `src/server/GuideAntsApi/Settings/ApplicationSettingsJson.cs`
- `src/server/GuideAntsApi/Settings/SettingsSectionRegistry.cs`
- `src/server/GuideAntsApi/Configuration/ServiceRoutingStartupValidator.cs`
- `src/client/src/pages/Settings.tsx`
- `src/client/src/services/api.ts`
- `src/client/src/types/settings.ts`
