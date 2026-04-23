# Notebook Header Service Toolbar Implementation Plan

Last updated: 2026-04-23

Companion spec:
- `docs/notebook-header-service-toolbar-spec.md`

## 1. Goal

Implement a notebook-header toolbar centered in `NotebookLayout` that provides
fast operational controls for these services, in this order:

1. Chat
2. Image Generation
3. Speech Synthesis
4. Speech Transcription

Explicitly excluded from this implementation:

- Embeddings
- Document Intelligence

This implementation is a quick-control surface only. It must not add setup
flows, credentials editing, downloads, or model onboarding.

## 2. Locked Implementation Assumptions

### 2.1 Chat semantics

The chat segment must not edit assistant definitions and must not mutate
`ChatDefaults`.

The cleanest path with the current codebase is:

- treat the toolbar chat selector as a **conversation-scoped model override**
- persist that override in `ConversationCurrentState.CurrentModelDeploymentId`
- send the override through the existing `SendMessageRequest.ModelDeploymentId`
  path

This keeps the behavior notebook/conversation-relevant without turning the
toolbar into assistant editing or global settings editing.

### 2.2 Runtime power semantics

For local-capable services:

- `Off` means unload/stop the local runtime and release memory
- `On` means restore the already-selected installed model/bundle/runtime

Where existing endpoints do not yet expose symmetric `On` / `Off`, backend work
 is required.

### 2.3 Read-model strategy

Do not force the header to fan out across many unrelated endpoints on first
paint. Add one notebook-scoped aggregate read endpoint for the toolbar, then
reuse existing write endpoints wherever possible.

## 3. Delivery Shape

The implementation should land in four workstreams:

1. Backend aggregate read model for the toolbar
2. Backend chat runtime / chat override mutations that do not exist yet
3. Frontend toolbar shell + per-service popovers/sheet
4. Tests for layout, notebook orchestration, toolbar UI, and new endpoints

## 4. Backend Plan

## 4.1 Add a notebook-scoped toolbar read model

### New files

- `src/server/GuideAntsApi/Models/NotebookHeaderToolbarDto.cs`
- `src/server/GuideAntsApi/Services/NotebookHeaderToolbar/INotebookHeaderToolbarService.cs`
- `src/server/GuideAntsApi/Services/NotebookHeaderToolbar/NotebookHeaderToolbarService.cs`
- `src/server/GuideAntsApi/Endpoints/NotebookHeaderToolbarEndpoints.cs`

### DTO shape

Create a notebook-scoped aggregate DTO that is compact and toolbar-oriented,
not a dump of settings DTOs. Suggested shape:

```csharp
public sealed record NotebookHeaderToolbarDto(
    NotebookToolbarChatDto Chat,
    IReadOnlyList<NotebookToolbarServiceDto> Services,
    DateTime GeneratedUtc);
```

Suggested per-service shape:

```csharp
public sealed record NotebookToolbarServiceDto(
    string ServiceId,
    string DisplayName,
    string Kind,            // chat | image | tts | asr
    string Status,          // ready | blocked | degraded | off | inProgress
    string Summary,
    string ActiveProviderId,
    string ActiveProviderLabel,
    bool SupportsLocalRuntimePower,
    bool LocalRuntimeOn,
    IReadOnlyList<NotebookToolbarProviderOptionDto> ProviderOptions,
    NotebookToolbarSelectionDto? Selection,
    IReadOnlyList<string> Blockers);
```

Suggested chat shape:

```csharp
public sealed record NotebookToolbarChatDto(
    string Status,
    string Summary,
    string? ConversationId,
    string? SelectedAssistantName,
    string? EffectiveModelId,
    string? EffectiveProvider,
    bool SupportsLocalRuntimePower,
    bool LocalRuntimeOn,
    IReadOnlyList<NotebookToolbarModelOptionDto> ModelOptions,
    IReadOnlyList<string> Blockers);
```

### Read sources

The aggregate service should compose from existing sources instead of inventing
new business logic:

- Chat catalog models:
  `src/server/GuideAntsApi/Endpoints/SettingsEndpoints.cs`
  `GET /api/settings/models`
- Chat target readiness:
  `RoutingReadinessService` / chat-target readiness path
- Notebook current conversation state:
  `src/server/GuideAntsApi/Services/Conversations/IConversationManager.cs`
- Notebook local llama status:
  `src/server/GuideAntsApi/Services/LlamaCpp/INotebookModelRuntimeService.cs`
- Service provider state:
  `src/server/GuideAntsApi/Settings/ApplicationSettingsService` service editor
  methods already surfaced by `/api/settings/services/{serviceId}`
- Local service runtime/model inventory:
  existing `settings.services.local-models` endpoints and readiness probes

### Existing files touched

- `src/server/GuideAntsApi/Program.cs`
  Register `MapNotebookHeaderToolbarEndpoints()`
- `src/server/GuideAntsApi/Configuration/StartupConfiguration.cs`
  Register the new toolbar service

## 4.2 Add notebook-scoped chat model override mutation

### Existing files touched

- `src/server/GuideAntsApi/Services/Conversations/IConversationManager.cs`
- `src/server/GuideAntsApi/Services/Conversations/ConversationManager.cs`
- `src/server/GuideAntsApi/Endpoints/NotebookConversationsEndpoints.cs`
- possibly `src/server/GuideAntsApi/Services/Conversations/ConversationService.cs`

### Required behavior

Add a notebook conversation endpoint that updates the current conversation model
override without editing the assistant definition.

Suggested endpoint:

- `PUT /api/projects/{projectId}/notebooks/{notebookId}/conversations/{convoId}/current-model`

Suggested body:

```json
{
  "modelDeploymentId": "gpt-5-mini"
}
```

Also support clearing the override:

```json
{
  "modelDeploymentId": null
}
```

### Implementation notes

- Persist to `ConversationCurrentState.CurrentModelDeploymentId`
- Invalidate the relevant conversation cache in `ConversationManager`
- Validate the requested model id against the active catalog
- Return the resolved effective model/provider summary used by the toolbar

### Why this path

The client streaming API already has a `modelDeploymentId` request field and
the server already honors it:

- client:
  `src/client/src/services/api.ts`
- server:
  `src/server/GuideAntsApi/Models/Conversations/ConversationDto.cs`
  `src/server/GuideAntsApi/Services/Conversations/ConversationService.cs`

This is a better fit than mutating assistants or global defaults.

## 4.3 Add notebook-scoped chat local-runtime `Off`

### Existing files touched

- `src/server/GuideAntsApi/Endpoints/NotebookLlamaRuntimeEndpoints.cs`
- `src/server/GuideAntsApi/Services/LlamaCpp/INotebookModelRuntimeService.cs`
- `src/server/GuideAntsApi/Services/LlamaCpp/NotebookModelRuntimeService.cs`

### Problem today

The notebook-scoped llama runtime endpoints support:

- status
- load
- operation poll
- restart

They do not expose a notebook-scoped unload/off path.

### Required behavior

Add:

- `POST /api/notebooks/{notebookId}/llama-runtime/unload`

Suggested contract:

- unload the local llama models currently loaded for the notebook conversation
  context
- serialize with the same alias lock / coordinator discipline already used by
  the load path
- invalidate router-model cache after unload

### Optional simplification

If notebook-scoped selective unload is unnecessarily complex for v1, the plan
may use a container-wide unload of the relevant loaded aliases so long as the
toolbar clearly labels the action as workspace-wide.

## 4.4 Reuse settings write paths for non-chat services

Do not create duplicate toolbar-specific write APIs when an authoritative
settings/runtime endpoint already exists.

### Reuse as-is

- provider switch:
  `PUT /api/settings/services/{serviceId}/active-provider`
- provider field persistence when needed:
  `PUT /api/settings/services/{serviceId}/providers/{providerId}`
- image bundle select-active:
  `POST /api/settings/services/ImageGeneration/local-models/{modelRef}/select-active`
- image engine load:
  `POST /api/settings/services/ImageGeneration/local-models/load`
- image engine unload:
  `POST /api/settings/services/ImageGeneration/local-models/unload`
- TTS installed model select:
  `POST /api/settings/services/SpeechSynthesis/local-models/{modelRef}/select-active`
- TTS unload:
  existing unload path, but backend support may need to be completed upstream
- ASR installed model select:
  `POST /api/settings/services/SpeechTranscription/local-models/{modelRef}/select-active`
- ASR unload:
  existing unload path, but backend support may need to be completed upstream

### Existing files touched

- `src/server/GuideAntsApi/Endpoints/SettingsEndpoints.cs`

Only touch this file when:

- a local service still lacks symmetric unload behavior
- the existing endpoint shape is insufficient for a setup-free quick switch

## 5. Frontend Plan

## 5.1 Add a header-center slot to `NotebookLayout`

### Existing files touched

- `src/client/src/components/layouts/NotebookLayout.tsx`
- `src/client/src/components/layouts/__tests__/NotebookLayout.test.tsx`

### Change

Add:

```ts
headerCenter?: React.ReactNode;
```

Render it between the current left identity block and right action block.

### Layout rules

- desktop: true three-zone header
- mobile: header-center becomes a single toolbar button that opens the sheet
- must preserve notebook title truncation and existing action visibility

## 5.1.1 Visual and interaction primitives to reuse

To conform to the current UI, the toolbar should reuse existing shared
primitives and styling direction instead of introducing a new visual language.

### Existing files reused

- `src/client/src/pages/settings/components/shared/ActionButtons.tsx`
- `src/client/src/components/common/dropdowns/CustomDropdown.tsx`
- `src/client/src/components/common/dropdowns/DropdownPanel.tsx`
- `src/client/src/components/common/ConfirmationDialog.tsx`
- `src/client/src/components/notebook/conversations/assistant-selector/AssistantButton.tsx`

### Concrete guidance

- top-level service controls should be implemented as button-style controls,
  visually aligned with the existing bordered rounded buttons
- reuse the current tone system:
  `primary`, `neutral`, `accent`, `info`, `success`, `danger`
- reuse the existing focus ring treatment:
  `focus-visible:ring-2 focus-visible:ring-blue-500`
- reuse the existing runtime action icon vocabulary already seen in settings:
  `FaPlay`, `FaStop`, `FaSpinner`, `FaSyncAlt`
- do not introduce pills, chips, or a new badge-led trigger style

## 5.1.2 Responsive behavior

### Existing files reused

- `src/client/src/components/layouts/NotebookLayout.tsx`
- `src/client/src/components/layouts/SidebarContainer.tsx`

### Concrete guidance

- desktop keeps an inline centered toolbar
- mobile collapses the toolbar into a single header trigger
- the mobile trigger opens a bottom sheet instead of a tiny dense dropdown
- use the same breakpoint logic already present in `NotebookLayout` and
  `SidebarContainer`
- preserve the existing mobile-first header affordances such as the sidebar
  toggle and back button

## 5.2 Create toolbar components

### New files

- `src/client/src/components/notebook/header-toolbar/NotebookServiceToolbar.tsx`
- `src/client/src/components/notebook/header-toolbar/NotebookServiceButton.tsx`
- `src/client/src/components/notebook/header-toolbar/NotebookServicePopover.tsx`
- `src/client/src/components/notebook/header-toolbar/NotebookServiceSheet.tsx`
- `src/client/src/components/notebook/header-toolbar/ChatToolbarPanel.tsx`
- `src/client/src/components/notebook/header-toolbar/ImageToolbarPanel.tsx`
- `src/client/src/components/notebook/header-toolbar/TtsToolbarPanel.tsx`
- `src/client/src/components/notebook/header-toolbar/AsrToolbarPanel.tsx`
- `src/client/src/components/notebook/header-toolbar/types.ts`
- `src/client/src/components/notebook/header-toolbar/toolbarFormatters.ts`
- `src/client/src/components/notebook/header-toolbar/useToolbarA11y.ts` (optional helper)

### Responsibilities

- toolbar shell and ordering
- top-level service buttons
- per-service popover/sheet panels
- small shared pieces: status badges, summary text, confirm rows, refresh button

### Accessibility expectations

Every new component should inherit existing accessibility patterns:

- top-level service buttons expose `aria-expanded` and `aria-haspopup`
- popovers use the dropdown/listbox semantics already used by the assistant
  selector, unless a menu-button pattern is a better fit
- icon-only actions use `aria-label` and visible tooltip/title text
- destructive confirmations use the shared modal dialog pattern in
  `ConfirmationDialog.tsx`
- focus returns to the triggering toolbar button when a popover or sheet closes

## 5.3 Add a toolbar controller at the notebook page level

### Existing files touched

- `src/client/src/pages/NotebookDetails.tsx`
- `src/client/src/pages/__tests__/NotebookDetails.test.tsx`

### New files

- `src/client/src/hooks/useNotebookHeaderToolbar.ts`

### Why page-level

`NotebookLayout` is rendered outside `ConversationProvider`, so the header
toolbar cannot depend directly on `ConversationContext` as currently composed.

The page-level hook should own:

- initial load of the toolbar aggregate DTO
- refresh and in-progress polling
- currently selected conversation id
- notebook-scoped chat model override state
- mutation handlers that call API methods

It should pass:

- `headerCenter={<NotebookServiceToolbar ... />}`

into `NotebookLayout`.

## 5.4 Thread chat override state into conversation send

### Existing files touched

- `src/client/src/contexts/conversation/types.ts`
- `src/client/src/contexts/ConversationContext.tsx`
- `src/client/src/contexts/conversation/useConversationActions.ts`
- possibly `src/client/src/contexts/conversation/reducer.ts`

### Recommended change

Add a provider prop such as:

```ts
modelDeploymentOverrideId?: string | null;
```

to `ConversationProvider`.

Then update `useConversationActions.sendMessage()` to forward:

```ts
modelDeploymentId: modelDeploymentOverrideId ?? undefined
```

through the existing streaming request.

### Why this is preferable

- no assistant mutation required
- no `ConversationHeader` rewrite required
- notebook page can keep header toolbar state at the top while conversation send
  remains context-owned

## 5.5 Add client API methods

### Existing files touched

- `src/client/src/services/api.ts`

### New methods

Notebook-scoped toolbar aggregate:

- `api.notebooks.toolbar.get(notebookId, conversationId?)`

Chat override:

- `api.projects.notebooks.conversations.setCurrentModel(projectId, notebookId, convoId, modelDeploymentId)`

Notebook llama unload:

- `api.projects.notebooks.conversations.unloadLlamaRuntime(projectId, notebookId)`

If the aggregate endpoint exposes typed DTOs, also add corresponding client
types:

- `src/client/src/types/notebookToolbar.ts`

## 5.6 Chat toolbar segment

### Existing files reused

- `src/client/src/components/chat-model/ChatModelConfigurator.tsx`
- `src/client/src/components/guides/editor/ModelSelector.tsx`

### Planned implementation

Do not embed the full `ChatModelConfigurator`; it includes sampling controls and
settings-like detail that the toolbar should not expose.

Instead:

- reuse the model-list loading pattern or selector primitives
- render a compact button-triggered menu of existing chat catalog models
- show effective provider and local-runtime state
- if the chosen model is `llama-cpp`, wire `On` / `Off` to notebook llama
  runtime endpoints

### Existing files optionally touched

- `src/client/src/components/notebook/conversations/ConversationHeader.tsx`

Only touch this file if product wants the current assistant/model summary in the
conversation header to visually reflect toolbar changes. It is not required for
first implementation.

## 5.7 Image toolbar segment

### Existing files reused

- `src/client/src/pages/settings/editors/image-generation/ImageBundleManager.tsx`
- `src/client/src/services/api.ts` local-model methods

### Planned implementation

Do not reuse the whole settings manager component in the header.

Instead build a compact read/write projection that uses the same backend:

- read current bundle list and engine state from the aggregate endpoint
- show active bundle and loaded bundle
- allow select-active among already-installed complete bundles
- allow `On` / `Off` via load/unload

## 5.8 TTS toolbar segment

### Existing files reused

- `src/client/src/pages/settings/editors/speech-synthesis/TtsModelManager.tsx`

### Planned implementation

Use the same installed-model inventory concept but strip it down to:

- provider switch
- installed model selector
- `On`
- `Off`
- short readiness/error line

No download modal, no file picker, no advanced runtime controls.

## 5.9 ASR toolbar segment

### Existing files reused

- `src/client/src/pages/settings/editors/speech-transcription/AsrModelManager.tsx`

### Planned implementation

Same approach as TTS:

- provider switch
- installed model selector
- `On`
- `Off`
- short readiness/error line

## 5.10 Mobile sheet

### New files

- `src/client/src/components/notebook/header-toolbar/NotebookServiceSheet.tsx`

### Behavior

- single toolbar button in the mobile notebook header
- opens a bottom sheet
- service order remains Chat, Image, TTS, ASR
- each service gets a stacked control section using the same data model as
  desktop popovers

### Touch requirements

- minimum tap targets should be larger than the current compact desktop icon
  buttons
- no hover-only meaning or interactions
- no critical action hidden behind tooltip-only explanations
- confirm dialogs must be easy to dismiss and confirm on touch

## 6. Tests

## 6.1 Frontend tests to add or update

### Existing files touched

- `src/client/src/components/layouts/__tests__/NotebookLayout.test.tsx`
  Add assertions for `headerCenter`
- `src/client/src/pages/__tests__/NotebookDetails.test.tsx`
  Update mocked `NotebookLayout` and verify toolbar mount conditions
- `src/client/src/components/notebook/conversations/assistant-selector/__tests__/AssistantDropdown.test.tsx`
  Use as the reference interaction pattern for dropdown semantics if helpful
- `src/client/src/contexts/__tests__/runtimeChecks.test.ts`
  Update if notebook llama unload or chat override changes runtime assumptions
- `src/client/src/contexts/__tests__/useStreamingEventHandler.test.tsx`
  Update if chat override metadata is surfaced in streaming UI

### New test files

- `src/client/src/components/notebook/header-toolbar/__tests__/NotebookServiceToolbar.test.tsx`
- `src/client/src/components/notebook/header-toolbar/__tests__/ChatToolbarPanel.test.tsx`
- `src/client/src/components/notebook/header-toolbar/__tests__/ImageToolbarPanel.test.tsx`
- `src/client/src/components/notebook/header-toolbar/__tests__/TtsToolbarPanel.test.tsx`
- `src/client/src/components/notebook/header-toolbar/__tests__/AsrToolbarPanel.test.tsx`
- `src/client/src/hooks/__tests__/useNotebookHeaderToolbar.test.ts`

### What to verify

- correct service order
- excluded services do not render
- desktop toolbar vs mobile sheet behavior
- button-style triggers render instead of pill/chip UI
- keyboard open/close and focus return behavior
- touch/mobile trigger path renders correctly
- provider switch calls the correct API
- image `On` / `Off` and bundle switch call correct APIs
- TTS/ASR model switch and `On` / `Off` call correct APIs
- chat model switch persists and affects subsequent send requests

## 6.2 Backend tests to add or update

### Existing files touched

- `src/server/GuideAntsApi.IntegrationTests/Infrastructure/SettingsRoutingIntegrationTestBase.cs`
  if shared helpers are useful
- `src/server/GuideAntsApi.Tests/Services/Routing/LlamaRuntimeCoordinatorTests.cs`
  if unload path extends coordinator usage
- `src/server/GuideAntsApi.Tests/Services/Routing/RoutingReadinessServiceTests.cs`
  if toolbar aggregate reuses readiness composition directly

### New test files

- `src/server/GuideAntsApi.Tests/Endpoints/NotebookHeaderToolbarEndpointsTests.cs`
- `src/server/GuideAntsApi.Tests/Services/NotebookHeaderToolbarServiceTests.cs`
- `src/server/GuideAntsApi.Tests/Services/LlamaCpp/NotebookModelRuntimeServiceUnloadTests.cs`
- `src/server/GuideAntsApi.Tests/Services/Conversations/ConversationManagerCurrentModelTests.cs`
- `src/server/GuideAntsApi.IntegrationTests/NotebookHeaderToolbar/NotebookHeaderToolbarIntegrationTests.cs`

### What to verify

- aggregate endpoint returns services in required order
- excluded services are absent
- chat model override updates current conversation state
- send-message path uses the override on later turns
- notebook llama unload returns coherent status
- image/TTS/ASR selections remain setup-free and bounded to installed inventory

## 7. Suggested Delivery Sequence

## Phase 1: backend read model

1. Add toolbar DTOs
2. Add aggregate service
3. Add aggregate endpoint
4. Register endpoint/service

This unblocks the frontend shell with real data.

## Phase 2: layout shell

1. Add `headerCenter` to `NotebookLayout`
2. Add page-level toolbar hook
3. Render placeholder toolbar with mocked handlers
4. Update layout/page tests

## Phase 3: non-chat controls

1. Image segment
2. TTS segment
3. ASR segment

These are the least ambiguous because they map cleanly to the existing settings
runtime endpoints.

## Phase 4: chat controls

1. Add notebook-scoped current-model mutation
2. Thread model override through `ConversationProvider`
3. Add notebook llama unload endpoint
4. Add chat toolbar panel

## Phase 5: mobile sheet + polish

1. Mobile sheet
2. Workspace-wide warning copy
3. final error/loading polish
4. accessibility pass

## 8. Risk Areas

### 8.1 Header / context boundary

Risk:

- header is outside `ConversationProvider`

Mitigation:

- keep toolbar orchestration in `NotebookDetails`
- pass only the minimum model override prop into `ConversationProvider`

### 8.2 Chat semantics drift

Risk:

- accidentally turning toolbar chat controls into assistant editing or global
  settings editing

Mitigation:

- lock the implementation to `ConversationCurrentState.CurrentModelDeploymentId`
- do not mutate assistants or `ChatDefaults`

### 8.3 Local runtime symmetry gaps

Risk:

- TTS/ASR/image may not all expose a reliable unload/off path yet

Mitigation:

- complete backend symmetry before exposing the final button
- do not ship a fake off-state that leaves memory resident

## 9. File Touchpoint Matrix

### Frontend existing files

- `src/client/src/components/layouts/NotebookLayout.tsx`
- `src/client/src/components/layouts/__tests__/NotebookLayout.test.tsx`
- `src/client/src/pages/NotebookDetails.tsx`
- `src/client/src/pages/__tests__/NotebookDetails.test.tsx`
- `src/client/src/services/api.ts`
- `src/client/src/contexts/conversation/types.ts`
- `src/client/src/contexts/ConversationContext.tsx`
- `src/client/src/contexts/conversation/useConversationActions.ts`
- optionally `src/client/src/components/notebook/conversations/ConversationHeader.tsx`

### Frontend new files

- `src/client/src/types/notebookToolbar.ts`
- `src/client/src/hooks/useNotebookHeaderToolbar.ts`
- `src/client/src/components/notebook/header-toolbar/NotebookServiceToolbar.tsx`
- `src/client/src/components/notebook/header-toolbar/NotebookServiceButton.tsx`
- `src/client/src/components/notebook/header-toolbar/NotebookServicePopover.tsx`
- `src/client/src/components/notebook/header-toolbar/NotebookServiceSheet.tsx`
- `src/client/src/components/notebook/header-toolbar/ChatToolbarPanel.tsx`
- `src/client/src/components/notebook/header-toolbar/ImageToolbarPanel.tsx`
- `src/client/src/components/notebook/header-toolbar/TtsToolbarPanel.tsx`
- `src/client/src/components/notebook/header-toolbar/AsrToolbarPanel.tsx`
- `src/client/src/components/notebook/header-toolbar/types.ts`
- `src/client/src/components/notebook/header-toolbar/toolbarFormatters.ts`

### Backend existing files

- `src/server/GuideAntsApi/Program.cs`
- `src/server/GuideAntsApi/Configuration/StartupConfiguration.cs`
- `src/server/GuideAntsApi/Endpoints/NotebookConversationsEndpoints.cs`
- `src/server/GuideAntsApi/Endpoints/NotebookLlamaRuntimeEndpoints.cs`
- `src/server/GuideAntsApi/Endpoints/SettingsEndpoints.cs`
- `src/server/GuideAntsApi/Services/Conversations/IConversationManager.cs`
- `src/server/GuideAntsApi/Services/Conversations/ConversationManager.cs`
- `src/server/GuideAntsApi/Services/LlamaCpp/INotebookModelRuntimeService.cs`
- `src/server/GuideAntsApi/Services/LlamaCpp/NotebookModelRuntimeService.cs`

### Backend new files

- `src/server/GuideAntsApi/Models/NotebookHeaderToolbarDto.cs`
- `src/server/GuideAntsApi/Services/NotebookHeaderToolbar/INotebookHeaderToolbarService.cs`
- `src/server/GuideAntsApi/Services/NotebookHeaderToolbar/NotebookHeaderToolbarService.cs`
- `src/server/GuideAntsApi/Endpoints/NotebookHeaderToolbarEndpoints.cs`

## 10. Done Definition

This implementation is done when:

1. The notebook header contains a centered toolbar, not a settings clone.
2. The toolbar order is Chat, Image Generation, Speech Synthesis, Speech
   Transcription.
3. Embeddings and Document Intelligence are absent.
4. Chat model switching changes the effective model for subsequent notebook
   turns without editing assistants or `ChatDefaults`.
5. Local `Off` / `On` works for every in-scope local-capable service and
   actually releases/restores memory.
6. No setup flows, downloads, credentials, or model onboarding appear in the
   toolbar.
7. The desktop and mobile variants share the same data model and mutation
   semantics.
