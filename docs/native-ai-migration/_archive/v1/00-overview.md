# Native AI Migration — Overview & Phase Plan

Last updated: 2026-07-02

This document set plans the refinement of the `guideants-ai` Docker image around three
specific goals. It is **not** a "remove all Python" effort.

1. **ASR + TTS on the audio.cpp backend** (`engine_runtime` library, `d:\repos\audio.cpp`)
   across **all** GPU build flavors (cuda13, vulkan, rocm, cpu) — replacing the torch-based
   `asr_service.py` and `tts_service.py`.
2. **Embeddings via llama.cpp** (`llama-server --embeddings`) — replacing the
   sentence-transformers-based `emb_service.py`.
3. **Service-surface simplification** — consolidating the control-plane/admin layers
   (llama-admin, SD admin, and the ASR/TTS/emb admin needs) instead of running many
   separate FastAPI processes.

**Explicit outcome — remove PyTorch from the `guideants-ai` full image.** Goals 1–2 exist
so that the torch stack (`torch`/`torchaudio`/`torchvision` + `sentence-transformers`,
`qwen-asr`, `kokoro`/`misaki`, `transformers`/`accelerate`) can leave `/opt/venv`. This is
a **success criterion**, not a side effect — see §7.1 and the
[`torch-removal-gate.md`](./torch-removal-gate.md). The authoritative dependency audit is
[`torch-dependencies-report.md`](./torch-dependencies-report.md). Two honesty caveats are
load-bearing and repeated throughout:

- **Torch only fully leaves after ASR, TTS, and embeddings have _all_ migrated.** Any one
  service still on torch keeps the whole wheel resident (§7.1). TTS was the hard blocker
  (`kokoro` pulls torch directly); the TTS model decision (D1) is a prerequisite for the
  torch-removal goal, tracked as **D8**. **D1 is now resolved (`chatterbox`, native/no-torch),
  so the goal is reachable once Phase 3 ships.**
- **Service torch and sandbox torch share one venv.** `guideants-ai` has a single
  `/opt/venv` used by both the AI services **and** the ScriptExecutionAgent sandbox
  (verified: `Dockerfile.cuda:150-170`, `build_guideants_ai.sh:244-259`,
  `script-agent-admin/reconcile.sh:146`). Removing the torch-*dependent service packages*
  is invisible to sandbox users; removing **torch itself** additionally requires a
  sandbox-scope decision (**D7**) because the sandbox declares `torch` for user code
  (`Sandboxes/python311TorchCUDA/requirements.txt:1-2,181`). See §1 and the gate doc.

> **Audience split**
>
> - `00-overview.md` (this): goals, non-goals, architecture, phase ordering, gate model,
>   open decisions requiring a human call. Read alongside [`DECISIONS.md`](./DECISIONS.md)
>   and [`STATUS.md`](./STATUS.md).
> - `phase-1-embeddings-llama-server.md` … `phase-4-control-plane-consolidation.md`:
>   one self-contained brief per phase with scope, required changes, contract
>   preservation, flavor matrix, risks, validation, rollback, gates, and a Definition of
>   Done. Each phase also reads [`DECISIONS.md`](./DECISIONS.md) and its gate docs.

---

## 0. How to use this folder

| File | Purpose |
|------|---------|
| `00-overview.md` (this) | Goals/non-goals, current vs target architecture, phase ordering + dependencies, gate model, open decisions. |
| [`DECISIONS.md`](./DECISIONS.md) | Locks the open decisions (D1–D11) + frozen invariants. Single source of truth; phases build against it, not against reinterpretations. |
| [`STATUS.md`](./STATUS.md) | Living ledger: baseline capture, per-phase state, gate results, blocking decisions, deviations. Updated after every phase + gate. |
| [`acceptance-evidence.md`](./acceptance-evidence.md) | Evidence template — the commands/outputs/test refs that prove each phase done. |
| [`phase-1-embeddings-llama-server.md`](./phase-1-embeddings-llama-server.md) | Embeddings served by a dedicated `llama-server --embeddings` instance behind the existing `/emb/` contract. |
| [`phase-2-asr-audiocpp.md`](./phase-2-asr-audiocpp.md) | ASR served by a new native adapter (`ga-audio-server`) linking `engine_runtime`, per flavor. |
| [`phase-3-tts-decision.md`](./phase-3-tts-decision.md) | TTS model decision (enable Kokoro upstream vs migrate to a released audio.cpp family) + migration. |
| [`phase-4-control-plane-consolidation.md`](./phase-4-control-plane-consolidation.md) | One consolidated admin/control-plane service; retire per-service FastAPI processes. |
| [`contract-preservation-gate.md`](./contract-preservation-gate.md) | Run after **every** phase: public routes + .NET client contracts unchanged; golden replay; no silent fallback. |
| [`flavor-build-gate.md`](./flavor-build-gate.md) | Run after build-touching phases: cuda13/vulkan/rocm/cpu build + `HEALTHCHECK` + correct backend (no silent CPU degrade). |
| [`codeql-gate.md`](./codeql-gate.md) | **End-only, changed-languages-only** security scan (the final gate). Not run per phase. |
| [`torch-removal-gate.md`](./torch-removal-gate.md) | Proves the explicit torch-removal outcome: Tier A (torch-dependent service packages gone, Phases 1–3) and Tier B (`torch` itself gone, needs D7 + D8). Records per-flavor image-size delta; no silent fallback. |
| [`torch-dependencies-report.md`](./torch-dependencies-report.md) | Authoritative audit of every package depending on torch in the `guideants-ai` image (direct + transitive). Source of the removal targets. |
| [`model-catalog-and-downloads.md`](./model-catalog-and-downloads.md) | **Embeddings/ASR/TTS** feature (D9): constrain each settings-UI model picker to a curated catalog manifest and restrict downloads to allowlisted sources. ASR/TTS entries derive from audio.cpp (`model_manager.py` ∩ `registry.cpp` ∩ README `released`); **embeddings entries are single-file GGUF for `llama-server --embeddings` with `producedDimension <= 1536`** (the consumer-invisible width is 1536 via `NormalizeToTarget`, not 1024; `> 1536` rejected loudly to avoid silent truncation; inverse format rule — GGUF required, safetensors rejected), **default `Qwen3-Embedding-0.6B`** (official GGUF, Apache-2.0, native 1024, multilingual) replacing Harrier, alternatives `EmbeddingGemma-300M`/`bge-m3`; license surfaced for transparency only (never a curation filter); build-time load-after-download inclusion gate; the Harrier → Qwen3 cutover needs a `SourceVectorDimensions` matched-pair check (stays 1024) + corpus re-embed (D3). Spans the embeddings picker (P1), ASR (P2), TTS + voice pack (P3), and enforcement in `ga-admin` (P4). **llama chat models / SD remain out of scope.** |
| [`voice-pack-and-attribution.md`](./voice-pack-and-attribution.md) | **TTS-only** feature (D10): the baked, model-agnostic **reference-voice pack** for voice-clone TTS (Chatterbox is the active consumer). Manifest schema + per-language CC0-first sourcing + a NOTICE artifact baked into the image + a build-time attribution-completeness check (loud fail). `KokoroVoiceNames` is replaced outright by the pack ids — **no legacy `VoiceName` migration / no backwards compat**. **Not** a model download (that is D9); local image-layer assets only. |

Every phase doc follows the same template: Scope → Out of scope → Required changes →
API contract preservation → Backend/flavor matrix → Risks → Validation → Rollback → Gates
→ Definition of Done.

---

## 1. Non-goals

- **Not** a Python purge. Python 3.11 stays in the image for the sandbox
  (ScriptExecutionAgent kernels), `media_service.py`, and (initially) the consolidated
  control plane.
- **Sandbox torch is a separate decision (D7) — and it is _not_ a separate venv.**
  Earlier framing here treated "service venv torch" and "sandbox venv torch" as
  independent; verification shows that is **inaccurate for the `guideants-ai` image**:
  there is a **single** `/opt/venv`. The Dockerfile installs torch + the ASR/TTS/emb
  requirements **and** the filtered sandbox requirements into it
  (`Dockerfile.cuda:152-170`, torch line `:160`; sandbox `requirements.txt` copied +
  `torch*`-stripped by `build_guideants_ai.sh:234-259`), and the sandbox admin reconcile
  installs into the same venv (`script-agent-admin/reconcile.sh:146`). Consequently this
  plan removes the torch-*dependent service packages* (`sentence-transformers`, `qwen-asr`,
  `kokoro`/`misaki`, and `transformers`/`accelerate`/`tokenizers` when unused) once ASR,
  TTS, and embeddings have all migrated — that part is invisible to sandbox users because
  none of those are declared by the sandbox requirement set (verified). Removing **torch
  itself** (`torch`/`torchaudio`/`torchvision`) would also strip it from user sandbox
  scripts, since the sandbox declares torch (`Sandboxes/python311TorchCUDA/requirements.txt`
  lines 1-2, 181). That is the current recommendation's out-of-scope item, locked as **D7**
  and enforced as "Tier B" in [`torch-removal-gate.md`](./torch-removal-gate.md).
- **No breaking changes to the `.NET` / client contracts** unless explicitly listed in a
  phase doc. The nginx route prefixes (`/asr/`, `/tts/`, `/emb/`, `/sd/`, `/llama-cpp/`,
  `/llama-admin/`, `/sandbox/`, `/media/`) are preserved throughout.
- **`Dockerfile.slim` is untouched** — it already runs only sandbox + media + nginx
  (`entrypoint.slim.sh`) with no torch and none of the affected services.
- **stable-diffusion.cpp inference path unchanged.** `sd_service.py`'s *admin/facade*
  role is absorbed in Phase 4, but the native `sd-server` and the `/sd/txt2img`,
  `/sd/img2img` contracts are not redesigned here.

---

## 2. Current architecture (verified 2026-07-02)

One container (`guideants-ai`). `docker/build/guideants-ai/entrypoint.sh` starts, without
serial wait dependencies:

| Process | Port | nginx route | Implementation |
|---------|------|-------------|----------------|
| nginx (ingress) | :80 | — | `docker/build/guideants-ai/nginx.conf` |
| llama-server (router mode) | :8080 | `/llama-cpp/` | prebuilt binary from `ghcr.io/ggml-org/llama.cpp:server-*` base images |
| llama-admin | :8086 | `/llama-admin/` | `llama-admin-service/llama_admin_service.py` (FastAPI, ~1500 lines) |
| ScriptExecutionAgent | :8081 | `/sandbox/` | .NET |
| ASR | :8082 | `/asr/` | `asr-service/asr_service.py` (FastAPI + torch, Qwen/Qwen3-ASR-0.6B via qwen-asr) |
| SD facade | :8083 | `/sd/` | `sd-service/sd_service.py` (FastAPI, ~2200 lines; spawns native `sd-server` on :18083) |
| TTS | :8084 | `/tts/` | `tts-service/tts_service.py` (FastAPI + torch, hexgrad/Kokoro-82M via kokoro) |
| Embeddings | :8085 | `/emb/` | `emb-service/emb_service.py` (FastAPI + sentence-transformers, microsoft/harrier-oss-v1-0.6b) |
| Media | :8087 | `/media/` | `media-service/media_service.py` (stays as-is) |

The entrypoint's monitor loop respawns **only llama-server** (to pick up
`router-models.ini` changes after llama-admin SIGTERMs it); all other services log-and-drop
on exit. nginx exit shuts the container down.

Torch 2.11.0 + transformers + accelerate are installed into `/opt/venv` by
`Dockerfile.cuda` (CUDA wheels), and CPU wheels on rocm/vulkan/cpu flavors — meaning ASR/
TTS/embeddings run **CPU-only torch on three of the four full flavors today**. Goal 1
directly fixes this: audio.cpp gives ASR/TTS a real GPU path on vulkan (and potentially
rocm) where torch never had one.

### Admin surface (identical FastAPI pattern on ASR/TTS/emb; verified line numbers)

Each of `asr_service.py` (:542–692), `tts_service.py` (:703–854), `emb_service.py`
(:648–829) exposes: `GET /health`, `GET /ready`, `POST /admin/load`,
`POST /admin/unload`, `GET /admin/models`, `POST /admin/models/download`,
`GET /admin/models/{operation_id}`, `DELETE /admin/models/{model_ref}`, plus the
inference route (`POST /transcribe`, `POST /synthesize`, `POST /embed`).
`emb_service.py` additionally has `POST /admin/models/{operation_id}/cancel`.

### .NET consumers that must keep working (verified)

- `src/server/GuideAntsApi/Services/Components/SpeechTranscriptionService.cs`
  (`TranscribeViaLocalAsrWithDurationAsync`): multipart field `audio` →
  `{SpeechTranscriptionBaseUrl}/asr/transcribe`; parses
  `{ requestId, text, durationSeconds, modelRef }` (`LocalAsrTranscriptionResponse`).
- `src/server/GuideAntsApi/Services/Components/SpeechSynthesisService.cs`
  (`SynthesizeViaLocalTtsAsync`): JSON `{ text, voice, lang_code, speed }` →
  `{SpeechSynthesisBaseUrl}/tts/synthesize`; expects WAV bytes back plus a duration
  header parsed by `ParseDurationSeconds`.
- `src/server/GuideAntsApi.BackgroundJobs/Services/Embeddings/LocalEmbeddingService.cs`:
  JSON `{ inputs, purpose }` (purpose ∈ `query`|`document`) →
  `{EmbeddingsBaseUrl}/emb/embed`; the local provider **hard-requires** `dimensions == 1024`
  today (the const `SourceVectorDimensions = 1024`, the current model's native dim — **not**
  a wire contract) and then normalizes each vector to the consumer width **1536**
  (`EmbeddingVectorDimensions.NormalizeToTarget`, `Target = 1536`; note it **silently
  truncates** any source > 1536). Response shape: `{ data: [{embedding}], dimensions, modelRef }`.
- `src/server/GuideAntsApi/Endpoints/LocalServiceAdminRouting.cs` +
  `Endpoints/Settings/SettingsServiceLocalModelsEndpoints.cs`: the settings UI proxies
  to `{host}/asr|/tts|/emb|/sd` + `/admin/models`, `/admin/models/download`,
  `/admin/models/{operationId}`, `/admin/load`, `/admin/unload`, `/ready`.
- `src/server/GuideAntsApi/Services/Bootstrap/LocalAiStartupWarmupService.cs`: **routing-aware
  reconcile** (D11) — `POST {adminBase}/admin/load` or `/admin/unload`,
  `GET /admin/models`, `GET /health`, `GET /ready` per service desired state; llama alias
  load/unload via `ILlamaServerRuntimeClient`. Admin HTTP contracts unchanged; reconcile
  **behavior** extended (see §2a).
- `src/server/GuideAntsApi/Services/LlamaCpp/LlamaRuntimeAdminClient.cs`
  (`ILlamaRuntimeAdminClient`) → `{host}/llama-admin/` (router entries CRUD,
  downloads, `POST /llama/restart`).

### Env var surface (docker-compose.{cuda,vulkan,rocm,cpu,ghcr-*}.yml)

`GA_ASR_*` (HOST/PORT/MODEL_DIR/DEFAULT_MODEL_PATH/DEFAULT_MODEL_ID/AUTO_LOAD_ON_STARTUP/
WAIT_FOR_READY_ON_STARTUP/READY_TIMEOUT_SECONDS/DEVICE_MAP/DTYPE/
MAX_INFERENCE_BATCH_SIZE/MAX_NEW_TOKENS/WARMUP_ON_LOAD/WARMUP_AUDIO_PATH/WARMUP_LANGUAGE),
`GA_TTS_*` (…/VOICE/LANG_CODE/SAMPLE_RATE/…), `GA_EMB_*` (…/DEVICE/FIX_MISTRAL_REGEX/
WARMUP_ON_LOAD/…). The entrypoint readiness monitors key off
`GA_{ASR,TTS,EMB}_AUTO_LOAD_ON_STARTUP` / `_WAIT_FOR_READY_ON_STARTUP` /
`_READY_TIMEOUT_SECONDS` — **retired under D11** (see §2a; removed from compose by Phase 4).
Each phase doc includes an env-var disposition table (kept / repurposed / retired).

### Runtime reconciliation policy (D11 — locked 2026-07-02)

**Rule:** if global routing uses the local provider for a service, that engine should be
**warm** (configured model/bundle loaded + warmup when enabled, if possible). If global
routing uses a remote provider (e.g. images via OpenRouter), the corresponding local engine
must be **idle** (unloaded — no GPU/VRAM). DocumentIntelligence and Media are **out of
scope** (separate hosts/containers).

| Concern | Policy |
|---------|--------|
| **Orchestrator** | `LocalAiStartupWarmupService` in the GuideAnts API (name unchanged) |
| **Routing source** | Default `ServiceModes` row per service (`IServiceModeResolver`); `ChatDefaults:DefaultModelId` for llama |
| **Warm** | `POST /admin/load` (+ active model/bundle resolution), poll `/ready` or SD `/health` |
| **Idle** | `POST /admin/unload`, poll until unloaded |
| **Llama when chat is remote** | Unload all loaded router aliases; **llama-server process stays up** |
| **Load order** | Unload aux reverse (SD → TTS → Emb → ASR) → llama → load aux forward (ASR → Emb → TTS → SD), warm only |
| **Triggers** | API startup; `ServiceModes` save; `ChatDefaults` save; watchdog after llama restart |
| **Notebook scope** | **Cannot** warm a local engine when global routing is remote for that service |
| **Container autoload** | `GA_*_AUTO_LOAD_ON_STARTUP` retired — API reconcile is the single writer |
| **Ship timing** | API reconcile fixes **before or in parallel with Phase 1** (existing Python services) |

Load/warmup failures log and continue; they do not fall back to a remote provider or leave a
stale loaded model. Routing resolution failure → idle (do not warm).

---

## 3. Target architecture

```text
nginx :80
 ├── /sandbox/      → ScriptExecutionAgent (.NET :8081)          [unchanged]
 ├── /llama-cpp/    → llama-server router (:8080)                [unchanged]
 ├── /media/        → media_service.py (:8087)                   [unchanged]
 ├── /asr/          → ga-audio-server ASR (native, :8082)        [Phase 2]
 ├── /tts/          → ga-audio-server TTS (native, :8084)        [Phase 3]
 ├── /emb/          → emb adapter → llama-server --embeddings    [Phase 1]
 │                    (native child on :18085)
 ├── /sd/txt2img|img2img → SD facade → sd-server (:18083)        [unchanged inference]
 └── /llama-admin/, /sd/admin/*, /asr/admin/*, /tts/admin/*,
     /emb/admin/*   → ONE consolidated control-plane service      [Phase 4]
                      (model downloads, router INI CRUD, engine
                       lifecycle, bundle store)
```

Design rules carried through every phase:

- **Data plane native, control plane consolidated.** Inference engines are persistent
  native processes (llama-server instances, `ga-audio-server`, `sd-server`). Admin/
  lifecycle/download logic converges into one service (Python/FastAPI initially — lower
  risk; a later rewrite is possible but out of scope).
- **Route compatibility over internal freedom.** nginx keeps the same public prefixes;
  where admin and inference share a prefix (e.g. `/asr/admin/*` vs `/asr/transcribe`),
  nginx `location` blocks split them (longest-prefix match: `location /asr/admin/` →
  control plane, `location /asr/` → engine).
- **No runtime fallbacks.** Rollback is "redeploy the previous image tag", never
  "silently fall back to the torch path inside the same image". Per repo policy, hidden
  fallback logic is a defect.
- **Per-request CLI invocation is not viable** (`audiocpp_cli` has no daemon mode; every
  invocation pays the full model load). All native engines are persistent processes.
- **Routing-aware reconciliation (D11).** Engines load only when global routing selects the
  local provider; otherwise they are explicitly unloaded. One API orchestrator; no container
  autoload.

---

## 4. Phase ordering, rationale, dependencies

```text
Phase 0  Routing-aware reconcile (D11)            API `LocalAiStartupWarmupService` fixes
         [idle remote services, retire              against existing Python services;
          container autoload competition]           ship before or in parallel with Phase 1
              │
              ▼
Phase 1  Embeddings via llama-server           (no new native code; llama-server
         [emb_service.py → thin facade          binary already in the image;
          spawning llama-server --embeddings]   proves the "facade over native
                                                engine" pattern sd_service uses)
              │
              ▼
Phase 2  ASR via audio.cpp adapter             (first audio.cpp integration;
         [new ga-audio-server binary,           qwen3_asr is "released" upstream
          per-flavor builds incl. the           and uses the SAME model artifacts
          ROCm decision]                        GuideAnts already downloads)
              │
              ▼
Phase 3  TTS decision + migration              (depends on Phase 2's adapter +
         [kokoro upstream work OR               build infrastructure; carries the
          released-model migration]             open model-choice decision)
              │
              ▼
Phase 4  Control-plane consolidation           (last: touches every admin surface;
         [one admin service; retire             consolidating BEFORE the engines
          per-service FastAPI processes;        stabilize would mean rewriting the
          drop torch from /opt/venv]            consolidation twice)
```

**Why this order:**

- Phase 1 first because it requires **zero new native code** — the image already ships
  `llama-server`, and `sd_service.py` already demonstrates the exact facade-spawns-native-
  child pattern. Fastest win, smallest blast radius, and it exercises the validation
  playbook (contract parity tests) the later phases reuse.
- Phase 2 before Phase 3 because ASR is the safest audio.cpp entry point: `qwen3_asr` is
  released and registered in the default registry
  (`d:\repos\audio.cpp\src\framework\runtime\registry.cpp:223`), and its documented model
  layout (`models/Qwen3-ASR-0.6B`, `docs/qwen3.md`) matches the HF safetensors snapshot
  GuideAnts already downloads to `/models-local/asr/Qwen3-ASR-0.6B`. TTS, by contrast,
  carries an unresolved model decision.
- Phase 3 after Phase 2 because it reuses the `ga-audio-server` adapter and per-flavor
  build stages, and because the TTS model choice (§6.1) may need product input that
  should not block ASR.
- Phase 4 last because consolidation is a contract-neutral refactor that is cheapest once
  the set of engines and their lifecycle needs is final. Torch is removed from
  `/opt/venv` only at the end of Phase 4 (or end of Phase 3 if Phase 4 is deferred),
  since ASR/TTS/emb all must be off torch first.

Phases 1 and 2 could technically run in parallel (disjoint services), but sequential is
recommended: Phase 1's parity-testing playbook and env/rollout conventions feed Phase 2.

---

## 5. Verification conventions (all phases)

- **Contract parity first.** Before swapping an implementation, capture golden
  request/response pairs against the current Python service (same inputs, recorded
  outputs); the replacement must satisfy the same JSON shapes, status codes, and header
  contracts. Each phase doc lists the exact endpoints.
- **Per-flavor build gate.** A phase touching Dockerfiles is not done until
  `docker/build/guideants-ai/Dockerfile.{cpu,cuda,rocm,vulkan}` all build and the
  container `HEALTHCHECK` (which curls `/asr/health`, `/tts/health`, `/emb/health`,
  etc. — see `Dockerfile.cuda:221-222`) passes on each.
- **.NET integration gate.** `LocalAiStartupWarmupService` reconcile path (D11: warm local,
  idle remote), settings-UI model management (`SettingsServiceLocalModelsEndpoints`), and
  runtime inference services. Admin HTTP contracts unchanged; D11 lists explicit .NET
  reconcile changes.
- **No silent behavior drift.** Where output cannot be bit-identical (ASR text, TTS
  audio, embedding vectors), the phase defines an explicit similarity/quality check
  instead of assuming equivalence.

### 5.1 Gate model

These conventions are enforced by three gate docs. The orchestrator runs the applicable
gates after each phase and records results in [`STATUS.md`](./STATUS.md); a downstream
phase never starts on a failed gate.

| Gate | Cadence | Enforces |
|------|---------|----------|
| [`contract-preservation-gate.md`](./contract-preservation-gate.md) | after **every** phase + final | Public nginx prefixes + the .NET client contracts in §2 are byte-shape-identical; golden request/response replay; kill-child recovery with **no silent fallback**. |
| [`flavor-build-gate.md`](./flavor-build-gate.md) | after any Dockerfile/entrypoint/start-script/compose change + final | All four flavors (cuda13/vulkan/rocm/cpu) build + boot + `HEALTHCHECK`; the backend actually used matches intent (no silent CPU degrade). |
| [`codeql-gate.md`](./codeql-gate.md) | **once, at the end** | Security scan of the migration's **changed** code only. It is **end-only** (after all phase code merges) and **changed-languages-only** (the CodeQL language matrix is derived from the diff — `cpp` for `ga-audio-server`, `python` for the emb facade + `ga-admin`, `csharp` only if a .NET change landed). Dockerfiles/nginx/shell are not CodeQL-analyzable and are covered by the other two gates. |
| [`torch-removal-gate.md`](./torch-removal-gate.md) | after each package-tier removal + **final** | The explicit torch-removal outcome. **Tier A**: reverse-dep tree of torch free of every *service* package (achievable by Phases 1–3, invisible to sandbox). **Tier B**: `pip show torch` fails in `/opt/venv` across all full flavors + no `download.pytorch.org` index left + image-size delta recorded — only when D7 authorizes dropping sandbox torch. No silent torch-fallback shim. |

Security scanning for this plan means the CodeQL gate above — run at the end, over changed
languages only. There is **no** per-phase security scan.

---

## 6. Open decisions requiring a human call

> These are locked in [`DECISIONS.md`](./DECISIONS.md) as `D1`–`D11` (D1 = §6.1, D2 = §6.2,
> D3 = §6.3, D4 = §6.4, D5 = §6.5, D6 = §6.7 control-plane, **D11 = §2a runtime reconcile —
> LOCKED 2026-07-02**). The subsections below are the rationale for D1–D10; `DECISIONS.md`
> is the contract and records confirmation status. **D1 (TTS family) is now LOCKED =
> `chatterbox`** (Option B, product call 2026-07-02).

### 6.1 TTS model choice (Phase 3) — DECIDED: `chatterbox`

Kokoro-82M is **not shippable from audio.cpp today**: the loader is commented out of the
default registry (`registry.cpp:11,208`, "Development registry entries from
Share/AudioCPP that are not present in this release tree yet") and `src/models/` contains
**no kokoro_tts sources at all** — enabling it is a port from a development tree, not an
uncomment. `docs/tts.md` documents kokoro with languages `a`/`b` only, vs the `a,b,e,f,h,
i,j,p,z` codes GuideAnts accepts today.

**Decision (D1, LOCKED): migrate to `chatterbox`** (`ResembleAI/chatterbox`) — released
**with full sources** in audio.cpp (`src/models/chatterbox/`), unlike Kokoro. It is
voice-clone only, so GuideAnts ships a **curated voice pack** of open reference clips
(CC0 Common Voice / GLOBE preferred; VCTK / LibriTTS-R with a NOTICE). This replaces the
user-visible voice presets (`ServiceEditorMetadataProvider.cs` `KokoroVoiceNames`) and
carries accepted deltas — no native `speed` (emulate via ffmpeg `atempo` or drop), non-
deterministic output (pin a seed), and **no `ja`/`zh`** (reject those `lang_code`s loudly).
Full analysis + design in `phase-3-tts-decision.md` §5–5.1.

Related observation: `docker/docker-compose.ghcr-vulkan.yml:109-112` already sets
`GA_TTS_DEFAULT_MODEL_PATH=VibeVoice-1.5B` / `GA_TTS_DEFAULT_MODEL_ID=microsoft/VibeVoice-1.5B`
even though `tts_service.py` only implements Kokoro — stale or aspirational config, but
evidence of prior intent to move off Kokoro. `vibevoice` **is** a released loader in
audio.cpp's registry.

### 6.2 cuda-multi embeddings parity (Phase 1)

`emb_service.py` supports `GA_EMB_DEVICE=cuda-multi` (multi-GPU
`encode_multi_process`). llama-server has no direct equivalent; options (accept
single-instance throughput, run N instances behind the facade, or use llama.cpp layer
split) are laid out in `phase-1-embeddings-llama-server.md` §Risks. Recommended default:
accept single-instance and measure before adding complexity — `LocalEmbeddingService`
already serializes and meters requests (`_requestGate` + `LocalMinIntervalMs`), so
current .NET-side concurrency is 1 anyway.

### 6.3 Re-embedding the existing corpus (Phase 1)

Vectors produced by the new default `Qwen3-Embedding-0.6B` (GGUF under llama.cpp) live in a
**different embedding space** than the stored sentence-transformers Harrier vectors — cosine
across two models' spaces is meaningless, and mixed-provenance vectors in the same index
degrade retrieval unpredictably. Because the Harrier → Qwen3 switch is a model change, the
corpus **must be re-embedded at cutover** (this is the real cost of the default swap;
retrieval parity is validated in Phase 1 before trusting scores).

### 6.4 ROCm route for audio.cpp (Phase 2)

audio.cpp has **no HIP/ROCm path today**: `CMakeLists.txt` exposes only
`ENGINE_ENABLE_CUDA/VULKAN/METAL` (lines 41–43), `scripts/build_linux.sh` accepts only
`--backend cuda|vulkan|cpu`, and `src/framework/core/backend.cpp` has no HIP branch. The
vendored ggml **does** support `GGML_HIP` (`external/ggml/CMakeLists.txt:215`). Two
routes: (a) patch/upstream an `ENGINE_ENABLE_HIP` that builds ggml-cuda as HIP (the
`BackendType::Cuda` path then drives the hipified backend — symbol compatibility to be
validated in Phase 2), or (b) ship the **Vulkan** build of `ga-audio-server` inside the
rocm image flavor (AMD GPUs run Vulkan via RADV; llama-server keeps its ROCm build from
`ghcr.io/ggml-org/llama.cpp:server-rocm`). Recommendation: (b) initially, (a) as
follow-up. Needs sign-off because (b) means the rocm flavor's ASR/TTS use a different GPU
API than its LLM.

### 6.5 Adapter vs fork of `audiocpp_server` (Phase 2 — recommendation made, confirm)

Prior analysis (re-verified): `app/server` (~1060 LOC across `main.cpp`, `http.cpp`,
`runtime.cpp`, `config.cpp`) hardcodes `BackendType::Cuda` (`runtime.cpp:396` — shallow,
not architectural), takes transcription input as a **server-local file path in JSON**
(no multipart upload), and lacks admin load/unload/download, warmup-gated `/ready`, and
streaming. Recommendation: build a thin custom HTTP adapter (`ga-audio-server`) linking
the `engine_runtime` static library, reusing `app/server`'s runtime/session-cache
patterns as reference. Forking/patching `audiocpp_server` is a viable alternative
evaluated in `phase-2-asr-audiocpp.md` §Alternatives.

### 6.6 Sandbox torch — the shared-venv decision (D7, requires a human call)

The original note here assumed sandbox torch lived in a separate venv and could simply be
left alone. It does **not** (§1): the sandbox executes user code in the same `/opt/venv`
the services use, and the sandbox requirement set declares `torch`/`torchaudio`/`torchtext`
(`Sandboxes/python311TorchCUDA/requirements.txt:1-2,181`). So the choice is explicit and
must be made by a human, locked as **D7**:

- **D7 = keep sandbox torch (recommended default, current scope):** torch stays resident in
  `/opt/venv`. Phases 1–3 still remove every torch-*dependent service package*, but
  `pip show torch` keeps succeeding and the **multi-GB image-size win is NOT realized**.
  The migration's honest claim becomes "removed the torch-based AI *services*," not
  "removed PyTorch from the image."
- **D7 = drop sandbox torch:** strip `torch*` from the sandbox requirements and the
  Dockerfile torch install; `pip show torch` fails; the big image win lands — **but user
  sandbox scripts that `import torch` break.** This is a user-visible sandbox behavior
  change requiring product sign-off, not an implementation detail.

Do not claim an image-size win from torch removal until D7 is resolved in favor of dropping
sandbox torch. See [`torch-removal-gate.md`](./torch-removal-gate.md) Tier A vs Tier B.

### 6.7 Control-plane technology + topology (Phase 4 — decided, D6)

For Phase 4, the consolidated control plane is **one Python/FastAPI `ga-admin` process
behind the existing nginx ingress** — the lowest-risk path, since it absorbs three
existing Python codebases (`llama_admin_service.py`, `sd_service.py`'s admin half, the new
engine admin skeletons). A **Kestrel-YARP ingress** and a **.NET control-plane rewrite**
are attractive longer-term (single runtime story with the rest of GuideAnts) but are
**optional, post-Phase-4** items — explicitly out of scope here so consolidation isn't
gated on a bigger rewrite.

**Security invariant (non-negotiable):** the AI control plane (`ga-admin`) **MUST NOT** be
merged into the `ScriptExecutionAgent` process. The sandbox runs untrusted user code; the
control plane holds model-download, router-INI-restart, and engine-lifecycle authority.
Co-locating them would hand sandboxed code a path to that authority. They stay separate
processes regardless of any future ingress/runtime change. Locked as D6 in
[`DECISIONS.md`](./DECISIONS.md).

### 6.8 Embeddings/ASR/TTS model catalog + download source (D9)

Today all three local services let a user download **any** Hugging Face repo (free-form;
the server only checks `model_id` is present — `SettingsServiceLocalModelsEndpoints.cs:61-97`;
ASR `asr_service.py:317-324`; **embeddings** `emb_service.py:227-237,771-776`), and TTS is
client-pinned to `hexgrad/Kokoro-82M`. None is constrained to what the target engine can
actually load. **D9** ships a curated **catalog manifest** and offers only known-good
models per picker:

- **ASR/TTS** entries derive from audio.cpp (`tools/model_manager.py` `CATALOG` ∩
  `registry.cpp` registered loaders ∩ README `released`); downloads are **safetensors/ggml
  only — GGUF rejected** (`d:\repos\audio.cpp\README.md:653`). ASR → `qwen3_asr_0_6b`; TTS →
  `chatterbox` (D1).
- **Embeddings** (added by the 2026-07-02 scope expansion) entries are **single-file GGUF**
  for `llama-server --embeddings --pooling last` with `producedDimension <= 1536` — the
  **inverse** format rule (safetensors-only repos rejected as the old torch path) plus a
  **≤ 1536 dimension ceiling**. The consumer-invisible width is **1536** (SQL `vector(1536)`,
  normalized at `ProviderRoutedEmbeddingService.cs:69`), **not** 1024; `NormalizeToTarget`
  **silently truncates** anything > 1536, so `> 1536` models are rejected loudly. **Curated set
  LOCKED (RESOLVED 2026-07-02): default `Qwen3-Embedding-0.6B`** (source
  `Qwen/Qwen3-Embedding-0.6B-GGUF`, official GGUF, Apache-2.0, Qwen3 arch, native 1024,
  multilingual) **replaces Harrier as the shipped default**; alternatives `EmbeddingGemma-300M`
  (footprint-first, official GGUF, native 768) and `bge-m3` (MIT, no-prefix multilingual, 1024,
  XLM-R, pinned trusted community GGUF). The incumbent Harrier is historical context only. The
  default cutover reuses the `--pooling last` path (same Qwen3 arch → lowest-risk swap) but
  needs the facade query prefix to move to the Qwen **`Instruct:`** form and a **one-time corpus
  re-embed (D3)** (the real cost); `SourceVectorDimensions` stays 1024. **License is NOT a
  curation filter** — the user downloads from source, so license compliance is the user's
  responsibility; we surface license info for transparency only (contrast the **voice pack /
  D10**, where GuideAnts redistributes assets and owns compliance). **Multilingual is treated
  as required/default-safe** (English-only models like `nomic-embed-text-v1.5` may be listed but
  tagged and never default). We do **not** convert-and-ship a GuideAnts artifact — the catalog
  points at the source (prefer official GGUF; else a pinned trusted community GGUF).

Downloads proceed **only** from allowlisted sources; non-allowlisted repos, wrong-format
(GGUF for ASR/TTS; non-GGUF for emb), `> 1536` dimension (emb), and gated repos without a
token are **rejected loudly**, honouring the no-arbitrary-fallback rule. Verification is a
**build/ship-time inclusion gate**: a GGUF is published into the manifest only after a
**load-after-download** check confirms it loads and its actual dim equals the declared
`producedDimension` ≤ 1536 (each entry still validated in Phase 1) — no runtime "unverified"
state.
Enforcement is authoritative in `ga-admin` (Phase 4) and the P1/P2/P3 engine skeletons
before that. The Chatterbox **voice pack** is separate — local bundled assets (D1), not an
HF download. **llama chat router presets and SD bundles remain out of scope.** Full design
in [`model-catalog-and-downloads.md`](./model-catalog-and-downloads.md); locked as D9 in
[`DECISIONS.md`](./DECISIONS.md).

### 6.9 Voice-pack sourcing + attribution compliance (D10)

Chatterbox (D1) is **voice-clone only — no built-in voices** (`d:\repos\audio.cpp\docs\tts.md:35`);
every synthesis needs a reference WAV via `--voice-ref`, and several other released TTS
families (`pocket_tts`, `miotts`, `qwen3_tts`, `voxcpm2`, `omnivoice`, `vevo2`) are likewise
reference-driven. **D10** ships a curated, **model-agnostic reference-voice pack** — short,
openly-licensed clips baked into the `guideants-ai` image and exposed as the selectable
`VoiceName` set that replaces `KokoroVoiceNames`
(`ServiceEditorMetadataProvider.cs:14-70`). Sourcing is **CC0-first** (Mozilla Common Voice /
GLOBE — no attribution burden, safest persona footing); **CC-BY-4.0** (VCTK / LibriTTS-R,
English-only) is used only where CC0 quality is insufficient and then **attribution is
mandatory**. A `manifest.json` is the source of truth; a **NOTICE artifact is baked into the
image** and a **build-time completeness check fails loudly** if any non-CC0 clip lacks
attribution (no silent gap). The pack is **local image-layer assets, not an HF download** —
distinct from the D9 model catalog. `KokoroVoiceNames` is **replaced outright** by the pack
ids — **no legacy `VoiceName` migration and no backwards compatibility** (product decision); an
unknown/legacy id is rejected loudly so the user reselects (no silent remap, no fallback). Full
design in
[`voice-pack-and-attribution.md`](./voice-pack-and-attribution.md); locked as D10 in
[`DECISIONS.md`](./DECISIONS.md). Phase 3 dependency.

---

## 7. Expected wins (qualitative; no invented numbers)

- **GPU ASR/TTS on vulkan (and rocm via §6.4) flavors** where today torch is CPU-only.
- **Image size**: `transformers` + `accelerate` + `sentence-transformers` + `kokoro`/
  `misaki` (+ `curated-transformers`/`spacy-curated-transformers`) leave `/opt/venv` on all
  full flavors once ASR/TTS/emb are migrated (Tier A — no invented numbers; measured at the
  torch-removal gate). The **large** win — `torch`/`torchaudio`/`torchvision` themselves
  (multi-GB CUDA wheels) — lands **only** under Tier B, i.e. only if D7 drops sandbox torch;
  otherwise torch stays resident for user scripts and the headline size win is not realized.
  See §7.1.

### 7.1 Torch-removal win, stated honestly

The torch-removal outcome is **two-tiered** (mirrored in
[`torch-removal-gate.md`](./torch-removal-gate.md)):

- **Tier A (Phases 1–3):** removes every package that *depends on* torch and is used only
  by the AI services. Invisible to sandbox users. Torch itself may still be present.
- **Tier B (needs D7 + D8):** removes `torch`/`torchaudio`/`torchvision`. Requires TTS off
  torch (D8 — `kokoro` is the last direct torch importer) **and** a decision to stop
  offering torch to sandbox scripts (D7, single shared venv). Until both hold,
  `pip show torch` still succeeds and no multi-GB size win exists.

Any one of ASR/TTS/emb remaining on torch keeps the full wheel resident — torch removal is
strictly an **all-of-Phases-1-3** outcome, not incremental.
- **Process count**: 9 supervised processes → 7 (nginx, llama-server(+emb instance),
  ga-audio-server ×2 or ×1, sd-server, ScriptExecutionAgent, media, control plane),
  with **one** admin codebase instead of four FastAPI variants of the same
  download/load/unload pattern.
- **Operational consistency**: every AI engine becomes a ggml-family native process with
  the same VRAM observability story.
