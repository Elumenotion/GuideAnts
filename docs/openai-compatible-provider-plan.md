# Plan: Generic OpenAI-Compatible Endpoint Provider (`openai-compatible`)

## Overview

The new provider follows the same patterns as the existing providers but stores per-row endpoint configuration (`BaseUrl`, optional `ApiKey`) in `LocalRuntimeJson` — the same column `llama-cpp` uses, but with a distinct JSON schema. The `Provider` column (`"openai-compatible"`) discriminates which parser applies. The existing `OpenAiChatClientFactory` from `AntRunner.Chat.OpenAI` is reused, since that factory already knows how to point to an arbitrary endpoint via `OpenAISettings(domain:...)`.

**Security note**: Storing `ApiKey` in `LocalRuntimeJson` stores it plaintext in the DB (unlike the encrypted `ApplicationSettings` table). This is a known limitation and should be called out for a follow-up ticket.

---

## New Files

### 1. `src/server/AntRunner.Chat/AntRunner.Chat.OpenAI/OpenAiCompatConfig.cs`

Config record for a per-call compatible endpoint. Fields: `BaseUrl` (required), `ApiKey` (nullable). This is analogous to `AzureOpenAiConfig` but with a mandatory base URL and no Azure-specific fields.

### 2. `src/server/GuideAntsApi/Services/LlamaCpp/OpenAiCompatRuntimeConfigurationParser.cs`

(Or placed adjacent to `LocalRuntimeConfigurationParser.cs`.) Parses and validates `LocalRuntimeJson` for the `openai-compatible` provider — extracts `baseUrl` and optional `apiKey`. Throws a structured exception on malformed JSON or missing `baseUrl`, consistent with how `LocalRuntimeConfigurationParser` works for llama-cpp.

### 3. `src/client/src/pages/settings/components/catalog/providers/OpenAiCompatForm.tsx`

Two exported components following the exact pattern of `OpenAiChatForm.tsx`:

- `OpenAiCompatAddForm` — renders a `baseUrl` text input (required) and `apiKey` text input (optional)
- `OpenAiCompatEditForm` — same fields, reads from `CatalogEditState`

---

## Modified Server Files

### 4. `src/server/GuideAntsApi/Services/Routing/IChatTargetValidator.cs`

- Add `"openai-compatible"` to `KnownProviders`
- Add a `case "openai-compatible":` in the `Validate` switch that calls `ValidateOpenAiCompatible(target)` — a new private method that:
  - Fails fast if `LocalRuntimeJson` is absent or empty
  - Parses via `OpenAiCompatRuntimeConfigurationParser`; surfaces a `RoutingException` with actionable message if `baseUrl` is missing or invalid

### 5. `src/server/GuideAntsApi/Services/Conversations/RoutingChatCompletionClientFactory.cs`

- Add `Provider.OpenAiCompatibleChat` to the private `Provider` enum
- Add `"openai-compatible" => Provider.OpenAiCompatibleChat` to `ParseProvider`
- In `CreateClient`: add a branch (similar to the llama-cpp block) that parses `LocalRuntimeJson` into `OpenAiCompatConfig` and calls `_openAiPlatformChatFactory.CreateClient(...)` with an `HttpClient` pre-configured to point at the base URL — **no new factory class needed** since `OpenAiChatClientFactory` already accepts `AzureOpenAiConfig` where `ResourceName = null` causes it to use an arbitrary domain
  - Alternatively, create an `OpenAiCompatChatClientFactory` if the `HttpClient`/`OpenAISettings` wiring is complex; this is a judgement call during implementation

### 6. `src/server/GuideAntsApi/Services/Routing/RoutingReadinessService.cs`

- `MapChatProviderToSection`: add `"openai-compatible" => null` (no global provider section — config comes from the row's `LocalRuntimeJson`)
- `ProbeChatTargetAsync`: add a branch for `openai-compatible` that parses `LocalRuntimeJson` and adds a blocker if `baseUrl` is absent/invalid — analogous to the `llama-cpp` block but without runtime inventory checks

### 7. `src/server/GuideAntsApi/Settings/ApplicationSettingsService.Contracts.cs`

No new entry in `ProviderSectionRequirements` (there is no global config section for this provider).

### 8. `src/server/GuideAntsApi/Settings/ProviderConfigurationResolver.cs`

No changes required (config is per-row, not section-based).

### 9. `src/server/GuideAntsApi/Configuration/StartupConfiguration.cs`

No new factory registration if the approach reuses `OpenAiChatClientFactory` directly. If a dedicated `OpenAiCompatChatClientFactory` is created, register it here as a singleton.

---

## Modified Client Files

### 10. `src/client/src/pages/settings/types.ts`

- Add `'openai-compatible'` to the `AddModelProvider` union
- Add two fields to `AddModelWizardState`: `openAiCompatBaseUrl: string`, `openAiCompatApiKey: string`
- Add two fields to `CatalogEditState`: `openAiCompatBaseUrl: string`, `openAiCompatApiKey: string`

### 11. `src/client/src/pages/settings/constants/displayLabels.ts`

- Add `'openai-compatible': 'OpenAI-Compatible Endpoint'` to `CATALOG_PROVIDER_LABELS`

### 12. `src/client/src/pages/settings/utils.ts`

- `createEmptyAddModelWizardState`: initialize `openAiCompatBaseUrl: ''`, `openAiCompatApiKey: ''`
- `createCatalogEditStateFromModel`: parse `localRuntimeJson` when `provider === 'openai-compatible'` to hydrate `openAiCompatBaseUrl` and `openAiCompatApiKey`
- `buildAddModelRequest`: when `provider === 'openai-compatible'`, build `localRuntimeJson` from `openAiCompatBaseUrl` + `openAiCompatApiKey` (throw if `baseUrl` is blank)
- `buildCatalogEditRequest`: same — set `localRuntimeJson` for `openai-compatible` rows
- `mapChatProviderToSection`: add `case 'openai-compatible': return null;` (kept in sync with server-side `RoutingReadinessService.MapChatProviderToSection`)

### 13. `src/client/src/pages/settings/components/catalog/AddModelWizard.tsx`

- Add `case 'openai-compatible':` in the provider form `switch` → returns `<OpenAiCompatAddForm {...props} />`
- Add `<option value="openai-compatible">OpenAI-Compatible Endpoint</option>` in the provider `<select>` (Step 1)

### 14. `src/client/src/pages/settings/components/catalog/CatalogRowEditModal.tsx`

- Add `case 'openai-compatible':` in the edit form `switch` → returns `<OpenAiCompatEditForm {...props} />`

---

## Tests to Update

- `src/server/GuideAntsApi.Tests/Services/RoutingChatCompletionClientFactoryTests.cs` — add cases for `openai-compatible` routing
- `src/server/GuideAntsApi.Tests/Services/Routing/ChatTargetValidatorTests.cs` (if it exists) — add validation cases for missing `LocalRuntimeJson`, invalid JSON, missing `baseUrl`
- `src/server/GuideAntsApi.Tests/Services/Routing/RoutingReadinessServiceTests.cs` — add readiness probe cases for `openai-compatible`

---

## Summary Table

| # | File | Action |
|---|------|--------|
| 1 | `AntRunner.Chat.OpenAI/OpenAiCompatConfig.cs` | **New** — config record |
| 2 | `GuideAntsApi/Services/…/OpenAiCompatRuntimeConfigurationParser.cs` | **New** — JSON parser |
| 3 | `client/…/providers/OpenAiCompatForm.tsx` | **New** — add + edit UI forms |
| 4 | `GuideAntsApi/Services/Routing/IChatTargetValidator.cs` | **Modify** — add to KnownProviders + validate |
| 5 | `GuideAntsApi/Services/Conversations/RoutingChatCompletionClientFactory.cs` | **Modify** — enum case + dispatch |
| 6 | `GuideAntsApi/Services/Routing/RoutingReadinessService.cs` | **Modify** — section mapping + probe |
| 7 | `client/…/settings/types.ts` | **Modify** — union type + state fields |
| 8 | `client/…/settings/constants/displayLabels.ts` | **Modify** — display label |
| 9 | `client/…/settings/utils.ts` | **Modify** — 4 functions |
| 10 | `client/…/catalog/AddModelWizard.tsx` | **Modify** — switch case + option |
| 11 | `client/…/catalog/CatalogRowEditModal.tsx` | **Modify** — switch case |
| Tests | Three existing test files | **Modify** — new cases |

---

## Open Questions / Follow-up Tickets

- **API key security**: `ApiKey` in `LocalRuntimeJson` is stored plaintext. Future work: encrypt the column, or support referencing a named credential from the `ApplicationSettings` encrypted store.
- **Responses API variant**: Should there be an `openai-compatible-responses` provider (using `OpenAiResponsesClientFactory`) in addition to the completions variant? Many compatible servers only implement the Chat Completions endpoint, so a single `openai-compatible` using `openai-chat` routing is the right default.
- **`OpenAiCompatChatClientFactory` vs reuse**: Decide during implementation whether to create a thin dedicated factory or reuse `OpenAiChatClientFactory` with a custom-configured `HttpClient`. The dedicated factory is cleaner if the `OpenAISettings` domain-extraction logic doesn't cleanly accept an arbitrary URL.
