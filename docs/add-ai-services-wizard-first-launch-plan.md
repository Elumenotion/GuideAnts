# Add AI Services Wizard (First-Launch) Plan

Last updated: 2026-04-29

Status: draft for iterative refinement.

## 1. Goal

Add an **Add AI Services Wizard** that opens automatically when the instance is
not setup-ready for AI usage: no configured **Connections** and/or no defined
model. Advanced users can cancel and configure manually.

This first-launch flow is for the open-source deployment model, where operators
must provide credentials/runtime configuration that SaaS normally has at deploy time.

## 2. Current Baseline (as-built)

- Home (`/`) is optimized for immediate usage (`Quick Start`, recent chats/projects),
  not setup-first onboarding.
- Startup gate currently polls `/api/startup`, which always reports ready and does
  not represent provider/service readiness.
- Real configuration surfaces already exist in Settings tabs:
  `Overview`, `Connections`, `Models & Runtime`, `Services`, `Infrastructure`.
- Model onboarding is already wizard-based via `AddModelWizard` in Settings.

Implication: we should build on existing Settings surfaces, not create a parallel
configuration system.

## 3. Product Requirements

1. Auto-open wizard when there are no configured **Connections**.
2. Auto-open wizard when there are no defined catalog models.
3. Allow cancel for advanced users who want manual setup.
4. Keep manual setup fully available and unchanged.
5. Reuse existing Settings APIs/contracts where possible.
6. Prepare for provider-stack profile guidance from:
   `docs/provider-stack-profiles-first-launch.md`.

## 4. Proposed UX (V1)

### 4.1 Entry points

- **Automatic**: from Home on first eligible load.
- **Manual**: `Settings` gets a visible `Add AI Services Wizard` action
  (for rerun after cancel/dismiss).

### 4.2 Auto-open trigger

Use existing `GET /api/settings/sections` (same source used by the Settings shell
and the Connections tab).

`Connections` tab semantics today:

- Each visible provider connection section has a `readinessStatus`:
  `configured`, `blocked`, `unconfigured`, or `not-applicable`.
- The tab’s readiness dot and “configured vs missing” meaning comes from this
  section-summary signal.

For first-launch auto-open, define:

- `connectionSectionNames =` same section-name set rendered by `ConnectionsTab`
  ownership categories (single shared constant source).
- `configuredConnections = sectionSummaries.filter(s => connectionSectionNames.has(s.sectionName) && s.readinessStatus === 'configured')`
- `models = GET /api/settings/models`
- `hasAtLeastOneModel = models.length > 0`
- `needsFirstLaunchWizard = configuredConnections.length === 0 || !hasAtLeastOneModel`

Current Connections tab section-name set (as-built):

- `AzureOpenAI`
- `OpenAI`
- `Anthropic`
- `GoogleGeminiApi`
- `OpenRouter`
- `HuggingFace`
- `AzureSpeechService`
- `AzureOpenAiImages`
- `AzureOpenAiEmbedding`
- `AzureDocumentIntelligence`

Note:

- This is intentionally aligned to current `ConnectionsTab` behavior.
- If `ConnectionsTab` taxonomy changes (for example, additional sections),
  first-launch detection should update automatically by reusing the same shared
  constant/module.

Auto-open only when:

- `needsFirstLaunchWizard === true`
- user has not dismissed persistent auto-open prompt
- sections + models calls succeeded

If section-summary or models load fails, do not auto-open (avoid blocking normal use on transient errors).

### 4.3 Cancel/manual behavior

Wizard footer actions:

- `Back` (always visible; disabled on first step)
- `Next` (always visible; step-validated)
- `Finish` (always visible; enabled once at least one model exists)
- `Configure manually`: closes wizard and opens Settings
- `Not now`: closes wizard, stays on current page

Include checkbox:

- `Don’t auto-open this again on this device`

Dismiss behavior:

- Clicking outside the wizard (backdrop/overlay) does **not** close it.
- Close/X and explicit footer actions are the supported dismissal paths.

This satisfies advanced-user cancel requirements without removing guided entry.

## 5. Wizard Scope (V1)

V1 should be an **orchestration wizard** over existing screens, not a full new
configuration editor.

Suggested steps:

1. **Choose setup path**
   - Recommended provider-stack profile cards (Azure OpenAI, Google Gemini,
     Local AI, OpenAI chat-only, Anthropic chat-only).
2. **Connections**
   - Deep-link to `Settings -> Connections`, focused section(s) for chosen stack.
3. **Models**
   - Add one or more chat models directly in wizard state.
   - Model step includes `Set this model as the global default chat model`.
   - If no models exist yet, the first added model is always forced as global
     default.
4. **Services**
   - Deep-link to `Settings -> Services` and service-specific editors.
5. **Verify**
   - Re-check `GET /api/settings/overview` and show readiness summary.

This keeps us aligned with current code and avoids duplicating editor logic.

## 6. UI Architecture (aligned to current client)

### 6.1 New component(s)

- `src/client/src/pages/settings/components/AddAiServicesWizard.tsx`
- optional: `src/client/src/pages/settings/components/first-launch/*`

Reuse existing primitives:

- `SettingsModal`
- `TextActionButton`
- existing tab routing patterns in `Settings.tsx`

### 6.2 Existing file touchpoints

- `src/client/src/pages/Home.tsx`
  - add section-summary probe and auto-open launch behavior.
- `src/client/src/pages/Settings.tsx`
  - host wizard state and manual launch action.
  - reuse existing handlers:
    - `handleOpenConnections`
    - `handleOpenServices`
    - `handleOpenModelsRuntime`
- `src/client/src/pages/settings/components/ConnectionsTab.tsx`
  - extract/share connection section-name taxonomy so first-launch and
    Connections use the same source of truth.
- `src/client/src/pages/settings/types.ts`
  - add first-launch wizard state types.
- `src/client/src/services/api.ts`
  - no new endpoint required for V1 (reuse `api.settings.getSections`, `api.settings.getModels`, existing settings APIs).

### 6.3 Persistence keys

Use local storage for deterministic user preference:

- `guideants.firstLaunch.addAiServicesWizard.dismissed.v1`
- optional session key for “already auto-opened this session”.

## 7. Provider Stack Profile Integration (V2 direction)

Use `docs/provider-stack-profiles-first-launch.md` as source of truth for
profile metadata and gaps.

V2 should add profile-specific checklists and completion tracking:

- **Azure OpenAI**: broadest cloud stack, includes non-chat services.
- **Google Gemini**: partial stack, known service model blockers.
- **Local AI**: runtime-host dependent, infrastructure validation heavy.
- **OpenAI / Anthropic**: explicitly chat-only in current architecture.

Important: wizard copy must honestly label partial/chat-only stacks.

## 8. Backend/API Plan

### 8.1 V1

- No new backend endpoint required.
- Use existing:
  - `GET /api/settings/sections`
  - `GET /api/settings/models`
  - existing settings sections/models/services endpoints.

### 8.2 V2+ (optional)

- Add server-side orchestration endpoint for atomic profile apply if needed,
  but only after validating V1 orchestration UX.

## 9. Acceptance Criteria (V1)

1. With zero connection sections in `readinessStatus === "configured"` (using the same
   section set as Connections tab), Home triggers wizard auto-open.
2. With zero catalog models from `GET /api/settings/models`, Home triggers wizard auto-open.
3. Wizard does not auto-open only when both are true:
   - at least one configured connection section
   - at least one model defined
4. Footer actions `Back`, `Next`, and `Finish` are always visible; labels do
   not change by step.
5. `Finish` stays disabled until at least one model exists, then becomes active.
6. Clicking the modal backdrop does not dismiss the wizard.
7. `Configure manually` closes wizard and lands user in Settings.
8. `Not now` closes wizard without blocking usage.
9. “Don’t auto-open again” prevents future automatic opens on that device.
10. Manual launcher in Settings always opens wizard.
11. Existing manual settings flows remain functional and unchanged.
12. If this is the first model, it is automatically written as
    `ChatDefaults:DefaultModelId`; otherwise the operator can choose whether
    the newly-added model becomes the global default.

## 10. Test Plan

Frontend (Vitest):

- Home auto-open predicate tests from mocked `api.settings.getSections` + `api.settings.getModels`.
- Shared taxonomy tests: first-launch detection and Connections tab include the
  same section-name set.
- Predicate matrix tests:
  - connections=0, models=0 -> open
  - connections>0, models=0 -> open
  - connections=0, models>0 -> open
  - connections>0, models>0 -> no auto-open
- Dismissal preference persistence tests (localStorage).
- Settings manual launcher + wizard open/close tests.
- Deep-link action tests (Connections/Services/Models & Runtime navigation hooks).
- Footer contract tests: `Back`/`Next`/`Finish` always rendered, `Finish`
  activation only after model existence.
- Model default tests:
  - first added model auto-sets global default
  - when models already exist, optional checkbox can set the new model as
    global default
- Backdrop click does not dismiss wizard.

Integration / E2E:

- First-run with empty config -> wizard opens.
- Cancel/manual path -> user can configure via existing tabs.
- Revisit behavior with dismissal key set.

## 11. Delivery Phases

### 11.1 Phase 1 (MVP)

- Auto-open detection on Home.
- Wizard shell with cancel/manual pathways.
- Settings manual relaunch button.
- Persistence key support.

### 11.2 Phase 2

- Provider-stack cards + guided checklist powered by existing APIs.
- Verification step with live readiness refresh.

### 11.3 Phase 3

- Optional server-side profile-apply orchestration for faster setup.

## 12. Non-goals (for V1)

- Replacing existing Settings editors.
- Creating duplicate forms for connection/service fields.
- Hiding advanced manual configuration.
- One-click profile mutation backend endpoint.
