# GuideAnts AI — Vulkan Build

## What it is

The **Vulkan** backend is a vendor-neutral local-AI image. It accelerates the
**LLM** (`llama.cpp`) and **image generation** (`stable-diffusion.cpp`) paths through the
[Vulkan](https://www.vulkan.org/) GPU API, so a **single image runs on NVIDIA, AMD, and
Intel** GPUs — on **both Windows (Docker Desktop) and native Linux**. This is the key
difference from the `cuda13` (NVIDIA-only) and `rocm` (AMD-only) backends, each tied to one
vendor's compute stack.

Vulkan covers compute for `llama.cpp` and `stable-diffusion.cpp` only. **PyTorch has no
Vulkan compute backend**, so torch installs CPU wheels (exactly like the ROCm image does)
and the **ASR / TTS / embeddings / docling** services run on CPU. The net result: a
universal, GPU-accelerated **LLM + image-gen** build that runs anywhere a Vulkan driver is
present, falling back to CPU for everything else.

| Backend | GPU API | Hardware | torch |
| --- | --- | --- | --- |
| `cpu` | — | any | CPU wheels |
| `cuda13` | CUDA | NVIDIA only | CUDA wheels |
| `rocm` | HIP/ROCm | AMD only | CPU wheels |
| **`vulkan`** | **Vulkan** | **NVIDIA + AMD + Intel** | **CPU wheels** |
| `slim` | — | any (no local model runtime) | CPU wheels |

Like the other full `guideants-ai` images, the Vulkan image bakes in **Node.js 22**
(`node` / `npx`) so `mcp+sandbox://` package MCP servers can run inside the container.

## How the GPU is reached

The Vulkan binaries are vendor-neutral; what differs per host is **which device node** the
container needs and **how the driver is delivered**. There are three paths, and **one compose
file** (`docker-compose.vulkan.yml`) covers all of them by interpolating a handful of
`GA_VULKAN_*` env vars. The **defaults target Windows / Docker Desktop**, so on Windows it runs
from git bash with zero configuration; on native Linux the installer exports the right values.

| Host | GPU path | Device | Driver delivery | ICD pinned to |
| --- | --- | --- | --- | --- |
| **Windows / Docker Desktop** (any vendor) | Vulkan → Mesa **dzn** → D3D12 | `/dev/dxg` | in-image dzn + `/usr/lib/wsl` (from the docker-desktop VM) | `dzn_icd.json` |
| **Native Linux — AMD / Intel** | Mesa **RADV / ANV** | `/dev/dri` | `mesa-vulkan-drivers` baked into the image | `radeon_icd` / `intel_icd` |
| **Native Linux — NVIDIA** | native NVIDIA ICD | (toolkit-injected) | nvidia-container-toolkit (`runtime: nvidia` + `graphics` cap) | `nvidia_icd.json` |

Two things make this robust:

- **On Docker Desktop, dzn is the path for *every* vendor** (NVIDIA included). The native NVIDIA
  ICD needs `/dev/nvidia*`, which doesn't exist under WSL2 — only `/dev/dxg` does — so the
  nvidia container runtime there exposes **CUDA only, no Vulkan ICD** and would land on `llvmpipe`
  (CPU). That's why Windows uses `runtime: runc` + dzn even on NVIDIA hardware. (The
  `docker-desktop` VM where containers run already has `/dev/dxg` + a fully-populated
  `/usr/lib/wsl`, so the dzn path works launched from git bash, PowerShell, **or** a WSL distro.)
- **No silent CPU fallback, anywhere.** `VK_DRIVER_FILES` is always pinned to exactly **one**
  ICD, so the CPU software rasterizer (`llvmpipe`) is never a loader candidate — the stack uses
  the GPU or fails loudly.

### The `GA_VULKAN_*` knobs

`docker-compose.vulkan.yml` interpolates these. **Windows needs none** (the defaults are the dzn
path). The installer sets them on native Linux; you can also set them by hand for a manual
`docker compose up` on Linux.

| Var | Default (Windows) | Native Linux AMD/Intel | Native Linux NVIDIA |
| --- | --- | --- | --- |
| `GA_VULKAN_RUNTIME` | `runc` | `runc` | `nvidia` |
| `GA_VULKAN_DEVICE` | `/dev/dxg` | `/dev/dri` | `/dev/dri` (or `/dev/null` if headless) |
| `GA_VULKAN_ICD` | `…/dzn_icd.json` | `…/radeon_icd.x86_64.json` / `…/intel_icd.x86_64.json` | `…/nvidia_icd.json` |
| `GA_VULKAN_DRIVER_LIBS` | `/usr/lib/wsl` | `/usr/lib` | `/usr/lib` |
| `GA_VULKAN_LD_LIBRARY_PATH` | `/usr/lib/wsl/lib` | `/usr/lib/x86_64-linux-gnu` | `/usr/lib/x86_64-linux-gnu` |
| `MESA_D3D12_DEFAULT_ADAPTER_NAME` | `Radeon` | (ignored) | (ignored) |
| `GGML_VK_PREFER_HOST_MEMORY` | (unset) | `1` optional on RADV/UMA | (unset) |

`GA_VULKAN_DRIVER_LIBS` is bind-mounted at `/usr/lib/wsl`; on Windows it carries the D3D12
runtime libs (`libd3d12core.so`/`libdxcore.so`), and on Linux it points at a harmless existing
dir (the in-image Mesa / toolkit-injected NVIDIA drivers are used instead, so the bind is unused).

## Dockerfile structure

`docker/build/guideants-ai/Dockerfile.vulkan` mirrors `Dockerfile.rocm` (closest analog:
torch on CPU, GPU only for llama/SD) with Vulkan-specific stages. The image carries the drivers
for **all three** paths, so the cross-OS support is purely a compose-layer concern:

- **`sd-cli-vulkan-builder`** — builds `stable-diffusion.cpp` with `-DSD_VULKAN=ON
  -DGGML_VULKAN=ON` on `ubuntu:24.04`. Installs the Vulkan build toolchain
  (`libvulkan-dev glslang-tools glslc spirv-headers`); `glslc` comes from the Ubuntu
  `universe` repo, and `spirv-headers` is required by ggml's Vulkan CMake config.
- **`dzn-vulkan-builder`** — builds **only** Mesa's **dzn** (Vulkan-on-D3D12) driver from
  source on `ubuntu:26.04` (`-Dvulkan-drivers=microsoft-experimental`, all other drivers /
  GLX / EGL / GBM / LLVM disabled), pinned to the Mesa release matching the runtime base
  (`MESA_DZN_REF=mesa-26.0.3`). Ubuntu's `mesa-vulkan-drivers` ships RADV/ANV/llvmpipe but
  **not** dzn, so it must be compiled. Stages `libvulkan_dzn.so`, `libspirv_to_dxil.so`, and
  `dzn_icd.json`.
- **`runtime-vulkan-base`** — `FROM ghcr.io/ggml-org/llama.cpp:server-vulkan`. Adds the
  universal driver layer: **`mesa-vulkan-drivers`** (RADV/ANV for native-Linux AMD/Intel), the
  **libglvnd/EGL** libs (needed for the native-Linux NVIDIA ICD injection), and `vulkan-tools`
  (for `vulkaninfo`). Then python3.11, dotnet, pwsh, and the shared venv env vars.
- **`pydeps-vulkan-builder`** — Python deps; torch wheels stay on
  `--index-url https://download.pytorch.org/whl/cpu`.
- **`deps-vulkan`** — runtime deps; copies `sd-cli`/`sd-server` and installs Playwright/chromium.
- **`final-vulkan`** — runtime image; copies service dirs, gateway config, entrypoint, **and the
  dzn driver + ICD** (then `ldconfig`). dzn lives here rather than `deps-vulkan` so an ordinary
  `--backend vulkan` build picks it up without a full `--rebuild-base`; BuildKit caches the Mesa
  compile across incremental builds.

### Ubuntu 26.04 base-image notes

The `server-vulkan` base is **Ubuntu 26.04** (the `server-rocm` base is 24.04). Four workarounds
account for the newer base:

- **`pkg-config`** added (26.04 omits it by default; `pygraphviz` needs it).
- **`ENV CFLAGS="-Wno-error=incompatible-pointer-types"`** — GCC 14+ promotes
  `-Wincompatible-pointer-types` to an error that breaks `pygraphviz 1.14`'s SWIG wrapper.
- **`/etc/os-release` spoofed to 24.04** during `playwright install` (Playwright 1.60 hard-blocks
  26.04; the 24.04 Chromium binaries are compatible).
- **`final-vulkan` creates the `video` and `render` groups** (`groupadd -f`) so the compose file's
  `group_add: [video, render]` resolves (the base ships no `render` group).

## Compose file

`docker/docker-compose.vulkan.yml` is a **single env-driven** file (with a hand-maintained copy in
`installer/docker/` that the installer runs). There are **no overlays**. The `guideants-ai`
service GPU block:

```yaml
runtime: ${GA_VULKAN_RUNTIME:-runc}
group_add: [ video, render ]
devices:
  - ${GA_VULKAN_DEVICE:-/dev/dxg}
volumes:
  - ${GA_VULKAN_DRIVER_LIBS:-/usr/lib/wsl}:/usr/lib/wsl:ro
environment:
  - LD_LIBRARY_PATH=${GA_VULKAN_LD_LIBRARY_PATH:-/usr/lib/wsl/lib}
  - VK_DRIVER_FILES=${GA_VULKAN_ICD:-/usr/share/vulkan/icd.d/dzn_icd.json}
  - VK_ICD_FILENAMES=${GA_VULKAN_ICD:-/usr/share/vulkan/icd.d/dzn_icd.json}
  - MESA_D3D12_DEFAULT_ADAPTER_NAME=${MESA_D3D12_DEFAULT_ADAPTER_NAME:-Radeon}
  - NVIDIA_VISIBLE_DEVICES=${NVIDIA_VISIBLE_DEVICES:-all}
  - NVIDIA_DRIVER_CAPABILITIES=${NVIDIA_DRIVER_CAPABILITIES:-graphics,compute,utility}
  - GA_EMB_DEVICE=${GA_EMB_DEVICE:-vulkan}   # llama-server embeddings on Vulkan
```

With no env set (Windows) this resolves to the dzn/`/dev/dxg` path. The `NVIDIA_*` vars matter
only when `GA_VULKAN_RUNTIME=nvidia` (native-Linux NVIDIA) and are harmless otherwise.

Vulkan llama defaults differ from ROCm on dzn: model weights offload to GPU
(`-ngl auto`), but KV cache stays in system RAM (`--no-kv-offload`) because placing
KV tensors on Vulkan buffers aborts large models. Embeddings use llama-server on Vulkan
(`GA_EMB_DEVICE=vulkan`, `-ngl auto`). Flash attention defaults to **off** on this
backend (Qwen + Mesa Dozen/dzn; Ollama #14854); native Linux RADV hosts can set
`GA_LLAMA_FLASH_ATTN=on` in `.env`. On startup, `entrypoint.sh` backfills per-alias
`ctx-size` from `GA_LLAMA_CTX_SIZE` when `GA_LLAMA_FORCE_ROUTER_CTX=1`.

> **Note:** the bare-file default targets Windows. On a native-Linux host *without* the
> `GA_VULKAN_*` env set, `${GA_VULKAN_DEVICE:-/dev/dxg}` resolves to `/dev/dxg`, which doesn't
> exist there — so use the installer (which sets the env) or export the Linux values yourself.

## Installer

`installer/guideants.sh`'s `select_vulkan_runtime()` detects the host and exports the `GA_VULKAN_*`
wiring before launch — shell-independent (it reads the **daemon**, not host device nodes, so it's
correct from git bash where `/dev/dxg` is invisible):

- **Docker Desktop** (`docker info` OperatingSystem contains `Docker Desktop`): leave the dzn
  defaults — exports nothing.
- **Native Linux NVIDIA** (`nvidia` runtime registered in `docker info`): `GA_VULKAN_RUNTIME=nvidia`
  + `GA_VULKAN_ICD=…/nvidia_icd.json`.
- **Native Linux AMD/Intel** (`/dev/dri` present): reads `/sys/class/drm/renderD*/device/vendor`
  (`0x1002`→RADV, `0x8086`→ANV) and pins the matching ICD over `/dev/dri`.
- **No GPU**: warns and degrades to CPU (never hard-fails).

`vulkan` is offered in the interactive backend menu as an always-available explicit opt-in.

## How to use it

### 1. Build the image locally

```sh
cd docker/build && ./build_guideants_ai.sh --backend vulkan
```
(or `pwsh ./docker/build/build_guideants_ai.ps1 -Backend vulkan`). Writes
`GA_AI_VULKAN_IMAGE=guideants-ai:vulkan-latest` to `docker/.env` (already seeded in both
`docker/.env` and `installer/docker/.env`).

### 2. Run it

**Windows — straight from git bash (zero config):**
```sh
docker compose -f docker/docker-compose.vulkan.yml --env-file docker/.env up -d
# or: cd installer && ./guideants.sh --compose local --backend vulkan
```
No WSL distro needed — the container gets the GPU via the docker-desktop VM's `/dev/dxg` +
`/usr/lib/wsl`.

**Native Linux — via the installer (auto-detects the GPU):**
```sh
cd installer && ./guideants.sh --compose local --backend vulkan
```
For a manual launch on Linux, export the `GA_VULKAN_*` values from the table above first, e.g. AMD:
```sh
GA_VULKAN_DEVICE=/dev/dri \
GA_VULKAN_ICD=/usr/share/vulkan/icd.d/radeon_icd.x86_64.json \
GA_VULKAN_DRIVER_LIBS=/usr/lib GA_VULKAN_LD_LIBRARY_PATH=/usr/lib/x86_64-linux-gnu \
  docker compose -f docker/docker-compose.vulkan.yml --env-file docker/.env up -d
```

Open `http://localhost:5107/`.

### 3. Verify GPU acceleration (not CPU)

```sh
docker exec guideants-ai sh -c '/app/llama-server --list-devices'
#   Vulkan0: Microsoft Direct3D12 (NVIDIA GeForce RTX 4060 Ti) ...   (Windows / dzn)
#   Vulkan0: AMD Radeon ... (RADV)                                   (native Linux AMD)
docker exec guideants-ai sh -c 'vulkaninfo --summary' | grep -iE "deviceName|driverName"
```
(Wrap the in-container command in `sh -c '...'` from git bash, or MSYS mangles the `/app/...`
path.) Expect a real GPU with `driverName = Dozen` (Windows) / `radv` / `Intel` / `NVIDIA` —
**not** `llvmpipe`. Because `VK_DRIVER_FILES` is pinned, a broken GPU path errors rather than
silently using CPU.

**Log check (after a model loads).** llama.cpp prints its Vulkan device line only when it
initializes the Vulkan backend, i.e. when a model is loaded — and this stack runs llama with
`GA_LLAMA_NO_AUTOLOAD=1`, so nothing loads at boot:

```sh
docker logs guideants-ai 2>&1 | grep -i vulkan
```
- **At rest (no model):** on Windows, only `WARNING: dzn is not a conformant Vulkan
  implementation, testing use only.` — harmless, and it confirms the dzn ICD is the active driver.
- **After loading a GGUF:** also
  `ggml_vulkan: Found 1 Vulkan devices:` / `ggml_vulkan: 0 = … (Dozen) | …`, and the GPU's VRAM
  climbs (`nvidia-smi` on the Windows host, or `radeontop` on Linux AMD).

### Picking a GPU on a multi-GPU machine

On Windows, `MESA_D3D12_DEFAULT_ADAPTER_NAME` (default `Radeon` for AMD iGPU/APU hosts like
Strix Halo) sets which adapter dzn lists first. Set `NVIDIA` on Windows boxes with a discrete
NVIDIA card. `GGML_VK_VISIBLE_DEVICES` picks/splits among enumerated devices on any platform —
set it in `.env` only when you need a specific index (for example `0` or `1`). **Do not** set
`GGML_VK_VISIBLE_DEVICES` to an empty string: llama.cpp treats that as "hide every device" and
falls back to CPU-only inference even when `vulkaninfo` sees the GPU.

Stable Diffusion can be pinned independently from llama with `GA_SD_VK_VISIBLE_DEVICES`.
Leave it unset to inherit the container-wide device list; set it when SD should use a different
Vulkan device. For example, `GGML_VK_VISIBLE_DEVICES=1` and `GA_SD_VK_VISIBLE_DEVICES=0`
keeps llama on Vulkan device 1 while the SD `sd-server` subprocess uses Vulkan device 0.

### Strix Halo (Radeon 8060S / gfx1151)

This is a well-supported llama.cpp target on **native Linux** (RADV + `/dev/dri`, or ROCm 7.2+).
Community references: [kyuz0/amd-strix-halo-toolboxes](https://github.com/kyuz0/amd-strix-halo-toolboxes),
[hec-ovi/llama-vulkan-strix](https://github.com/hec-ovi/llama-vulkan-strix), Framework's
[Fedora llama.cpp guide](https://community.frame.work/t/amd-strix-halo-llama-cpp-installation-guide-for-fedora-42/75856).

On **Docker Desktop + Windows**, the stack uses Mesa **dzn** (Vulkan → D3D12 → `/dev/dxg`) instead
of RADV. `docker-compose.vulkan.yml` defaults cover this path; tune llama/SD via `.env` if needed.
No user-WSL install is required (unlike ROCDXG).

Device enumeration works, but dzn cannot allocate large contiguous Vulkan buffers for full-GPU
35B weights or SD diffusion graphs. Use **ROCm** on this host for production GPU diffusion/35B;
Vulkan dzn here is viable for **auto-fit llama** (e.g. 9B) when buffers fit.

On native Linux RADV/UMA (Strix Halo), set `GGML_VK_PREFER_HOST_MEMORY=1` in `.env` so weights use
GTT rather than the BIOS VRAM carve-out.

**ROCm on the same hardware** (when the host exposes it) remains the most mature path in
GuideAnts today. Vulkan is viable once device enumeration and offload are confirmed:

```sh
docker exec guideants-ai /app/llama-server --list-devices
# expect: Vulkan0: Microsoft Direct3D12 (AMD Radeon … 8060S …)
```

If `vulkaninfo` sees the GPU but `--list-devices` is empty, check `printenv GGML_VK_VISIBLE_DEVICES`
inside the container — an empty value is the usual cause. Recreate the stack after updating
compose (we no longer inject an empty default).

A separate [llama.cpp #24474](https://github.com/ggml-org/llama.cpp/issues/24474) regression
(shader build corruption in some `server-vulkan` tags) can also yield zero devices on Linux
`/dev/dri` hosts; pin `server-vulkan-b9570` or a post-#24595 tag if enumeration fails on a
clean env.

## Publishing

The Vulkan image publishes to GHCR alongside the other backends:

- **CI:** the `publish-vulkan` job in `.github/workflows/publish-guideants-ai-images.yml`
  (manual `workflow_dispatch`, variant `vulkan` or `all`) builds `final-vulkan` and pushes
  `ghcr.io/<owner>/guideants-ai-vulkan` with branch / `sha-*` / `latest` tags.
- **Local push:** `docker/push-ghcr-guideants-ai.{ps1,sh}` include `vulkan` (optional, like
  rocm/slim): they tag + push the newest local `guideants-ai:vulkan-*` build as
  `guideants-ai-vulkan` `:<build>` / `:<compose-tag>` / `:latest`.
- **Consume:** `installer/docker/docker-compose.ghcr-vulkan.yml` pulls the published image, and
  the installer's `ghcr` branch routes `--backend vulkan` to it — so once published,
  `./guideants.sh --backend vulkan` (default `--compose ghcr`) works.

> The GHCR package is created the first time the publish workflow runs on `main`; until then use
> `--compose local`.

## File map

```text
docker/
  .env                                          # GA_AI_VULKAN_IMAGE (local tag)
  docker-compose.vulkan.yml                     # single env-driven compose (Windows + Linux)
  guideants-ai-vulkan.md                        # this file
  build/
    build_guideants_ai.sh / .ps1                # backend 5 = vulkan
    guideants-ai/Dockerfile.vulkan              # incl. dzn-vulkan-builder stage
    Sandboxes/python311TorchVulkan/requirements.txt
installer/
  guideants.sh                                  # vulkan backend + select_vulkan_runtime() host detection
  docker/
    .env                                        # GA_AI_VULKAN_IMAGE (hand-maintained)
    docker-compose.vulkan.yml                   # single env-driven compose (installer copy, local build)
    docker-compose.ghcr-vulkan.yml              # GHCR variant (pulls the published image)
.github/workflows/
  publish-guideants-ai-images.yml               # includes the publish-vulkan job
docker/
  push-ghcr-guideants-ai.{ps1,sh}               # include the optional vulkan push target
```
