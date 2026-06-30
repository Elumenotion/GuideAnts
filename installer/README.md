# GuideAnts — Portable Quick Start

This is one universal bundle for every operating system. It contains launcher
scripts (`guideants.sh` and `guideants.ps1`), a stop script (`stop_guideants.sh`),
host-mount helper scripts, and the Docker Compose files. Docker downloads the actual
application images on first run, so the download itself is tiny.

> **Requirement:** Docker must be installed and running.
>
> - **Windows / macOS:** [Docker Desktop](https://www.docker.com/products/docker-desktop/)
> - **Linux:** Docker Engine 24+ with the Compose plugin (legacy `docker-compose` v1 is **not** supported)

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

## What the launcher does

1. Detects your OS and shell environment (Linux, macOS, Windows/WSL, Git Bash).
2. Checks Docker is installed and the daemon is running.
3. Reports host and Docker engine memory and free disk (warns if Docker has < 16 GiB RAM).
4. Walks you through which backend to use — auto-detects your GPU and recommends the best option.
5. Validates GPU driver versions for CUDA and ROCm backends (see [GPU requirements](#gpu-driver-requirements)).
6. Checks the registry for newer images and **asks before updating**.
7. Starts the stack and waits for the health check at <http://localhost:5107/>.
8. Opens your browser automatically.

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

## Options

| Flag | What it does |
|------|--------------|
| `--doctor` | Run all checks, change nothing. Prints the compose command that would be used. |
| `--backend <cpu\|cuda13\|rocm\|slim\|vulkan>` | Skip the interactive backend prompt. |
| `--compose <ghcr\|local>` | Use GHCR pre-built images (default) or local build images. |
| `--mount /path/to/folder` | Mount a host folder into a project on startup (requires browser login). |
| `--unmount` | Interactively remove a host folder mount (requires browser login). |
| `--reconfigure` | Re-prompt for backend even if one was previously saved. |
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
`mssql_log`, `ai_local_models-new`, etc.) and in the `docker/volumes/content-files`
bind mount. They persist across stops and updates.

## File structure

```
installer/
├── guideants.sh                    # Main launcher
├── guideants.ps1                   # Main launcher (PowerShell)
├── stop_guideants.sh               # Stop script
├── .installer_state.env            # Saved backend/compose state (auto-generated)
├── README.md
├── scripts/
│   ├── guideants-host-mount.sh     # Host mount helper (bash)
│   └── guideants-host-mount.ps1    # Host mount helper (PowerShell)
└── docker/
    ├── .env                        # Compose environment variables
    ├── docker-compose.ghcr-*.yml   # GHCR compose files (one per backend)
    ├── docker-compose.*.yml        # Local build compose files
    ├── docker-compose.host-mounts.generated.yml  # Auto-generated mount overrides
    └── volumes/
        ├── content-files/          # Project file storage (bind-mounted)
        └── searxng/                # SearXNG config and cache
```
