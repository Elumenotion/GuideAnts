# Local Llama UX Redo — File-Level Plan

Last updated: 2026-07-11

This plan replaces the shipped Phase 6/7 operator surface with the product goal from the Jul 10 conversation.

**Phase 1 (this plan):** client garbage removal + catalog-row editors.  
**Phase 2 (backend cleanup):** remove operator APIs and ceremony that mirror removed UI; delete misnamed Phase 6/7 internal machinery (`FleetLlama*`, migration issues SQL, customize).

Ops image rebuild remains prerequisite for curated E2E.

---

## Vocabulary — what a local model IS

**A local (llama-cpp) catalog model is a complete definition.** Three layers, one row:

1. HF artifacts  
2. **llama-server settings/args** for that model (`defaults.routerPreset` → alias section in `router-models.ini`)  
3. Chat behavior (`defaults.runtimeProfileId` → runtime profile)

**When a model is set to local, the system ensures llama-server has the right settings/args** — from that definition. Install, repair, and load apply the model's preset; the operator does not wire INI by hand and does not go to a separate screen to "configure llama."

**Layer 2 has a simple edit surface on that model's catalog row** — open preset keys (ctx-size, cache-ram, MTP, future llama.cpp switches). Save → `putLlamaRouterEntry` → system applies. This **is** the llama-server configuration for **this** model. Not a side quest. Not read-only.

**What "fleet" was (and why it's garbage):** Phase 6/7 **split layer 2 off the model** — SQL `FleetLlama*` store, revision algebra, a separate panel, `fleet-preset` API, "fleet-scoped" vs "alias-scoped" routing, `GA_LLAMA_*` editor. That invented a **parallel assembly path** next to the model definition. **Delete it.** Llama-server args for a local model belong **on the model**, not in a thing called fleet, not in a thing called globals, not in a third tab.

**What is NOT layer 2:** Profiles tab, Customize ceremony, migration issues panel, router mapping preview, diagnostics tables — assembly and noise, not model definition.

**Compose / `GA_LLAMA_*`:** installer bootstraps the container. It is **not** the operator edit surface for a local model's args. The operator edits **on the model row**. (Whether some process-wide keys also exist in compose is an implementation detail — they are **not** a separate product destination next to the model.)

---

## Product goal (one sentence)

**Curated install = pick model + quant; the definition supplies HF + llama-server args + chat behavior. The system applies them. Curated tune = simple edit on that model's catalog row (layer 2 preset + layer 3) — no Profiles tab, no fleet, no assembly ceremony.**

---

## What "holistic" means (non-negotiable)

A curated manifest entry (`docker/build/guideants-ai/llama-admin-service/catalog/manifest.json`) is a **complete recipe** for one offering. Each row declares HF source, `defaults.routerPreset`, and `defaults.runtimeProfileId` together. The operator does **not** pick profile, projector, ctx-size, MTP keys, or `parallel_tool_calls` at install.

| Layer | Declared in manifest | Operator at install | Operator after install (Catalog → Edit this llama-cpp row) |
|-------|----------------------|---------------------|--------------------------------------------------------------|
| **1 — HF artifacts** | `source.repository`, quant from live API | Pick **quant** only | Change quant (curated) |
| **2 — llama-server args** | `defaults.routerPreset` | Nothing — **system applies on install/load** | **Router preset** on catalog row — open key/value editor → `putLlamaRouterEntry` → system ensures llama-server matches |
| **3 — chat behavior** | `defaults.runtimeProfileId` (+ bootstrap profile in `Resources/bootstrap/runtime-profiles/`) | Nothing | **Not field forms.** Profile is bound by the recipe; operator changes behavior by **repair/adopt** when the manifest/profile defaults change, or by editing the **bound profile document** on this row (JSON fields — same extensibility model as layer 2’s open preset keys, not per-param React forms) |

**Holistic install:** one card → system wires all three layers; llama-server gets this model's args without operator assembly.

**Holistic tune:** layer 2 = simple preset editor **on the model** (the llama-server edit surface). Layer 3 = recipe-bound profile, repair/adopt, optional document edit — not schema forms.

### How layer 2 and layer 3 editing differ (both on catalog row)

| | Layer 2 (llama-server args) | Layer 3 (chat behavior) |
|--|------------------------|-------------------------|
| **Part of model definition** | Yes — `routerPreset` + installation snapshot | Yes — `runtimeProfileId` + profile |
| **System applies** | Install / repair / load writes INI | Profile bound at install; repair re-applies |
| **Operator edit surface** | Open preset keys on **this model's catalog row** | Repair/adopt; optional collapsed profile document |
| **Save** | `putLlamaRouterEntry` | `updateRuntimeProfile` (document path only) |

### Wrong interpretations — do not ship these

| Wrong | Why it fails |
|-------|----------------|
| Runtime Profiles as a third tab (even "demoted") | Treats layer 3 as something the operator must discover and assemble |
| Layer 3 post-install = "Nothing in normal UX" / read-only only | Contradicts holistic — behavior must be **present and effective** from the recipe; operator must be able to **re-apply** or override without a Profiles tab |
| Customize dialog to "unlock" preset/profile editing | Ceremony instead of direct edit on the model row |
| **Schema-driven forms** over layer 3 (sliders for `top_k`, mappers over `samplingParametersJson`, per-family React fields) | Too variable; every new model family becomes UI work — exactly what holistic curation avoids |
| Profiles tab / profile **entity management** as operator workflow | Assembly; separate from the model row |
| **Separate "fleet" / "globals" / "router defaults" UI** | Splits llama-server args **off the model** — layer 2 belongs on the model row |
| **"Compose only" for llama-server tuning** | Denies the model definition; operator tunes **on the model**, system applies |
| **Rejecting preset keys as "fleet-scoped" with "set in compose"** | Same split — if a key is part of this model's server config, it belongs in layer 2 on the row |
| **Read-only layer 2 after install** | Regresses working edit; ctx/MTP/cache must stay editable on the model |

### Advanced install only (not curated)

Custom Hugging Face and Attach existing alias still use a **profile dropdown** at install time (pick bootstrap profile ID). That is install-time selection, not a Profiles tab and not layer-3 editing.

### Shared profiles (v1 storage reality)

Layer-3 values live in SQL runtime profiles referenced by `runtimeProfileId`. Editing on a model row saves the bound profile. If multiple catalog rows share the same profile ID, show a warning before save: *"This updates chat behavior for all models using profile qwen3_6."* Editing still happens on the model row — operator does not go elsewhere. Per-model profile forks are out of scope for this redo.

---

## Catalog edit layout (llama-cpp row)

Operator sees this order in `CatalogRowEditModal` → `LlamaCppEditForm` → `LlamaInstalledSummary`:

1. **Presentation** — display name, description, active, display order
2. **Router preset** (layer 2) — `AliasPresetSavePanel` (open key/value; primary tune surface for llama.cpp)
3. **Chat behavior** (layer 3) — bound `runtimeProfileId`, effective profile summary, **repair/adopt** when recipe changes; optional collapsed **profile document** editor (JSON — not generated forms)
4. **Secondary ops** — change quant, repair, adopt
5. **Installation details** — collapsed provenance

Layers 2 and 3 are **never** buried inside installation details.

---

## Operator-visible acceptance

| Action | Where | Success |
|--------|-------|---------|
| Add model | Catalog → Add Model → llama-cpp | Curated picker default; Custom/Attach inside collapsed `<details>` |
| Install | Wizard review | Read-only snapshot of all three layers **before** commit (`LlamaCuratedReview` technical block) |
| Install completes | Wizard done | Catalog row exists; **llama-server has model's preset applied**; selectable in chat |
| Edit llama-server args (ctx, MTP, cache, future keys) | Catalog edit → **Router preset** on **this model** | Save → `putLlamaRouterEntry` → INI updated; system state matches definition |
| Re-apply layer 3 after curator updates manifest/profile | Catalog edit → **Chat behavior** + Repair | Repair/adopt pulls recipe defaults; profile binding unchanged unless manifest says so |
| Override layer 3 document (rare) | Catalog edit → collapsed **profile document** on row | Save → `updateRuntimeProfile`; shared-profile warning if applicable |
| Attach unbound alias | Loaded models | Link → wizard with alias pre-filled |
| **Must not exist** | Runtime Profiles tab | Gone |
| **Must not exist** | `FleetLlamaPanel`, migration panel, Profiles tab, Customize | Parallel paths that split config off the model |
| **Must not exist** | Migration issues panel, Customize dialog, Technical configuration panel | Deleted |
| **Must not exist** | Router mapping preview, runtime diagnostics second table | Deleted from Loaded models |
| **Must not exist** | `managementMode` as prominent operator UI | Internal only; collapsed provenance at most |

**Models & Runtime sub-tabs:** `Catalog`, `Loaded models` only.

---

## Garbage removal (P0 — do this first)

Phase 6/7 shipped **Profiles / migration / diagnostics / misnamed `FleetLlama*` panels** that are not part of curated local llama. This redo is mostly **subtraction**.

### Delete these files (grep for imports after; fix or delete tests)

| File | Why it exists / why it goes |
|------|-----------------------------|
| `installed/CustomizeInstallationDialog.tsx` | Management-mode ceremony; preset edit does not require "customize to unlock" |
| `installed/TechnicalConfigurationPanel.tsx` | Read-only duplicate; replaced by catalog-row editors |
| `llama/FleetLlamaPanel.tsx` | Misnamed Phase 6/7 panel — **delete** |
| `llama/MigrationIssuesPanel.tsx` | Legacy migration algebra — operator-facing garbage |
| `features/localModelOnboarding/fleetPresetSchema.ts` | Delete with panel — validation moves to open preset on model row |

`ProfilesTab.tsx` and `RuntimeProfileDialog.tsx`: **unmount** from Models & Runtime in this redo; delete files only if nothing else imports them (cloud settings may relocate later).

### Strip from `LocalLlamaRuntimeTab.tsx`

| Remove | Keep |
|--------|------|
| Entire "Router mapping preview" section | Main inventory table (load/unload/delete/attach) |
| Entire "Runtime diagnostics" **second table** | Optional: `getLlamaRuntimeStatus` poll **only** for in-progress badge on main table rows |
| `<details>Router defaults & migration</details>` + `FleetLlamaPanel` + `MigrationIssuesPanel` | Refresh inventory button |
| `aliasLoadStartedAt` / `aliasLastLoadMs` timing UI (diagnostics-only) | — |

Target: **one table**, load/unload/attach. No second diagnostic surface.

### Strip from `ModelsRuntimeWorkspace.tsx` + `Settings.tsx`

| Remove |
|--------|
| Third sub-tab `profiles` + `{subTab === 'profiles' && <ProfilesTab …>}` |
| All props to workspace used **only** by `ProfilesTab`: `profileDialogOpen`, `editingProfileId`, `profileForm`, `profileSaving`, `profilesError`, `deletingProfileId`, `onProfileFormChange`, `onOpenCreateProfile`, `onImportProfile`, `onResetProfileForm`, `onSaveProfile`, `onRetryLoadProfiles`, `onEditProfile`, `onRequestDeleteProfile`, `onInsertRuntimeProfileTemplate` |
| `handleOpenModelsRuntime(..., 'profiles')` and any deep-link to profiles sub-tab |
| `handleOpenRouterDefaults` / any deep-link to deleted panels (if present) |
| `pendingConfirmation` kind `delete-profile` **if** only reachable from removed Profiles tab |

| Keep |
|------|
| `profiles` + `profilesLoading` + `loadProfiles()` — **only** for Custom HF profile dropdown and cloud catalog edit forms |
| `llamaInventory` + load/unload handlers |

### Strip from catalog / onboarding / installed

| Remove |
|--------|
| `onOpenFleetSettings` prop chain (misnamed) — remove from: `LlamaCppForm`, `LlamaInstalledSummary`, `AliasPresetSavePanel`, `AliasPresetEditor`, `CustomHfOnboardingForm`, `LlamaLocalModelOnboardingPanel`, `AddModelWizard` |
| Copy in `AliasPresetSavePanel` pointing at "Router defaults" or fleet-wide switches | Delete |
| `managementMode` / `runtimeState` **prominent** boxes in `LlamaInstalledSummary` (provenance may stay collapsed) |
| Any remaining import/call to `customizeLlamaInstallation` |

### Client API methods — no UI callers after redo

These may remain in `api.ts` until a dead-code pass; **no component may call them**:

- `customizeLlamaInstallation`
- `getLlamaFleetPreset` / `putLlamaFleetPreset` (misnamed `/runtime/fleet-preset` — delete endpoints)
- `getLlamaMigrationStatus` / `getLlamaMigrationIssues`

### Tests to update or delete

| File | Action |
|------|--------|
| `__tests__/ProfilesTab.test.tsx` | Keep only if `ProfilesTab` file kept unmounted; else delete |
| `__tests__/RuntimeProfileDialog.test.tsx` | Same |
| `__tests__/ModelsRuntimeWorkspace.test.tsx` | Remove ProfilesTab mock expectations |
| `__tests__/LocalLlamaRuntimeTab.test.tsx` | Remove mapping/diagnostics/migration panel assertions |
| `__tests__/phase7Advanced.test.ts` | Remove `fleetPresetSchema` describe block |
| `catalog/__tests__/LlamaCppForm.test.tsx` | Remove `getLlamaFleetPreset` mock |
| `routerPreset.ts` | Drop fleet-scoped redirect logic; open preset on model row |

### Wrong interpretations — also garbage

| Wrong | Why it fails |
|-------|----------------|
| Migration issues panel in operator UI | Legacy bookkeeping |
| `managementMode` shown as primary UI | Internal; not operator vocabulary |
| Router mapping preview table | Duplicates install detail |
| Second runtime diagnostics table | Duplicates inventory + load state |
| Onboarding **mode cards** as first screen | Curated picker must be default without choosing a "mode" first |

---

## Already started (incomplete)

| File | State |
|------|-------|
| `installed/AliasPresetSavePanel.tsx` | Created — ctx/cache + open preset rows + `putLlamaRouterEntry`. Tests written, not verified. |
| `installed/LlamaInstalledSummary.tsx` | Uses `AliasPresetSavePanel`; Customize/Technical removed. Noisy management-mode box; no layer-3 panel yet. |
| `llama/FleetLlamaPanel.tsx` | **Delete** |
| `LocalLlamaRuntimeTab.tsx` | Partial — mapping preview + diagnostics + `FleetLlama*` block must go |
| `curated/LlamaLocalModelOnboardingPanel.tsx` | Advanced modes in `<details>`. |
| `ModelsRuntimeWorkspace.tsx` | Attach wizard passes both args. **Still has Profiles tab — must remove.** |

---

## File changes

### A. Layer 2 — router preset on model row (P0)

#### `installed/AliasPresetSavePanel.tsx`
- Keep. Refresh `routerEntry` from parent after save.
- Add `data-testid="alias-preset-save-panel"`.
- **Remove** `onOpenFleetSettings` and copy pointing at deleted parallel panels.
- **Remove** `isFleetScopedPresetKey` / "set in compose" rejection — layer 2 open preset on the model is the edit surface for this model's llama-server args. (Server may still validate keys llama-admin cannot persist on alias — error explains that, no redirect to another product screen.)

#### `installed/LlamaInstalledSummary.tsx`
- Remove top `managementMode` / `runtimeState` grid.
- Keep `AliasPresetSavePanel` as primary layer-2 surface (not inside collapsed details).
- Collapse provenance into `<details summary="Installation details">`.
- Wire `onChanged` from catalog edit modal.
- Add `ModelChatBehaviorPanel` below preset panel (see A2).

#### `catalog/providers/LlamaCppForm.tsx`
- Add `onDetailChanged?: () => Promise<void>` → `LlamaInstalledSummary.onChanged`.
- Add `sharedProfileModelCount?: number` for layer-3 warning (see A2).

#### `catalog/CatalogRowEditModal.tsx`, `ModelsTab.tsx`, `ModelsRuntimeWorkspace.tsx`, `Settings.tsx`, `catalog/AddModelWizard.tsx`
- Pass `onDetailChanged={onSaved}` so saves refresh catalog state.
- Pass `sharedProfileModelCount` into llama edit form when available.
- **Do not** add router-defaults deep-link chain.

#### `advanced/AliasPresetEditor.tsx`, `routerPreset.ts`
- Remove `onOpenFleetSettings` link.
- Remove fleet-scoped / router-base split that sends operator elsewhere — layer 2 is on the model.

#### `curated/LlamaLocalModelOnboardingPanel.tsx`, `advanced/CustomHfOnboardingForm.tsx`
- Remove `onOpenFleetSettings` prop passthrough.

#### Delete
- `installed/CustomizeInstallationDialog.tsx`
- `installed/TechnicalConfigurationPanel.tsx`
- `llama/FleetLlamaPanel.tsx`
- `llama/MigrationIssuesPanel.tsx`
- `features/localModelOnboarding/fleetPresetSchema.ts` (delete with panel; rejection stays in `routerPreset.ts`)

---

### A2. Layer 3 — recipe-bound chat behavior on model row (P0)

**UI principle:** layer 3 is **authored in the curation list** (`manifest.json` → `runtimeProfileId` → bootstrap profile JSON). The client does **not** introspect profile schema to build forms. That is the same class of mistake as a Profiles tab.

**Normal operator path for layer 3 changes:** curator updates manifest and/or bootstrap profile → operator runs **Repair** (or **Adopt**) on the model row to re-apply the recipe. No per-param UI.

**Escape hatch (rare):** collapsed `<details>` on the model row with existing `RuntimeProfileEditor` JSON text areas (`samplingParametersJson`, `thinkingControlJson`, `requestFieldsWhenToolsPresentJson`) — document edit, identity fields disabled. Same “open editor” philosophy as layer 2’s preset key rows: extensible without shipping new React fields per model family.

**Do not build:** `top_k` sliders, thinking choice widgets derived from JSON schema, or any mapper from `samplingParametersJson` structure to form controls.

#### `installed/ModelChatBehaviorPanel.tsx` (**new** — name kept; not a form generator)
- **Show:** bound `runtimeProfileId` (from installation detail), read-only summary of effective profile (display name + optional collapsed JSON preview).
- **Primary actions:** link/context for **Repair** / **Adopt curated** (reuse existing dialogs) — “re-apply recipe defaults.”
- **Collapsed:** `<details summary="Edit profile document (advanced)">` → load profile via `getRuntimeProfile`, render `RuntimeProfileEditor` `mode="inline"`, `disableIdentityFields`, save via `updateRuntimeProfile`.
- **Warn** when `sharedProfileModelCount > 1` on document save.
- **No** Profiles tab navigation. **No** schema-driven controls.

#### `src/client/src/types/settings.ts`, `pages/settings/types.ts`, `pages/settings/utils.ts`
- Wire `requestFieldsWhenToolsPresentJson` through client types and `ProfileFormState` (for document editor + API parity only).

#### `pages/settings/components/RuntimeProfileEditor.tsx`
- Ensure three JSON fields editable in inline mode (document path only).

#### `installed/__tests__/ModelChatBehaviorPanel.test.tsx`
- Renders bound profile id and repair affordance.
- Document save calls `updateRuntimeProfile` (advanced path).
- Does **not** assert generated sampling sliders exist.

---

### B. Models & Runtime shell (P0 — overlaps garbage removal)

**Loaded models tab is:** what's loaded, load/unload, attach unbound alias. Nothing else.

#### `ModelsRuntimeWorkspace.tsx`
- Two sub-tabs only: `Catalog`, `Loaded models`.
- Remove `ProfilesTab` mount and all profile-dialog props (see garbage removal table).

#### `pages/settings/types.ts`
- `ModelsRuntimeSubTab` = `'catalog' | 'local-llama'` only.

#### `Settings.tsx`
- Remove profile-dialog state/handlers wired only to removed Profiles tab (see garbage removal).
- Keep `loadProfiles()` for wizard/cloud dropdowns only.

#### `LocalLlamaRuntimeTab.tsx`
- After garbage strip: single inventory table only (see garbage removal).

#### `ProfilesTab.tsx`, `RuntimeProfileDialog.tsx`
- Unmount from Models & Runtime; not deleted unless repo-wide grep shows no imports.

---

### C. Curated onboarding (P1)

#### `curated/LlamaLocalModelOnboardingPanel.tsx`, `LocalAiModelsStep.tsx`
- Curated default; Custom/Attach inside `<details summary="Custom Hugging Face or attach existing alias">`.
- No profile picker on curated path.

#### `curated/LlamaCuratedReview.tsx`
- Read-only all-three-layers snapshot at install review only. After install, layers 2+3 editable on catalog row.

---

### D. Tests (P0 before claiming done)

| File | Assert |
|------|--------|
| `installed/__tests__/AliasPresetSavePanel.test.tsx` | `putLlamaRouterEntry` on save |
| `installed/__tests__/ModelChatBehaviorPanel.test.tsx` | `updateRuntimeProfile` on save |
| `installed/__tests__/LlamaInstalledSummary.test.tsx` | Both panels render; management-mode box absent |
| `__tests__/ModelsRuntimeWorkspace.test.tsx` | Two tabs only; no `profiles-tab-panel` |
| `__tests__/LocalLlamaRuntimeTab.test.tsx` | Inventory + attach only |

```powershell
cd src/client
npm run build
npm test -- --run src/features/localModelOnboarding/installed/__tests__/
npm test -- --run src/pages/settings/components/__tests__/ModelsRuntimeWorkspace.test.tsx
npm test -- --run src/pages/settings/components/__tests__/LocalLlamaRuntimeTab.test.tsx
```

---

### E. Ops prerequisite (blocks curated E2E)

Rebuild `guideants-ai` per `contracts/FROZEN-COMMANDS.md` so `GET /admin/catalog` serves the 14-model manifest. Client-only changes cannot fix a stale image.

---

## Backend cleanup (Phase 2 — yes, required)

The Phase 6/7 backend mirrors the same garbage: misnamed `FleetLlama*` revision APIs, migration issue surfacing, Customize ceremony, `managementMode` gating. Client removal alone leaves dead routes, hosted reconciliation nobody asked for, and DTO fields nothing should display.

### Tier 1 — Remove operator HTTP surface (do with or immediately after client garbage)

Mirror every removed client call with endpoint removal or `410 Gone`.

| Endpoint | File | Action |
|----------|------|--------|
| `POST /api/settings/llama/installations/{modelId}/customize` | `Endpoints/Settings/SettingsLlamaInstallationEndpoints.cs` | **Remove** |
| `GET/PUT /api/settings/llama/runtime/fleet-preset` | `Endpoints/Settings/SettingsLlamaEndpoints.cs` | **Remove** — misnamed; "fleet" is not a product concept |
| `GET /api/settings/llama/migration/status` | same | **Remove** |
| `GET /api/settings/llama/migration/issues` | same | **Remove** |

| Service / handler | Action |
|-------------------|--------|
| `LocalModelInstallationService.CustomizeAsync` (+ `CustomizeInstallationRequestDto`) | **Remove** — direct preset/profile edit does not require mode flip |
| `LocalModelLifecycleService` checks that require `managementMode === curated` before repair/adopt | **Relax** — repair/adopt available whenever installation provenance exists; curated tracking is metadata not a lock |
| `LocalModelLifecycleErrorCodes.ManagementModeInvalid` | **Remove** if no callers |

**DTO trim (stop leaking garbage to any client):**

| DTO field | Action |
|-----------|--------|
| `LlamaInstallationDetailDto.ManagementMode` | Remove from API response (keep DB column internal if still useful) |
| `LlamaInstallationProvenanceSummaryDto.ManagementMode` | Remove |
| `LlamaRuntimeInventoryItemDto.FleetPreset` (`FleetPresetSummaryDto`) | **Remove** — revision algebra is not operator-facing |
| `FleetLlamaPresetResponseDto`, `FleetLlamaPresetPutRequestDto` | **Remove** with endpoints |

**Tests to update:** `LlamaAuthorizationEndpointsTests` (customize route), `CuratedInstallTests`, `LocalModelLifecycleTests`, `ManagementModeInvalid`, `/runtime/fleet-preset` contract tests.

### Tier 2 — Delete misnamed `FleetLlama*` machinery (after Tier 1)

On every API startup, `FleetLlamaStartupReconciliationService` currently runs:

```text
EnsureSeededAndReconciledAsync()  → SQL FleetLlamaRuntimeSettings + revision algebra → llama-admin
RunMigrationAsync(apply: true)    → legacy RuntimeConfigJson migration + issues table
```

**Target:** **model definition is source of truth for this model's llama-server args.** Install/repair/load applies `routerPreset` to INI via existing paths. Delete parallel `FleetLlama*` SQL store and revision algebra — it duplicated/split what belongs on the model.

| Component | Action |
|-----------|--------|
| `FleetLlamaRuntimeSettings` table + `IFleetLlamaRuntimeSettingsService` + `FleetLlamaPresetSchema.cs` | **Delete** — llama-server args for a local model live on the **model** (layer 2), not a parallel SQL fleet row |
| `FleetLlamaStartupReconciliationService` | **Delete** or reduce to operation reconciliation only (no `FleetLlama*` / migration calls) |
| `LocalModelMigrationService` + `LocalModelMigrationIssues` | **Delete** after legacy DBs migrated — one-time upgrade script if needed, not hosted startup |
| `LocalModelInstallation.ManagementMode` | **Collapse** — curated = has `CatalogId`/`CatalogVersion`; drop `operatorManaged` + Customize flip |

**Files:** `FleetLlamaRuntimeSettingsService.cs`, `FleetLlamaStartupReconciliationService.cs`, `LocalModelMigrationService.cs`, `DataModel/Models/FleetLlamaRuntimeSettings.cs`, `LocalModelMigrationIssue.cs`, `StartupConfiguration.cs` registrations.

**DECISIONS.md:** mark **D4** (SQL fleet preset / `fleet-preset` API) and **D9** (Customize / separate fleet editors) **superseded** by this plan.

### Tier 3 — Keep (core curated path)

Do **not** remove as part of garbage cleanup:

| Keep | Why |
|------|-----|
| `GET /admin/catalog`, `GET .../quants` | Curated picker |
| `POST models:add` curated install + `LocalModelOperation` | Install path |
| `GET installations/{id}`, change-quant, repair, adopt | Model-row lifecycle |
| `GET/PUT router/entries/{alias}` | Layer 2 preset (`putLlamaRouterEntry`) |
| `GET/PUT runtime-profiles/{id}` | Layer 3 document save |
| `GET runtime/inventory`, load/unload | Loaded models tab |
| `LocalModelInstallation` provenance + artifact records | Repair/change-quant/adopt |

### Backend implementation order

1. Tier 1 — remove endpoints + DTO fields + customize/mode gating (pair with client garbage PR)
2. Tier 2 — delete `FleetLlama*` SQL + hosted reconciliation + migration service; EF migration; supersede D4/D9
3. Update `DECISIONS.md` + integration tests

**Do not** claim UX redo complete while Tier 1 operator endpoints still exist.

---

## Files explicitly NOT changed (Phase 1 client)

| Path | Reason |
|------|--------|
| `catalog/manifest.json` | Curated recipes already correct |
| `docs/llama-router-preset-ui-proposal.md` | Product spec reference |
| `STATUS.md` | User owns ledger |

Backend Tier 1+2: see **Backend cleanup** section.

---

## Implementation order

### Client (Phase 1)

1. **Garbage removal** — delete files, unmount Profiles / `FleetLlama*` / migration panels, strip `LocalLlamaRuntimeTab`, clean `Settings` props
2. A — layer 2 wiring; clean `LlamaInstalledSummary` layout
3. A2 — layer 3 panel (repair-first + collapsed document editor)
4. Tests (section D)
5. C — onboarding parity
6. E — ops image rebuild (user)

### Backend (Phase 2 — pair Tier 1 with client garbage)

7. Tier 1 — remove customize, `/runtime/fleet-preset`, migration endpoints; trim DTOs; drop mode gating
8. Tier 2 — delete `FleetLlama*` machinery + migration SQL; EF migration; supersede DECISIONS D4/D9

**Do not** build new client panels before client garbage removal. **Do not** claim done while backend Tier 1 endpoints remain.

---

## What this plan does NOT claim

- Does not fix stale Docker / missing `/admin/catalog`.
- Does not add per-model profile forks (shared-profile warning on document edit only).
- Does not build schema-driven layer-3 form controls (ever — new families ship via manifest/bootstrap).
- Llama-server args are **part of the model definition**; system applies on install/load; simple edit on catalog row.
- Does not split layer 2 into a parallel "fleet" / "globals" / "router defaults" path.
- Does not leave Phase 6/7 garbage that splits config off the model (Profiles tab, `FleetLlama*` panel, migration UI, Customize).
- Does not leave dead operator APIs (`customize`, `fleet-preset`, `migration/*`) after Phase 2 Tier 1.
- Tier 2 deletes `FleetLlamaRuntimeSettings` table and revision algebra — not "hidden," **gone**.

If any P0 step is skipped, the redo is incomplete.
