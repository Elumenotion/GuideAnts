# Phase 2 — ASR via audio.cpp (`engine_runtime`) across all flavors

Parent: [`00-overview.md`](./00-overview.md). Prerequisite: Phase 1 complete (reuses its
parity-testing playbook and rollout conventions; no hard technical dependency).
Decisions: [`DECISIONS.md`](./DECISIONS.md) **D4** (rocm = Vulkan build) + **D5** (thin
`ga-audio-server` adapter, not a fork) — confirm both before dispatch. Gates:
[`contract-preservation-gate.md`](./contract-preservation-gate.md),
[`flavor-build-gate.md`](./flavor-build-gate.md).

**Mission:** replace the torch-based `docker/build/guideants-ai/asr-service/asr_service.py`
with a persistent native HTTP service (`ga-audio-server`) linking audio.cpp's
`engine_runtime` static library, running Qwen/Qwen3-ASR-0.6B on the best available
backend of each image flavor (cuda13, vulkan, rocm, cpu), while preserving the `/asr/`
HTTP contract.

---

## 1. Scope

- New native binary **`ga-audio-server`**: a thin HTTP adapter over `engine_runtime`
  (public headers under `d:\repos\audio.cpp\include\`, links ggml). One process, one
  loaded model + reusable session, multipart upload support, admin lifecycle endpoints.
  Designed from the start to host the TTS task too (Phase 3) — same process or a second
  instance of the same binary, decided in Phase 3.
- Per-flavor Docker build stages compiling audio.cpp + the adapter.
- Replace the ASR entry in `entrypoint.sh` / `start-asr.sh`; retire
  `asr-requirements.txt` (qwen-asr and friends).
- The ROCm backend decision (overview §6.4) is executed here.

## 2. Out of scope

- TTS (Phase 3) — but the adapter's HTTP/admin skeleton must not hardcode ASR-only
  assumptions.
- Any .NET change. `SpeechTranscriptionService.cs` is untouched.
- Streaming transcription (current service is request/response; keep parity, note as
  future work).
- Diarization (the local provider path never had it; Azure provider handles that).

## 3. Contract to preserve (verified)

`SpeechTranscriptionService.TranscribeViaLocalAsrWithDurationAsync`
(`src/server/GuideAntsApi/Services/Components/SpeechTranscriptionService.cs:774-849`):

- `POST {SpeechTranscriptionBaseUrl}/asr/transcribe`, multipart form, file field named
  **`audio`**, `x-request-id` header. Audio may be wav/mp3/ogg/flac/webm/opus/etc.
  (video is pre-extracted to audio by .NET before this call).
- JSON response `{ requestId, text, durationSeconds, modelRef }`
  (`LocalAsrTranscriptionResponse`, case-insensitive parse).
- Admin surface (same shape as emb; consumed by `SettingsServiceLocalModelsEndpoints.cs`
  and `LocalAiStartupWarmupService.cs`): `GET /health`, `GET /ready` (warmup-gated),
  `POST /admin/load` (`{model_id | model_path, hf_token, …}`), `POST /admin/unload`,
  `GET /admin/models`, `POST /admin/models/download` (+ operation poll at
  `GET /admin/models/{operation_id}`), `DELETE /admin/models/{model_ref}`.
- Entrypoint monitor (`entrypoint.sh:421-428`): `/ready` on `GA_ASR_PORT` (8082) when
  `GA_ASR_AUTO_LOAD_ON_STARTUP=1` + `GA_ASR_WAIT_FOR_READY_ON_STARTUP=1`; warmup uses
  `GA_ASR_WARMUP_AUDIO_PATH` (default `/app/asr-service/warmup.webm` — note: webm, so
  decode support is part of warmup parity).
- Health filter detail: `asr_service.py:41` suppresses access-log noise for
  `/health` + `/ready` — cosmetic, not contract.

## 4. audio.cpp facts this phase builds on (verified)

- `qwen3_asr` loader is registered in the default release registry
  (`src/framework/runtime/registry.cpp:223`).
- Documented model layout `models/Qwen3-ASR-0.6B` (`docs/qwen3.md:106-114`) is the HF
  safetensors snapshot — **the same artifacts** GuideAnts already downloads to
  `/models-local/asr/Qwen3-ASR-0.6B` (`GA_ASR_DEFAULT_MODEL_PATH=Qwen3-ASR-0.6B`,
  `GA_ASR_DEFAULT_MODEL_ID=Qwen/Qwen3-ASR-0.6B`). GGUF is not needed and not supported
  for this family. Weight-type knob exists
  (`--session-option qwen3_asr.weight_type=native|f32|f16|bf16|q8_0`, `docs/qwen3.md:154`).
- Backend selection is per-session (`SessionOptions.backend.type`,
  `include/engine/framework/core/module.h:13-19`: `Cpu, Cuda, Vulkan, Metal,
  BestAvailable`; init in `src/framework/core/backend.cpp:87-152`).
- Reference server `app/server/*` (~1060 LOC): reusable patterns (config, model/session
  cache in `runtime.cpp`, lazy load) but hardcodes `BackendType::Cuda`
  (`runtime.cpp:396`), takes transcription input as a **server-local path in JSON**
  (`README.md` `POST /v1/audio/transcriptions`), and has no multipart, no admin
  load/unload/download, no warmup-gated ready. Hence the custom adapter (overview §6.5).
- `audiocpp_cli` has no daemon mode → per-request CLI is not viable; persistent process
  required.

## 5. Required changes

| Component | Change |
|-----------|--------|
| New: `docker/build/guideants-ai/audio-server/` (or a dedicated repo/submodule — decide at implementation) | `ga-audio-server` sources: HTTP layer (multipart + JSON), admin/op-tracking, warmup, backend selection from env/config, linking `engine_runtime`. Reuse `app/server/runtime.cpp` session-cache approach as reference. Audio decode: shell out to the image's `ffmpeg` (already installed on all full flavors, used by current warmup path) to normalize uploads to 16 kHz mono WAV — do not grow a native demuxer dependency in v1. |
| Docker build stages | New builder stage per flavor compiling audio.cpp: cuda13 → `-DENGINE_ENABLE_CUDA=ON` (align `CMAKE_CUDA_ARCHITECTURES` with the SD builder's `SD_CUDA_ARCHITECTURES="75;80;86;89;90"`); vulkan → `-DENGINE_ENABLE_VULKAN=ON`; cpu → both OFF; rocm → per §7. audio.cpp source pinned by ref (ARG like `SDCPP_REF`). |
| `docker/build/guideants-ai/start-asr.sh` | Launch `ga-audio-server` instead of `python asr_service.py`; keep `apply_cuda_visible_devices_override "GA_ASR_CUDA_VISIBLE_DEVICES"`. |
| `entrypoint.sh` | Unchanged structurally (still `/app/start-asr.sh &`); ASR remains log-and-drop on exit (no respawn), matching today. |
| Removed | `asr-service/asr_service.py` runtime (warmup.webm asset kept), `asr-requirements.txt` (torch itself remains until TTS/emb also migrated). |
| `docker/guideants-ai-build.md` | Document the new builder stage + model notes. |

### HTTP surface of `ga-audio-server` (v1)

Exactly the contract in §3 — no more. Model download = HF snapshot download (safetensors
layout) with operation tracking; implement natively (libcurl) or delegate downloads to
the control plane in Phase 4 — for this phase the endpoint must exist and work, since the
settings UI and warmup service call it.

**Model selection is constrained here, not left free-form (D9).** Per
[`model-catalog-and-downloads.md`](./model-catalog-and-downloads.md), the ASR picker is a
curated catalog dropdown (this phase ships `qwen3_asr_0_6b` from the allowlisted repo
`Qwen/Qwen3-ASR-0.6B`), replacing today's free-text HF browse
(`AsrModelManager.tsx:654,678`) and free-form `snapshot_download`
(`asr_service.py:317-324`). The `/admin/models/download` handler must resolve a manifest
id → allowlisted repo/files and **reject** any non-allowlisted repo, GGUF, or
gated-without-token request loudly (never coerce). Add `GET /asr/admin/catalog` under the
existing `/asr/` prefix. This enforcement moves into `ga-admin` in Phase 4.

### Env var disposition

| Var | Disposition |
|-----|-------------|
| `GA_ASR_HOST/PORT/MODEL_DIR/DEFAULT_MODEL_PATH/DEFAULT_MODEL_ID/AUTO_LOAD_ON_STARTUP/WAIT_FOR_READY_ON_STARTUP/READY_TIMEOUT_SECONDS/TIMEOUT_SECONDS/WARMUP_ON_LOAD/WARMUP_AUDIO_PATH/WARMUP_LANGUAGE/CUDA_VISIBLE_DEVICES` | Kept, same meaning. |
| `GA_ASR_DEVICE_MAP`, `GA_ASR_DTYPE` | Retired (torch concepts). Replacement: `GA_ASR_BACKEND` (`cuda|vulkan|cpu|best`, default per flavor) and optional weight-type knob mapping to `qwen3_asr.weight_type`. Unknown-set values fail loudly at startup. |
| `GA_ASR_MAX_INFERENCE_BATCH_SIZE`, `GA_ASR_MAX_NEW_TOKENS` | Map to engine session/request options if equivalents exist; **to be validated in this phase** against `engine_runtime` request options — otherwise retire with a deprecation log. |

## 6. Backend/flavor matrix

| Flavor | audio.cpp build | Session backend | Notes |
|--------|-----------------|-----------------|-------|
| cuda13 | `ENGINE_ENABLE_CUDA=ON` | `BackendType::Cuda` | Primary target; matches upstream's tested path. |
| vulkan | `ENGINE_ENABLE_VULKAN=ON` | `BackendType::Vulkan` | New capability: GPU ASR where torch was CPU-only. Validate qwen3_asr op coverage on Vulkan (see `validate_backend_graph_supported`, `backend.cpp:220`) — **to be validated in this phase**. |
| rocm | Decision §7 | Vulkan (recommended) or hipified Cuda | |
| cpu | both OFF | `BackendType::Cpu` | Throughput check vs current CPU torch path. |

## 7. ROCm route (open decision 6.4 — execute here)

Verified: audio.cpp exposes no HIP option (`CMakeLists.txt:41-43`), `build_linux.sh`
knows `cuda|vulkan|cpu` only, `backend.cpp` has no HIP branch; vendored ggml supports
`GGML_HIP` (`external/ggml/CMakeLists.txt:215`).

- **Option A (recommended v1): Vulkan build inside the rocm image.** AMD GPUs run
  Vulkan via RADV; rocm flavor's llama-server keeps its ROCm build (from
  `ghcr.io/ggml-org/llama.cpp:server-rocm`). Cost: mixed GPU APIs in one image; needs
  the Vulkan ICD/loader packages added to the rocm runtime image and validation that
  RADV coexists with the ROCm userspace already present.
- **Option B: add `ENGINE_ENABLE_HIP` to audio.cpp** setting `GGML_HIP=ON` (ggml builds
  the CUDA backend sources as HIP; `ggml_backend_cuda_*` entry points are expected to
  exist under HIP builds — as evidenced by stable-diffusion.cpp's `GGML_HIPBLAS` build in
  `Dockerfile.rocm:41-43` — so `BackendType::Cuda` would drive it). Upstream (audio.cpp)
  work + CI; symbol/behavior compatibility **to be validated** before committing.
- Do **not** ship rocm with CPU-only ASR silently; if neither option lands in time, the
  rocm flavor ships `GA_ASR_BACKEND=cpu` **explicitly documented** as such.

## 8. Risks

- **Transcript drift**: ggml inference will not produce byte-identical text vs
  torch/qwen-asr. Mitigation: golden-set WER-style comparison (see §9); human review of
  divergent samples before cutover.
- **Vulkan op coverage** for qwen3_asr is unproven here (upstream examples show `--backend
  cuda`). If unsupported ops surface, fall back to… no — *decide*: fix upstream, or ship
  that flavor CPU-backed explicitly. No silent degradation.
- **Build weight**: compiling audio.cpp (+ vendored ggml) per flavor adds significant
  image build time; mirror the existing sd-cli builder-stage pattern and cache.
- **Duration reporting**: `durationSeconds` feeds .NET logging/metering; compute from
  the decoded audio (ffprobe/ffmpeg output), matching `asr_service.py`'s
  `get_audio_duration_seconds` semantics.
- **Concurrent requests**: current service serializes inference internally. The adapter
  must define its concurrency story (recommend: single inference lane + bounded queue,
  matching today's behavior).
- **Memory pressure**: on single-GPU hosts, ASR now shares VRAM as a ggml process; the
  existing `GA_ASR_CUDA_VISIBLE_DEVICES` and `GA_LLAMA_TENSOR_SPLIT` operator knobs are
  the mitigation (already documented in `start-llama.sh:92-97`).

## 9. Validation

1. Golden set: ≥50 clips (short/long, clean/noisy, webm/wav/mp3, the languages GuideAnts
   users rely on). Transcribe on current torch service and on `ga-audio-server`
   per flavor; compare WER/CER between the two outputs against reference transcripts.
   Acceptance threshold is set by human review — do not invent one in code.
2. Contract tests: multipart `/asr/transcribe` (field `audio`), all admin ops, `/ready`
   gating (must stay false until warmup completes when `GA_ASR_WARMUP_ON_LOAD=1`).
3. All four flavors: image builds green; `HEALTHCHECK` passes; entrypoint readiness
   monitor path exercised (`GA_ASR_WAIT_FOR_READY_ON_STARTUP=1`).
4. .NET e2e: voice-note upload through `SpeechTranscriptionService` against a running
   container; `LocalAiStartupWarmupService` load/unload cycle; settings UI model
   download → load → transcribe → unload → delete.
5. Latency/VRAM comparison vs torch baseline on cuda13 recorded in the PR description
   (measured, not estimated).

## 10. Rollback

Image-tag rollback. Model artifacts are unchanged (same safetensors snapshot dir), so no
volume migration is involved — the previous torch image boots against the same
`/models-local/asr` content. Keep `GA_ASR_DTYPE`/`GA_ASR_DEVICE_MAP` values in compose
files until the fleet is off the old image, since the old image requires them.

## 11. Gates

Run after this phase; record results in [`STATUS.md`](./STATUS.md).

- [`contract-preservation-gate.md`](./contract-preservation-gate.md) — `/asr/transcribe`
  multipart golden replay (field `audio`, `{ requestId, text, durationSeconds, modelRef }`);
  `/asr/admin/*` cycle; warmup-gated `/ready` (incl. the `.webm` warmup decode path).
- [`flavor-build-gate.md`](./flavor-build-gate.md) — all four flavors build the new
  audio.cpp builder stage + boot; **rocm runs the Vulkan build (D4)**; no flavor silently
  falls back to CPU (if a flavor must ship CPU-backed, it is explicitly documented).
- CodeQL is **not** run here. The new C++ (`ga-audio-server`) surface — multipart upload +
  ffmpeg shell-out — is scanned in the **end-only** [`codeql-gate.md`](./codeql-gate.md).

## 12. Definition of Done

- [ ] `ga-audio-server` links `engine_runtime`, hosts qwen3_asr, and preserves the `/asr/`
      contract exactly; adapter (not fork) per D5.
- [ ] Backend selectable per flavor (`GA_ASR_BACKEND`); `GA_ASR_DEVICE_MAP`/`DTYPE` retired
      with unknown-set values failing loudly.
- [ ] Vulkan op-coverage for qwen3_asr validated; result recorded (fix upstream or ship that
      flavor CPU-backed **explicitly**, never silently).
- [ ] WER/CER golden comparison vs torch reviewed before cutover.
- [ ] Contract-preservation + per-flavor build gates green on all four flavors.
- [ ] **Torch-removal step (Tier A, partial):** `asr-requirements.txt` retired (`qwen-asr`
      + `sentencepiece` + `soundfile` gone); `pipdeptree -r -p torch` no longer lists
      `qwen-asr`. `accelerate` is dropped **only if** TTS no longer needs it
      (`tts-requirements.txt:1` still declares it → keep until Phase 3, do not force). `torch`
      itself stays until TTS migrates (D8). See [`torch-removal-gate.md`](./torch-removal-gate.md)
      §3 step 3.
