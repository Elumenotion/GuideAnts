# Notebook Header Service Toolbar Specification

Last updated: 2026-04-23

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

That button opens a bottom sheet containing the same service controls in a
stacked layout.

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

Each top-level service button/group shows four things in compact form:

1. service name
2. readiness state
3. active provider label
4. runtime/model summary

Example summaries:

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

Each expanded service panel follows the same order:

1. service title + one-line scope note
2. current readiness summary
3. provider selector
4. model or bundle selector, only if valid for that service
5. local runtime power section, only if valid for that service
6. concise blocker details when not ready
7. `Open in Settings` link

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

### 9.2 Model Switching

- Model switching is allowed only when the app already has a bounded,
  pre-existing inventory to choose from.
- The toolbar must not expose raw text inputs for model ids, paths, or
  deployment names.
- The toolbar must not browse or download models.

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

Toolbar support:

- show readiness
- show the effective chat model in use for the notebook conversation surface
- show the effective chat provider
- switch between already-available chat models in the catalog
- if the effective chat model is local, show local runtime readiness
- if the effective chat model is local, turn the local runtime off to release
  memory
- if the effective chat model is local, turn the local runtime on again using
  the current selected local chat model

V1 restriction:

- no assistant editing
- no add-model flow
- no direct catalog editing
- no runtime-profile editing

Reason:

- chat is central to notebook work and belongs first in the toolbar order
- this surface controls existing catalog-backed chat choices, not chat setup

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

Example:

`Switch Image Generation from Local to Azure for the whole workspace?`

## 13. Data Sources and Endpoint Reuse

The toolbar should reuse existing settings and runtime endpoints where possible.

Read paths:

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

## 19. Proposed Client Structure

Suggested component split:

- `NotebookServiceToolbar`
- `NotebookServiceButton`
- `NotebookServicePopover`
- `NotebookServiceSheet`
- `NotebookServiceStatusBadge`

Suggested `NotebookLayout` change:

- add a `headerCenter?: React.ReactNode` prop
- render it between the existing left and right header groups

This keeps the notebook page responsible for data orchestration while the
layout remains a generic shell.

## 20. Polling and Freshness

The toolbar must feel live, but it should not spam the backend.

Recommended behavior:

- initial load on notebook page open
- background refresh on a modest interval while the page is visible
- faster polling only while a toolbar-triggered operation is in progress
- manual refresh affordance inside the expanded panel or the sheet header

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
   desktop and a collapsed button exists on mobile.
2. The toolbar shows readiness and active provider or effective model summary
   for the in-scope services in this order: Chat, Image Generation, Speech
   Synthesis, Speech Transcription.
3. Provider switching is possible from the toolbar without navigating to
   Settings.
4. The toolbar does not allow adding configurations, editing credentials,
   downloading models, or creating runtime profiles.
5. Chat appears first and supports quick switching among existing catalog
   choices without exposing setup flows.
6. Image Generation supports active bundle switching and engine off/on from the
   toolbar using already-installed bundles only.
7. Speech Synthesis and Speech Transcription support switching among
   already-installed local models and explicit off/on runtime control without
   exposing setup flows.
8. Embeddings and Document Intelligence are excluded from this toolbar.
9. Every mutation is clearly labeled as workspace-wide.
10. Every service panel includes an `Open in Settings` escape hatch.

## 23. Explicit Deferrals

The following are intentionally deferred beyond this spec:

- assistant-level chat model mutation from the notebook header
- adding embeddings to this toolbar
- adding Document Intelligence to this toolbar
- exposing advanced service tuning in the notebook header
- setup and download workflows
- replacing Settings as the canonical operations workspace
