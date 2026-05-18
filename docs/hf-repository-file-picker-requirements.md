# Hugging Face Repository File Picker — Cross-Service Requirements

Status: Draft / design doc
Owner: Settings / local-model admin
Related: `docs/settings-architecture.md`, `docs/llama-model-download-and-runtime-management.md`, `docs/settings-and-llama-completion-requirements.md#r-13-non-chat-service-editor-requirements`

## 1. Motivation

The Add-Model wizard for **llama-cpp** was redesigned (see the current Settings architecture doc `docs/settings-architecture.md`) so an operator can paste a Hugging Face `owner/repo` and pick the model GGUF and optional mmproj file from dropdowns, instead of authoring glob patterns. That UX change removed a whole class of user-visible bugs (silent multi-file quant downloads, mmproj mismatches, typoed patterns).

The same bad UX still exists for every other Hugging-Face-backed service in the app:

| Service            | Current HF input (as of this doc)                                                                                          |
|--------------------|----------------------------------------------------------------------------------------------------------------------------|
| `ImageGeneration`  | Three `(repo, filename)` pairs (diffusion, vae, text_encoder) typed free-hand; `*`/`?` rejected server-side                |
| `SpeechTranscription` (Whisper ASR) | `model_id` (repo only), optional `revision`; whole-snapshot download                                      |
| `SpeechSynthesis` (Kokoro TTS)      | `model_id`, optional `tokenizer_id`, optional `revision`; whole-snapshot download                        |
| `Embeddings`        | `model_id` (or local `model_path`) on load; whole-snapshot download when `model_id` is set                                |

This document specifies how the repository-browse pattern generalizes to those services, what varies per service, and what the acceptance criteria are.

## 2. Definitions

- **Repo ref** — the string `owner/repo` shown at the top of a Hugging Face model page, e.g. `unsloth/Qwen3.6-35B-A3B-GGUF`.
- **Role** — a logical slot a given service needs to fill from one or more HF repos. Examples: `llamaCpp.model`, `llamaCpp.mmproj`, `sd.diffusion`, `sd.vae`, `sd.textEncoder`, `asr.snapshot`, `tts.snapshot`, `tts.tokenizer`, `embeddings.snapshot`.
- **Selection shape** — how roles map to files:
  - **Per-file** — one specific filename inside the repo is picked (llama-cpp GGUF, SD weights).
  - **Whole-snapshot** — the whole repo (or HF-`huggingface_hub` snapshot) is downloaded; the UI is informational, not file-selective (ASR, TTS, Embeddings).
- **Manual fallback** — a free-text escape hatch for operators who want to type a filename the picker did not classify.

## 3. Non-goals

1. Browsing non-HF registries (Civitai, ModelScope, Ollama hub). HF-only.
2. Resumable / partial downloads beyond the existing admin-service behavior.
3. Rendering the repo README / model card inline. A link out to `https://huggingface.co/{owner}/{repo}` is sufficient.
4. Pre-download integrity / SHA verification. That stays with the downstream admin services (`llama-admin`, `sd-service`, `asr-service`, `tts-service`).
5. Authoring or inferring runtime parameters (quant choices, context length, VAE scaling). The picker only resolves **which files** to pull; runtime knobs stay in the existing per-service editors.
6. Multi-repo fan-out for a single role. SD roles stay `(repo, file)` pairs; if a bundle needs cross-repo files, the operator fills each role's repo independently.

## 4. Shared requirements (all services)

### 4.1 Single server-side HF proxy

One endpoint backs every service's picker. It already exists and was introduced with the llama-cpp rewrite:

```
GET /api/settings/llama/huggingface/repositories/{owner}/{repo}/files
```

Despite its current URL being under `/llama/`, this endpoint is **service-agnostic** — it returns every file in the repo tree with stable metadata. Requirement: **move / alias it under a neutral path** before adopting it for other services.

- New canonical path: `GET /api/settings/huggingface/repositories/{owner}/{repo}/files`.
- Keep the legacy `/llama/...` path as a 301 / internal alias for one release to avoid breaking the llama-cpp wizard mid-flight.
- Response shape remains `HuggingFaceRepositoryListingDto` (see `src/client/src/types/settings.ts`).
- HF token injection continues to go through `IHuggingFaceTokenResolver`. The browser must never see the token.
- Pagination via the upstream `Link: rel="next"` header is already handled and MUST NOT regress.

### 4.2 Error contract

The existing `HuggingFaceBrowseException` codes are the union the UI handles; adding services introduces no new error codes:

| Code                       | HTTP | User-facing surface                                                                                                     |
|----------------------------|------|-------------------------------------------------------------------------------------------------------------------------|
| `REPO_INVALID`             | 400  | "Repository must be in the form `owner/repo`."                                                                          |
| `REPO_NOT_FOUND`           | 404  | "We could not find `owner/repo` on Hugging Face. Check the spelling."                                                   |
| `REPO_TOKEN_MISSING`       | 401  | "This repository is gated or private. Add a Hugging Face token under Settings → Hugging Face, then try again."          |
| `REPO_TOKEN_INSUFFICIENT`  | 403  | "Your configured Hugging Face token does not grant access to `owner/repo`. Accept the license on the model page first." |
| `HF_UPSTREAM`              | 502  | "Hugging Face is unreachable or returned an unexpected response. Try again."                                            |

Every per-service picker must surface these messages inline, close to the Browse button, and must never swallow them.

### 4.3 Shared UI component

One React component, `RepositoryFilePicker`, lives under `src/client/src/pages/settings/editors/common/`. It is extracted from the current `LlamaCppForm.tsx` and generalized:

- Props
  - `repository: string` (controlled `owner/repo`).
  - `onRepositoryChange(next: string): void`.
  - `roles: RolePickerSpec[]` — what the service needs to resolve.
  - `classify: (files, context) => Record<RoleId, RoleClassification>` — service-specific heuristic (see §5).
  - `onChange(values: Record<RoleId, string>): void` — emits the currently selected filenames (or `""` when cleared).
  - `initialValues?: Record<RoleId, string>` — for "Edit existing" flows.
  - `manualFallbackEnabled?: boolean` (default `true`).
  - `previewOnly?: boolean` — for whole-snapshot services; disables per-role selection but still shows the file list, gated flag, total size, and link-out.
- Behavior
  - Inline `Browse repository files` button → calls `api.settings.browseHuggingFaceRepository(repo)`.
  - Loading state must be explicit (spinner + disabled button); never silent.
  - Sharded GGUF (`-00001-of-00005.gguf`) entries are rendered but disabled, tagged "multi-file quant — not supported". Same rule applies to sharded `safetensors` (`-00001-of-00005.safetensors`).
  - Auto-select rules live in the `classify` callback; the component does not hard-code any.
  - A "Enter filename manually" toggle restores per-role text inputs so operators can paste a filename the classifier did not suggest. Manual values must round-trip into `onChange` unchanged.
  - `previewOnly` mode replaces selection UI with a read-only table: path, size, badge (`weights` / `tokenizer` / `config` / `other`), link to the HF file page.

### 4.4 Token handling and redaction

- The HF token is resolved only inside the server proxy. It is **not** accepted on the picker endpoint as a query/body parameter, and it is **not** echoed in the response. `tokenUsed` is a boolean hint for telemetry and nothing more.
- When an operator has no HF token configured and the repo is public, the picker must work without any warning banner. Only `REPO_TOKEN_MISSING`/`REPO_TOKEN_INSUFFICIENT` paths prompt configuration.
- Any log line that mentions the token MUST redact it. This is already true of `HuggingFaceTokenResolver`; regressions are blocked by existing tests.

### 4.5 Accessibility and operator ergonomics

- Repo input is `spellcheck=false`, `autocomplete="off"`, monospace.
- `Enter` while focus is in the repo input triggers Browse. `Esc` cancels an in-flight Browse.
- Errors render with `role="alert"` so screen readers announce them without the operator having to hunt.
- Each role select has an associated `<label>` and an inline hint describing the role ("GGUF weights", "vision projector (optional)", etc.).

## 5. Per-service requirements

Each sub-section lists: roles, what the classifier does, selection shape, and what changes end-to-end (client + existing server admin-routing endpoints).

### 5.1 llama-cpp (reference — already shipped)

- Roles: `llamaCpp.model` (required), `llamaCpp.mmproj` (optional).
- Selection shape: per-file.
- Classifier:
  - `llamaCpp.model`: files with `.gguf` extension whose leaf does **not** contain `mmproj`.
    - Auto-select: the largest non-sharded `.gguf` whose `QuantLabel` is one of `Q5_K_M`, `Q5_K_S`, `Q4_K_M`, in that preference order; otherwise the largest non-sharded `.gguf`.
  - `llamaCpp.mmproj`: files with `.gguf` extension whose leaf contains `mmproj`.
    - Auto-select: the first file whose leaf contains `F16`, else the first entry; else empty.
- Manual fallback: typed filenames must end in `.gguf`.
- Download endpoint unchanged: `POST /api/settings/models:add` with the picked filenames bound to `llamaHuggingFaceQuantIncludePattern` / `llamaHuggingFaceMmprojIncludePattern` as exact filenames (no glob metacharacters).

### 5.2 ImageGeneration (Stable Diffusion / Flux bundles)

This is the highest-value next target: today operators type **six** free-text fields (`diffusion_repo`, `diffusion_file`, `vae_repo`, `vae_file`, `text_encoder_repo`, `text_encoder_file`) and any typo silently causes a bundle download to target the wrong file.

- Roles (all required):
  - `sd.diffusion` — UNet / DiT weights.
  - `sd.vae` — variational autoencoder.
  - `sd.textEncoder` — text encoder (T5 / CLIP / Llama-based).
- Selection shape: per-file, **per repo** (roles can live in different repos; typical SD operator uses one repo for diffusion+vae and another for the text encoder).
- UI layout: three stacked `RepositoryFilePicker` instances, each with its own repo input and its own role set of size 1. The client writes all six values back into the existing `ImageBundleManager` form model (`diffusion_repo`/`diffusion_file`/etc.).
- Classifier (per role):
  - Valid weight extensions: `.safetensors`, `.ckpt`, `.bin`, `.gguf`, `.onnx`. Everything else is hidden unless manual-mode is on.
  - Role heuristics — prioritized substring matches against the **full repo-relative path** (case-insensitive):
    - `sd.diffusion`: matches `unet`, `dit`, `transformer`, `flux`, `sdxl_base`, `model_index` adjacency. Exclusions: leaves containing `vae`, `text_encoder`, `clip`, `t5`, `tokenizer`.
    - `sd.vae`: leaf contains `vae` (e.g. `vae/diffusion_pytorch_model.safetensors`, `ae.safetensors`).
    - `sd.textEncoder`: leaf contains `text_encoder`, `clip`, `t5`, `llama`, or path begins with `text_encoder_2/` / `text_encoder/`.
  - Ambiguity policy: if the classifier finds more than one candidate and none is obviously larger, default to "no selection"; the operator MUST pick explicitly. Never guess silently for SD.
  - Sharded `.safetensors` entries are disabled with a "multi-file weights — combine locally, not supported" message, matching the existing server-side "no glob metacharacters" rule.
- Server contract:
  - The existing `POST /service-editors/{serviceId}/local-models/downloads` endpoint already enforces `(repo, filename)` without glob metacharacters; this requirement does not change. The picker just guarantees the client always sends concrete filenames.
  - Add a browsing-mode flag to the `ImageBundleManager` UI (`bundle wizard` vs `paste JSON`); paste-JSON remains for power users but is no longer the default.
- Acceptance criteria:
  1. Pasting `stabilityai/stable-diffusion-xl-base-1.0` and selecting `unet/diffusion_pytorch_model.safetensors`, `vae/diffusion_pytorch_model.safetensors`, and `text_encoder/model.safetensors` produces a working bundle download without any free-text edits.
  2. Gated SDXL repos surface `REPO_TOKEN_MISSING`/`_INSUFFICIENT` correctly.
  3. The form refuses to submit if any role's filename is empty.
  4. Tests for `ImageBundleManager` cover classifier auto-select + manual-override + sharded-disabled cases.

### 5.3 SpeechTranscription (Whisper ASR)

- Roles: `asr.snapshot` (single logical role, whole snapshot).
- Selection shape: whole-snapshot.
- Picker mode: `previewOnly: true`. The picker UI shows:
  - Repo ref + Browse button (same pattern).
  - Total size (sum of file sizes) once the listing loads.
  - Table: `path`, `size`, classification badge. Classifier categorizes files as:
    - `weights` — `.safetensors`, `.bin`, `pytorch_model*`, `model.safetensors*`.
    - `tokenizer` — leaves `tokenizer.json`, `tokenizer_config.json`, `vocab.json`, `merges.txt`, `added_tokens.json`, `special_tokens_map.json`, `normalizer.json`.
    - `config` — `config.json`, `generation_config.json`, `preprocessor_config.json`, `feature_extractor*`.
    - `other` — everything else (READMEs, images, test audio).
  - A warning if total weights size > 10 GiB so the operator sees they're about to pull a very large snapshot.
  - A gated-repo banner driven by `REPO_TOKEN_MISSING`/`_INSUFFICIENT`.
- Download remains `POST /service-editors/SpeechTranscription/local-models/downloads` with `{ model_id: "<repo>" }` and optional `revision`; the server still stamps in the HF token.
- Operator workflow change: `AsrModelManager` replaces the bare "Model id" input with repo + Browse. The actual download button stays where it is and is enabled only after the Browse response has succeeded (so operators can't kick off an 8 GiB pull without at least seeing what's in the repo).
- Acceptance:
  1. Pasting `openai/whisper-large-v3` produces a preview, and the download submits unchanged.
  2. Pasting a misspelled repo surfaces `REPO_NOT_FOUND` inline without the download button becoming reachable.
  3. `revision` is still editable.

### 5.4 SpeechSynthesis (Kokoro TTS)

- Roles: `tts.snapshot` (primary, whole snapshot), `tts.tokenizer` (optional secondary snapshot).
- Selection shape: whole-snapshot × (1 or 2 repos).
- Picker: **two** `RepositoryFilePicker` instances in `previewOnly: true` mode — one for `model_id`, one for `tokenizer_id`. The tokenizer picker is collapsed by default and can be expanded when the operator needs to override; empty tokenizer repo is allowed.
- Classifier (reused from §5.3) plus TTS-specific badges:
  - `voice` — files under `voices/`, `.npz`, `.pt`, or leaves containing `voice`. Surfaced as a badge so operators can see how many voice packs are present.
  - `weights`, `tokenizer`, `config`, `other` as for ASR.
- Download unchanged: `POST /service-editors/SpeechSynthesis/local-models/downloads` with `{ model_id, tokenizer_id?, revision? }`.
- Acceptance:
  1. Pasting `hexgrad/Kokoro-82M` lists voice files and weights with correct badges and allows the existing download flow to succeed.
  2. Leaving `tokenizer_id` blank keeps the current behavior (service-side default tokenizer).

### 5.5 Embeddings

- Roles: `embeddings.snapshot` (whole snapshot; optional — operator can also load by local `model_path`).
- Selection shape: whole-snapshot OR local path. This service is the only one where "no HF repo" is a valid final state.
- Picker: one `RepositoryFilePicker` in `previewOnly: true` mode, shown only when the operator toggles "Download from Hugging Face" (matches the existing `model_id` vs `model_path` split on the load endpoint).
- Classifier: same categories as ASR (weights / tokenizer / config / other). No embeddings-specific role.
- Load endpoint unchanged: `POST /service-editors/Embeddings/local-models/load` with `{ model_id }` or `{ model_path }`.
- Acceptance:
  1. With `model_path` selected, the picker is hidden and no HF requests are made.
  2. With `model_id` selected, the preview populates and the load submit is enabled only after a successful Browse.

## 6. Client architecture

### 6.1 Extraction plan for the shared component

1. Lift the existing `RepositoryFilePicker` body out of `src/client/src/pages/settings/editors/image-generation/...`... no wait — it currently lives inside `LlamaCppForm.tsx`. Move it to:
   - `src/client/src/pages/settings/editors/common/RepositoryFilePicker.tsx`.
   - Re-export from `common/index.ts`.
2. Parameterize the hard-coded classifier and auto-select heuristics through the `classify` prop (§4.3). Keep the existing llama-cpp behavior by providing a `llamaCppClassifier` alongside.
3. Add per-service classifiers:
   - `sdClassifier(role: 'diffusion' | 'vae' | 'textEncoder')` in `image-generation/sdClassifier.ts`.
   - `snapshotPreviewClassifier` in `common/snapshotPreviewClassifier.ts` (shared by ASR / TTS / Embeddings), with a small `extraBadges?: (file) => string[]` extension point used by TTS for the `voice` badge.
4. All classifiers are pure functions over `HuggingFaceRepositoryFileDto[]` plus a small context record; they are unit-testable and MUST have coverage for: valid pick, ambiguous pick → no auto-select, sharded entry → disabled, empty listing.

### 6.2 API surface

`src/client/src/services/api.ts` keeps the single method introduced for llama-cpp:

```
api.settings.browseHuggingFaceRepository(repoRef: string): Promise<HuggingFaceRepositoryListingDto>
```

No additional client API methods are required for this rollout. The method's URL is updated if / when §4.1 aliases it under `/settings/huggingface/...`.

### 6.3 State persistence

- Per-role selections are owned by the parent editor (e.g. `ImageBundleManager`, `AsrModelManager`). The picker itself is controlled.
- When the wizard is resumed from `sessionStorage` (see `ActiveAddOperationState`), the editor rehydrates both the repo ref and the role selections; the picker must render the selections as if a Browse had just succeeded, without forcing another HF round-trip.

## 7. Server work

Beyond the aliasing described in §4.1, the server work for this doc is minimal:

1. **No new endpoints.** The existing `/huggingface/repositories/{owner}/{repo}/files` endpoint is sufficient because all classification happens on the client.
2. **No per-service branches inside the browse endpoint.** Service-agnostic data, service-specific interpretation, so the server stays narrow and testable.
3. **Logging / telemetry.** Keep the existing `tokenUsed` flag and failure-code log statements; add a structured log line `HfBrowseByService` that includes `serviceId` (e.g. `ImageGeneration`) so we can see which services drive the traffic — but **serviceId** must be passed via an *optional* `X-Service-Origin` header, not a URL/query parameter, to keep the endpoint URL stable and cacheable. Missing header falls through silently.

## 8. Error / edge-case matrix

| Scenario                                                      | Behavior                                                                                         |
|---------------------------------------------------------------|--------------------------------------------------------------------------------------------------|
| Operator pastes `https://huggingface.co/unsloth/…`            | Client parses owner/repo out of the URL before sending; no server change needed.                 |
| Repo has only sharded `.safetensors`                          | Rows are disabled with "multi-file weights — not supported"; role selection stays empty; submit blocked. |
| Classifier finds no candidates for a role                     | Role is reported as "No matching files in this repo"; manual fallback remains available.         |
| Classifier finds multiple plausible candidates                | First match is highlighted but not auto-selected; operator must confirm.                         |
| Public repo, no HF token configured                           | Browse works; no banner.                                                                         |
| Gated repo, no token                                          | `REPO_TOKEN_MISSING` → inline "Add a Hugging Face token" with a deep link to `Settings → Hugging Face`. |
| Gated repo, token without acceptance                          | `REPO_TOKEN_INSUFFICIENT` → inline "Accept the license on the model page" with a deep link to the repo. |
| HF 5xx / transport error                                      | `HF_UPSTREAM` → "Try again"; the download button stays disabled until a Browse succeeds.          |
| HF takes >30 s                                                | Existing 30 s `HttpClient` timeout fires; surfaced as `HF_UPSTREAM`; operator can retry.         |
| Operator changes repo ref after a successful Browse           | Pending selections reset; role dropdowns are disabled until a new Browse completes.              |
| Operator toggles manual fallback mid-flight                   | Dropdown selections are preserved as the initial text values of the manual inputs.               |

## 9. Tests

Minimum-viable test set per service. All classifiers have unit tests; each integrated editor has at least one Playwright-CLI acceptance.

- `common/RepositoryFilePicker.test.tsx`
  - Renders loading, error, success, preview-only, manual-fallback states.
  - Emits `onChange` for each role on selection changes.
  - Disables sharded entries.
- `image-generation/sdClassifier.test.ts`
  - SDXL base repo → diffusion/vae/textEncoder picks land in the right roles.
  - Flux repo → diffusion = `flux1-dev.safetensors`, text encoder in separate repo.
  - Ambiguous diffusion → no auto-select.
- `common/snapshotPreviewClassifier.test.ts`
  - Whisper-large-v3 file list → weights/tokenizer/config/other buckets populated as expected.
  - Kokoro file list → `voice` badge present for files under `voices/`.
- Playwright-CLI end-to-end per service
  - `ImageGeneration`: paste SDXL repo, pick three files, kick off download, assert operation appears in bundle list.
  - `SpeechTranscription`: paste `openai/whisper-large-v3`, preview renders, download completes.
  - `SpeechSynthesis`: paste `hexgrad/Kokoro-82M`, preview renders + voices counted, download completes.
  - `Embeddings`: toggle between `model_path` and `model_id`; picker only shows in the `model_id` case.

## 10. Rollout order

1. **Phase A — extract and alias.**
   - Move `RepositoryFilePicker` to `editors/common/` with the classifier prop.
   - Leave llama-cpp on the extracted component to verify no regression.
   - Add the `/api/settings/huggingface/...` alias; keep the `/llama/...` path functional.
2. **Phase B — Image Generation bundle wizard.** Highest user-visible payoff; replaces the three-repo six-text-field form.
3. **Phase C — SpeechTranscription.** Straightforward preview-only adoption.
4. **Phase D — SpeechSynthesis.** Adds the `voice` badge and the optional tokenizer repo picker.
5. **Phase E — Embeddings.** Only the model_id branch; smallest change, last because the existing UX is already the least painful of the four.
6. **Phase F — cleanup.** Remove the `/llama/...` legacy alias after all callers have switched, and remove any remaining free-text "paste JSON" escape hatches that duplicate the picker (unless operators still need them for out-of-app model sources — decide when Phase B lands).

## 11. Out-of-scope follow-ups (tracked here so they don't sneak in)

- Remembering recent HF repos the operator has browsed (nice but orthogonal).
- Showing HF download counts / likes / license inline (requires a second HF API call; defer until requested).
- In-browser license acceptance flow (HF does not expose this; must stay out-of-app).
- Replacing the existing `ImageBundleManager` "paste JSON" mode entirely. It remains as an advanced entry point until we have telemetry showing no one uses it.

