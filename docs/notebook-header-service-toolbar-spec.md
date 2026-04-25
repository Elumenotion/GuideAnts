# Notebook Header Service Toolbar Specification

Last updated: 2026-04-23 (spec text reconciled with in-repo implementation)

This document describes product intent, UX expectations, and **current as-built
behavior** where the implementation is complete. A consolidated list of
**gaps and deviations** appears in [§24](#24-implementation-alignment-and-deviations).

## 1. Purpose

Define a compact, notebook-native control surface that lives in the center of
the `NotebookDetails` header and gives operators fast access to the service
controls they already use during notebook work:

- switch between already-configured providers
- see service readiness and runtime state at a glance
- turn local runtimes off and on to manage memory
- switch to already-available models / bundles where the runtime already
  exposes a first-class selection concept

This surface is an operations shortcut, not a configuration workspace.

## 2. Core Product Decision

This is **not** a setup feature.

The toolbar must let the user operate on already-configured services, but it
must **not** let the user:

- add or edit provider credentials
- create new provider configurations
- add catalog models
- download models from Hugging Face
- create runtime profiles
- browse repositories
- enter raw deployment ids, endpoints, paths, or secrets
- manually manage llama aliases or local runtime inventory as a setup task

If the current system is not ready for an action because configuration is
missing, the toolbar shows the status and offers a deep link to Settings. It
does not try to solve configuration from the notebook page.

## 3. Scope

The toolbar covers the same runtime and routing state that already exists in
Settings:

- service provider selection for the in-scope notebook runtime services
- runtime state for local services
- model or bundle selection only when there is already an existing selectable
  inventory behind the service

This toolbar does **not** introduce notebook-only shadow configuration. When
the user switches a provider or changes an active model from the toolbar, it
is operating on the same authoritative state used elsewhere in the app.

Because of that, the UI must label the surface as **workspace-wide** or
**instance-wide** so the user understands these actions are not local to one
notebook tab.

**As implemented (2026-04-23):** Most service controls (image, TTS, ASR) only
mutate shared settings. The **Chat** panel additionally reads
`ChatDefaults` (including the “override all chat models” flag) and, when
override is on, updates the **global** default model from the header. When
override is off, the panel shows the active catalog line items but does not
change the model from the toolbar. This differs from the earlier “conversation
override only” experiment—see [§24](#24-implementation-alignment-and-deviations).

## 4. Non-Goals

The following are explicitly out of scope for this control surface:

- replacing the full Settings experience
- exposing every diagnostic field from Settings
- free-form editing of provider sections
- manual llama model load workflows
- cloud connection onboarding
- per-service advanced tuning
- managing secrets
- multi-step setup wizards

## 5. Placement

### 5.1 Desktop

The control surface is added to the center of the
`NotebookLayout` header, between:

- the left identity block: notebook title, avatar, mobile menu
- the right action block: edit, back, help

`NotebookLayout.tsx` should move from a two-zone header to a three-zone header:

1. left: notebook identity
2. center: notebook service toolbar
3. right: existing actions

### 5.2 Mobile

The center toolbar does not remain always expanded on mobile.

On screens below the existing mobile breakpoint, it collapses to a single
button in the header, for example:

- `Services`
- `Runtime`
- `Controls`

**As implemented:** The trigger label is **`Services`**. The breakpoint matches
`768px` (same as `md` in `NotebookLayout` / `NotebookDetails` resize logic).
The sheet is a bottom-anchored white panel over a dimmed overlay; the overlay
starts below the main header (`~3.25rem` from the top) so it does not cover the
top app chrome. The mobile sheet **omits** the per-panel “Workspace controls…”
line that desktop popovers show (see [§24](#24-implementation-alignment-and-deviations)).

## 6. Information Architecture

The toolbar is a horizontal control bar made of service buttons and compact
status regions. Each top-level service control can expand into a popover on
desktop or a sheet section on mobile.

Initial services:

1. Chat
2. Image Generation
3. Speech Synthesis
4. Speech Transcription

Explicit exclusions from this toolbar:

- Embeddings
- Document Intelligence

## 7. Pill Design

The original target was a single compact “pill” that encodes all four fields at
a glance. **As implemented,** the collapsed control is slimmer: a **status
color dot** (mapped to the normalized status string) plus a **truncated service
name**; provider and model/bundle **summary** appear in the expanded popover or
sheet, not in the bar.

Each top-level service button/group *in the product target* should show four things in compact form:

1. service name
2. readiness state
3. active provider label
4. runtime/model summary

Example summaries (for panel body / future polish, not the current bar):

- `Chat  Ready  GPT-5`
- `Image  Ready  Local  bundle: flux-q5`
- `TTS  Ready  Local  loaded`
- `ASR  Blocked  Azure  credentials missing`

The toolbar must use a stable state color model:

- green: ready
- amber: degraded, warming, or partially ready
- red: blocked
- gray: off or inactive local runtime
- blue: in-progress operation

## 8. Popover / Sheet Content

Each expanded service panel follows the same order (target order):

1. service title + one-line scope note
2. current readiness summary
3. provider selector
4. model or bundle selector, only if valid for that service
5. local runtime power section, only if valid for that service
6. concise blocker details when not ready
7. `Open in Settings` link

**As implemented (2026-04-23):** Desktop popovers are titled only implicitly (no
`h2` in the popover); the first line is usually scope copy
(`WORKSPACE_CONTROLS_COPY` where `showWorkspaceCopy` is true) and then
`summary`. **Chat** interposes `ChatDefaults` UI: “Override all chat models”
checkbox, helper copy, then the list of **active** catalog models as buttons
(enabled only when override is on) **before** local power and Settings.
**Image, TTS, and ASR** use provider list → local model / bundle list →
engine or local power → Settings. A **Refresh** control on the **desktop
toolbar** re-fetches the aggregate (not in every spec draft). **ASR** currently
repeats readiness as a `Readiness: {status}` line (polish may consolidate).

The panel must stay compact. It is a quick-switch surface, not a full editor.

## 9. Interaction Rules

### 9.1 Provider Switching

- Provider switching is allowed only among providers already declared for that
  service.
- Switching provider uses the same authoritative service editor endpoints as
  Settings.
- The toolbar never exposes provider-section field editing.
- If the target provider is blocked by missing config, the user may still see
  it in the list, but choosing it must require explicit confirmation because it
  changes workspace-wide routing into a blocked state.

  **As implemented:** Provider buttons call the same active-provider update
  the Settings editor uses, **without** an extra confirmation when selecting a
  potentially blocked provider. This remains a product gap.

### 9.2 Model Switching

- Model switching is allowed only when the app already has a bounded,
  pre-existing inventory to choose from.
- The toolbar must not expose raw text inputs for model ids, paths, or
  deployment names.
- The toolbar must not browse or download models.

**As implemented (chat):** Model “switching” from the list updates
`ChatDefaults.defaultModelId` and keeps `OverrideAllChatModels` enabled when
the user picks a different row—it does **not** use a per-conversation-only
`CurrentModelDeploymentId` update in the current UI. The aggregate
`GET /api/notebooks/{id}/header-toolbar?conversationId=` still uses assistant +
resolver + `ChatDefaults:OverrideAllChatModels` for the **read** model.

### 9.3 Local Runtime Power

- For local-capable services, the toolbar must expose a true `Off` / `On`
  control surface, even if backend work is required to make that lifecycle
  consistent.
- `Off` means release memory by unloading or stopping the local runtime for the
  service.
- `On` means start or restore the local runtime using already-installed,
  already-configured state.
- The toolbar must not frame this as setup. The user is powering an existing
  runtime down or back up, not configuring it.
- If a local service does not yet expose symmetric on/off lifecycle endpoints,
  closing that gap is implementation work required by this spec rather than a
  reason to omit the control.

**As implemented:** `Off` / `On` for local image / TTS / ASR call the existing
load/unload **without** a confirmation dialog (unlike the **local chat** unload,
which uses a confirmation and copy about freeing memory for the workspace).

### 9.4 Setup Boundary

If the operator needs to:

- fix credentials
- install a model
- add a provider section
- create a runtime profile
- register a llama alias

the toolbar stops and routes them to Settings.

## 10. Service-by-Service Behavior

## 10.1 Chat

**Product target** (unchanged intent):

- show readiness
- show the effective chat model in use for the surfaces the resolver covers
- show the effective provider where available
- allow switching between catalog models **without** new setup flows, assistant
  editing, or runtime profile editing
- for llama-as-chat-model, expose local power consistent with other services

**As implemented (2026-04-23):**

- The toolbar **reads** the aggregate chat segment, including
  `overrideAllChatModels` from `ChatDefaults` and an effective model derived via
  `IChatModelResolver` / readiness for the current conversation’s assistant
  (when a conversation is selected).
- The panel loads **Settings → Chat defaults** in the client and presents an
  **“Override all chat models”** checkbox. Toggling it and picking a default
  model when override is on **mutates** `ChatDefaults` via
  `api.settings.chatDefaults.update`—i.e. **workspace-wide settings**, not a
  narrow conversation-only field.
- When **override is off**, catalog rows are **disabled**; the list reflects
  active items but the user cannot “quick switch” from the header.
- **Local chat runtime** load polls the llama operation; unload requests
  **confirmation** with workspace-oriented copy.
- **Deviation:** This goes beyond the original V1 “no `ChatDefaults` / catalog
  editing from the header” line in the first draft. It was introduced to align
  with real routing behavior and a single place to set global default chat
  models. Per-conversation `CurrentModelDeploymentId` may exist in the data
  model, but the **header UI** does not expose a dedicated
  “conversation-scoped only” model picker.

Reason (product):

- chat is central to notebook work and belongs first in the toolbar order
- operators need fast control over the same state Settings uses, without
  duplicating full Settings UI

## 10.2 Image Generation

Toolbar support:

- show readiness
- show active provider
- switch provider
- show active bundle
- show loaded bundle
- switch active bundle among already-installed bundles
- turn engine off to release GPU / RAM
- turn engine on using the already-active bundle

V1 restriction:

- no bundle download, edit, import, export, or removal from the notebook header

## 10.3 Speech Synthesis

Toolbar support:

- show readiness
- show active provider
- switch provider
- show active installed local model
- switch among already-installed local models
- turn the local runtime off to release memory
- turn the local runtime on again using the currently selected installed model

V1 restriction:

- no download flow
- no tokenizer/config authoring
- no arbitrary path entry

## 10.4 Speech Transcription

Toolbar support:

- show readiness
- show active provider
- switch provider
- show currently active installed local model when local is selected
- switch between already-installed local models using the existing active-model
  shortcut behavior
- turn the local runtime off to release memory
- turn the local runtime on again using the currently selected installed model

V1 restriction:

- no download flow
- no free-form model id or path entry
- no separate explicit "load this arbitrary model" action

Product rule:

- choosing a model from the bounded installed list is allowed even if the
  underlying service implements that selection by loading the chosen installed
  model under the hood
- the UI must frame this as `Change model`, not `Load model`

## 11. States

Every toolbar service control and panel should normalize to the following
user-facing
states:

1. `Ready`
2. `Blocked`
3. `Degraded`
4. `Off`
5. `In progress`

The detail panel may show one concise supporting line, for example:

- `Blocked: AzureOpenAI deployment missing`
- `Off: local engine stopped to save memory`
- `In progress: switching active image bundle`

Raw routing strings, section names, and JSON payloads must not be the primary
UI language.

## 12. Workspace-Wide Warning Model

Because the toolbar operates on shared service state, every mutation path must
make the blast radius obvious.

Requirements:

- the overall surface includes a small label such as `Workspace controls`
- destructive power-off actions use confirmation only when they may interrupt
  work already in progress
- provider changes and model changes should use lightweight confirmation copy
  when they can immediately affect the next notebook action

**As implemented (2026-04-23):** The string **“Workspace controls”** appears
next to the desktop toolbar only from the **`lg` breakpoint and up**
(`hidden lg:block`); on `md` the bar shows refresh + service buttons without that
wordmark. Each **desktop** popover (not the mobile sheet) also starts with
`WORKSPACE_CONTROLS_COPY` (`Workspace controls apply to this entire workspace, not
one notebook.`). **Local chat** unload has a **confirmation dialog**; other
engine load/unload paths **do not** yet. Provider/model **confirmations** as in
the “Example:” line below are **not** shown before applying changes.

Example:

`Switch Image Generation from Local to Azure for the whole workspace?`

## 13. Data Sources and Endpoint Reuse

The toolbar should reuse existing settings and runtime endpoints where possible.

**As implemented read path:** `GET
/api/notebooks/{notebookId}/header-toolbar?conversationId={optional}` returns
`NotebookHeaderToolbarDto` (see `GuideAntsApi` `NotebookHeaderToolbarService`,
`NotebookHeaderToolbarEndpoints`). The client hook `useNotebookHeaderToolbar`
polls this endpoint on a **45s** interval (or **2s** while `inFlight`), skips
work when the tab is not visible, listens for a `refresh-notebook-toolbar` window
event, and can `refresh` manually from the **desktop** icon button.

Read paths (still accurate at intent level; service implementation composes the above):

- settings overview for top-level readiness rollups
- service editor state for provider options and current selection
- local model list or runtime readiness endpoints for bounded local inventories
- llama runtime status only for summary or future read-only signals

Write paths:

- service active-provider update
- service provider-field updates only when a provider switch requires selecting
  an already-existing saved operative field set
- local model `select-active` where that already means "use this installed
  model"
- local load/unload only where it maps to engine power semantics, not setup

New backend work is acceptable and expected when existing endpoints do not yet
support the setup-free `Off` / `On` lifecycle required by this toolbar.

## 14. UX Requirements

1. No raw JSON editors.
2. No large modal wizard flows from the notebook header.
3. No settings-tab-level verbosity.
4. The top-level visual language must be a toolbar with buttons and compact
   control groups, not pills, chips, or badge-only affordances.
5. Every action must complete in one step or a very small confirmation dialog.
6. Every service panel must fit in a compact desktop popover width.
7. The toolbar must remain usable when the notebook title is long.
8. The toolbar must not push the existing right-side notebook actions offscreen.
9. Mobile must use a sheet, not a tiny overflowing horizontal scroll strip.

## 15. Accessibility Requirements

1. Every toolbar button is keyboard reachable.
2. Expanded panels use standard popover or dialog focus management.
3. Status is never color-only; it includes text.
4. Icon-only runtime actions must have explicit accessible labels.
5. Provider and model changes announce success or failure through the existing
   toast system and an inline state update.

## 16. Visual Language

The toolbar must conform to the existing notebook and settings interaction
language already used in the app.

### 16.1 Buttons

Use the same button direction as the shared settings action components:

- rounded rectangle buttons
- bordered white/neutral controls for non-primary actions
- blue primary actions
- compact icon + text composition
- blue focus ring

**As implemented:** `NotebookServiceButton` and list actions use
`textButtonClassName('neutral')` (shared with Settings action styling). The
**status dot** is a small `rounded-full` span, not a large badge.

The toolbar must not introduce a new pill, chip, segmented capsule, or badge-led
design language.

### 16.2 Icons

Use the existing runtime and action icon vocabulary already familiar in
Settings:

- `Play` for `On`
- `Stop` for `Off`
- `Spinner` for in-progress
- `Refresh` for manual refresh
- optional `Chevron` for dropdown/open state

Use icon-only buttons only for secondary actions inside the expanded panel, not
for the top-level service controls.

### 16.3 Colors

Match the existing app tones:

- blue for primary action and active focus
- white/slate neutral for default controls
- emerald for success / ready
- amber for degraded / caution
- red for blocked / failure / destructive
- gray or slate for inactive / off / unavailable

The header surface itself should remain aligned with the current notebook
header shell: white background, gray border, restrained contrast, no dark
theme divergence, no saturated custom gradients.

## 17. Responsive and Touch Behavior

### 17.1 Desktop

On desktop, the toolbar is inline in the header center and uses compact button
groups with popovers.

Requirements:

- service order remains Chat, Image Generation, Speech Synthesis, Speech
  Transcription
- the toolbar must not force the notebook title or right-side actions out of
  view
- desktop popovers should open from toolbar buttons and remain compact

### 17.2 Mobile

On mobile and other narrow layouts, the toolbar collapses to a single header
button that opens a bottom sheet.

Requirements:

- do not attempt to squeeze the full toolbar into a horizontally scrolling
  strip of tiny controls
- the sheet uses stacked service sections in the same order as desktop
- the mobile toolbar trigger should sit naturally beside the existing mobile
  header controls

### 17.3 Touch Devices

The toolbar must be touch-friendly.

Requirements:

- tap targets must be comfortably larger than tiny icon-only affordances
- no hover-only behavior
- no tooltip-only meaning
- open/close behavior must work reliably on touch without requiring precise
  pointer positioning
- power actions and provider/model changes must still be confirmable on touch

## 18. Interaction Accessibility Details

In addition to the baseline requirements above, the toolbar should inherit the
existing accessibility patterns already used in notebook and dropdown UI.

Requirements:

- top-level service controls expose `aria-expanded` and `aria-haspopup` when
  they open a panel
- selectable lists use listbox/option semantics or equally clear menu-button
  semantics
- icon-only buttons include explicit `aria-label`
- confirmation dialogs use true modal dialog behavior
- keyboard escape closes popovers and dialogs
- focus returns to the triggering toolbar button after a panel closes
- loading and status changes are represented in text, not just icons or color

**As implemented (2026-04-23):** **Escape** closes open popover and mobile
sheet (listened on `document`). Click outside the toolbar root closes popovers
(`mousedown` on `document`). **Focus return** to the opener after close and
full **modal** focus trapping for popovers are **not** verified in the current
`NotebookServicePopover` + `DropdownPanel` stack; `aria-modal` is **false** on
the popover container. Mobile sheet uses `aria-modal="true"` on the overlay.

## 19. Proposed Client Structure

**As implemented (2026-04-23)** in `src/client/src/components/notebook/header-toolbar/`:

- `NotebookServiceToolbar` — orchestration, mobile `Services` trigger, desktop
  refresh, popover state, `ConfirmationDialog` for local chat unload
- `NotebookServiceButton` — collapsed trigger (status dot + label, `aria-expanded`)
- `NotebookServicePopover` — anchors under each service button, uses
  `DropdownPanel`, `role="dialog"`, `aria-modal="false"`
- `NotebookServiceSheet` — mobile stacked sections
- `ChatToolbarPanel`, `ImageToolbarPanel`, `TtsToolbarPanel`, `AsrToolbarPanel` —
  per-service content
- `toolbarFormatters.ts` — `WORKSPACE_CONTROLS_COPY`, `statusDotClass` /
  `statusToneClass`

There is **no** separate `NotebookServiceStatusBadge` file; the dot lives inside
`NotebookServiceButton`.

**NotebookLayout** (`headerCenter?: React.ReactNode`):

- render the center group between identity and right actions, **visible from
  `md` up** as `hidden md:flex …`; on narrow viewports the child toolbar renders
  only the compact `Services` control in that slot
- `NotebookDetails` injects `NotebookServiceToolbar` with data from
  `useNotebookHeaderToolbar(notebookId, activeConversationId)` and
  `headerIsMobile` from `innerWidth < 768`

The notebook page stays responsible for data wiring; the layout stays a shell.

## 20. Polling and Freshness

The toolbar must feel live, but it should not spam the backend.

**As implemented:** Initial load and `conversationId` / `notebookId` changes
trigger fetch; **45s** background poll when idle; **2s** while `inFlight` is
true; **no** poll when `document.visibilityState` is not `visible`.
**Manual** refresh: **desktop** only, via a sync icon on the **toolbar** (not
inside each popover or the mobile sheet header). See also [§13](#13-data-sources-and-endpoint-reuse).

Recommended behavior (for future polish if needed):

- initial load on notebook page open
- background refresh on a modest interval while the page is visible
- faster polling only while a toolbar-triggered operation is in progress
- optional manual refresh inside the expanded panel or the sheet header

## 21. Failure Handling

When the toolbar cannot complete an action:

- show a toast
- keep the previous known-good selection visible until the server confirms the
  change
- surface one short inline error in the relevant service panel
- offer `Open in Settings` when the failure is configuration-shaped

The notebook header must never become a dumping ground for stack traces,
problem-details blobs, or upstream proxy envelopes.

## 22. Acceptance Criteria

1. A centered service toolbar exists in the `NotebookDetails` header on
   desktop and a collapsed button exists on mobile. **Met** (`headerCenter` +
   `Services` at `<768px`).
2. The toolbar shows readiness and active provider or effective model summary
   for the in-scope services in this order: Chat, Image Generation, Speech
   Synthesis, Speech Transcription. **Met in panels**; **collapsed bar** shows
   only status dot + name (summary in popover/sheet).
3. Provider switching is possible from the toolbar without navigating to
   Settings. **Met** (image, TTS, ASR; chat is provider-agnostic in the list).
4. The toolbar does not allow adding configurations, editing credentials,
   downloading models, or creating runtime profiles. **Met** for those—**note**
   chat does edit `ChatDefaults` (allowed models / override), which is
   settings-backed but not a “download or credential” flow.
5. Chat appears first and supports quick switching among existing catalog
   choices without exposing setup flows. **Partially met:** quick switch only when
   **“Override all chat models”** is on; when off, list is read-only in the
   header.
6. Image Generation supports active bundle switching and engine off/on from the
   toolbar using already-installed bundles only. **Met** via `localModels` +
   load/unload.
7. Speech Synthesis and Speech Transcription support switching among
   already-installed local models and explicit off/on runtime control without
   exposing setup flows. **Met.**
8. Embeddings and Document Intelligence are excluded from this toolbar. **Met.**
9. Every mutation is clearly labeled as workspace-wide. **Partially met**—copy
   exists in desktop popovers and the `lg+` “Workspace controls” label; **not**
   shown in the mobile sheet body; the wordmark is **absent** below `lg` on
   desktop.
10. Every service panel includes an `Open in Settings` escape hatch. **Met.**

## 23. Explicit Deferrals

The following are intentionally deferred beyond this spec:

- assistant-level chat model mutation from the notebook header
- adding embeddings to this toolbar
- adding Document Intelligence to this toolbar
- exposing advanced service tuning in the notebook header
- setup and download workflows
- replacing Settings as the canonical operations workspace

## 24. Implementation alignment and deviations

**Purpose:** This section is the **single place** to compare the original
product spec (§§1–18, goals in §20–22) to the code that shipped in this repo
as of 2026-04-23. It is the checklist before “polish and layout” work.

### 24.1 Aligned

- **Placement:** `NotebookLayout` has `headerCenter`; `NotebookDetails` injects
  the toolbar between identity and right actions. Center column is
  `hidden md:flex` for the **full** toolbar; below `md`, the slot still renders
  the **compact** `Services` control.
- **Service set and order:** Chat → Image → TTS → ASR; no Embeddings or
  Document Intelligence in the list.
- **Read model:** one aggregate,
  `GET /api/notebooks/{notebookId}/header-toolbar?conversationId=…`, composed
  on the server (`NotebookHeaderToolbarService`).
- **Write paths (non-chat):** Active provider, local `selectActive`, and
  load/unload for local engines reuse the same Settings-facing APIs as the
  spec intent.
- **State colors:** `toolbarFormatters` maps `ready` / `blocked` / `degraded` /
  `off` / in-progress to dot and text classes (emerald / red / amber / slate /
  blue).
- **Escape hatch:** Every panel has **Open in Settings** with a cog icon.
- **Exclusions:** No credential forms, no HF download, no raw JSON editors in
  the toolbar.

### 24.2 Deviations and gaps (polish queue)

1. **Collapsed bar density (§7):** Shows **dot + name** only, not a four-field
   mini-summary. Provider and readiness text are **in** the popover/sheet.
2. **Chat semantics (§3, §9.2, §10.1, companion implementation plan):** The UI
   toggles **`ChatDefaults.overrideAllChatModels`** and, when on, changes
   **`defaultModelId`** from the header. It does **not** (currently) use a
   dedicated **per-conversation** `PUT …/current-model` action in the
   `ChatToolbarPanel`. The aggregate read still takes `conversationId` for
   **effective** display, but the **primary write path** the user can invoke is
   **workspace-wide** chat defaults, not a conversation-only override panel.
3. **Quick model switch (§5 ac / §10.1):** Model buttons are **disabled** until
   **“Override all chat models”** is on—unlike a simple “always quick switch
   from catalog” story.
4. **Workspace copy (§12, §22.9):** The short **“Workspace controls”** label is
   only on **desktop `lg+`**. The mobile sheet does **not** include
   `WORKSPACE_CONTROLS_COPY` in section bodies (`showWorkspaceCopy={false}`).
5. **Confirmations (§9.1, §9.3, §12):** **No** confirmation for switching
   provider into a **blocked** state. **No** confirmation for image / TTS / ASR
   power-off (unlike local **chat** unload, which is confirmed). **No**
   lightweight “whole workspace?” copy (as in §12 example) before applying
   provider or model changes.
6. **Polling & refresh (§20):** Polling is **45s** idle / **2s** in-flight, not
   a tunable “modest” unspecified interval. Manual refresh is on the **toolbar**
   chrome, not inside every panel.
7. **Accessibility (§18):** `Escape` and outside click work; **modal focus
   trap** and **return focus to opener** are not guaranteed for desktop
   popovers. Popover uses `aria-modal="false"`.
8. **ASR copy:** An extra `Readiness: {status}` line is redundant with summary;
   may be removed during polish.
9. **Components (§19):** No `NotebookServiceStatusBadge` file; no separate
   `NotebookServicePill`—formatting is split across the button, formatters, and
   panels.

### 24.3 Files (reference)

| Area | Location |
|------|----------|
| Client shell | `src/client/src/components/notebook/header-toolbar/` |
| Page wiring | `src/client/src/pages/NotebookDetails.tsx` |
| Hook + poll | `src/client/src/hooks/useNotebookHeaderToolbar.ts` |
| DTOs (TS) | `src/client/src/types/notebookToolbar.ts` |
| API client | `src/client/src/services/api.ts` (`headerToolbar` helper) |
| Server | `src/server/GuideAntsApi/Services/NotebookHeaderToolbar/`, `…/Models/NotebookHeaderToolbarDto.cs`, `…/Endpoints/NotebookHeaderToolbarEndpoints.cs` |
