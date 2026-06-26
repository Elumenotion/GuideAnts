# Add a Vulkan local build variant

## Context

GuideAnts ships GPU-accelerated AI as Docker images selected by **backend**: today
`cpu`, `cuda13`, `rocm`, `slim`. Each backend is a thin set of changes layered on a
shared structure:

- A `Dockerfile.<backend>` under `docker/build/guideants-ai/` that (a) builds
  `stable-diffusion.cpp` with vendor-specific flags and (b) derives the runtime from a
  vendor-specific `ggml-org/llama.cpp` base image.
- A `python311Torch<BACKEND>/requirements.txt` sandbox folder that supplies the Python
  deps (the torch wheels themselves are pinned in the Dockerfile, not here).
- A `docker-compose.<backend>.yml` that mounts the right GPU devices and points at the
  built image via a `GA_AI_<BACKEND>_IMAGE` env var.
- A case in `build_guideants_ai.sh` mapping the backend to its target/Dockerfile/requirements.

We want to add a **`vulkan`** backend. Vulkan is a vendor-neutral GPU API: `llama.cpp`
publishes `ghcr.io/ggml-org/llama.cpp:server-vulkan` and `stable-diffusion.cpp` supports
`-DSD_VULKAN=ON`, so the **LLM and image-generation** paths get GPU acceleration on AMD,
Intel, and NVIDIA hardware through one build. **PyTorch has no Vulkan compute backend**,
so torch installs CPU wheels (exactly like the ROCm Dockerfile already does) and the
ASR/TTS/embeddings/docling services run on CPU. Outcome: a Vulkan-accelerated LLM + image
gen build that runs anywhere a Vulkan driver is present.

**Scope (per user):** local build only. Naming uses **`vulkan`** everywhere. The build is
**universal across NVIDIA + AMD + Intel** — one image, one compose file.

**How one Vulkan build runs on every vendor** (researched; sources in
[llama.cpp #16138](https://github.com/ggml-org/llama.cpp/discussions/16138) and the
[NVIDIA Container Toolkit docs](https://docs.nvidia.com/datacenter/cloud-native/container-toolkit/latest/docker-specialized.html)):

- The Vulkan binary itself is vendor-neutral. Only *driver delivery* differs:
  - **AMD/Intel:** the **Mesa Vulkan driver** must be **in the image** (`mesa-vulkan-drivers`)
    and the container needs `/dev/dri` + `video`/`render` groups.
  - **NVIDIA:** the **nvidia-container-toolkit injects** NVIDIA's Vulkan ICD + driver libs at
    runtime, but **only** when the **nvidia runtime** is used **and**
    `NVIDIA_DRIVER_CAPABILITIES` includes **`graphics`** (compute alone is insufficient).
  - The stock `ggml-org/llama.cpp:server-vulkan` image is **missing libglvnd/EGL**, which
    makes the ICD loader fail *silently* ("no devices found"). Installing
    `libglvnd0 libgl1 libglx0 libegl1 libgles2` fixes NVIDIA and is harmless to AMD/Intel.
- So the **image** is made universal by adding `mesa-vulkan-drivers` + the glvnd/EGL libs.
- The **compose file** is made universal by `/dev/dri` + `video`/`render` groups (AMD/Intel)
  **plus** NVIDIA env (`NVIDIA_VISIBLE_DEVICES`, `NVIDIA_DRIVER_CAPABILITIES=graphics,...`,
  ignored when no nvidia runtime) **plus** an interpolated `runtime: ${GA_VULKAN_RUNTIME:-runc}`.
- The **only** per-host variable is whether the nvidia runtime exists (it hard-errors on
  AMD-only hosts). **The installer (`installer/guideants.sh`) detects the GPU and exports
  `GA_VULKAN_RUNTIME`** = `nvidia` when the nvidia container runtime is registered with
  Docker, else the harmless `runc` default — right before it runs `docker compose up`.
  Because Compose reads shell env vars (which override `--env-file`), the single compose
  file adapts with no extra script. On a mixed NVIDIA+AMD host the same file sees *both*
  GPUs at once.

## Files to create

### 1. `docker/build/Sandboxes/python311TorchVulkan/requirements.txt`
Copy verbatim from `docker/build/Sandboxes/python311TorchCUDA/requirements.txt` (the ROCm
folder uses the same list). This is the only file the build script reads from the sandbox
folder.

### 2. `docker/build/guideants-ai/Dockerfile.vulkan`
Clone `Dockerfile.rocm` (closest analog: torch on CPU wheels, GPU only for llama/SD) and
change only the vendor-specific parts. Use `Dockerfile.rocm` as the structural template
and apply these diffs:

- **SD builder stage** `sd-cli-vulkan-builder`:
  - Base on a Vulkan-capable builder. Use `ubuntu:24.04` and install Vulkan build deps:
    `build-essential ca-certificates cmake git libvulkan-dev glslang-tools` **plus `glslc`**
    (ggml's Vulkan shader compile step needs `glslc` from shaderc — on Ubuntu this comes
    from the LunarG Vulkan SDK apt repo; verify availability during build, see Verification).
  - Replace the cmake flags `-DSD_HIPBLAS=ON -DGGML_HIPBLAS=ON "-DAMDGPU_TARGETS=..."`
    with `-DSD_VULKAN=ON -DGGML_VULKAN=ON`. Drop the `gfx*` `SD_AMDGPU_TARGETS` ARG.
  - Keep the existing sd-cli/sd-server copy+strip logic unchanged.
- **Runtime base** `runtime-vulkan-base`: `FROM ghcr.io/ggml-org/llama.cpp:server-vulkan`
  (replaces `:server-rocm`). The rest of the stage (python3.11 copy, dotnet, pwsh, venv
  env vars) is identical — keep the ROCm version (no NCCL `LD_LIBRARY_PATH`, unlike CUDA).
- **Universal GPU driver layer (the key addition).** In `runtime-vulkan-base`'s apt install,
  add the packages that make one image work on every vendor:
  - `mesa-vulkan-drivers` — Mesa Vulkan ICD for AMD (RADV) + Intel (ANV), shipped in-image.
  - `libglvnd0 libgl1 libglx0 libegl1 libgles2` — without these the ICD loader fails
    silently and **NVIDIA** reports "no devices found" (see #16138).
  - `vulkan-tools` — provides `vulkaninfo`/`--list-devices` for debugging (optional but cheap).
  The NVIDIA ICD itself is **not** installed — it is injected at runtime by the
  nvidia-container-toolkit. This single image then runs on NVIDIA, AMD, and Intel.
- **`pydeps-vulkan-builder`**: identical to ROCm — torch wheels stay on
  `--index-url https://download.pytorch.org/whl/cpu`.
- **`deps-vulkan`** and **`final-vulkan`**: rename the ROCm stages; copy
  `sd-cli`/`sd-server` from `sd-cli-vulkan-builder`. Pull from `${GA_DEPS_VULKAN_IMAGE}`.
- Top ARG: `ARG GA_DEPS_VULKAN_IMAGE=deps-vulkan` (replaces `GA_DEPS_ROCM_IMAGE`).
- Keep entrypoint, COPY of service dirs, HEALTHCHECK, EXPOSE identical.

### 3. `docker-compose.vulkan.yml` (single universal file — create in BOTH docker dirs)
Create **two identical copies**: `docker/docker-compose.vulkan.yml` (dev/build workflow)
and `installer/docker/docker-compose.vulkan.yml` (the copy the installer actually runs —
`installer/docker/` is a hand-maintained mirror, not a symlink, so it needs its own copy
exactly like `docker-compose.rocm.yml` is duplicated there today).

Clone `docker/docker-compose.rocm.yml` and change only the `guideants-ai` service so it
covers every vendor at once:

- `image: ${GA_AI_VULKAN_IMAGE:-ghcr.io/elumenotion/guideants-ai-vulkan:latest}`
- Replace the ROCm `group_add`/`devices` block (which has `/dev/kfd`) with:
  ```yaml
  runtime: ${GA_VULKAN_RUNTIME:-runc}   # installer sets 'nvidia' on NVIDIA hosts; 'runc' is the harmless default
  group_add:
    - video
    - render
  devices:
    - /dev/dri        # AMD/Intel (Mesa) + NVIDIA-with-drm; see verification note for headless NVIDIA
  ```
- Add to the service `environment:` (harmless when the nvidia runtime is absent):
  ```yaml
  - NVIDIA_VISIBLE_DEVICES=${NVIDIA_VISIBLE_DEVICES:-all}
  - NVIDIA_DRIVER_CAPABILITIES=${NVIDIA_DRIVER_CAPABILITIES:-graphics,compute,utility}
  ```
- `GA_EMB_DEVICE=${GA_EMB_DEVICE:-cpu}` (torch is CPU — ROCm leaves this `rocm`, but `cpu`
  is correct here).
- `docling-serve` keeps the CPU image (already the case in the ROCm file). All other
  services unchanged.

Note: do **not** use the CUDA file's `deploy.resources.reservations.devices: driver: nvidia`
block — that can't be interpolated away and hard-errors on non-NVIDIA hosts. The
`runtime:` + `NVIDIA_*` env approach is the portable equivalent, and the installer supplies
`GA_VULKAN_RUNTIME` (see section 6).

## Files to edit

### 4. `docker/build/build_guideants_ai.sh`
- Usage text (line ~15): `cpu | cuda13 | rocm | slim | vulkan`.
- Interactive menu (lines 129-134): add `echo "  5) Vulkan"` and update the prompt range.
- `--backend` case map (lines 136-145): add `vulkan) choice="5" ;;` and update the
  invalid-backend message.
- Add a `5)` block (after the `4)` slim block, lines ~173-180):
  ```sh
  5)
    BACKEND="vulkan"
    FULL_TARGET="final-vulkan"
    DEPS_TARGET="deps-vulkan"
    DEPS_IMAGE_ARG="GA_DEPS_VULKAN_IMAGE"
    REQUIREMENTS_SRC="$SCRIPT_DIR/Sandboxes/python311TorchVulkan/requirements.txt"
    DOCKERFILE_PATH="$BUILD_CONTEXT/Dockerfile.vulkan"
    ;;
  ```
- Env-key case (lines 337-342): add `vulkan) IMAGE_ENV_KEY="GA_AI_VULKAN_IMAGE" ;;`.

### 5. `.env` — add `GA_AI_VULKAN_IMAGE` to BOTH env files
The build script and the installer read **different** env files, and only the build script
auto-writes image keys (to the top-level one). So add the line in both:

- **`docker/.env`** (after line 23) — `build_guideants_ai.sh` will also overwrite/refresh
  this automatically after a vulkan build, but seed it for clarity:
  ```
  GA_AI_VULKAN_IMAGE=guideants-ai:vulkan-latest
  ```
- **`installer/docker/.env`** (alongside the other `GA_AI_*_IMAGE` template lines) — **must
  be added by hand**; nothing populates it. Without this, the installer falls back to the
  compose default `ghcr.io/elumenotion/guideants-ai-vulkan:latest`, which isn't published
  yet (deferred), so local-mode vulkan would fail to find an image:
  ```
  GA_AI_VULKAN_IMAGE=guideants-ai:vulkan-latest
  ```

(`GA_VULKAN_RUNTIME` is exported by the installer at launch — not stored in either `.env`;
optionally a user can pin it in one of them per host.)

### 6. `installer/guideants.sh` — register `vulkan` + detect GPU/runtime
This is where the GPU detection and runtime pick live (replacing the standalone wrapper).
Reuse the existing helpers (`have nvidia-smi`, `nvidia_driver_major`, `/dev/kfd` checks):

- **Make `vulkan` a valid backend:**
  - `--backend` validation regex (line 76): `^(cpu|cuda13|rocm|slim|vulkan)$`.
  - `choose_backend` saved-state regex (line 415): add `|vulkan`.
  - Help text (header comment lines 12/18 and `--backend` line 37): mention `vulkan`.
- **Offer it in the menu** (`choose_backend`, after the cuda13/rocm conditional blocks,
  lines ~426-440): add an always-available entry, e.g.
  `backend_keys+=("vulkan"); backend_labels+=("vulkan  Local AI on any GPU via Vulkan (NVIDIA/AMD/Intel)")`.
  Leave `recommend_backend` unchanged (cuda13/rocm/cpu stay the auto-recommendation;
  vulkan is an explicit opt-in).
- **Map it to the compose file** (`compose_file_for`, lines 466-482): add
  `vulkan) echo "docker-compose.vulkan.yml" ;;` to **both** the `local` and `ghcr` branches
  (only the local file exists in this scope; a published `docker-compose.ghcr-vulkan.yml`
  is deferred — until then `--compose local` is the supported path for vulkan).
- **Detect GPU and pick the runtime.** Add a small function and call it right after
  `COMPOSE_FILE` is resolved (after line 1055), before `plan_pull`/`up`:
  ```sh
  select_vulkan_runtime() {
    [[ "$SELECTED_BACKEND" == "vulkan" ]] || return 0
    if docker info --format '{{json .Runtimes}}' 2>/dev/null | grep -q '"nvidia"'; then
      export GA_VULKAN_RUNTIME=nvidia
      log "Vulkan: NVIDIA container runtime detected → injecting NVIDIA Vulkan ICD."
    else
      export GA_VULKAN_RUNTIME=runc
      if [[ -e /dev/dri ]]; then
        log "Vulkan: using Mesa via /dev/dri (AMD/Intel)."
      else
        warn "Vulkan: no nvidia runtime and no /dev/dri — GPU may be unavailable; will run on CPU."
      fi
    fi
  }
  ```
  The `export` makes the value win over `--env-file` during Compose interpolation, so the
  single `up` at line 1102 adapts automatically. (Detection uses the **docker runtime**,
  not just `nvidia-smi`, because Vulkan ICD injection requires the nvidia container runtime.)
- **Optional:** add a `vulkan` branch to `check_gpu_drivers` (lines 296-382) that just logs
  the detected GPU and warns if neither path is present — mirrors the cuda13/rocm reporting
  but never hard-fails (Vulkan degrades to CPU).

## Deferred (out of scope, noted for later)
Still not touched: a published `docker-compose.ghcr-vulkan.yml` + the matching `ghcr`
branch image wiring, a `publish-vulkan` job in
`.github/workflows/publish-guideants-ai-images.yml`, and the `ValidateSet`/switch in
`docker/build/build_guideants_ai.ps1`.

## Verification

1. **Build the image:**
   ```sh
   cd docker/build && ./build_guideants_ai.sh --backend vulkan
   ```
   Expect: ScriptExecutionAgent publishes, deps image builds, `final-vulkan` builds, and
   the script writes `GA_AI_VULKAN_IMAGE=guideants-ai:vulkan-latest` to `docker/.env`.
   - **Watch the SD builder stage** — the `glslc`/Vulkan-SDK install is the highest-risk
     step. If `glslc` is unavailable via apt, add the LunarG Vulkan SDK apt repo in that
     stage (or fall back to building SD without Vulkan as a stopgap).
2. **Sanity-check torch is CPU (expected):** runs without a CUDA/ROCm runtime.
3. **Confirm the image is universal:** `vulkaninfo` should list the right ICD —
   `docker run --rm --gpus all guideants-ai:vulkan-latest vulkaninfo --summary` on NVIDIA,
   and `docker run --rm --device /dev/dri guideants-ai:vulkan-latest vulkaninfo --summary`
   on AMD/Intel. Each should report a physical device (the libglvnd/EGL + mesa-vulkan-drivers
   layer is what makes both work).
4. **Run via the installer (runtime auto-selected):** with `guideants-ai:vulkan-latest`
   built locally and `GA_AI_VULKAN_IMAGE` set in `installer/docker/.env`:
   ```sh
   cd installer && ./guideants.sh --compose local --backend vulkan
   ```
   Confirm the installer logs the detected runtime (`Vulkan: NVIDIA container runtime
   detected …` or `Vulkan: using Mesa via /dev/dri …`), the stack starts,
   `http://localhost:5107/` is reachable, and llama reports a Vulkan device:
   ```sh
   docker logs guideants-ai 2>&1 | grep -i vulkan   # expect "ggml_vulkan: Found N Vulkan devices"
   ```
   Sanity-check the runtime actually applied:
   `docker inspect guideants-ai --format '{{.HostConfig.Runtime}}'`.
5. **Both vendors / mixed host:** on an NVIDIA host the installer exports
   `GA_VULKAN_RUNTIME=nvidia` (ICD injected via the toolkit); on AMD/Intel it stays `runc`
   (Mesa via `/dev/dri`); on a host with **both** GPUs and the nvidia runtime, the same file
   exposes both at once (use `GGML_VK_VISIBLE_DEVICES` to pick). **Headless NVIDIA caveat:**
   if the host has no `/dev/dri` (no nvidia-drm modeset), remove the `/dev/dri` device line —
   the nvidia runtime injection alone is enough for NVIDIA Vulkan.
