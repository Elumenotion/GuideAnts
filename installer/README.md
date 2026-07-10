# GuideAnts — Portable Quick Start

This is one universal bundle for every operating system. It contains launcher
scripts (`guideants.sh` and `guideants.ps1`), stop scripts (`stop_guideants.sh` and
`stop_guideants.ps1`), host-mount helper scripts, and the Docker Compose files. Docker downloads the actual
application images on first run, so the download itself is tiny.

> **Requirement:** Docker must be installed and running.
>
> - **Windows / macOS:** [Docker Desktop](https://www.docker.com/products/docker-desktop/)
> - **Linux:** Docker Engine 24+ with the Compose plugin (legacy `docker-compose` v1 is **not** supported)
>
> **Windows also needs WSL2** — Docker Desktop uses it as the default backend. The launcher
> runs `wsl --status` and warns if WSL2 is not confirmed. Install or upgrade WSL:
> [Microsoft WSL install guide](https://learn.microsoft.com/windows/wsl/install).

## How to run

**Linux / macOS**

```bash
chmod +x guideants.sh
./guideants.sh
```

If macOS blocks it (Gatekeeper), either right-click → **Open**, or run:

```bash
xattr -d com.apple.quarantine guideants.sh
```

> **Apple Silicon note:** Images run as `linux/amd64` under emulation. The
> `slim` backend is recommended on Apple Silicon for best performance.

**Windows (PowerShell)**

Open PowerShell in this folder and run:

```powershell
./guideants.ps1
```

**Windows (WSL / Git Bash)**

Docker Desktop installs WSL2, which includes bash. Open a WSL terminal
(or Git Bash), change into this folder, and run:

```bash
bash guideants.sh
```

On Windows, **PowerShell (`guideants.ps1`) is recommended** — it handles WSL distro
probing, ROCm library staging, and browser launch more reliably than Git Bash.

### WSL2 check (Windows)

```powershell
wsl --status
```

You should see **Default Version: 2**. If not, run `wsl --set-default-version 2` and
reinstall or upgrade your Linux distro. Docker Desktop must have WSL integration
enabled for your distro (Settings → Resources → WSL integration).

## What the launcher does

1. Detects your OS and shell environment (Linux, macOS, Windows/WSL, Git Bash).
2. Checks Docker is installed and the daemon is running.
3. Reports host and Docker engine memory and free disk (warns if Docker has < 16 GiB RAM).
4. Walks you through which backend to use — auto-detects your GPU and recommends the best option.
5. Validates GPU driver versions for CUDA and ROCm backends (see [GPU requirements](#gpu-driver-requirements)).
6. For the `rocm` backend, detects native Linux vs WSL ROCDXG and writes a generated
   runtime override (`docker-compose.rocm-runtime.generated.yml`) with the correct
   GPU device binds (see [AMD ROCm setup](#amd-rocm-setup)).
7. Checks the registry for newer images and **asks before updating**.
8. Starts the stack and waits for the health check at <http://localhost:5107/>.
9. Opens your browser automatically.

On first load you'll be sent to `/register`; the first account becomes **Admin**.

## Backends

| Backend  | Use for | Approximate download |
|----------|---------|----------------------|
| `cuda13` | NVIDIA GPU (driver R580+, CUDA 13+) | Large |
| `rocm`   | AMD GPU with ROCm 6.0+ (Linux / Windows via WSL2) | Large |
| `vulkan` | Local AI on NVIDIA, AMD, or Intel GPUs through Vulkan | Large |
| `cpu`    | Local AI, no GPU (slower) | ~60 GB |
| `slim`   | Cloud AI providers only — no local model runtime | ~15 GB |

The launcher auto-detects your GPU and recommends a backend. `vulkan` is also
available as a broad GPU path. If Docker has limited RAM (< 16 GiB) and no GPU
is found, it recommends `slim`.

If the pre-built GHCR Vulkan image is not yet pullable, build it locally first:

```powershell
powershell -ExecutionPolicy Bypass -File ..\docker\build\build_guideants_ai.ps1 -Backend vulkan
powershell -ExecutionPolicy Bypass -File .\guideants.ps1 --backend vulkan --compose local --reconfigure
```

Your backend choice is saved in `.installer_state.env` and reused on subsequent
runs. Pass `--reconfigure` to re-prompt.

## GPU driver requirements

**NVIDIA (cuda13 backend)**

The launcher inspects the CUDA container image for its `NVIDIA_REQUIRE_CUDA`
label and validates your local driver against it. Fallback minimums if the
image can't be inspected:

- NVIDIA driver >= R580
- CUDA >= 13.0

If your driver is too old the launcher will abort with upgrade instructions.

**AMD (rocm backend)**

- ROCm >= 6.0.0

If ROCm is below the minimum, the launcher warns and asks whether to continue.

See [AMD ROCm setup](#amd-rocm-setup) for native Linux and Windows/WSL2 installation.

## AMD ROCm setup

ROCm uses **different container wiring** on native Linux vs Windows/WSL2. The static
compose files stay host-agnostic; the launcher writes
`docker/docker-compose.rocm-runtime.generated.yml` at startup (git-ignored, regenerated
each run).

| Host | GPU path | What the override adds |
|------|----------|------------------------|
| Native Linux | `/dev/kfd` + `/dev/dri` (kernel fusion driver) | `devices`, `group_add: [video, render]` |
| Windows / Docker Desktop | ROCDXG via `/dev/dxg` (DXCore bridge) | `/dev/dxg`, `librocdxg` binds, `HSA_ENABLE_DXG_DETECTION=1`, `SYS_PTRACE`, `seccomp:unconfined` |

Full design notes: [`docs/rocm-container-runtime-and-wsl-setup.md`](../docs/rocm-container-runtime-and-wsl-setup.md).

### Native Linux

Prerequisites:

- ROCm installed on the host (`/dev/kfd` and `/dev/dri` exist).
- Host user in `video` and `render` groups (for non-root Docker), or run the daemon appropriately.

Launch:

```bash
./guideants.sh --backend rocm
```

The launcher detects native Linux and writes the KFD override automatically.

Install ROCm: [AMD ROCm on Linux](https://rocm.docs.amd.com/projects/install-on-linux/en/latest/).

### Windows (WSL2 + ROCDXG)

On Windows, `/dev/kfd` does not exist inside Docker Desktop. AMD's **ROCDXG** library
(`librocdxg.so`) bridges ROCm/HIP calls to the Windows GPU driver through `/dev/dxg`.
That library lives in a **user WSL distro** (not `docker-desktop`), so the launcher
**stages** it to `docker/volumes/rocm-wsl/lib/` before bind-mounting it into the container.

Prerequisites:

- Windows 11 with WSL2 and Docker Desktop.
- AMD **Adrenalin driver 26.2.2+** (ROCDXG-capable; e.g. Strix Halo / Radeon 8060S).
- A user WSL distro — **Ubuntu 24.04** recommended (`docker-desktop` cannot hold the ROCm install).

**1. Install a user distro** (if needed):

```powershell
wsl --install -d Ubuntu-24.04
```

**2. Install ROCm + ROCDXG inside it** (run as root; adjust the path to this repo):

```powershell
wsl -d Ubuntu-24.04 -u root bash /mnt/c/path/to/GuideAnts/installer/scripts/install-rocm-wsl.sh
```

Or from the installer folder on Windows:

```powershell
.\guideants.ps1 --install-rocm-wsl
```

This installs ROCm 7.2.4, `librocdxg` 1.2.0, and `/etc/profile.d/rocm-wsl.sh` (from
`scripts/rocm-wsl.profile`), then verifies with `rocminfo`.

**3. Confirm the GPU is visible in WSL:**

```powershell
wsl -d Ubuntu-24.04 sh -lc "HSA_ENABLE_DXG_DETECTION=1 rocminfo | grep -A2 gfx"
```

You should see your AMD GPU (e.g. `gfx1151` on Strix Halo).

**4. Launch GuideAnts:**

```powershell
.\guideants.ps1 --backend rocm
```

The launcher stages `librocdxg`, writes the WSL override, and starts the stack.

Use `--doctor --backend rocm` to run all checks read-only and print the exact
`docker compose ... up -d` command (including the runtime override) without starting
anything.

### ROCm troubleshooting (Windows)

| Symptom | Likely cause |
|---------|----------------|
| `librocdxg not found` | ROCm not installed in a user WSL distro — run `install-rocm-wsl.sh` |
| `librocdxg.so ... Is a directory` | Do not bind-mount via `//wsl.localhost/...`; let the launcher stage to `volumes/rocm-wsl/lib/` |
| GPU not detected on Windows | Probes ran in `docker-desktop` instead of your user distro — install ROCm in Ubuntu (or similar) |
| `unable to find group render` | Expected on Docker Desktop — `group_add` is native-Linux-only in the generated override |

Docker Desktop reports ~2.5 GiB of `/dev/dxg` VRAM overhead (WDDM virtualization); that
is normal, not a GuideAnts issue.

Verify GPU visibility inside the ROCm image (adjust paths to your install folder):

```powershell
docker run --rm --entrypoint sh `
  -v /usr/lib/wsl/lib/libdxcore.so:/usr/lib/libdxcore.so:ro `
  -v "C:/path/to/installer/docker/volumes/rocm-wsl/lib/librocdxg.so:/lib/librocdxg.so:ro" `
  -v "C:/path/to/installer/docker/volumes/rocm-wsl/lib/librocdxg.so:/usr/lib/librocdxg.so:ro" `
  --device /dev/dxg --cap-add SYS_PTRACE --security-opt seccomp=unconfined `
  -e HSA_ENABLE_DXG_DETECTION=1 `
  ghcr.io/elumenotion/guideants-ai-rocm:main `
  -c "/app/llama-server --list-devices"
```

Expected output includes your AMD GPU under `ROCm0:`.

## Options

| Flag | What it does |
|------|--------------|
| `--doctor` | Run all checks, change nothing. Prints the compose command that would be used. |
| `--backend <cpu\|cuda13\|rocm\|slim\|vulkan>` | Skip the interactive backend prompt. |
| `--compose <ghcr\|local>` | Use GHCR pre-built images (default) or local build images. |
| `--mount /path/to/folder` | Mount a host folder into a project on startup (requires browser login). |
| `--unmount` | Interactively remove a host folder mount (requires browser login). |
| `--reconfigure` | Re-prompt for backend even if one was previously saved. |
| `--install-rocm-wsl` | Install ROCm + ROCDXG in a user WSL distro (Windows only), then continue. |
| `--yes` / `-y` | Accept prompts automatically (use recommended backend, auto-accept updates). |
| `--help` / `-h` | Show help text. |

## Host folder mounting

The `--mount` and `--unmount` flags let you bind-mount a folder from your host
machine into a GuideAnts project so that files are shared between your local
filesystem and the running containers.

**Mounting a folder:**

Linux / macOS:

```bash
./guideants.sh --mount /path/to/your/folder
```

Windows (PowerShell):

```powershell
./guideants.ps1 --mount C:/path/to/your/folder
```

1. The stack starts normally.
2. After health check passes, a CLI authentication session is created.
3. Your browser opens an authorization page — approve the request.
4. You select which project (and optionally which notebook) to mount into.
5. The folder is bind-mounted into the `guideants-webapi-ui`, `guideants-ai`,
   and `plantuml` containers at `/app/HostMounts/<mount-key>`.
6. Affected services restart automatically.

**Unmounting a folder:**

Linux / macOS:

```bash
./guideants.sh --unmount
```

Windows (PowerShell):

```powershell
./guideants.ps1 --unmount
```

Follows the same authentication flow, then lets you pick an active mount to
remove.

Mount state is stored in `docker/docker-compose.host-mounts.generated.yml`
(auto-generated, do not edit manually). The helper scripts in `scripts/`
(`guideants-host-mount.sh` for bash, `guideants-host-mount.ps1` for
PowerShell) handle writing this file and restarting services.

## Compose modes

| Mode | Description |
|------|-------------|
| `ghcr` (default) | Pulls pre-built images from GitHub Container Registry. |
| `local` | Uses locally built images (for development). |

Each mode has its own set of compose files:

- GHCR: `docker-compose.ghcr-cpu.yml`, `docker-compose.ghcr-cuda13.yml`, `docker-compose.ghcr-rocm.yml`, `docker-compose.ghcr-slim.yml`, `docker-compose.ghcr-vulkan.yml`
- Local: `docker-compose.cpu.yml`, `docker-compose.cuda.yml`, `docker-compose.rocm.yml`, `docker-compose.slim.yml`, `docker-compose.vulkan.yml`

For the `rocm` backend, the launcher also layers
`docker-compose.rocm-runtime.generated.yml` (auto-generated GPU wiring). An example
of both native and WSL shapes is in the repo at
`docker/docker-compose.rocm-runtime.generated.example.yml`.

## Stopping

Use the stop script:

Linux / macOS:

```bash
./stop_guideants.sh
```

Windows (PowerShell):

```powershell
./stop_guideants.ps1
```

It reads the saved backend from `.installer_state.env` and runs
`docker compose down` on the correct compose file(s). You can also override:

Linux / macOS:

```bash
./stop_guideants.sh --backend slim
```

Windows (PowerShell):

```powershell
./stop_guideants.ps1 --backend slim
```

## Services

The stack runs the following containers:

| Container | Purpose |
|-----------|---------|
| `guideants-webapi-ui` | Web API and UI (port 5107) |
| `guideants-ai` | AI services (LLM, ASR, TTS, embeddings, image generation) |
| `mssql-express` | SQL Server database |
| `docling-serve` | Document intelligence / conversion |
| `documentserver` | Office document editing (OnlyOffice) |
| `plantuml` | PlantUML diagram rendering |
| `searxng` | Web search and browser rendering |

## Data persistence

Projects, database, and models live in Docker named volumes (`mssql_data`,
`mssql_log`, `ai_local_models`, etc.) and in the `docker/volumes/content-files`
bind mount. They persist across stops and updates.

## File structure

```
installer/
├── guideants.sh                    # Main launcher
├── guideants.ps1                   # Main launcher (PowerShell)
├── stop_guideants.sh               # Stop script
├── stop_guideants.ps1              # Stop script (PowerShell)
├── .installer_state.env            # Saved backend/compose state (auto-generated)
├── README.md
├── scripts/
│   ├── guideants-host-mount.sh     # Host mount helper (bash)
│   ├── guideants-host-mount.ps1    # Host mount helper (PowerShell)
│   ├── install-rocm-wsl.sh         # One-shot ROCm + ROCDXG install inside Ubuntu WSL
│   ├── rocm-wsl.profile            # Env vars copied to /etc/profile.d/rocm-wsl.sh
│   ├── rocm-probe.ps1              # Shared WSL/ROCm detection (PowerShell)
│   ├── rocm-probe.sh               # Shared WSL/ROCm detection (bash)
│   ├── rocm-runtime-compose.sh     # ROCm runtime override (bash / WSL launcher)
│   └── rocm-runtime-compose.ps1    # ROCm runtime override + WSL library staging (Windows)
└── docker/
    ├── .env                        # Compose environment variables
    ├── docker-compose.ghcr-*.yml   # GHCR compose files (one per backend)
    ├── docker-compose.*.yml        # Local build compose files
    ├── docker-compose.host-mounts.generated.yml   # Auto-generated mount overrides
    ├── docker-compose.rocm-runtime.generated.yml  # Auto-generated ROCm GPU wiring (rocm only)
    └── volumes/
        ├── content-files/          # Project file storage (bind-mounted)
        ├── rocm-wsl/               # Staged librocdxg for Docker Desktop binds (git-ignored)
        └── searxng/                # SearXNG config and cache
```
