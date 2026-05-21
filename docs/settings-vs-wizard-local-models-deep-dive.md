# Settings UI vs Wizard: Local Models Deep Dive

This document compares how local AI models are added in:

- **Settings UI** (`Settings -> Models & Runtime -> Add Model`)
- **Home Add AI Services Wizard** (Local AI path)

It highlights logic differences that are likely defect sources.

## 1. Architecture Split

- **Settings UI** local chat model add uses a single-operation modal flow (`AddModelWizard`) with a dedicated progress step.
  - `src/client/src/pages/settings/components/catalog/AddModelWizard.tsx`
- **Home Wizard** local chat model add uses queued drafts (`useLocalAiWizardState` + `LocalAiModelsStep`) and allows multiple in-session installs.
  - `src/client/src/components/home/addAiServicesWizard/useLocalAiWizardState.ts`
  - `src/client/src/components/home/addAiServicesWizard/steps/LocalAiModelsStep.tsx`

## 2. Shared Endpoint, Different Validation Strictness

Both flows call:

- `POST /settings/models:add` (`api.settings.addModel`)
  - `src/client/src/services/api.ts`

Differences:

- **Settings UI (stricter llama validation):**
  - Requires `repository`, `quant pattern`, `mmproj pattern`, and `target directory` for Hugging Face installs.
  - Validates `routerContextSize` and `routerCacheRamMib` with explicit ranges.
  - `src/client/src/pages/settings/utils.ts`
- **Wizard local (looser):**
  - If `mmproj` is blank, falls back to `quant pattern`.
  - If `targetDirectory` is blank, falls back to `routerModelId`.
  - Router knobs accepted only when parseable and `> 0`, without settings-modal range enforcement.
  - `src/client/src/components/home/addAiServicesWizard/utils.ts`

## 3. Duplicate Model ID Handling Differs

- **Settings UI** pre-validates model ID uniqueness on blur via `getModels()`, and blocks step continuation when duplicate.
  - `src/client/src/pages/settings/components/catalog/AddModelWizard.tsx`
- **Wizard local** does not preflight uniqueness against catalog before submit for local draft installs; conflicts surface from server errors.
  - `src/client/src/components/home/addAiServicesWizard/useLocalAiWizardState.ts`

## 4. Global Default Model Behavior Differs

- **Settings AddModelWizard** does not set chat default during model add.
- **Wizard local** can set default per draft and auto-sets first model as default.
  - `src/client/src/components/home/addAiServicesWizard/steps/LocalAiModelsStep.tsx`
  - `src/client/src/components/home/addAiServicesWizard/useLocalAiWizardState.ts`

## 5. Async Progress Tracking Differs

- **Settings UI:** one active add operation tracked and polled in wizard progress step.
  - `src/client/src/pages/settings/components/catalog/AddModelWizard.tsx`
- **Wizard local:** per-draft polling with multiple concurrent rows possible.
  - `src/client/src/components/home/addAiServicesWizard/useLocalAiWizardState.ts`

## 6. Existing Alias Eligibility Mismatch

- **Settings UI attach-existing-alias** filters to aliases with:
  - `hasModelFile`
  - `hasMmprojFile`
  - no catalog bindings
  - `src/client/src/pages/settings/components/catalog/providers/LlamaCppForm.tsx`
- **Wizard local attach-existing-alias** filters to aliases with:
  - `hasModelFile`
  - no catalog bindings
  - (`hasMmprojFile` is not required)
  - `src/client/src/components/home/addAiServicesWizard/steps/LocalAiModelsStep.tsx`

## 7. Local Optional Services Flow in Wizard

- Local AI wizard currently uses **dedicated per-service steps**:
  - `localAiSpeechTranscription`
  - `localAiImageGeneration`
  - `localAiSpeechSynthesis`
  - `localAiDocumentIntelligence`
  - `localAiEmbeddings`
  - `src/client/src/components/home/AddAiServicesWizard.tsx`
- `LocalAiOptionalServicesStep.tsx` exists but is not currently wired into the active local provider step sequence.
  - `src/client/src/components/home/addAiServicesWizard/steps/LocalAiOptionalServicesStep.tsx`

## 8. Reuse of Service Editors in Wizard

The local wizard per-service steps reuse the same service managers used in Settings editors:

- `AsrModelManager`
- `TtsModelManager`
- `ImageBundleManager`
- `EmbRuntimeManager`

They are wrapped by `LocalAiServiceStepBase`, which enforces:

- fixed provider for the step
- readiness checks (where required)
- save through shared service editor controller

Files:

- `src/client/src/components/home/addAiServicesWizard/steps/LocalAiServiceStepBase.tsx`
- `src/client/src/components/home/addAiServicesWizard/steps/LocalAiSpeechTranscriptionStep.tsx`
- `src/client/src/components/home/addAiServicesWizard/steps/LocalAiImageGenerationStep.tsx`
- `src/client/src/components/home/addAiServicesWizard/steps/LocalAiSpeechSynthesisStep.tsx`
- `src/client/src/components/home/addAiServicesWizard/steps/LocalAiEmbeddingsStep.tsx`

## Likely Defect Hotspots

1. Validation parity gaps between settings modal and wizard local chat installs (mmproj/target-dir/router knob constraints).
2. Alias eligibility mismatch (`hasMmprojFile` required in one flow, not the other).
3. Duplicate model ID UX inconsistency (settings preflight vs wizard server-time failure).

