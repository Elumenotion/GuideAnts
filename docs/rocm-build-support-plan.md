# GuideAnts ROCm Build Support Plan

## 1. Purpose

Add a first-class AMD/ROCm backend to GuideAnts local AI builds and runtime orchestration, equivalent in quality to existing `cpu` and `cuda13` lanes, while preserving current CUDA/CPU behavior.

This plan is implementation-focused and tied to current repo internals.

## 2. Scope

### In scope

- New backend lane: `rocm`
- Local and GHCR compose profiles for ROCm
- Build/publish/tag pipelines for ROCm images
- Runtime script and service changes needed to remove CUDA-only assumptions
- Test contract updates
- Operational docs updates

### Out of scope (v1)

- Windows/macOS ROCm parity
- Re-architecting service boundaries
- Replacing CUDA lane behavior
- Deep performance tuning beyond baseline parity

## 3. Current-State Gaps (from code audit)

### 3.1 Dockerfile/backend staging is CUDA-hardcoded

- `docker/build/guideants-ai/Dockerfile.cpu`
- `docker/build/guideants-ai/Dockerfile.cuda`
- `docker/build/guideants-ai/Dockerfile.rocm`
  - CUDA SD build stage uses `nvidia/cuda:13.0.0-devel-ubuntu24.04`
  - CUDA compile flags: `-DSD_CUDA=ON -DGGML_CUDA=ON`
  - Runtime base: `ghcr.io/ggml-org/llama.cpp:server-cuda13`
  - Torch index: `https://download.pytorch.org/whl/cu130`
  - Targets end at `final-cpu` and `final-cuda13` only

### 3.2 Runtime internals contain CUDA-specific control paths

- `docker/build/guideants-ai/start-emb.sh`
  - Defaults to `GA_EMB_DEVICE=cuda` and `GA_EMB_TARGET_DEVICES=cuda:0,cuda:1`
- `docker/build/guideants-ai/emb-service/emb_service.py`
  - Device parser only supports `cpu|cuda|mps|cuda-multi`
- `docker/build/guideants-ai/start-sd.sh`
  - Uses `GA_SD_CUDA_VISIBLE_DEVICES` -> `CUDA_VISIBLE_DEVICES`
- `docker/build/guideants-ai/entrypoint.sh`
  - Llama crash classifier matches CUDA-only OOM markers

### 3.3 Compose is NVIDIA-specific in GPU lanes

- `docker/docker-compose.cuda.yml`
- `docker/docker-compose.ghcr-cuda13.yml`
  - `driver: nvidia`
  - `capabilities: [gpu]`
  - CUDA-specific env defaults for embeddings and SD
  - CUDA docling image tag `docling-serve-cu130`

### 3.4 Build/publish orchestration is 2-lane only

- `docker/build/build_guideants_ai.ps1`: only `cpu` and `cuda13`
- `.github/workflows/publish-guideants-ai-images.yml`: only `cpu|cuda13`
- `docker/push-ghcr-guideants-ai.ps1`: only `cpu|cuda13`
- `docker/.env`: no `GA_AI_ROCM_IMAGE`
- No ROCm requirements sandbox in `docker/build/Sandboxes`

### 3.5 Contract/tests/docs assume 2 lanes

- `src/server/GuideAntsApi.Tests/Configuration/ComposeEnvironmentContractTests.cs`
  - hardcoded compose file list excludes ROCm files
- Docs and setup scripts repeatedly reference `cpu|cuda13`

## 4. Target Architecture (v1)

Add a third backend lane that mirrors current structure:

- Build targets:
  - `runtime-rocm-base`
  - `pydeps-rocm-builder`
  - `deps-rocm`
  - `final-rocm`
- Image tags:
  - Local: `guideants-ai:rocm-<yyddd.hhmm>`
  - Deps cache: `guideants-ai-deps:rocm-<hash12>`
  - GHCR: `ghcr.io/<owner>/guideants-ai-rocm:<tag>`
- Compose files:
  - `docker/docker-compose.rocm.yml`
  - `docker/docker-compose.ghcr-rocm.yml`
- Startup selection:
  - `--backend cpu|cuda13|rocm`

## 5. Execution Plan

## Phase 0: Preflight decisions (required before coding)

1. ROCm base strategy for llama runtime:
   - Use only upstream `ghcr.io/ggml-org/llama.cpp:server-rocm`
2. ROCm torch wheel lane:
   - Pin exact ROCm wheel index/version pair for Linux
3. SD ROCm strategy:
   - Enable HIP build path (likely HIPBLAS), define supported `AMDGPU_TARGETS`
4. Docling policy:
   - Temporary CPU in ROCm stacks, or custom ROCm docling image

Deliverable: short ADR note in this file before implementation starts.

### Phase 0 ADR (locked decisions from review)

1. Llama ROCm base image policy
   - Use only upstream `ghcr.io/ggml-org/llama.cpp:server-rocm`
   - Pinning policy must match current CUDA lane policy
2. Distros/OS support envelope
   - Same Linux distro envelope as current CUDA lane
3. GPU support posture
   - Best-effort support (no narrow SKU allowlist for v1)
4. `AMDGPU_TARGETS`
   - Implementation-owned selection is authorized
5. Backend selection behavior
   - Autodetect should include ROCm path
6. Fallback behavior
   - CPU fallback is required when ROCm is unavailable (all services, not only docling)
7. Env naming standard
   - CUDA-specific runtime env naming is considered a defect
   - Migration must move to backend-neutral names with backward-compatible aliases
8. CI/publish
   - ROCm must be first-class in GHCR publish workflow (same class as cpu/cuda13)
9. Acceptance bar for initial integration
   - Must build
10. Explicit non-scope item
   - Model volume naming cleanup is out of scope for ROCm work

### Phase 0 unresolved items and default implementation stance

1. Exact ROCm torch wheel index/version pin
   - Not provided by product decision yet
   - Phase 1 implementation pin selected:
     - `torch==2.10.0`
     - `torchaudio==2.10.0`
     - `torchvision==0.25.0`
     - index `https://download.pytorch.org/whl/rocm7.1`
2. ROCm compose security options details
   - Not provided yet
   - Default stance: start with minimal required ROCm device mapping, add additional security options only when needed for successful runtime

## Phase 1: Build system enablement

### Files

- `docker/build/guideants-ai/Dockerfile.rocm`
- `docker/build/build_guideants_ai.ps1`
- `docker/.env`
- `docker/build/Sandboxes/*` (new ROCm requirements source)

### Changes

1. Dockerfile
   - Add new args:
     - `GA_DEPS_ROCM_IMAGE=deps-rocm`
     - ROCm SD build args (ex: `AMDGPU_TARGETS`)
   - Add SD ROCm builder stage
   - Add runtime/deps/final ROCm stages
   - Add ROCm torch install index in ROCm pydeps stage
2. Build script
   - Add backend menu option `ROCm`
   - Map to new targets (`deps-rocm`, `final-rocm`)
   - Map to `GA_DEPS_ROCM_IMAGE`
   - Write `GA_AI_ROCM_IMAGE` into `docker/.env`
3. Requirements source
   - Introduce ROCm lane requirements source (mirroring CPU/CUDA staging behavior)

### Acceptance criteria

- `docker buildx build ... --target final-rocm` succeeds
- Build script can produce local `guideants-ai:rocm-*` tag
- `.env` gets `GA_AI_ROCM_IMAGE=<new-tag>`

## Phase 2: Runtime internals hardening (remove CUDA-only assumptions)

### Files

- `docker/build/guideants-ai/start-emb.sh`
- `docker/build/guideants-ai/emb-service/emb_service.py`
- `docker/build/guideants-ai/start-sd.sh`
- `docker/build/guideants-ai/entrypoint.sh`
- (as needed) `asr-service/asr_service.py`, `tts-service/tts_service.py`

### Changes

1. Embeddings device model
   - Introduce backend-neutral env contract (e.g., support `rocm`/`hip` aliases)
   - Keep backward compatibility for existing `cuda` values
2. SD GPU pinning env
   - Generalize from `GA_SD_CUDA_VISIBLE_DEVICES` to backend-neutral option (preserve old var as alias)
3. Crash classification
   - Add ROCm/HIP OOM markers while retaining existing CUDA markers
4. ASR/TTS validation
   - Verify no CUDA-only branch blocks ROCm runtime; patch dtype/device fallbacks if required

### Acceptance criteria

- Services boot under ROCm target with no forced CUDA env overrides
- Health endpoints pass for `/llama-cpp`, `/asr`, `/tts`, `/emb`, `/sd`
- Crash envelope still classifies OOM cases correctly

## Phase 3: Compose profiles and startup orchestration

### Files

- `docker/docker-compose.rocm.yml` (new)
- `docker/docker-compose.ghcr-rocm.yml` (new)
- `start_linux.sh`
- `start_windows.cmd`
- `start_macos.sh`

### Changes

1. Compose ROCm profiles
   - Add ROCm AI image variables (`GA_AI_ROCM_IMAGE` / GHCR equivalent)
   - Replace NVIDIA reservations with AMD device access model
   - Set ROCm-appropriate embedding and SD env defaults
   - Decide docling behavior in ROCm profile (CPU fallback or ROCm image)
2. Startup scripts
   - Extend accepted backend values to include `rocm`
   - Linux detection path should allow explicit `--backend rocm`
   - Keep existing CPU/CUDA behavior unchanged

### Acceptance criteria

- `docker compose -f docker/docker-compose.rocm.yml up -d` works
- `start_linux.sh --backend rocm --compose local` works
- Existing `cpu` and `cuda13` flows remain unchanged

## Phase 4: Publish and artifact flow

### Files

- `.github/workflows/publish-guideants-ai-images.yml`
- `docker/push-ghcr-guideants-ai.ps1`
- `docker/build-processes.md`
- `docker/guideants-ai-build.md`

### Changes

1. GitHub workflow
   - Add input variant `rocm`
   - Add `publish-rocm` job mirroring current CPU/CUDA pattern
   - Add rocm cache scope + metadata labels + tags
2. Push helper script
   - Extend variant validate set and tag regex patterns
   - Include ROCm package mapping
3. Docs
   - Document 3-lane matrix and release process

### Acceptance criteria

- Manual dispatch with `variant=rocm` publishes GHCR image
- Push script can discover/push latest ROCm local tag

## Phase 5: Tests/contracts/docs parity

### Files

- `src/server/GuideAntsApi.Tests/Configuration/ComposeEnvironmentContractTests.cs`
- `docs/setup-guide.md`
- `docs/local-ai-setup-guide.md`
- `README.md`

### Changes

1. Add ROCm compose files to contract tests
2. Update setup/usage docs to show `cpu|cuda13|rocm`
3. Add explicit Linux + AMD prerequisites and troubleshooting section

### Acceptance criteria

- Contract tests pass with ROCm compose files included
- Setup docs are internally consistent with implemented backend options

## 6. Rollout Strategy

1. Internal alpha
   - Enable ROCm builds only for maintainers
   - Collect boot + inference smoke data
2. Limited beta
   - Publish `guideants-ai-rocm` tags
   - Mark docling behavior explicitly if CPU fallback is used
3. General availability
   - Promote to default documented backend option on Linux AMD hosts

## 7. Validation Matrix

Minimum required checks before merge:

1. Build
   - Local build script for `cpu`, `cuda13`, `rocm`
2. Runtime
   - Compose up/down for local and GHCR profiles
3. Health
   - All service health endpoints green
4. Functional smoke
   - One inference each: chat, ASR, TTS, embeddings, image
5. Non-regression
   - Existing CUDA lane unchanged
6. Tests
   - `ComposeEnvironmentContractTests` pass with new files

## 8. Risks and Blockers

1. SD ROCm builder complexity
   - HIP/HIPBLAS build path may require iteration and target-specific tuning
2. Third-party image parity (docling)
   - ROCm equivalent may be unavailable; CPU fallback may be needed initially
3. Runtime env compatibility
   - Current env names are CUDA-centric; migration must preserve backward compatibility
4. Host/device variability
   - ROCm support varies by GPU generation/driver stack; stricter validation is required
5. Pipeline maintenance overhead
   - Third lane increases CI time and cache management complexity

## 9. Proposed Work Breakdown (PR-sized)

1. PR-1: Dockerfile + build script + `.env` (`final-rocm` buildable)
2. PR-2: Runtime scripts/service env model cleanup (emb/sd/entrypoint)
3. PR-3: ROCm compose files + launcher updates
4. PR-4: GHCR workflow + push tooling
5. PR-5: Tests + docs + setup polish

## 10. Reviewer Checklist

- [ ] ROCm lane builds locally from `build_guideants_ai.ps1`
- [ ] ROCm compose files are complete and consistent with CPU/CUDA stacks
- [ ] CUDA-only assumptions in runtime scripts/services are removed or aliased
- [ ] Publish pipeline supports `rocm` variant end-to-end
- [ ] Contract tests include ROCm compose files
- [ ] Docs reflect actual implemented behavior

---

## Appendix A: File Impact Matrix

- Build internals:
  - `docker/build/guideants-ai/Dockerfile.rocm`
  - `docker/build/build_guideants_ai.ps1`
  - `docker/.env`
  - `docker/build/Sandboxes/*` (ROCm requirements source)
- Runtime internals:
  - `docker/build/guideants-ai/start-emb.sh`
  - `docker/build/guideants-ai/emb-service/emb_service.py`
  - `docker/build/guideants-ai/start-sd.sh`
  - `docker/build/guideants-ai/entrypoint.sh`
  - `docker/build/guideants-ai/asr-service/asr_service.py` (verify/patch if needed)
  - `docker/build/guideants-ai/tts-service/tts_service.py` (verify/patch if needed)
- Compose/orchestration:
  - `docker/docker-compose.rocm.yml` (new)
  - `docker/docker-compose.ghcr-rocm.yml` (new)
  - `start_linux.sh`
  - `start_windows.cmd`
  - `start_macos.sh`
- Publish/process:
  - `.github/workflows/publish-guideants-ai-images.yml`
  - `docker/push-ghcr-guideants-ai.ps1`
  - `docker/build-processes.md`
  - `docker/guideants-ai-build.md`
- Tests/docs:
  - `src/server/GuideAntsApi.Tests/Configuration/ComposeEnvironmentContractTests.cs`
  - `docs/setup-guide.md`
  - `docs/local-ai-setup-guide.md`
  - `README.md`
