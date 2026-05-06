# Model Sampling Parameter Policy — Regression Fix Plan

## Problem Statement

The Configuration tab in the guide and assistant builders shows only the model selection
dropdown with no sampling parameter controls (Temperature, Top P, Reasoning Effort).
This is a regression introduced during the settings system implementation.

The same regression affects the Default Chat Model section of the Settings → Overview tab,
which uses the same `ChatModelConfigurator` → `ConfigParams` pipeline.

## Root Cause

The `ConfigParams` component in GuideAnts was correctly upgraded from waterfall's
client-side heuristic approach (model ID prefix matching) to a database-driven approach:
it only renders controls when `model.samplingParameterPolicy` is populated in the catalog
API response.

However, the write path was never completed:

1. `CatalogService.GetModelsAsync()` only populates `samplingParameterPolicy` inside the
   `if (!string.IsNullOrEmpty(m.LocalRuntimeJson))` block — cloud models (OpenAI, Azure,
   Anthropic, Gemini, etc.) always exit with `samplingPolicy = null`.

2. The `LocalRuntimeJson` field and the RuntimeProfile mechanism it references are the
   correct general-purpose solution — but cloud models have never been required to
   reference a profile, so they carry no sampling parameter definitions.

3. The field name `LocalRuntimeJson` implies the mechanism is only for local/llama-cpp
   models; in practice the function (profile reference → sampling parameter resolution)
   is provider-agnostic.

4. The settings UI (Add Model wizard, Catalog edit modal) only exposes the runtime profile
   picker to llama-cpp models, so operators have no path to configure this for cloud models.

## Design Decision

The RuntimeProfile is already a general-purpose parameter definition mechanism. The fix is
to make this explicit:

- **Rename** the fields so their names match their actual function (not tied to "local")
- **Require** a profile for all models at creation time — cloud and local alike
- **Expose** the profile picker in the settings UI for all providers

No new database columns are required. No new DTO shapes are required. The mechanism
already works end-to-end for llama-cpp; the work is completing the wiring for cloud models.

### On `RouterModelId`

All models have identifiers. For llama-cpp, `RouterModelId` is the llama router alias
(which may differ from `ModelId`). For cloud models, `RouterModelId` is the model
identifier the provider API expects (typically the same value as `ModelId`). It is
non-nullable in all cases.

---

## Scope of Changes

### 1. Field Renames

| Old name | New name | Location |
|---|---|---|
| `Model.LocalRuntimeJson` | `Model.RuntimeConfigJson` | DB entity + EF migration |
| `LocalRuntimeDescriptorDto` | `ModelRuntimeConfigDto` | Backend DTO |
| `localRuntimeJson` | `runtimeConfigJson` | All frontend types and state |

The rename migration renames the existing column; no data is lost.

### 2. Backend

#### `GuideAntsApi.DataModel/Models/Model.cs`
- Rename property `LocalRuntimeJson` → `RuntimeConfigJson`

#### EF Core migration
- `RenameColumn` on the `Models` table: `LocalRuntimeJson` → `RuntimeConfigJson`

#### `Models/Guides/CatalogDto.cs`
- Rename `LocalRuntimeDescriptorDto` → `ModelRuntimeConfigDto`

#### `Models/Settings/SettingsDtos.cs`
- Rename `LocalRuntimeJson` → `RuntimeConfigJson` in `SettingsModelDto`,
  `CreateSettingsModelRequest`, `UpdateSettingsModelRequest`

#### `Services/Guides/CatalogService.cs` — `GetModelsAsync()`
- Remove the `if (!string.IsNullOrEmpty(m.RuntimeConfigJson))` guard that gates profile
  resolution to local models only
- Resolve the profile for **any** model whose `RuntimeConfigJson` carries a
  `runtimeProfileId`, regardless of provider
- Profile resolution failure (profile not found) continues to leave `samplingPolicy` and
  `reasoningChoices` null — no change to the catch behavior

#### `Settings/ApplicationSettingsService.Models.cs`
- Rename all references to `LocalRuntimeJson` → `RuntimeConfigJson`
- `NormalizeLocalRuntimeJson` → `NormalizeRuntimeConfigJson`; llama-cpp-specific router
  model ID validation and INI sync remain gated on `provider == "llama-cpp"`
- `ToSettingsModelDto`: use renamed field

#### Seed data
- Create standard cloud runtime profiles (e.g., `openai-standard`, `anthropic-standard`,
  `gemini-standard`) with appropriate `SamplingParameters` defined
- Update seeded cloud model rows to carry `RuntimeConfigJson` referencing these profiles

### 3. Frontend

#### `src/types/settings.ts`
- Rename `localRuntimeJson` → `runtimeConfigJson` in `SettingsModelDto`,
  `UpdateSettingsModelRequest`, `CreateSettingsModelRequest`

#### `src/pages/settings/types.ts`
- `CatalogEditState`: rename `localRuntimeRouterModelId` etc. where appropriate; the
  profile ID field (`localRuntimeProfileId` → `runtimeProfileId`) is now present and
  meaningful for all providers
- `AddModelWizardState`: same — `runtimeProfileId` applies to all providers

#### `src/pages/settings/utils.ts`
- `createCatalogEditStateFromModel`: read `runtimeConfigJson` instead of `localRuntimeJson`
- `buildCatalogEditRequest`: write `runtimeConfigJson`; llama-cpp router model ID
  validation remains provider-gated

#### `src/pages/settings/components/catalog/CatalogRowEditModal.tsx`
- Move the runtime profile selector (`runtimeProfileId`) out of `LlamaCppForm` into the
  shared top-level section of the modal — visible and required for all providers
- `LlamaCppForm` retains only the llama-cpp-specific fields: router model ID, load params,
  parallel tool calls, context size, cache RAM

#### `src/pages/settings/components/catalog/AddModelWizard.tsx`
- Profile selection step exposed for all providers
- llama-cpp-specific fields (router alias, HuggingFace install, context size, cache RAM)
  remain in the llama-cpp-only wizard path

#### `src/pages/settings/components/catalog/providers/types.ts`
- Update shared form props type if it carries the runtime config field

### 4. Runtime Profile — kind discrimination

The `RuntimeProfileEditor` currently renders all fields unconditionally. Several are
only meaningful for local (llama-cpp) models:

| Field | Applies to |
|---|---|
| Sampling Parameters JSON | All models |
| Profile ID / Display Name / Description | All models |
| Combine System and Developer Messages | Local only |
| Thought Block Pattern (Regex) | Local only |
| Thinking Control JSON | Local only |
| Insert template buttons (qwen3_5, qwen3_6, gemma4) | Local only |

#### `SettingsRuntimeProfileDto` / `CreateRuntimeProfileRequest` / `UpdateRuntimeProfileRequest`
- Add a `kind` discriminator: `"local"` | `"cloud"` (backend DTOs and DB entity)
- EF Core migration adds the `Kind` column (non-nullable, default `"local"` for existing rows)

#### `RuntimeProfileEditor.tsx`
- Accept `kind` as a prop (or derive it from `ProfileFormState`)
- Show the local-only fields only when `kind === 'local'`
- Show insert-template buttons only when `kind === 'local'`
- For `kind === 'cloud'` profiles: only Profile ID, Display Name, Description, and
  Sampling Parameters JSON are shown

#### `ProfileFormState` (`types.ts`)
- Add `kind: 'local' | 'cloud'`

#### `ProfilesTab` / profile creation flow
- When creating a new profile, operator selects kind first (or it is pre-selected based
  on context — e.g., the Add Model wizard for a cloud provider pre-selects `cloud`)

#### Seeded cloud profiles

Profiles are derived from the actual capabilities of the seeded model catalog (mirroring
waterfall). Reasoning-based models use `ReasoningChoicesJson` on the model entity to drive
the Reasoning Effort selector; their profiles carry empty sampling parameters.

| Profile ID | Display Name | Kind | Sampling Parameters |
|---|---|---|---|
| `openai-chat-standard` | OpenAI Chat Standard | cloud | temperature 0–2 step 0.1, top_p 0–1 step 0.05 |
| `openai-responses-reasoning` | OpenAI Responses (Reasoning) | cloud | _(empty — reasoning choices on model drive UI)_ |
| `anthropic-standard` | Anthropic Standard | cloud | temperature 0–1 step 0.05, top_p 0–1 step 0.05 |

Seeded model → profile assignments:

| Model ID | Provider | Profile |
|---|---|---|
| gpt-4.1 | openai-chat | `openai-chat-standard` |
| gpt-4.1-mini | openai-chat | `openai-chat-standard` |
| gpt-4o | openai-chat | `openai-chat-standard` |
| gpt-4o-mini | openai-chat | `openai-chat-standard` |
| gpt-5-chat | openai-chat | `openai-chat-standard` |
| gpt-5 | openai-responses | `openai-responses-reasoning` |
| gpt-5-mini | openai-responses | `openai-responses-reasoning` |
| o3 | openai-responses | `openai-responses-reasoning` |
| o4-mini | openai-responses | `openai-responses-reasoning` |
| gpt-5.2-codex | openai-responses | `openai-responses-reasoning` |
| claude-opus-4-5 | anthropic | `anthropic-standard` |
| claude-sonnet-4-5 | anthropic | `anthropic-standard` |
| claude-haiku-4-5 | anthropic | `anthropic-standard` |

Operators adding models via other providers (Gemini, HuggingFace, OpenRouter) create a
cloud profile with appropriate sampling parameters at model-add time; no generic
catch-all profiles are seeded for these.

### 6. No changes required

The following are already correct and require no modification once the above changes land:

- `ConfigParams.tsx` — already renders controls from `model.samplingParameterPolicy`
- `ConfigurationTab.tsx` — already passes the model through `ChatModelConfigurator`
- `ChatModelConfigurator.tsx` — already calls `ConfigParams` with the model
- `OverviewTab.tsx` — already uses `ChatModelConfigurator` for the default chat model

---

## Expected Outcome

- Every model in the catalog references a RuntimeProfile
- `CatalogService.GetModelsAsync()` returns populated `samplingParameterPolicy` for all
  active models
- The Configuration tab in guide and assistant builders shows the correct sliders for all
  model types
- The Default Chat Model section in Settings → Overview shows the correct sliders
- Operators can define custom profiles for any provider and reference them when adding
  or editing models
