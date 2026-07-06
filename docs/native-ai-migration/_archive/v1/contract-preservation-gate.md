# Contract-Preservation Gate — Native AI Migration

Companion to [`00-overview.md`](./00-overview.md). Run after **every** phase (and at final
acceptance).

This is the load-bearing gate of the whole migration: the entire premise is that we can
swap engine implementations **without** the .NET server or the client ever noticing. A phase
that changes an observable public route, response shape, header, or admin-operation contract
— outside the single explicitly-approved Phase 3 voice-preset change — is an automatic FAIL.

Reference invariants: [`DECISIONS.md`](./DECISIONS.md) Part B (route compatibility; no .NET
contract changes; embedding consumer width **1536** via normalization — the local provider
reports `dimensions == 1024` for the default `Qwen3-Embedding-0.6B`; no runtime fallback).

---

## 1. Gate intent

Pass when all are true for the phase under test:

- Every **public nginx prefix** the phase touches still resolves with identical request and
  response semantics: `/asr/`, `/tts/`, `/emb/`, `/sd/`, `/llama-cpp/`, `/llama-admin/`
  (plus untouched `/sandbox/`, `/media/`). Internal ports may change; the public surface may
  not.
- Every **.NET client contract** listed in `00-overview.md` §2 still works **unmodified**
  (no code change on the .NET side) unless the phase doc explicitly lists a .NET change:
  - `SpeechTranscriptionService.cs` → `/asr/transcribe` (multipart field `audio`;
    `{ requestId, text, durationSeconds, modelRef }`).
  - `SpeechSynthesisService.cs` → `/tts/synthesize` (`{ text, voice, lang_code, speed }` →
    WAV bytes + duration header).
  - `LocalEmbeddingService.cs` → `/emb/embed` (`{ inputs, purpose }` →
    `{ data:[{embedding}], dimensions, modelRef }`; `dimensions` = the active model's dim
    (**1024** hard-checked today via the `SourceVectorDimensions` const), then normalized to
    the consumer width **1536**).
  - `LlamaRuntimeAdminClient.cs` → `/llama-admin/` (router CRUD, downloads,
    `POST /llama/restart`).
  - `SettingsServiceLocalModelsEndpoints.cs` / `LocalServiceAdminRouting.cs` → the `/admin/*`
    proxy surface for `asr|tts|emb|sd`.
  - `LocalAiStartupWarmupService.cs` → `/admin/load`, `/admin/unload`, `/admin/models`,
    `/health`, `/ready` (**D11:** reconcile behavior extended — warm local-routed services,
    idle remote-routed; admin HTTP contracts unchanged).
- The **admin-operation shape** is preserved: `POST /admin/models/download` →
  `GET /admin/models/{operation_id}` poll → `load`/`unload`/`DELETE` — same JSON field names,
  same status codes, same operation-id semantics (Phase 4 must not regress the "op lost on
  restart" behavior; persist or document the same limitation).
- **No runtime fallback** was introduced to paper over a gap (user rule): where output can't
  be bit-identical (ASR text, TTS audio, embedding vectors) the phase's own §Validation
  quality check governs — this gate only asserts the *contract shape*, and that failures are
  explicit (`/ready` false / `/health` degraded), not silently masked.

---

## 2. Procedure — golden request/response replay

1. **Capture goldens (pre-flight, before Phase 1)** against the running torch/Python
   services for every route above: exact request bytes and response
   bodies/headers/status codes. Store as fixtures; record in [`STATUS.md`](./STATUS.md).
2. **After each phase**, replay the goldens for the routes that phase owns against the new
   image and diff:
   - **Structural diff (must be identical):** JSON field names + types, status codes,
     header names (`x-request-id`, duration header), `dimensions` present (= active model's
     dim, `1024` for the default `Qwen3-Embedding-0.6B`), `modelRef` present; and .NET consumers still receive
     **1536**-wide vectors after normalization.
   - **Semantic diff (governed by the phase's quality check, not byte-equality):** ASR
     transcript text, TTS WAV samples, embedding vector values — compared per the phase doc's
     similarity/quality method, never assumed equal.
3. **`/ready` warmup gating** behaves identically: stays false until warmup completes when
   `GA_{ASR,TTS,EMB}_WARMUP_ON_LOAD=1` during a **Warm** reconcile.
4. **D11 routing reconcile:** global default = remote provider → service reports idle
   (`/ready` false, SD `/health` `status: unloaded`); flip to local → load + warmup;
   partial stack (local chat + remote images) → llama loaded, SD idle; routing save triggers
   reconcile without container restart; `GA_*_AUTO_LOAD_ON_STARTUP=0` — only API reconcile
   loads models.
5. **Kill-child resilience:** SIGKILL the native child (llama-server emb child, sd-server,
   `ga-audio-server`); the facade/engine reports unhealthy and recovers on next
   `/admin/load` — **no silent in-process fallback**.

---

## 3. Per-phase applicability

| Phase | Routes replayed | .NET contracts asserted | Approved contract change |
|---|---|---|---|
| 0 — D11 | n/a (API reconcile) | `LocalAiStartupWarmupService` warm/idle per routing; `ServiceModes` save trigger | D11 reconcile behavior (admin HTTP unchanged) |
| 1 — emb | `/emb/*` | `LocalEmbeddingService` (`dimensions==1024` for the default `Qwen3-Embedding-0.6B`; consumer width 1536 via normalization), warmup, settings admin | additive only: new `GET /emb/admin/catalog` under the existing `/emb/` prefix + curated GGUF dropdown (D9 §3.2 below). **No** change to `/emb/embed` route shape or the normalized 1536-wide consumer vector. |
| 2 — asr | `/asr/*` | `SpeechTranscriptionService` (multipart `audio`, duration), warmup, settings admin | none |
| 3 — tts | `/tts/*` | `SpeechSynthesisService` (WAV + duration header) | **only** the voice-preset list if D1 = Option B (`ServiceEditorMetadataProvider.cs` + `VoiceName` migration); plus the **D10 voice-pack attribution-completeness check** (§4.1) |
| 4 — control plane | all `/*/admin/*`, `/llama-admin/`, `/sd/` | `LlamaRuntimeAdminClient`, settings endpoints, `NotebookImageService.LocalSd.cs` | none (pure refactor behind nginx splits) |

---

### 3.2 Phase 1 embeddings-catalog check (D9 — extended to embeddings)

Folded into this gate because it is a Phase-1 "no silent gap" selection/download concern
that must not disturb the `/emb/*` contract. When Phase 1 ships the curated embeddings
catalog ([`model-catalog-and-downloads.md`](./model-catalog-and-downloads.md) §3.5/§5), the
following must be **green** and proven to **fail loudly** on a synthetic defect:

- **`/emb/embed` route shape unchanged.** Same request `{ inputs, purpose }`, same response
  `{ data:[{embedding}], dimensions, modelRef }`; `dimensions` reflects the active model
  (`== 1024` for the default `Qwen3-Embedding-0.6B`) and .NET consumers still receive **1536**-wide vectors after
  normalization. The new `GET /emb/admin/catalog` is additive under the existing `/emb/`
  prefix — it does not alter any existing route.
- **Curated dropdown, no free-text.** The embeddings picker offers **only** `task:emb`
  catalog entries (GGUF, `producedDimension <= 1536`); the former free-text
  `RepositoryFilePicker` (`EmbRuntimeManager.tsx:668-691`) no longer submits arbitrary repos.
- **Download allowlist + format + dimension ceiling (loud fail).** A non-manifest repo, a
  **non-GGUF / safetensors-only** source, a multi-file/globbed request, a gated repo
  without a token, or a model whose produced dimension is **> 1536** is **rejected loudly**
  (contract-compatible 400 / failed operation) — never coerced, and never allowed to be
  silently truncated by `EmbeddingVectorDimensions.NormalizeToTarget` (`:15`) (banned
  fallback). A **≤ 1536** model is accepted but is a model change: `SourceVectorDimensions`
  is updated as a matched pair and the corpus is re-embedded (D3).
- **Load-after-download inclusion gate (build/ship-time, not a runtime flag).** Every
  **published** `emb` entry passed a build-time check: it loads on our
  `llama-server --embeddings --pooling last` build and its actual produced dimension equals
  the recorded `producedDimension` and is `<= 1536`. Candidates that fail simply do not ship,
  so the manifest is the verified set — there is no runtime "unverified" state and the picker
  branches on no `loadVerified`-style flag.

A wrong-format / `> 1536`-dimension / non-allowlisted selection is a **loud** rejection —
the model is never silently substituted, defaulted, or dimension-truncated.

### 3.1 Phase 3 voice-pack attribution-completeness check (D10)

Folded into this gate (rather than a separate gate doc) because it is a Phase-3 "no silent
gap" contract concern. When Phase 3 ships the voice pack
([`voice-pack-and-attribution.md`](./voice-pack-and-attribution.md) §5.4), the build/CI
step over `voice-pack/manifest.json` must be **green** and proven to **fail loudly** on a
synthetic defect:

- Every `voiceId` is unique + pattern-valid; every `clipPath` exists (and matches
  `checksumSha256` when present).
- **Every `licence != CC0-1.0` clip has complete `attribution`** (creator, sourceUrl,
  licenceUrl; title when supplied) and a `modified` note when changed. CC0 clips record
  provenance.
- `NOTICE.md` regenerated from the manifest equals the committed `NOTICE.md` (no drift).
- **No `VoiceName` migration exists** (§6 of the voice-pack doc): legacy Kokoro ids are not
  mapped. Assert that an unknown/legacy voice id is **rejected loudly** (no silent remap, no
  fallback) rather than round-tripped.

A missing/incomplete attribution is a **loud build/gate FAIL** — the clip is never silently
dropped or shipped unattributed.

## 4. Report-back addition (every phase)

```text
CONTRACT-PRESERVATION GATE (Phase N):
- Public routes unchanged (list + pass/fail): <...>
- .NET client contracts unmodified (or approved change ref): <pass/fail + which>
- Golden replay structural diff (field names/status/headers/`dimensions`=active model dim, 1024 for the default Qwen3-Embedding-0.6B; consumer vectors 1536-wide): <pass/fail>
- Semantic quality check (per phase doc): <pass/fail + method>
- /ready warmup gating + entrypoint monitor parity: <pass/fail>
- Kill-child recovery, no silent fallback: <pass/fail>
- [Phase 1 only] D9 embeddings-catalog check (curated GGUF dropdown of the published set, `producedDimension <= 1536`; non-manifest/non-GGUF/`> 1536` rejected loudly; build-time inclusion gate = published ⇒ verified, actual dim == recorded dim, no runtime flag; model change → re-embed D3 + `SourceVectorDimensions` matched pair): <pass/fail>
- [Phase 3 only] D10 voice-pack attribution-completeness check + migration round-trip: <pass/fail>
```
