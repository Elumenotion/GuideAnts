# Provider-Service Routing Working Draft

Last updated: 2026-04-19

> Status: historical working draft. Kept for implementation history only.
> This is not authoritative for current routing behavior and must not be
> used as new-install guidance.
> Current docs:
> - `docs/setup-guide.md`
> - `docs/settings-page-provider-model-llama-redesign.md`
> - `docs/llama-model-download-and-runtime-management.md`

## 0) Execution Progress (Implementation Run)
Status as of 2026-04-14:
- [x] `ActiveProviderId` refactor landed across service sections and runtime routing.
- [x] `LocalServiceHosts:*BaseUrl` runtime transport roots wired and used for non-cloud endpoints.
- [x] `ConfigMode` removed from runtime startup/config loader/service CRUD logic.
- [x] Data model updated to single-key `ApplicationSettings` (`SectionName` PK).
- [x] Migration scaffolded and customized to:
  - snapshot/backup source rows into `ApplicationSettingsBackup_20260414_ProviderRouting`,
  - dedupe mode rows,
  - transform legacy `Provider` and local URL fields to `ActiveProviderId` shape,
  - prune non-keep-list sections,
  - drop `ConfigMode`.
- [x] `encv2::` secret encryption implemented (`AES-GCM`, key-id envelope, shared key map).
- [x] Legacy `enc::` read fallback retained for migration compatibility.
- [x] Startup validator updated for provider IDs, required provider config, required local host roots, and encrypted-secret/key checks.
- [x] Compose/appsettings contract updated to runtime host overrides (provider overrides removed).
- [x] Unit tests updated and expanded (including `ApplicationSettingsJson` `encv2`/legacy fallback coverage).
- [x] Pre-migration live DB backup execution and restore metadata capture completed.
- [x] Migration applied and verified against `guideants-dev` (`ConfigMode` dropped, dedupe/transform confirmed, backup table populated).
- [x] Secret reseed pass executed through updated API runtime; secret-at-rest values now `encv2::` (or empty where intentionally unset).
- [x] Targeted verification tests rerun post-migration (`43/43` passing).
- [x] Compose `guideants-webapi-ui` rebuilt/redeployed against migrated schema.
- [x] Compose smoke checks passed (`/api/settings/sections` healthy; service `ActiveProviderId` values and `LocalServiceHosts__*` env roots validated).
- [x] ASR runtime incident triaged and resolved:
  - root cause: running `guideants-ai` container had `GA_ASR_AUTO_LOAD_ON_STARTUP=0`, so startup was healthy but `/asr/transcribe` returned `model_not_loaded`,
  - fix: recreated `guideants-ai` with compose-evaluated env (`GA_ASR_AUTO_LOAD_ON_STARTUP=1`) and validated `/asr/health` (`loaded=true`) + `/api/speech/transcribe` success.
- [x] ASR diagnostics improved: added explicit `asr_transcribe_rejected` log events for `model_not_loaded` / `model_not_ready` responses.
- [x] Settings IA parity fix: Providers tab now includes non-service provider sections (`AzureOpenAI`, `OpenAI`, `Anthropic`, `LlamaCpp`, etc.) so endpoint/key/deployment settings are editable.

Execution artifacts (2026-04-14):
- backup file: `.\backups\guideants-dev-20260414-144255.bak`
- backup metadata + checksum: `docs/20260414-provider-routing-backup-metadata.txt`
- migration script executed: `docs/20260414-provider-routing-migration.sql`
- compose runtime image: `guideants-webapi-ui:26104.1451` (deployed)
- compose AI runtime image: `guideants-ai:cuda13-26104.1510` (deployed; includes ASR rejected-request logging patch)

Operational note:
- Pre-redeploy image mismatch (`Invalid column name 'ConfigMode'`) was resolved by rebuilding/recreating `guideants-webapi-ui`.

## 1) Purpose
This is the single editable working spec for the provider/service settings refactor.  
It is intentionally detailed and decision-focused so we can implement directly from this doc and keep extending it as requirements evolve.

## 2) Problem Statement
Current settings behavior has accumulated conflicting patterns:
- Service provider selection is currently binary in several paths (`cloud|local`, `azure|local-tts`, `azure|docling`, etc.).
- Non-cloud transport addresses are mixed into service sections and duplicated across runtime modes.
- `ApplicationSettings` uses a `ConfigMode` split (`local` vs `docker`) that duplicates rows and creates ambiguity.
- Compose currently overrides provider selection and local URLs directly, which fights DB-first settings intent.

Target outcome:
- Services choose one explicit active provider ID.
- Providers own provider-specific settings.
- Non-cloud provider addressing supports host-vs-compose runtime context without duplicating full configuration rows.
- UI/API settings flows remain stable.

## 3) Locked Requirements
1. Services choose providers by explicit provider ID (N-way), not binary class labels.
2. Each service has exactly one active provider at runtime.
3. Provider-specific settings are owned by provider sections.
4. Non-cloud provider calls support host and compose base URL resolution.
5. Runtime address overrides are from appsettings/env (including compose).
6. Cloud-only provider settings are not duplicated for runtime mode differences.
7. Startup fails fast for:
   - unknown active provider ID,
   - missing required provider settings,
   - missing/invalid runtime base URL for selected non-cloud provider.
8. UI endpoints and optimistic concurrency behavior are preserved.
9. Secret masking and encryption-at-rest semantics are preserved.
10. Migration removes row duplication from `ConfigMode` while preserving operational behavior.
11. Secrets saved in host and compose must be decryptable in both runtimes.

## 4) Existing System Constraints (Must Preserve)
### UI/API behavior
- Settings API:
  - `GET /api/settings/sections`
  - `GET /api/settings/sections/{sectionName}`
  - `PUT /api/settings/sections/{sectionName}` with `rowVersion`.
- Section metadata (display name, order, secret field metadata) remains registry-driven.
- Secret values are still masked on read and encrypted in DB.

### Runtime/service behavior
- Existing local endpoint path contracts are fixed:
  - ASR: `/asr/transcribe`
  - TTS: `/tts/synthesize`
  - Image txt2img: `/sd/txt2img`
  - Image img2img: `/sd/img2img`
  - Embeddings: `/emb/embed`.
  - Document extraction (Docling): `/v1/convert/file`.
- Existing `LlamaCpp` and `ServiceRouting` routing behavior remains supported and validated.

## 5) Target Configuration Model
## 5.1 Service sections (DB-backed)
Each service section carries only routing choice and service-level execution settings.

### SpeechTranscription (DB row)
```json
{
  "ActiveProviderId": "SpeechTranscription.LocalAsr.Http",
  "TimeoutSeconds": 300
}
```

### SpeechSynthesis (DB row)
```json
{
  "ActiveProviderId": "SpeechSynthesis.LocalTts.Http",
  "TimeoutSeconds": 300
}
```

### ImageGeneration (DB row)
```json
{
  "ActiveProviderId": "ImageGeneration.LocalSd.Http",
  "TimeoutSeconds": 900
}
```

### Embeddings (DB row)
```json
{
  "ActiveProviderId": "Embeddings.LocalEmb.Http",
  "TimeoutSeconds": 300,
  "LocalMinIntervalMs": 5000
}
```

### DocumentIntelligence (DB row)
```json
{
  "ActiveProviderId": "DocumentIntelligence.LocalDocling.Http",
  "TimeoutSeconds": 300
}
```

## 5.2 Provider sections (DB-backed)
Provider-specific settings remain in provider-owned sections.

Keep one row per section:
- `AzureDocumentIntelligence`
- `AzureSpeechService`
- `AzureOpenAI`
- `AzureOpenAiImages`
- `AzureOpenAiEmbedding`
- `OpenAI`
- `Anthropic`
- `LlamaCpp`
- `ServiceRouting`

Prune from active table after backup snapshot:
- `AzureOpenAiSora`
- `Postmark`
- `Stripe`
- any non-keep-list section rows.

## 5.3 Runtime transport section (file/env authority, not DB authority)
Introduce runtime-only transport roots:
- `LocalServiceHosts:SpeechTranscriptionBaseUrl`
- `LocalServiceHosts:SpeechSynthesisBaseUrl`
- `LocalServiceHosts:ImageGenerationBaseUrl`
- `LocalServiceHosts:EmbeddingsBaseUrl`
- `LocalServiceHosts:DocumentIntelligenceBaseUrl`

Example host defaults:
```json
{
  "LocalServiceHosts": {
    "SpeechTranscriptionBaseUrl": "http://localhost:8110",
    "SpeechSynthesisBaseUrl": "http://localhost:8110",
    "ImageGenerationBaseUrl": "http://localhost:8110",
    "EmbeddingsBaseUrl": "http://localhost:8110",
    "DocumentIntelligenceBaseUrl": "http://localhost:5001"
  }
}
```

Compose overrides:
```yaml
- LocalServiceHosts__SpeechTranscriptionBaseUrl=http://guideants-ai:80
- LocalServiceHosts__SpeechSynthesisBaseUrl=http://guideants-ai:80
- LocalServiceHosts__ImageGenerationBaseUrl=http://guideants-ai:80
- LocalServiceHosts__EmbeddingsBaseUrl=http://guideants-ai:80
- LocalServiceHosts__DocumentIntelligenceBaseUrl=http://docling-serve:5001
```

## 5.4 Provider IDs (canonical pattern)
Service-scoped, Dot-Pascal IDs:
- `SpeechTranscription.AzureSpeech.Batch`
- `SpeechTranscription.LocalAsr.Http`
- `SpeechSynthesis.AzureSpeech.Ssml`
- `SpeechSynthesis.LocalTts.Http`
- `ImageGeneration.AzureOpenAI.Images`
- `ImageGeneration.LocalSd.Http`
- `Embeddings.AzureOpenAI.Embedding`
- `Embeddings.LocalEmb.Http`
- `DocumentIntelligence.Azure.DocumentIntelligence`
- `DocumentIntelligence.LocalDocling.Http`

Note: only IDs currently implemented in code are activated initially; others can exist as reserved IDs for staged rollout.

## 6) Runtime Resolution Rules
1. Read service section `ActiveProviderId`.
2. Resolve provider class and required provider section.
3. If selected provider is cloud-only:
   - use provider section settings directly.
4. If selected provider is non-cloud:
   - read corresponding `LocalServiceHosts:*BaseUrl`,
   - append fixed suffix for that service/operation.

Exact endpoint composition:
- transcription = `${SpeechTranscriptionBaseUrl}/asr/transcribe`
- synthesis = `${SpeechSynthesisBaseUrl}/tts/synthesize`
- image txt2img = `${ImageGenerationBaseUrl}/sd/txt2img`
- image img2img = `${ImageGenerationBaseUrl}/sd/img2img`
- embeddings = `${EmbeddingsBaseUrl}/emb/embed`
- document extraction (docling) = `${DocumentIntelligenceBaseUrl}/v1/convert/file`

## 7) DB Ownership vs Runtime Ownership
## 7.1 DB-owned settings
- Service rows (`SpeechTranscription`, `SpeechSynthesis`, `ImageGeneration`, `Embeddings`, `DocumentIntelligence`) including `ActiveProviderId` and timeouts.
- Provider rows (`Azure*`, `OpenAI`, `Anthropic`, etc.).
- `LlamaCpp`, `ServiceRouting`.

## 7.2 Runtime-owned settings (not DB-overridden)
- `LocalServiceHosts:*`.
- runtime context selector `API_RUNTIME_CONTEXT` (`host|compose`), only where needed for existing dual-host fields (`LlamaCpp`, `ServiceRouting`).

## 7.3 Guardrail
- DB loader ignores/rejects any attempted `ApplicationSettings` row named `LocalServiceHosts`.

## 8) Secret Encryption Strategy (Cross-Runtime Safe)
## 8.1 Problem
- Current `ApplicationSettings` secret encryption uses machine/runtime-bound Data Protection keys.
- Host dev runtime and compose runtime can end up with different key material.
- Result: secrets written in one runtime fail to decrypt in the other runtime.

## 8.2 Design Goal
- Keep secrets encrypted at rest.
- Ensure one encrypted secret value is readable from both host and container runtime.
- Support staged migration from legacy `enc::` values without breaking startup.

## 8.3 Proposed Format and Keying
- Introduce versioned secret format `encv2::`.
- `encv2` encryption uses application-managed symmetric keys shared across runtimes.
- Cipher recommendation: AES-GCM with random nonce per value.
- Include key ID in ciphertext envelope so multiple keys can be supported during rotation.

Example conceptual envelope (exact wire format can be finalized in implementation):
```text
encv2::<keyId>::<base64url(nonce+ciphertext+tag)>
```

## 8.4 Configuration Keys (runtime/env)
- `SettingsSecrets:ActiveKeyId` (or env `SettingsSecrets__ActiveKeyId`)
- `SettingsSecrets:Keys:{keyId}` for one or more base64-encoded keys (or env `SettingsSecrets__Keys__{keyId}`)

Minimum runtime requirement:
- both host and compose must receive identical key material for at least the active key ID.

## 8.5 Read/Write Behavior
Read path:
1. If secret starts with `encv2::`, decrypt via configured symmetric key map.
2. Else if secret starts with legacy `enc::`, attempt legacy Data Protection decrypt path.
3. If required secret remains undecryptable, fail startup with explicit key name.

Write path:
1. All new/updated secret writes use `encv2::` only.
2. Legacy `enc::` values are never written by new code.

## 8.6 Migration and Backward Compatibility
- Keep legacy decrypt support temporarily to avoid hard cutover.
- On successful section update, any decryptable legacy `enc::` values are re-encrypted to `encv2::`.
- Optional one-time admin re-encryption task:
  - read section,
  - decrypt with available legacy key context,
  - write back as `encv2::`.
- If legacy ciphertext cannot be decrypted in a runtime, value must be re-entered/resaved from secret source of truth.

## 8.7 Key Rotation
- Maintain at least two keys during rotation window:
  - active write key,
  - previous read key.
- Encryption uses only `ActiveKeyId`; decryption may use any configured key ID.
- Rotation procedure:
  1. add new key and set as active,
  2. run opportunistic or batch re-encryption,
  3. remove old key after verification window.

## 8.8 Scope Boundary
- This change is only for `ApplicationSettings` secret payload encryption.
- Existing ASP.NET Data Protection usage for unrelated framework features is unchanged.

## 8.9 Files/Components Impacted
- `src/server/GuideAntsApi/Settings/ApplicationSettingsJson.cs`
  - add `encv2` encrypt/decrypt support and key-ID envelope handling.
- `src/server/GuideAntsApi/Configuration/StartupConfiguration.cs` and/or `Program.cs`
  - bind and validate `SettingsSecrets` key configuration.
- `src/server/GuideAntsApi/Configuration/ServiceRoutingStartupValidator.cs`
  - keep fail-fast behavior for undecryptable required secrets with actionable error text.

## 8.10 Test Additions
- Unit: `encv2` roundtrip encryption/decryption.
- Unit: legacy `enc::` decrypt fallback still works when legacy key is available.
- Unit: write path always emits `encv2::`.
- Integration: secret written on host can be read in compose and vice versa when shared key config is present.
- Negative integration: missing key material causes explicit startup failure.

## 9) Data Model and Migration
## 9.1 Final table model
- `ApplicationSettings` keyed by `SectionName` only.
- Remove `ConfigMode` from:
  - table schema,
  - EF model,
  - DB provider query filters,
  - bootstrap/update flows,
  - startup validator expectations.

## 9.2 Data migration algorithm
1. Backup database.
2. Snapshot `ApplicationSettings` into backup table with timestamp.
3. For each `SectionName`, merge duplicated mode rows:
   - prefer non-empty decrypted values,
   - preserve latest `UpdatedUtc` metadata.
4. Convert service payloads:
   - map legacy `Provider` to `ActiveProviderId`,
   - remove legacy local base URL fields from service payloads.
5. Ensure keep-list sections exist with valid default schema.
6. Prune non-keep-list rows from active table.
7. Preserve snapshot for rollback and forensic validation.

## 9.3 Compatibility aliases (temporary)
Read aliases for one migration cycle:
- service selector alias:
  - `Provider` -> `ActiveProviderId` mapping adapter.
- legacy URL aliases (read-only migration path):
  - `SpeechTranscription:LocalAsrBaseUrl`
  - `SpeechSynthesis:LocalTtsBaseUrl`
  - `ImageGeneration:LocalSdBaseUrl`
  - `Embeddings:LocalBaseUrl`
  - `DocumentIntelligence:LocalDoclingBaseUrl`
  -> migrated into `LocalServiceHosts:*BaseUrl` roots.
- legacy provider aliases (read-only migration path):
  - `DocumentIntelligence:Provider` (`azure|docling`) -> `DocumentIntelligence:ActiveProviderId`

Remove aliases once:
- production data migration is complete,
- compose/host parity is validated.

## 10) File and Layer Change Plan
## 10.1 API startup/config loading
- `src/server/GuideAntsApi/Program.cs`
  - remove `ConfigMode` resolution from DB provider initialization.
- `src/server/GuideAntsApi/Settings/ApplicationSettingsConfigurationProvider.cs`
  - remove `ConfigMode` ctor/input and SQL filter.
  - add runtime-only section guard (`LocalServiceHosts` not loaded from DB).
- `src/server/GuideAntsApi/Settings/ApplicationSettingsService.cs`
  - remove active-mode filtering in CRUD/bootstrap paths.

## 10.2 Data model/migrations
- `src/server/GuideAntsApi.DataModel/Models/ApplicationSetting.cs`
  - remove `ConfigMode`.
- `src/server/GuideAntsApi.DataModel/ApplicationDbContext.cs`
  - PK changes from `(SectionName, ConfigMode)` to `SectionName`.
- add new migration:
  - drop `ConfigMode`,
  - dedupe rows,
  - backfill transformed payloads.

## 10.3 Settings registry and DTOs
- `src/server/GuideAntsApi/Settings/SettingsSectionRegistry.cs`
  - service sections use `ActiveProviderId`.
  - remove local URL fields from service section schemas.
  - keep provider sections and secret metadata.
- API DTO contracts remain unchanged (`rowVersion`, payload object shape by section).

## 10.4 Service resolution updates
- `SpeechTranscriptionService`, `SpeechSynthesisService`, `NotebookImageService`, `LocalEmbeddingService`,
  `ProviderRoutedDocumentIntelligenceService`, `DoclingServeDocumentIntelligenceExtractor`, provider routers:
  - switch selection logic from binary provider value to provider IDs.
  - resolve local endpoints via `LocalServiceHosts`.
  - preserve cloud provider execution paths.

## 10.5 Validation updates
- `ServiceRoutingStartupValidator`:
  - remove `ConfigMode` checks.
  - validate `ActiveProviderId` per service.
  - validate required cloud settings per selected provider.
  - validate required `LocalServiceHosts:*BaseUrl` when selected provider is non-cloud.
  - include `DocumentIntelligence` rules:
    - `DocumentIntelligence.Azure.DocumentIntelligence` requires `AzureDocumentIntelligence:Endpoint` + `ApiKey`.
    - `DocumentIntelligence.LocalDocling.Http` requires `LocalServiceHosts:DocumentIntelligenceBaseUrl`.
  - retain existing encrypted-secret fail-fast behavior.

## 10.6 Runtime files
- `src/server/GuideAntsApi/appsettings.json`
  - add `LocalServiceHosts`.
  - remove legacy service local URL fields.
  - remove `ConfigMode`.
- `src/server/GuideAntsApi/appsettings.Development.json`
  - same shape as base appsettings with dev overrides.
- `docker/docker-compose.yml`
  - remove `ConfigMode`.
  - remove service provider/local URL env overrides.
  - add `API_RUNTIME_CONTEXT=compose`.
  - add `LocalServiceHosts__*BaseUrl` overrides.
  - replace `DocumentIntelligence__Provider` and `DocumentIntelligence__LocalDoclingBaseUrl`
    with `LocalServiceHosts__DocumentIntelligenceBaseUrl`.
  - preserve `docling-serve` network alias contract for both `docling-cpu` and `docling-cuda` profiles.

## 10.7 Scripts/docs
- replace or retire `tmp-reseed-settings.ps1` assumptions that rely on old service field names.
- update docs that reference `ConfigMode` or legacy local URL keys.

## 11) UI Impacts
Expected UI behavior remains:
- same Settings pages and API endpoints.
- same rowVersion concurrency semantics.
- updated field names in service section payloads shown in UI:
  - `ActiveProviderId` replaces `Provider`.
- Services tab remains service-first and does not host provider credential sections.
- Providers tab includes all non-service DB-backed sections so chat/provider credentials are configurable in UI.
- local transport roots are runtime config (not editable through DB-backed settings UI).

## 12) Test Plan (Detailed)
## 12.1 Unit tests
- Provider selection:
  - each service accepts known provider IDs.
  - unknown ID throws startup validation error.
- Runtime local endpoint composition:
  - ASR/TTS/SD/Emb/Docling endpoints composed from `LocalServiceHosts`.
- Provider-specific required config:
  - missing cloud keys fail with explicit key names.
  - missing runtime local base URL fails with explicit key names.
- DB guard:
  - injected `LocalServiceHosts` DB row does not override runtime config.

## 12.2 Migration tests
- dedupe from dual rows to single row by section.
- legacy selector mapping to `ActiveProviderId`.
- legacy URL fields removed from service payloads post-migration.

## 12.3 Integration tests
- Host run:
  - local providers route to localhost roots (including docling `http://localhost:5001`).
- Compose run:
  - local providers route to `guideants-ai:80` roots and docling routes to `docling-serve:5001`.
- Compose profile run:
  - `docling-cpu` and `docling-cuda` profiles both satisfy the `docling-serve` alias used by API config.
- Compose contract test:
  - compose env keys map to appsettings/runtime-authorized keys.
- UI settings CRUD:
  - GET/PUT sections still function with rowVersion.

## 13) Acceptance Criteria
1. Exactly one active DB row per keep-list section.
2. `ConfigMode` is absent from runtime settings logic and schema.
3. Services route by explicit provider IDs.
4. Non-cloud providers resolve endpoints from runtime `LocalServiceHosts`.
5. Compose works without overriding provider selection directly.
6. Host and compose differ only by runtime transport roots for local calls.
7. Document extraction works with either provider:
   - Azure provider uses `AzureDocumentIntelligence` settings.
   - Local Docling provider uses `LocalServiceHosts:DocumentIntelligenceBaseUrl`.
8. UI settings endpoints remain operational.
9. Missing/invalid selected provider or required config fails startup explicitly.
10. Secrets remain encrypted at rest and masked on read.
11. Legacy duplicate and deprecated rows are removed from active table and retained in backup snapshot.
12. Secrets written by host are decryptable by compose, and secrets written by compose are decryptable by host.

## 14) Open Items (Active)
1. Final naming decision:
   - `ActiveProviderId` (recommended) vs `ProviderId`.
2. Exact initial provider ID whitelist per service for first deploy.
3. Alias deprecation date for legacy `Provider` and legacy local URL keys.
4. Final secret key distribution source for each environment (local dev, CI, production).
5. Default compose docling profile policy (`docling-cpu` default vs explicit profile selection every run).

## 15) Execution Order
1. Take full DB backup and verify backup restore metadata before any schema/config mutation.
2. Finalize naming (`ActiveProviderId` vs `ProviderId`).
3. Land `encv2` secret encryption support and key configuration binding.
4. Land model/migration and provider loader changes.
5. Land validator and service endpoint resolution changes.
6. Update appsettings + compose contract.
7. Update tests.
8. Run migration in staging with snapshot validation.
9. Roll out to production with rollback snapshot retained.

## 15.1) Mandatory Pre-Migration Backup Details
- Backup target DB: `guideants-dev` (from source settings files).
- Backup must run before:
  - dropping `ConfigMode`,
  - deduplicating `ApplicationSettings`,
  - re-encrypting any secret payloads.
- Backup validation:
  - backup file created and checksum recorded,
  - restore metadata captured (DB name, timestamp, logical files),
  - rollback drill command/script prepared.
- Operational checklist and commands:
  - see `docs/provider-routing-rollout-checklist.md`.

## 15.2) Seed Secret and Provider Values from Source Files
Source files:
- `E:\appsettings.json`
- `E:\appsettings.Development.json`

Rule:
- Use these source values as plaintext seed inputs.
- Persist into DB settings sections, then encrypt at rest with `encv2`.
- If same logical setting appears in both files, prefer `E:\appsettings.json` unless value is empty/template-only.

### Target Section Mapping from Source Values
`DocumentIntelligence`:
- No explicit plaintext `DocumentIntelligence` section found in `E:\appsettings.json` / `E:\appsettings.Development.json`.
- Seed defaults:
  - `ActiveProviderId` = `DocumentIntelligence.Azure.DocumentIntelligence`
  - `TimeoutSeconds` = `300`
- Runtime non-cloud default host root:
  - `LocalServiceHosts:DocumentIntelligenceBaseUrl` = `http://localhost:5001`

`AzureDocumentIntelligence`:
- `Endpoint` = `https://waterfall-dev.cognitiveservices.azure.com/`
- `ApiKey` = `2fV7Z76MjsMiOXPIZ7i1OTTnMcNmqHJWm1VlmQS101LONjy3LHIXJQQJ99BGACHYHv6XJ3w3AAALACOGpBuw`
- `ApiVersion` = `2024-11-30`
- `TimeoutSeconds` = `300`
- `MaxRetries` = `3`

`AzureSpeechService`:
- `Endpoint` = `https://waterfall-dev-speech.cognitiveservices.azure.com/`
- `ApiKey` = `9ycD0BPSbu5mPCIjMaVzLxGZQfz4UcCwtFumPvfqRoqTwAtodJ3wJQQJ99BGACHYHv6XJ3w3AAAEACOGn7NB`
- `Region` = `eastus2`
- `TimeoutSeconds` = `600`
- `MaxRetries` = `3`

`AzureOpenAI`:
- `Resource` = `ai-dougwareai685749536435`
- `ApiKey` = `4jkA1lITBY5fzdvJJdgiDIW2Sde76bAnkHYcWVsM18b1utu9yNJsJQQJ99BEACHYHv6XJ3w3AAABACOGb5qw`
- `Deployment` = `gpt-4.1`
- `ApiVersion` = `2025-04-01-preview`

`AzureOpenAiImages`:
- `Endpoint` = `https://guideants-ai-images.cognitiveservices.azure.com/`
- `ApiKey` = `Bq5ScLlLlkKMWlXuXYbNuL9abBCkM20GeA5wYpdkvDKgXLfiGlkFJQQJ99BHACHYHv6XJ3w3AAAAACOGIDxZ`
- `Deployment` = `FLUX.1-Kontext-pro`
- `EditModelDeployment` = `FLUX.1-Kontext-pro`
- `ApiVersion` = `2025-04-01-preview`

`AzureOpenAiEmbedding`:
- `Endpoint` = `https://ai-dougwareai685749536435.openai.azure.com/`
- `ApiKey` = `4jkA1lITBY5fzdvJJdgiDIW2Sde76bAnkHYcWVsM18b1utu9yNJsJQQJ99BEACHYHv6XJ3w3AAABACOGb5qw`
- `Deployment` = `text-embedding-3-small`

`Anthropic`:
- `BaseUrl` = `https://guideants-ai-images.services.ai.azure.com/anthropic`
- `ApiKey` = `Bq5ScLlLlkKMWlXuXYbNuL9abBCkM20GeA5wYpdkvDKgXLfiGlkFJQQJ99BHACHYHv6XJ3w3AAAAACOGIDxZ`
- `DefaultModel` = `claude-sonnet-4-5`
- `DefaultMaxTokens` = `64000`
- `ThinkingBudgetMinimal` = `1024`
- `ThinkingBudgetLow` = `1536`
- `ThinkingBudgetMedium` = `2048`
- `ThinkingBudgetHigh` = `3072`

`OpenAI`:
- `ApiKey` = `${OPENAI_KEY}` (template token from source file; no concrete plaintext value present)
- `Endpoint` = `${OPENAI_URL}` (template token from source file)
- `Deployment` = `${OPENAI_DEPLOYMENT_NAME}` (template token from source file)

Legacy rows present in source files (do not keep active after migration, retain in backup snapshot only):
- `AzureOpenAiSora`
- `Postmark`
- `Stripe`

## 16) Change Log
- 2026-04-14: Created initial short draft.
- 2026-04-14: Expanded into full decision-level spec with data model, runtime ownership, migration flow, and acceptance criteria.
- 2026-04-14: Added cross-runtime secret encryption strategy (`encv2`) with key management, migration, and rotation plan.
- 2026-04-14: Added mandatory step-1 DB backup and seeded provider/secret source values from `E:\appsettings.json` and `E:\appsettings.Development.json`.
- 2026-04-14: Added DocumentIntelligence/Docling implementation alignment (service model, host mapping, compose profile/alias concerns, migration aliases, and acceptance criteria).
- 2026-04-14: Updated Settings UI notes to reflect provider-credential coverage in Providers tab (including chat providers).
- 2026-04-15: Removed the legacy `Search` settings section from the current provider inventory; SearXng search remains runtime-configured through `SearXngSearch`.

## 17) Scratch Notes
- Keep editing here during collaboration, then move settled items into sections above.
