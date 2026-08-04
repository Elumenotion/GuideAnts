# GuideAnts — Portable Quick Start

This is one universal bundle for every operating system. It contains launcher
scripts (`guideants.sh` and `guideants.ps1`), stop scripts (`stop_guideants.sh` and
`stop_guideants.ps1`), host-mount helper scripts, and Docker Compose **fragments**
under `docker/compose/`. Docker downloads the actual application images on first
run, so the download itself is tiny.

> **Requirement:** Docker must be installed and running.
>
> - **Windows / macOS:** [Docker Desktop](https://www.docker.com/products/docker-desktop/)
> - **Linux:** Docker Engine 24+ with the Compose plugin (legacy `docker-compose` v1 is **not** supported)
>
> **Windows also needs WSL2** — Docker Desktop uses it as the default backend. The launcher
> runs `wsl --status` and warns if WSL2 is not confirmed. Install or upgrade WSL:
> [Microsoft WSL install guide](https://learn.microsoft.com/windows/wsl/install).

### Validation

```powershell
.\scripts\validate-installer.ps1   # PowerShell parse + bash -n + compose config (Windows)
```

```bash
./scripts/validate-installer.sh    # bash -n + compose config (Linux/macOS/WSL)
```

Both check all launcher `.ps1` / `.sh` files under `installer/` and merge representative compose fragment stacks.

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
> `slim` AI backend is recommended on Apple Silicon for best performance.

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
4. **Asks what to install** — database layout (first install only), AI backend, and optional services. Hardware detection only **recommends**; choices are saved in `.installer_state.env`.
5. Validates GPU driver versions when a local GPU AI backend is selected (see [GPU requirements](#gpu-driver-requirements)).
6. For the `rocm` backend, detects native Linux vs WSL ROCDXG and writes a generated
   runtime override (`docker-compose.rocm-runtime.generated.yml`) with the correct
   GPU device binds (see [AMD ROCm setup](#amd-rocm-setup)).
7. **Pulls each selected image sequentially** with per-image progress (not one monolithic `compose pull`).
8. Starts only the selected services, then stops/removes containers you deselected on a prior reconfigure.
9. Waits for the health check at <http://localhost:5107/> and opens your browser.

On first load you'll be sent to `/register`; the first account becomes **Admin**.

## Component wizard

The installer builds a custom stack from compose fragments. You choose:

### Database layout

| Layout | Images | Approx. size | Notes |
|--------|--------|--------------|-------|
| **Bundled** | `guideants-webapi-ui-mssql` | ~7.3 GB | UI + SQL in one container. |
| **Separate** | `guideants-webapi-ui-slim` + `mssql-express` | ~7.6 GB | Same features; SQL in its own container. |

After first install, `DB_LAYOUT` is fixed and is not prompted or changed on rerun/`--reconfigure`.

### AI backend

| Backend | Approx. size | What you get |
|---------|--------------|--------------|
| `none` | 0 | No AI container. Cloud chat that does not need the sandbox may still work. **Without AI:** sandbox/tool execution, scripted skills, sandboxed MCP servers, and local LLM/ASR/TTS/embeddings/image gen will not work. |
| `slim` | ~4.3 GB | Sandbox for all providers, skills with script dependencies, and local sandboxed MCP servers — **no** large local model runtime. |
| `cpu` | ~8.2 GB | Sandbox + local CPU model runtime. |
| `cuda13` | ~14 GB | Sandbox + NVIDIA CUDA 13 local runtime. |
| `rocm` | ~20 GB | Sandbox + AMD ROCm local runtime. |
| `vulkan` | ~8.5 GB | Sandbox + Vulkan local runtime. |

Image sizes do **not** include model weights downloaded later inside the AI container.

**Start slim, add local AI later:** run the launcher again with `--reconfigure` and change only the AI backend. `DB_LAYOUT` stays unchanged.

### Optional services

| Component | Approx. size | Without it |
|-----------|--------------|------------|
| DocLing | ~7.1 GB (CPU) / ~13.8 GB (with CUDA stack) | Document intelligence features will not work unless you configure Azure Document Intelligence in Settings. |
| DocumentServer | ~7.2 GB | In-app Office open/edit will not work. |
| PlantUML | ~0.7 GB | PlantUML generation/rendering will not work. |
| SearXNG | ~4.2 GB | Web search / browser-render features will not work. |

The wizard shows a **running total** of selected image sizes before pulls begin.

### Example selections

- Bundled core, no AI: `DB_LAYOUT=bundled`, `AI_BACKEND=none`
- Separate SQL + slim AI: `DB_LAYOUT=separate`, `AI_BACKEND=slim`
- Bundled core + CUDA later: keep `DB_LAYOUT=bundled`, reconfigure `AI_BACKEND` → `cuda13`

Saved state (`.installer_state.env`) includes `DB_LAYOUT`, `AI_BACKEND`, `COMPONENTS`, and `COMPOSE_FILES`.

## GPU driver requirements

Driver checks run **only** for the local GPU backends you select (`cuda13`, `rocm`, `vulkan`). `none`, `slim`, and `cpu` skip NVIDIA/ROCm minimum checks (except where noted for CPU).

**NVIDIA (`cuda13` backend)**

The launcher inspects the CUDA container image for its `NVIDIA_REQUIRE_CUDA`
label and validates your local driver against it. Fallback minimums if the
image can't be inspected:

- NVIDIA driver >= R580
- CUDA >= 13.0

If your driver is too old the launcher will abort with upgrade instructions.

**AMD (`rocm` backend)**

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
| Native Linux | `/dev/kfd` + `/dev/dri` (kernel fusion driver) | `devices`, `group_add` with host GIDs for `video`/`render` |
| Windows / Docker Desktop | ROCDXG via `/dev/dxg` (DXCore bridge) | `/dev/dxg`, `librocdxg` binds, `HSA_ENABLE_DXG_DETECTION=1`, `SYS_PTRACE`, `seccomp:unconfined` |

Full design notes: [`docs/rocm-container-runtime-and-wsl-setup.md`](../docs/rocm-container-runtime-and-wsl-setup.md).

### Native Linux

Prerequisites:

- ROCm installed on the host (`/dev/kfd` and `/dev/dri` exist).
- Host user in `video` and `render` groups (for non-root Docker), or run the daemon appropriately.

Launch:

```bash
./guideants.sh --reconfigure
# choose rocm when prompted for AI backend
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

**3. Confirm the GPU is visible in WSL:**

```powershell
wsl -d Ubuntu-24.04 sh -lc "HSA_ENABLE_DXG_DETECTION=1 rocminfo | grep -A2 gfx"
```

**4. Launch GuideAnts** with `rocm` selected in the wizard (or `--backend rocm --reconfigure`).

Use `--doctor` to run all checks read-only and print the exact
`docker compose ... up -d` command without starting anything.

### ROCm troubleshooting (Windows)

| Symptom | Likely cause |
|---------|----------------|
| `librocdxg not found` | ROCm not installed in a user WSL distro — run `install-rocm-wsl.sh` |
| `librocdxg.so ... Is a directory` | Do not bind-mount via `//wsl.localhost/...`; let the launcher stage to `volumes/rocm-wsl/lib/` |
| GPU not detected on Windows | Probes ran in `docker-desktop` instead of your user distro — install ROCm in Ubuntu (or similar) |
| `unable to find group render` | Expected on Docker Desktop — `group_add` is native-Linux-only in the generated override |

## Options

| Flag | What it does |
|------|--------------|
| `--doctor` | Run all checks, change nothing. Prints the compose command that would be used. |
| `--backend <none\|cpu\|cuda13\|rocm\|slim\|vulkan>` | Skip the interactive AI backend prompt. |
| `--compose <ghcr\|local>` | Use GHCR pre-built images (default) or local build images. |
| `--mount /path/to/folder` | Mount a host folder into a project on startup (requires browser login). |
| `--unmount` | Interactively remove a host folder mount (requires browser login). |
| `--reconfigure` | Re-prompt AI backend and optionals only. `DB_LAYOUT` remains fixed from first install. |
| `--install-rocm-wsl` | Install ROCm + ROCDXG in a user WSL distro (Windows only), then continue. |
| `--yes` / `-y` | Accept prompts automatically (bundled DB, slim AI, all optionals, auto-pull). |
| `--help` / `-h` | Show help text. |

## Host folder mounting

The `--mount` and `--unmount` flags let you bind-mount a folder from your host
machine into a GuideAnts project so that files are shared between your local
filesystem and the running containers.

**Mounting a folder:**

```bash
./guideants.sh --mount /path/to/your/folder
```

```powershell
./guideants.ps1 --mount C:/path/to/your/folder
```

1. The stack starts normally.
2. After health check passes, a CLI authentication session is created.
3. Your browser opens an authorization page — approve the request.
4. You select which project (and optionally which notebook) to mount into.
5. The folder is bind-mounted into running services (`guideants-webapi-ui`, and
   `guideants-ai` / `plantuml` only when those components are selected).
6. Affected services restart automatically.

**Unmounting:** use `--unmount` with the same auth flow.

Mount state is stored in `docker/docker-compose.host-mounts.generated.yml`
(auto-generated, do not edit manually). Helper scripts in `scripts/` read the
multi-file `COMPOSE_FILES` list from `.installer_state.env`.

## Compose modes

| Mode | Description |
|------|-------------|
| `ghcr` (default) | Pulls pre-built images from GitHub Container Registry. |
| `local` | Uses locally built images (for development). |

The launcher assembles `-f compose/base.yml -f compose/core-*.yml -f compose/ai-*.yml ...`
from your selections. Legacy monolith files (`docker-compose.ghcr-*.yml`) remain for
reference but are not used by the new wizard path.

For the `rocm` backend, the launcher also layers
`docker-compose.rocm-runtime.generated.yml` (auto-generated GPU wiring).

## Release image pins and updates

Published installer zips include `docker/images.env` with **immutable digest pins**
for the GuideAnts GHCR images that belong to that release. Compose loads
`.env` then `images.env` (later wins).

On each start in GHCR mode the launcher:

1. Pulls any **missing** pinned images.
2. Compares each local digest to the remote **update channel** (`:main` by default,
   from `GA_UPDATE_CHANNEL`).
3. If the channel moved, asks **Update now before starting?** (auto-yes with `--yes`).
4. On accept, pulls the channel tags and rewrites `images.env` pins to the new digests.

Dev checkouts without `images.env` keep using compose defaults (`:main`) and the same
detect/ask/update flow against those floating tags.

Generate pins locally (same script the release workflow runs):

```bash
./installer/scripts/generate-release-image-pins.sh v1.2.3 elumenotion main
```

## Stopping

```bash
./stop_guideants.sh
```

```powershell
./stop_guideants.ps1
```

Reads saved `COMPOSE_FILES` from `.installer_state.env` and runs `docker compose down`.

## Services

Containers are included only when selected:

| Container | When included |
|-----------|----------------|
| `guideants-webapi-ui` | Always (bundled or slim image depending on DB layout) |
| `mssql-express` | `DB_LAYOUT=separate` |
| `guideants-ai` | `AI_BACKEND` is not `none` |
| `docling-serve` | DocLing optional selected |
| `documentserver` | DocumentServer optional selected |
| `plantuml` | PlantUML optional selected |
| `searxng` | SearXNG optional selected |

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
├── .installer_state.env            # Saved selections (auto-generated)
├── README.md
├── scripts/
│   ├── installer-wizard.sh         # Shared wizard (bash)
│   ├── installer-wizard.ps1        # Shared wizard (PowerShell)
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
    ├── compose/                    # Fragment compose files (assembled by wizard)
    │   ├── base.yml
    │   ├── core-bundled.yml
    │   ├── core-separate.yml
    │   ├── ai-*.yml
    │   └── *.yml                   # optional service fragments
    ├── docker-compose.ghcr-*.yml   # Legacy monolith compose (compatibility)
    ├── docker-compose.host-mounts.generated.yml
    ├── docker-compose.rocm-runtime.generated.yml
    └── volumes/
        ├── content-files/
        ├── rocm-wsl/
        └── searxng/
```
