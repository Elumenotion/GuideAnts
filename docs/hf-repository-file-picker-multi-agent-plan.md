# Hugging Face Repository File Picker — Multi-Agent Delivery Plan

Status: Proposed execution plan
Input requirements: `docs/hf-repository-file-picker-requirements.md`

## Objective

Deliver the Hugging Face repository browse flow across llama-cpp, Image Generation, Speech Transcription, Speech Synthesis, and Embeddings without widening the server surface beyond the neutral browse alias and telemetry/header changes already called for in the requirements.

This plan is optimized for parallel agent work in the current repo, with explicit file ownership to minimize merge conflicts.

## Delivery strategy

Use one lead/integration agent plus six implementation agents:

1. `Lead` coordinates merge order, keeps the contract stable, and owns final integration + verification.
2. `Server/API` owns the neutral browse route alias, optional `X-Service-Origin` header handling, and client API contract updates.
3. `Shared Picker` extracts the llama picker into a reusable component and keeps llama-cpp behavior unchanged.
4. `Image Generation` replaces the six free-text HF fields with three single-role picker instances and preserves paste-JSON as advanced mode.
5. `ASR` converts the download dialog to a preview-only browse-first flow.
6. `TTS` converts the download dialog to dual preview-only pickers with optional tokenizer repo support.
7. `Embeddings` adds the optional HF preview/load flow while preserving the existing local-path/default-load behavior.

A final `QA` pass can be done by the lead once the feature branches land, or split to a dedicated validation agent if extra capacity is available.

## Agent ownership

### Agent 0: Lead / Integrator

Owns:

- `docs/hf-repository-file-picker-multi-agent-plan.md`
- final conflict resolution across shared files
- final test run and rollout checklist

Responsibilities:

- Lock merge order.
- Enforce the shared picker prop contract before service agents branch from it.
- Resolve overlap in:
  - `src/client/src/services/api.ts`
  - `src/client/src/types/settings.ts`
  - `src/client/src/pages/settings/editors/common/index.ts`
- Run the end-to-end verification pass after all lanes merge.

### Agent 1: Server/API Contract

Owns:

- `src/server/GuideAntsApi/Endpoints/SettingsEndpoints.cs`
- `src/client/src/services/api.ts`
- `src/client/src/types/settings.ts`
- relevant server/client tests touching the browse endpoint contract

Scope:

- Add canonical `GET /api/settings/huggingface/repositories/{owner}/{repo}/files`.
- Keep `/api/settings/llama/huggingface/repositories/{owner}/{repo}/files` functional as legacy alias for one release.
- Accept optional `X-Service-Origin` header and emit the structured browse telemetry entry.
- Update `api.settings.browseHuggingFaceRepository(...)` to target the neutral path.
- Add optional header support so callers can pass service origin without inventing new API methods.
- Update DTO comments in `settings.ts` to stop implying the response is llama-only.

Key constraints:

- Do not add service-specific branches to the browse endpoint.
- Do not expose the HF token.
- Preserve existing pagination and current error codes.

### Agent 2: Shared Picker / Llama Preservation

Owns:

- `src/client/src/pages/settings/editors/common/RepositoryFilePicker.tsx`
- `src/client/src/pages/settings/editors/common/index.ts`
- new shared helper files under `src/client/src/pages/settings/editors/common/`
- `src/client/src/pages/settings/components/catalog/providers/LlamaCppForm.tsx`
- shared picker tests

Scope:

- Extract the inline picker from `LlamaCppForm.tsx`.
- Generalize it to the requirements-driven controlled API:
  - `repository`
  - `onRepositoryChange`
  - `roles`
  - `classify`
  - `onChange`
  - `initialValues`
  - `manualFallbackEnabled`
  - `previewOnly`
- Preserve current llama-cpp behavior through a llama-specific classifier instead of hard-coded picker logic.
- Add shared keyboard/accessibility behavior:
  - `spellCheck={false}`
  - enter-to-browse
  - escape-to-cancel
  - inline `role="alert"` errors
- Add repo normalization helper that accepts either `owner/repo` or full Hugging Face model URLs before the API call.

Key constraints:

- This agent is the only one that edits `RepositoryFilePicker.tsx`.
- Keep sharded entries visible-but-disabled.
- Do not bake SD/ASR/TTS-specific heuristics into the shared component.

### Agent 3: Image Generation

Owns:

- `src/client/src/pages/settings/editors/image-generation/ImageBundleManager.tsx`
- `src/client/src/pages/settings/editors/image-generation/sdClassifier.ts`
- `src/client/src/pages/settings/editors/image-generation/__tests__/ImageBundleManager.test.tsx`
- new classifier tests under `src/client/src/pages/settings/editors/image-generation/`

Scope:

- Add the browse-first bundle wizard mode.
- Keep paste-JSON mode available but no longer default.
- Replace free-text `(repo, file)` entry with three stacked single-role picker instances:
  - diffusion
  - vae
  - text encoder
- Write selections back into the existing download payload:
  - `diffusion_repo`
  - `diffusion_file`
  - `vae_repo`
  - `vae_file`
  - `text_encoder_repo`
  - `text_encoder_file`
- Implement `sdClassifier(...)` and ambiguity behavior:
  - auto-select only when there is one defensible choice
  - ambiguous matches stay unselected
  - sharded safetensors stay disabled
- Block submit until all three filenames are selected or manually entered.

Key constraints:

- Do not alter the existing server download contract.
- Preserve definition upload/download behavior.
- Keep Image Generation independent of preview-only snapshot code.

### Agent 4: Speech Transcription

Owns:

- `src/client/src/pages/settings/editors/speech-transcription/AsrModelManager.tsx`
- `src/client/src/pages/settings/editors/speech-transcription/__tests__/...` if added

Depends on:

- Agent 1 contract merge
- Agent 2 shared picker merge

Scope:

- Replace the current free-text `modelId` input inside `DownloadModelDialog`.
- Use `RepositoryFilePicker` in `previewOnly` mode.
- Add snapshot preview classification and total-size display.
- Keep `revision` editable.
- Disable the dialog submit/start action until a browse succeeds.
- Surface browse errors inline beside the browse action, not only at submit time.

Key constraints:

- Download body remains `{ model_id, revision? }`.
- Do not alter load/unload/remove list behavior outside the dialog.

### Agent 5: Speech Synthesis

Owns:

- `src/client/src/pages/settings/editors/speech-synthesis/TtsModelManager.tsx`
- `src/client/src/pages/settings/editors/speech-synthesis/__tests__/...` if added
- TTS-specific snapshot badge helpers if needed

Depends on:

- Agent 1 contract merge
- Agent 2 shared picker merge

Scope:

- Replace free-text `modelId` and `tokenizerId` inputs with two preview-only pickers.
- Keep tokenizer picker collapsed by default.
- Allow empty tokenizer repo.
- Surface `voice` badges for `voices/`, `.npz`, `.pt`, and voice-named files.
- Keep `revision` editable.
- Submit unchanged payload `{ model_id, tokenizer_id?, revision? }`.

Key constraints:

- Do not change load/unload/remove semantics.
- Do not force tokenizer selection when the field is intentionally blank.

### Agent 6: Embeddings

Owns:

- `src/client/src/pages/settings/editors/embeddings/EmbRuntimeManager.tsx`
- `src/client/src/pages/settings/editors/embeddings/EmbeddingsEditor.tsx` if wiring changes are needed
- `src/client/src/pages/settings/editors/embeddings/__tests__/EmbRuntimeManager.test.tsx`

Depends on:

- Agent 1 contract merge
- Agent 2 shared picker merge

Scope:

- Extend the local embeddings UI so the operator can choose:
  - default/local-path flow
  - `Download from Hugging Face` flow
- Show the preview-only picker only in the HF branch.
- Keep existing no-HF/default load behavior working when the picker is hidden.
- Enable submit/load only after a successful browse in the HF branch.
- Preserve `model_path` use cases.

Key constraints:

- The current embeddings runtime manager only loads with `{}`; this lane likely needs the largest local UX redesign among the preview-only services.
- Avoid pushing this into `ProviderFieldsSection`; keep the change local to embeddings runtime UX unless integration proves otherwise.

### Agent 7: Shared Snapshot Classifier and Tests

Owns:

- `src/client/src/pages/settings/editors/common/snapshotPreviewClassifier.ts`
- `src/client/src/pages/settings/editors/common/RepositoryFilePicker.test.tsx`
- `src/client/src/pages/settings/editors/common/snapshotPreviewClassifier.test.ts`

Depends on:

- Agent 2 shared picker structure

Scope:

- Build the shared preview-only classification buckets:
  - `weights`
  - `tokenizer`
  - `config`
  - `other`
- Add extension point for extra badges so TTS can add `voice`.
- Cover:
  - empty listings
  - large snapshots
  - sharded disabled rows
  - badge rendering in preview-only mode

Key constraints:

- This agent must not edit service-specific managers except for minimal wiring agreed with the owning agent.
- If staffing is tight, fold this lane into Agent 2.

## Parallel execution waves

### Wave 1: Contract and shared foundation

Run in parallel:

- Agent 1: server/API alias + header + DTO/API contract
- Agent 2: extract shared picker + preserve llama flow

Sequencing note:

- Agent 2 can start from the current `LlamaCppForm.tsx`, but should not finalize API call details until Agent 1 locks the neutral route and optional header contract.

Deliverable gate:

- llama-cpp still works on the extracted picker
- neutral `/settings/huggingface/...` route exists
- no new server endpoints beyond aliasing

### Wave 2: Shared preview support and highest-value service

Run in parallel:

- Agent 3: Image Generation
- Agent 7: snapshot preview classifier/tests

Sequencing note:

- Image Generation only depends on the per-file picker, so it can move ahead while Agent 7 finishes preview-only support for ASR/TTS/Embeddings.

Deliverable gate:

- Image Generation uses the shared picker end-to-end
- shared preview classifier exists and is test-covered

### Wave 3: Preview-only services

Run in parallel:

- Agent 4: ASR
- Agent 5: TTS
- Agent 6: Embeddings

Sequencing note:

- These lanes should branch only after Agent 2 and Agent 7 land, otherwise they will all collide in the shared picker API.

Deliverable gate:

- each service uses browse-first flow
- each service keeps its existing submit/load payload unchanged

### Wave 4: Integration and acceptance

Lead only, or Lead + QA agent:

- merge residual conflicts
- run client tests
- run targeted server tests
- run Playwright acceptance
- verify the legacy llama route still works for one release

## File-level conflict rules

To keep the multi-agent rollout tractable:

- Only Agent 1 edits `src/client/src/services/api.ts`.
- Only Agent 1 edits `src/client/src/types/settings.ts` unless the lead explicitly reassigns it.
- Only Agent 2 edits `src/client/src/pages/settings/editors/common/RepositoryFilePicker.tsx`.
- Only Agent 3 edits `src/client/src/pages/settings/editors/image-generation/ImageBundleManager.tsx`.
- Only Agent 4 edits `src/client/src/pages/settings/editors/speech-transcription/AsrModelManager.tsx`.
- Only Agent 5 edits `src/client/src/pages/settings/editors/speech-synthesis/TtsModelManager.tsx`.
- Only Agent 6 edits `src/client/src/pages/settings/editors/embeddings/EmbRuntimeManager.tsx`.

If a lane needs a shared-file change outside its ownership, it hands that delta back to the lead instead of editing opportunistically.

## Technical decisions to lock early

These decisions should be made before implementation branches drift:

1. `browseHuggingFaceRepository(...)` should accept an optional service-origin argument so the UI can send `X-Service-Origin` without proliferating API helpers.
2. Repo normalization should happen client-side before validation, so pasted Hugging Face URLs resolve to `owner/repo`.
3. The shared picker should not depend on the server's current llama-oriented `category` values for preview-only services; snapshot badges should be derived client-side from file paths and extensions.
4. Cancellation should use `AbortController` in the shared picker so `Esc` can cancel an in-flight browse across all services.
5. Manual fallback should remain enabled by default for per-file services, but preview-only services should not expose filename text boxes because they do not submit filenames.

## Acceptance matrix by lane

### Contract/shared

- `api.settings.browseHuggingFaceRepository(...)` targets `/settings/huggingface/...`
- legacy `/settings/llama/huggingface/...` continues to function
- llama-cpp keeps existing auto-pick behavior

### Image Generation

- operator can complete a bundle download without typing filenames
- empty role selections block submit
- ambiguous classification does not silently pick

### ASR

- browse required before download button becomes active
- misspelled repo shows inline `REPO_NOT_FOUND`
- revision stays editable

### TTS

- model picker works without tokenizer override
- tokenizer picker can be expanded and used when needed
- voice files are visibly categorized

### Embeddings

- local/default load path still works without any HF browse
- HF branch shows preview-only picker and requires successful browse before load

## Test plan

Unit/integration:

- `src/client/src/pages/settings/editors/common/RepositoryFilePicker.test.tsx`
- `src/client/src/pages/settings/editors/common/snapshotPreviewClassifier.test.ts`
- `src/client/src/pages/settings/editors/image-generation/sdClassifier.test.ts`
- expand `src/client/src/pages/settings/editors/image-generation/__tests__/ImageBundleManager.test.tsx`
- add focused tests for ASR/TTS dialogs
- expand `src/client/src/pages/settings/editors/embeddings/__tests__/EmbRuntimeManager.test.tsx`

Server:

- endpoint alias coverage in `GuideAntsApi.Tests`
- browse error contract regression checks
- header/telemetry coverage if a logging test harness already exists

Acceptance:

- one Playwright flow per service, matching the requirements doc rollout section

## Recommended merge order

1. Agent 1
2. Agent 2
3. Agent 7
4. Agent 3
5. Agent 4
6. Agent 5
7. Agent 6
8. Lead final integration

This order minimizes rework because the three preview-only services all depend on the stabilized shared picker contract.

## Risks and mitigations

Risk: shared picker prop churn causes all service branches to rebase repeatedly.

Mitigation: Agent 2 publishes the prop contract first and treats it as frozen before Waves 2 and 3 branch.

Risk: embeddings scope expands unexpectedly because the current UI only supports default load/unload.

Mitigation: isolate embeddings in its own lane and treat it as the last functional merge before QA.

Risk: DTO/type changes in `settings.ts` conflict with service branches.

Mitigation: reserve `settings.ts` ownership to Agent 1.

Risk: preview-only services accidentally depend on llama-specific server file categories.

Mitigation: classify snapshot files client-side from path/extension, not server category.

## Exit criteria

The plan is complete when:

- all five services use the shared browse flow appropriate to their selection shape
- the neutral browse route is canonical and the llama route remains as temporary alias
- shared and service-specific tests pass
- the acceptance scenarios in `docs/hf-repository-file-picker-requirements.md` are all covered by either automated tests or explicit manual verification
