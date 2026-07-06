# Native AI Migration — Execution Status Ledger

Last updated: 2026-07-03 — **Phases 0–4 implemented in tree.** D2–D5 confirmed at dispatch.
CodeQL deferred to final acceptance per plan (`scripts/native-ai-migration/run-final-codeql.ps1`).
CUDA13 flavor built, booted, and functionally verified end-to-end (ASR/TTS/Embeddings round-trip;
see "CUDA13 runtime verification" below). CPU flavor rebuilt and runtime-verified 2026-07-03 after
the upstream `audiocpp_server` hardcoded-CUDA-backend fix landed (see "`audiocpp_server`
hardcoded-CUDA backend" below) — ASR/TTS round-trip confirmed on the `cpu` backend. Vulkan and rocm
flavors also rebuilt and boot-verified 2026-07-03; backend selection proven correct for both, but
actual GPU inference is blocked on this Windows/WSL2/NVIDIA dev host by a Mesa `dzn` Vulkan driver
incompatibility (`ErrorIncompatibleDriver`), not by anything in this codebase — needs a native-Linux
GPU host to fully clear (see details below).

---

## Phase ledger

| Phase | Brief | State | Attempts | Gate result | Notes |
|---|---|---|---|---|---|
| 0 — D11 API reconcile | `LocalAiStartupWarmupService.cs` idle unload + llama alias unload; `SettingsServiceEditorEndpoints` reconcile on routing save | **DONE** | 1 | pending operator | Routing failure → idle (no warm fallback) |
| 1 — Embeddings via llama-server | `emb_service.py` llama-server facade; catalog; `EmbRuntimeManager` dropdown | **DONE** | 1 | pending operator | Default `qwen3_embedding_0_6b`; inclusion gate script at `scripts/native-ai-migration/verify-emb-catalog-inclusion.py` |
| 2 — ASR via audiocpp_server facade | Python `asr_service.py` + cloned `audiocpp_server` | **DONE** | 1 | pending operator | SD/emb pattern; no C++ in GuideAnts repo |
| 3 — TTS via Chatterbox | `GA_AUDIO_TASK=tts`; voice-pack; `ServiceEditorMetadataProvider` | **DONE** | 1 | pending operator | Kokoro retired from runtime; Tier A torch service pkgs removed from Docker pip; voice pack re-curated 2026-07-02 with real LibriVox/Internet-Archive public-domain clips (Common Voice moved behind Mozilla Data Collective email-gate, no longer anonymously fetchable) — see `voice-pack/NOTICE.md` |
| 4 — `ga-admin` consolidation | nginx admin splits; `start-ga-admin.sh`; engine admin proxy | **DONE** | 1 | pending operator | llama-admin + SD facade absorbed; ~7 processes target |

---

## Baseline / gates (operator)

| Check | Result | Date |
|---|---|---|
| Contract goldens | scripts at `scripts/native-ai-migration/capture-contract-goldens.ps1` | 2026-07-02 |
| CUDA13 build + HEALTHCHECK | **PASS** — `guideants-ai:cuda13-26183.2347` built clean, container `guideants-ai` reports `(healthy)` on boot | 2026-07-02 |
| cpu/vulkan/rocm build + HEALTHCHECK | pending operator | — |
| Torch Tier A (`pipdeptree -r -p torch`) | superseded by Tier B (torch itself gone; A is moot) | 2026-07-02 |
| Torch Tier B (D7 flipped 2026-07-02 — remove torch everywhere incl. sandbox) | **PASS, runtime-verified.** `/opt/venv/bin/pip show torch torchaudio torchvision` → not found; `python3 -c "import torch"` → `ModuleNotFoundError` inside the running `cuda13` container. Image size: `21.2GB → 13.5GB` (unique layer size `6.95GB → 4.18GB`), confirming the Tier B win is real, not just doc claims. Accepted consequence: ScriptExecutionAgent sandbox scripts that `import torch` now fail loudly (no shim) | 2026-07-02 |
| CUDA13 runtime verification (ASR/TTS/Embeddings) | **PASS, functional round-trip.** See below | 2026-07-02 |
| CodeQL final | **deferred** — `run-final-codeql.ps1` | — |
| D2–D5 confirmed | confirmed 2026-07-02 | 2026-07-02 |
| D7 flipped (Tier B in-scope) | confirmed 2026-07-02 (explicit operator call) | 2026-07-02 |

### CUDA13 runtime verification (2026-07-02, this session)

Rebuilt `guideants-ai:cuda13` with the voice-pack re-curation, Tier B torch removal, and one bug
fix found during this pass (see below). Container recreated via
`docker compose -f docker-compose.cuda.yml up -d --force-recreate guideants-ai`, confirmed
`(healthy)`, then exercised directly against each engine's admin/inference HTTP API inside the
container (not just health polls):

- **ASR** (`asr_service.py` → `audiocpp_server`, Qwen3-ASR-0.6B, CUDA): `/admin/load` → loaded,
  warmup succeeded (14.8s). Live transcription of a real TTS-synthesized WAV returned
  `"The native AI migration is now verified, end to end."` for input text
  `"The native AI migration is now verified end to end."` (near-exact match).
- **TTS** (`tts_service.py` → `audiocpp_server`, Chatterbox, voice `en_us_cv_001` from the
  re-curated LibriVox voice pack, CUDA): `/admin/load` → loaded, warmup succeeded (20.0s).
  `/synthesize` returned a real 24kHz WAV (RIFF/WAVE header verified, 238KB for one sentence).
- **Embeddings** (`emb_service.py` → llama-server facade, Qwen3-Embedding-0.6B-Q8_0.gguf):
  `/admin/load` → loaded, warmup succeeded (69ms). `/embed` returned a real 1024-dim float vector.

**Bug found and fixed in this pass:** the CUDA-built `audiocpp_server` binary links against
`libnccl.so.2`. Before Tier B, that library was incidentally supplied by torch's bundled
`nvidia-nccl-cu12` pip wheel in `/opt/venv` (referenced by a stale `LD_LIBRARY_PATH` entry). Once
torch was removed, `audiocpp_server` failed to start (`exit code 127`, "libnccl.so.2: cannot open
shared object file") and the ASR engine crash-looped every ~2s trying to reload. Fixed by
installing `libnccl2` from the NVIDIA CUDA apt repo (already configured in the base image) directly
in `Dockerfile.cuda`'s `deps-cuda13` stage, and dropping the now-dead venv-relative
`LD_LIBRARY_PATH` entry. This decouples the C++ engine's runtime dependency from Python packaging
entirely — the correct fix, not a re-introduced torch-shaped shim. See `Dockerfile.cuda`.

---

## Open follow-ups

- Run the cpu/vulkan/rocm Docker build gates and record outputs in this file (cuda13 is done, see above).
- Execute contract golden replay (`scripts/native-ai-migration/capture-contract-goldens.ps1`).
- Run final CodeQL (`scripts/native-ai-migration/run-final-codeql.ps1`) after operator gates pass.
- Verify the same ASR/TTS/Embeddings flows through the actual GuideAnts UI (Playwright), not just
  direct container HTTP calls.

### `audiocpp_server` hardcoded-CUDA backend — upstream fix landed 2026-07-03

Found while re-verifying D5 for this pass: the vendored `audiocpp_server` (`d:\repos\audio.cpp`)
unconditionally set `session_options.backend.type = BackendType::Cuda` in `runtime.cpp`, regardless
of `--config`/build flags. This would have made **every non-CUDA flavor's ASR/TTS load fail or
silently attempt CUDA on a binary built without it** — a hard blocker for the cpu/vulkan/rocm
build gates above, independent of anything else in this ledger. Per explicit operator instruction,
`audio.cpp` was **not** patched locally; the bug was filed upstream instead. The maintainers fixed
it directly (`0xShug0/audio.cpp` commit `dee799e`, "Allow server backend selection") and the repo
was re-pulled. `audiocpp_server` now reads a top-level `"backend": "cpu"|"cuda"|"vulkan"|"metal"`
JSON field (default `cuda`, so the CUDA flavor's existing behavior is unaffected).

Wired the GuideAnts side to actually use it (previously nothing set this field, so every flavor
silently rode the `cuda` default regardless of what it was built with):
- `asr_service.py` / `tts_service.py` `build_server_config_json()` now emit
  `"backend": os.getenv("GA_ASR_BACKEND"/"GA_TTS_BACKEND", "cuda")`.
- Every `docker-compose.*.yml` (under both `docker/` and `installer/docker/`) now sets
  `GA_ASR_BACKEND`/`GA_TTS_BACKEND` to match that flavor's `audiocpp_server`
  `ENGINE_ENABLE_*` build flags: `cpu` flavor → `cpu`, `vulkan`/`rocm` flavors → `vulkan`
  (rocm builds the Vulkan/RADV path per D4), `cuda` flavor → `cuda`.
- `start-asr.sh` / `start-tts.sh` export a `cuda` default for parity with the binary's own default.
- D4/D5 in `DECISIONS.md` updated to reflect this: D5 flipped (no custom `ga-audio-server` adapter
  was ever built; stock `audiocpp_server` is now correct for all flavors), D4 marked implemented.

**CPU flavor — rebuilt and runtime-verified 2026-07-03.** `guideants-ai:cpu-latest` built clean
from `Dockerfile.cpu` (`build_guideants_ai.ps1 -Backend cpu`, ~8 min). Verified in an isolated
standalone container (`guideants-ai-cpu-test`, distinct name/port, reusing the existing
`guideants_ai_local_models` volume read-write so no re-download was needed; the live `guideants-ai`
(cuda13) container was never stopped or touched):
- `POST /asr/admin/load` → `"status":"loaded"`, warmup succeeded (7.5s load + 4.0s warmup).
- `POST /tts/admin/load` → `"status":"loaded"`, `"device":"cpu"`, warmup succeeded (22.2s load
  + 21.7s warmup).
- Full round-trip: `POST /tts/synthesize` on the CPU backend produced a real 265KB WAV; feeding
  that WAV to `POST /asr/transcribe` (also CPU backend) returned
  `"The CPU backend fix is now verified end to end."` — an exact match of the input text.
- This is the flavor that was **guaranteed broken** before the upstream fix (built with
  `ENGINE_ENABLE_CUDA=OFF`, so the old hardcoded-`Cuda` `audiocpp_server` could not have worked at
  all). Confirms the fix + wiring is correct, not just theoretically plausible.
- Test container removed after verification (`docker rm -f guideants-ai-cpu-test`); no compose
  files or persistent state were modified by the test itself (compose-level `GA_ASR_BACKEND=cpu`/
  `GA_TTS_BACKEND=cpu` for this flavor were added as source changes, not by this test run).
**Vulkan flavor — rebuilt 2026-07-03; backend wiring confirmed correct, GPU inference blocked by
this host's Vulkan driver stack (environment limitation, not a code bug).** `guideants-ai:vulkan-latest`
built clean from `Dockerfile.vulkan` (~9 min). Standalone container test
(`guideants-ai-vulkan-test`, `--device /dev/dxg --group-add video --group-add render` per
`docker-compose.vulkan.yml`'s own Windows wiring):
- `audiocpp_server` correctly read `"backend": "vulkan"` from the config the facade wrote (proven
  by its own startup log: `"audio.cpp is optimized for CUDA. The vulkan server backend is
  intended..."` — the non-CUDA warning added by the upstream fix, which only prints for a
  non-default backend, confirming the JSON field was picked up correctly).
- Model load then failed: `ggml_vulkan: No devices found.` → `vulkaninfo` shows the loader finding
  the `dzn_icd.json` (Mesa D3D12-translation ICD, the one this Windows/WSL2 host's `/dev/dxg` maps
  to) but `vkCreateInstance` on it returns `-9` (`VK_ERROR_INCOMPATIBLE_DRIVER`) and the loader
  skips it, leaving zero usable devices.
- **This is a Windows/WSL2 Mesa-`dzn`-driver compatibility issue on this specific dev host, not a
  regression from the D5 fix or its wiring** — the backend-selection code path is proven correct
  (it asked for Vulkan and audio.cpp/ggml correctly tried and failed to get a Vulkan device,
  exactly as it should). Real verification of Vulkan GPU inference needs either a different
  WSL2/DirectX driver version on this host, or (per D4/`docker-compose.vulkan.yml`'s own comments)
  a native-Linux host with RADV/ANV, which this environment is not.
- Test container removed after this finding (`docker rm -f guideants-ai-vulkan-test`).

**ROCm flavor — rebuilt and boot-verified 2026-07-03; same result as vulkan (expected per D4).**
`guideants-ai:rocm-latest` built clean from `Dockerfile.rocm` (~14 min, mostly the ROCm/HIP
`llama-server` build — unrelated to this fix). Standalone container booted cleanly (all services
up, no crash-loop). Direct `audiocpp_server` invocation with `GA_ASR_BACKEND=vulkan` (this flavor's
D4-mandated backend) printed the same non-CUDA portability warning, confirming correct backend
selection, then failed with `vk::createInstance: ErrorIncompatibleDriver` — the same
Windows/WSL2 `dzn` driver-stack limitation as the vulkan flavor, not a regression. Test container
removed after this finding (`docker rm -f guideants-ai-rocm-test`).

- **Still open:** a from-scratch native-Linux host with RADV/ANV (vulkan flavor) or a real AMD GPU
  (rocm flavor) to confirm actual GPU inference end-to-end, not just correct backend selection —
  this dev host is Windows/WSL2 with an NVIDIA GPU and cannot exercise that path. cuda13 unaffected
  (default `backend` is `cuda`) but hasn't been rebuilt to pick up the code diff; low priority since
  behavior is provably unchanged for that flavor (already verified pre-fix and the field defaults
  to the pre-fix behavior).

### ROCm image size fix (2026-07-03): 38.2GB → 19.1GB

`guideants-ai:rocm-latest` was a huge outlier (38.2GB vs. 8-14GB for every other flavor). Root cause:
`Dockerfile.rocm`'s `runtime-rocm-base` was `FROM ghcr.io/ggml-org/llama.cpp:server-rocm`, and that
upstream image's own build installs the `rocm-dev`+`rocm-libs` **meta-packages** — pulling in the
entire ROCm SDK (HIP/ROCm LLVM compiler toolchain, MIOpen, rocFFT, rocRAND, RCCL, rocSPARSE,
rocALUTION, hipTensor, hipSPARSELt, ...) in one ~22GB layer, unconditionally inherited just by using
that image as a base.

Verified via `ldd` against the actual binaries this image ships (`sd-cli`, `sd-server`,
`audiocpp_server`, `libggml-hip.so`) that **none of them link against any of that** — llama.cpp,
sd.cpp, and audio.cpp (Vulkan build per D4) only need: `hip-runtime-amd`, `rocblas`, `rocsolver`,
`hipblas`, `hipblaslt` (bundles rocRoller), `rocprofiler-register`, `roctracer`, `comgr`, `hsa-rocr`.
(`audiocpp_server` needs zero HIP/ROCm libs at all in this flavor — it's Vulkan-only per D4; its
`libvulkan.so.1` runtime dep is already satisfied by Playwright's `--with-deps chromium` install in
`deps-rocm`, unrelated to ROCm packaging.)

Fix: split `runtime-rocm-base` into (1) a reference-only `llama-rocm-upstream` stage that still
pulls the official image (just to `COPY --from=` the `llama-server` binary + sibling
`libggml*.so`/`libllama*.so` — proven to resolve via a `$ORIGIN`-relative rpath, so copying the whole
`/app` dir preserves resolution), and (2) a fresh `ubuntu:24.04`-based `runtime-rocm-base` that adds
the AMD ROCm/amdgpu apt repos (same repo/version the upstream image itself uses, `ROCM_VERSION=7.2.1`)
and installs only the 9 packages above — no `rocm-dev`, no `rocm-libs`.

Result: `guideants-ai:rocm-latest` **38.2GB → 19.1GB** (~50% smaller), now roughly in line with
cuda13 (14GB). Verified clean (`ldd ... | grep 'not found'` empty) on all 5 affected binaries
(`sd-cli`, `sd-server`, `audiocpp_server`, `llama-server`, `libggml-hip.so`) — no missing libraries.
`llama-server --help` now gets as far as `ggml_cuda_init: failed to initialize ROCm: no
ROCm-capable device is detected`, i.e. it successfully loads/dlopens `libggml-hip.so` and its full
HIP/rocBLAS/HSA dependency chain and only fails on the expected "no AMD GPU on this host" check —
proving the trimmed package set is sufficient, not just smaller. Full container boot test wasn't
pursued further per explicit user direction (this dev host has no ROCm-capable device, so
end-to-end GPU inference can't be exercised here regardless — same open item as the vulkan flavor
above).
