# 0. Install Checklist

Install these on your dev machine before doing anything else. Most are cross-platform; where a tool needs a platform-specific link, all three are listed. Sections 1–6 below describe *why* each tool is needed and how it is wired into the codebase.

## Required for every lane

- **Git** — <https://git-scm.com/downloads>
- **Docker Desktop** (Windows / macOS) or **Docker Engine 24+ with the Compose plugin** (Linux)
  - Windows / macOS: <https://www.docker.com/products/docker-desktop/>
  - Linux Engine: <https://docs.docker.com/engine/install/>
  - Linux Compose plugin: <https://docs.docker.com/compose/install/linux/>
- **PowerShell 7+ (PowerShell Core, `pwsh`)** — required by every build/deploy helper in `docker/build/` and the npm `server:rebuild` / `db:reset` scripts. PowerShell Core is cross-platform and works on Windows, macOS, and Linux; this is **not** Windows-only.
  - Windows: <https://learn.microsoft.com/powershell/scripting/install/installing-powershell-on-windows>
  - macOS: <https://learn.microsoft.com/powershell/scripting/install/installing-powershell-on-macos>
  - Linux: <https://learn.microsoft.com/powershell/scripting/install/installing-powershell-on-linux>
- **`curl`** — pre-installed on Windows 10+, macOS, and most Linux distros. Used by the launchers to poll `http://localhost:5107/`.
- **Disk: ~60 GB free** — for Docker images and local model sets.

## Required for client dev (`src/client`)

- **Node.js 20.x or 22.x** (ships with `npm`)
  - All platforms: <https://nodejs.org/en/download>
- After cloning, install all JS dependencies in one step from the `package.json` folder:

  ```bash
  cd src/client
  npm install
  ```

  `npm install` is the single source of truth for client tooling. It installs everything pinned in `package.json` (React 19, Vite 6, TypeScript 5.8, Vitest 4, Electron 41, electron-builder, TailwindCSS, Knip, cross-env, etc.) — there is no separate install step for any of those tools.

## Required for server dev (`src/server`)

- **.NET 8 SDK** — <https://dotnet.microsoft.com/download/dotnet/8.0>
- **EF Core CLI tools** (only if running/creating migrations on the host):

  ```bash
  dotnet tool install --global dotnet-ef
  ```

NuGet packages restore automatically on `dotnet build` / `dotnet run`.

## Windows-only host requirement

- **WSL2** — Docker Desktop's default backend on Windows. Verify with `wsl --status` (the Windows launcher does this for you).
  - <https://learn.microsoft.com/windows/wsl/install>

## Optional accelerators

- **NVIDIA driver R580+** plus the **NVIDIA Container Toolkit** — enables the `cuda13` backend. The Windows launcher enforces driver major ≥ 580 via `nvidia-smi`.
  - Driver: <https://www.nvidia.com/Download/index.aspx>
  - Container Toolkit: <https://docs.nvidia.com/datacenter/cloud-native/container-toolkit/latest/install-guide.html>
- **AMD GPU + ROCm-capable driver** — enables the experimental `rocm` backend.
  - <https://rocm.docs.amd.com/projects/install-on-linux/en/latest/>
- With neither installed, the launcher auto-selects the `cpu` backend.

## Optional secrets

- **Hugging Face token** (read scope) — needed by the in-app wizard to download models. Configured inside the app at `Settings → Connections → HuggingFace`, **not** via environment variable.
  - Generate at <https://huggingface.co/settings/tokens>

## One-shot smoke check

From the repo root, after installing:

```bash
git --version
docker --version
docker compose version
pwsh -Version
node --version          # client dev only
npm --version           # client dev only
dotnet --list-sdks      # server dev only
```

Every command should print a version. If any fail, fix the install before moving on.

---

# 1. Cross-Cutting / Host Requirements

These are needed regardless of which lane you work in.

| Requirement | Used By | Notes |
|---|---|---|
| **Docker Desktop (Win/macOS) or Docker Engine 24+ with Compose plugin** | All lanes; mandatory for SQL, AI gateway, search, docling, plantuml | The root launcher (`start_windows.cmd`, `start_linux.sh`, `start_macos.sh`) validates `docker`, `docker compose`, and that the daemon is reachable. |
| **PowerShell 7+ (`pwsh`, PowerShell Core)** | All build/deploy helpers under `docker/build/` and the npm scripts `server:rebuild` / `db:reset` invoke `pwsh -File ...` | Cross-platform: Windows, macOS, and Linux. See §0 for install links per OS. |
| **WSL2** (Windows only) | Docker Desktop backend | `start_windows.cmd` runs `wsl --status` and warns if default version isn't 2. |
| **`curl`** | Health probing | Used by all launcher scripts to wait for `http://localhost:5107/`. |
| **Git** | All work | Submodule-free, but builds clone llama.cpp / stable-diffusion.cpp inside Docker. |
| **Disk: ~60 GB** | Local model sets and Docker images | Called out in `docs/setup-guide.md` §3. |

Optional accelerators:

- **NVIDIA driver R580+** (with NVIDIA Container Toolkit) → enables `cuda13` backend. `start_windows.cmd` enforces driver major ≥ 580 via `nvidia-smi`.
- **AMD GPU + ROCm-capable driver** → enables experimental `rocm` backend.
- Otherwise → `cpu` backend is selected automatically.

Optional secrets:

- **Hugging Face token** (read scope) — needed when using the in-app wizard to download models from HF. Configured in `Settings → Connections → HuggingFace`, *not* via env at request-time.

---

## 2. Client Lane — `src/client`

A React 19 + Vite app that targets both browser and Electron.

### Runtime tooling

| Tool | Version (from configs) | Source of truth |
|---|---|---|
| **Node.js** | Compatible with Vite 6 / Vitest 4 / Electron 41 (Node 20+ recommended) | `package.json` engines aren't pinned; `@types/node` is `^22.15.24` |
| **npm** | Bundled with Node | `package-lock.json` is committed |
| **TypeScript** | `^5.8.3` | dependency in `package.json` |
| **Vite** | `^6.3.5` | dev/build |
| **Vitest** | `^4.0.10` | tests, with `@vitest/coverage-v8` and `@vitest/ui` |
| **Electron** | `^41.2.0` (devDep) | desktop build only |
| **electron-builder** | `^26.8.1` | desktop packaging (NSIS x64 target) |
| **TailwindCSS** | `^3.4.1` (+ `postcss`, `autoprefixer`) | styling |
| **Knip** | `^5.0.0` | orphan/dependency analysis |
| **Cross-env** | `^7.0.3` | sets `ANALYZE=true` for bundle visualizer |

### Required env files

The client has three Vite modes; each requires an env file with `VITE_API_URL`:

```7:7:src/client/.env.development
VITE_API_URL=http://localhost:5106/api
```

```7:7:src/client/.env.production
VITE_API_URL=http://localhost:5106/api
```

```7:7:src/client/.env.docker
VITE_API_URL=/api
```

`vite.config.browser.ts` throws on startup if `VITE_API_URL` is missing or malformed — this is a hard requirement, no fallback.

### Notable npm scripts

```10:31:src/client/package.json
    "dev": "vite",
    "build": "tsc && vite build",
    "browser:dev": "vite --config vite.config.browser.ts",
    "browser:build:docker": "tsc --project tsconfig.browser.json && vite build --mode docker --config vite.config.browser.ts",
    "electron:dev": "node scripts/electron-dev.mjs",
    "electron:build": "npm run build && electron-builder",
    "docker:up": "cd ../server && docker compose up -d",
    "server:rebuild": "cd ../server && pwsh -File ./rebuild_server.ps1",
    "db:reset": "cd ../server && pwsh -File ./reset_dev_env.ps1",
    "test": "vitest",
```

Heads-up: `server:rebuild` and `db:reset` point at `../server/rebuild_server.ps1` and `../server/reset_dev_env.ps1`, which are not present in `src/server` in the current tree — those scripts are missing or stale references.

### Browser dev server expectations

`vite.config.browser.ts` proxies `/api`, `/chat`, `/sandbox` to whatever absolute URL is in `VITE_API_URL`. So a typical browser dev flow is: API on `:5106`, Vite dev server on `:5173`.

---

## 3. Server Lane — `src/server`

ASP.NET Core 8 solution with multiple projects.

### Required SDKs

| Tool | Version | Where pinned |
|---|---|---|
| **.NET SDK** | **8.0** | `GuideAntsApi.csproj` (`<TargetFramework>net8.0</TargetFramework>`) and the build Dockerfile (`mcr.microsoft.com/dotnet/sdk:8.0`) |
| **EF Core CLI** (`dotnet-ef`) | 9.0.11 design package | `Microsoft.EntityFrameworkCore.Design` pinned in csproj; needed for migrations |

### Solution structure (`GuideAntsApi.sln`)

- `GuideAntsApi` — main Web API host + serves built UI from `Ui:RootPath`
- `GuideAntsApi.DataModel` (+ tests) — EF Core models, `DbContext`, migrations
- `GuideAntsApi.BackgroundJobs` — extraction / transcription / indexing / embeddings / retention
- `GuideAnts.Usage` — usage & cost tracking
- `AntRunner.Chat` family (Abstractions, OpenAI, Anthropic, GoogleGemini, HuggingFace, LlamaCpp, OpenRouter, ToolCalling) — provider-routed chat runtime
- `HtmlAgilityPackExtensions` — HTML utilities
- `ScriptExecutionAgent` — staged into the AI image during build
- `GuideAntsApi.Tests`, `GuideAntsApi.IntegrationTests`

### Runtime dependencies (from API project)

- ASP.NET Core 8.0 hosting
- `Microsoft.AspNetCore.Authentication.JwtBearer` (first-party app-issued JWT auth)
- `Microsoft.AspNetCore.SpaServices.Extensions`
- `Swashbuckle.AspNetCore` 9.0.6 (Swagger)
- `Microsoft.CognitiveServices.Speech` 1.47.0 (Azure Speech SDK)
- `SixLabors.ImageSharp` 3.1.12
- `CliWrap` 3.10.0

### Required local config

The API ships *example* settings only; you must create the live files yourself:

- `src/server/GuideAntsApi/appsettings.json`
- `src/server/GuideAntsApi/appsettings.Development.json`

Minimum config keys needed for local dev:

- `ConnectionStrings:DefaultConnection` — SQL Server. Default points to `localhost,1434` which matches the compose-published port for `mssql-express`.
- `Jwt:Issuer`, `Jwt:Audience`, `Jwt:LifetimeMinutes`, `Jwt:SigningKey` — first-party auth token settings. Keep `SigningKey` out of source control (use `dotnet user-secrets` or environment variable `Jwt__SigningKey` for local host runs).
- `SettingsSecrets:ActiveKeyId` + `SettingsSecrets:Keys:<id>` — a base64-encoded 16/24/32-byte AES key. The container default `MDEyMzQ1Njc4OUFCQ0RFRjAxMjM0NTY3ODlBQkNERUY=` is `local-dev` only.
- `LocalServiceHosts:*` URLs — six keys pointing at the AI/docling containers (probed by the Settings → Infrastructure UI). Docling probes use `{DocumentIntelligenceBaseUrl}/version`.
- `DocumentIntelligence:Docling*` — per-conversion options forwarded to docling-serve multipart requests (OCR, tables, pipeline, enrichments). Optional `DocumentIntelligence:DoclingApiKey` must match `DOCLING_SERVE_API_KEY` when the server requires `X-Api-Key` (do not set an empty `DOCLING_SERVE_API_KEY` in compose — docling-serve v1.21+ rejects it).
- `LlamaCpp:BaseUrl` — `http://localhost:8110/llama-cpp` for direct host runs.
- `Ui:RootPath` — where the built browser UI lives (`./ui` in container, blank in dev when Vite serves it).
- `FileStorage:Path` — host or container path for project/notebook content files. Dev launchSettings uses `../../../docker/volumes/content-files`.
- `DocumentServer:*` — for DocumentServer integration:
  - `DocumentServer:Enabled` (true/false)
  - `DocumentServer:PublicUrl` (for local compose default: `http://localhost:8082`)
  - `DocumentServer:ApiBaseUrl` (host-API dev: `http://host.docker.internal:5106`)
  - `DocumentServer:JwtEnabled` (optional, default false)
  - `DocumentServer:JwtSecret` (required only when `DocumentServer:JwtEnabled=true`)
  - `DocumentServer:JwtHeader` / `DocumentServer:JwtInBody` (optional advanced settings)

JWT enablement rule (must match exactly):

- `GA_DOCUMENTSERVER_JWT_ENABLED=true` and `DOCUMENTSERVER_JWT_SECRET=<secret>` in Docker env
- `DocumentServer:JwtEnabled=true` and `DocumentServer:JwtSecret=<same secret>` in API config

### Launch profiles

```16:33:src/server/GuideAntsApi/Properties/launchSettings.json
    "http": {
      "applicationUrl": "http://localhost:5106",
      ...
    },
    "https": {
      "applicationUrl": "https://localhost:7096;http://localhost:5106",
```

So local API URL = `http://localhost:5106` (which is what the client `.env.development` expects).

### Database expectation

- **SQL Server 2025 Express** with **Full-Text Search** enabled.
- Easiest path: `docker compose -f docker/docker-compose.mssql.yml up -d` (binds host port 1434 → container 1433) and let the API run migrations on first boot.

### Bootstrap requirements

On first boot the API seeds `Resources/bootstrap/` data (guides, assistants, runtime profiles `qwen3_5`, `qwen3_6`, `gemma4`). Seeding is idempotent.

---

## 4. Docker Lane — `docker/`

This is the operator + integration build lane.

### Pre-requisites to *use* the stack

Already covered above: Docker, Compose plugin, optional GPU runtime, ~60 GB disk, an `HF_TOKEN` if you'll download models through the wizard, and `curl`.

### Pre-requisites to *build* the images locally

| Image | Build tool | Extra pre-requisites |
|---|---|---|
| `guideants-webapi-ui` (`docker/build/webapi-ui/Dockerfile`) | `build_webapi_ui.ps1` / `.sh` | Requires the client UI to be built first via `npm run browser:build:docker` (produces `src/client/dist-browser/`). Multi-stage uses `mcr.microsoft.com/dotnet/sdk:8.0`. |
| `guideants-ai` (`docker/build/guideants-ai/Dockerfile.{cpu,cuda,rocm,slim}`) | `build_guideants_ai.ps1` / `.sh` | Requires `dotnet publish` of `ScriptExecutionAgent` (so a host .NET 8 SDK is needed even though Dockerfiles also have an SDK stage) and BuildKit (`DOCKER_BUILDKIT=1`). CPU/CUDA/ROCm are full local AI variants; `slim` is the sandbox-oriented AI image for Python script execution without starting local model runtime services. The AI image also bakes the script-agent admin assets and `ga-script-exec` privacy wrapper. For cache export (`--cache-to`), use `desktop-linux` context and enable Docker Desktop containerd image store. |
| `mssql2025-express-fts` (`docker/build/mssql-fts/Dockerfile`) | `-All` switch on the AI build script | Standard Docker build. |
| `plantuml-1.2025.2` (`docker/build/Sandboxes/PlantUml/dockerfile`) | `-All` switch on the AI build script | Standard Docker build. |
| `guideants-searxng` | `docker compose build searxng` | Repo-root build context. |

### Windows line endings and Linux container entrypoints

If freshly built Linux containers fail at startup with:

`/usr/bin/env: 'bash\r': No such file or directory`

this indicates CRLF line endings in a shell entrypoint copied into the image.

Why this can be machine-specific:
- One Windows machine may have Git `core.autocrlf=true` while others do not.
- Without explicit attributes, `.sh` files can be checked out as CRLF on that machine.

Repo policy:
- `.gitattributes` enforces LF for shell scripts: `*.sh text eol=lf`.

Recovery:
1. Rebuild WebAPI+UI with no cache:
   - `pwsh .\docker\build\build_webapi_ui.ps1 -NoCache`
2. Recreate service:
   - `docker compose -f docker/docker-compose.cuda.yml up -d --no-deps --force-recreate guideants-webapi-ui`
3. Rebuild/recreate SearXNG:
   - `docker compose -f docker/docker-compose.cuda.yml build searxng`
   - `docker compose -f docker/docker-compose.cuda.yml up -d --no-deps --force-recreate searxng`

If SearXNG still appears stale, remove and rebuild:
- `docker rmi guideants-searxng:latest`
- rerun the SearXNG build.

The full AI builds are heavy: they compile local model runtime pieces such as `stable-diffusion.cpp` and include the full local AI service set. The `guideants-ai slim` build is intentionally for sandbox use: it starts `ScriptExecutionAgent` and the non-model media service, and it does not start llama, llama-admin, ASR, TTS, SD, or embeddings. BuildKit cache is mandatory for sane iteration (the script tags `guideants-ai-deps:<backend>-cache` for layer reuse).

Script execution package/config persistence is handled by `script_agent_admin_state`, mounted into `guideants-ai` at `/var/lib/guideants/script-agent-admin`. Use the admin API/state files for durable apt/pip changes; direct `apt-get install` or `pip install` inside a running container is lost when the container is recreated. Scoped venvs extend the baked `/opt/venv` runtime through `SCRIPT_EXECUTION_BASE_PYTHON_VENV`, so per-guide packages add to or override the image-provided packages instead of replacing them.

### Required env values for compose

The committed `docker/.env`:

```1:13:docker/.env
GA_WEBAPI_UI_IMAGE=guideants-webapi-ui:26132.1055
DOCLING_SERVE_MAX_SYNC_WAIT=600
DOCLING_SERVE_MAX_FILE_SIZE=524288000
DOCLING_SERVE_ENG_LOC_NUM_WORKERS=2
DOCLING_NUM_THREADS=4
# DOCLING_SERVE_API_KEY=
# GA_DOCLING_CUDA_VISIBLE_DEVICES=
GA_CONTENT_FILES_HOST_PATH=./volumes/content-files
GA_SEARXNG_CONFIG_HOST_PATH=./volumes/searxng/config
GA_SEARXNG_DATA_HOST_PATH=./volumes/searxng/data
GA_AI_CUDA_IMAGE=guideants-ai:cuda13-26132.1047
GA_AI_CPU_IMAGE=guideants-ai:cpu-26126.1012
GA_AI_ROCM_IMAGE=guideants-ai:rocm-26131.2226
GA_EMB_DEFAULT_MODEL_PATH=harrier-oss-v1-0.6b
GA_EMB_AUTO_LOAD_ON_STARTUP=1
GA_EMB_WARMUP_ON_LOAD=1
GA_DB_NAME=guideants-dev-major-refactor-20260415
GA_DOCUMENTSERVER_ENABLED=true
GA_DOCUMENTSERVER_JWT_ENABLED=false
```

These tags must exist locally for the `local` compose files. For `ghcr` compose files (the launcher default), the images are pulled from `ghcr.io/elumenotion/*` and these `GA_AI_*` tags are not strictly required.

Optional env passthroughs the compose files read: `HF_TOKEN`, `GA_SQL_SA_PASSWORD`, GPU layer counts, ASR/TTS/SD/EMB warmup flags. Defaults are fine for first boot.
For local debugging with API on the host and services in Docker, use `docker/.env.api-local-debug.example` as the baseline env file.

### Ports the stack publishes

| Port | Service |
|---|---|
| `5107` | `guideants-webapi-ui` (UI + API) |
| `1434` | `mssql-express` (host) → `1433` (container) |
| `8110` | `guideants-ai` (full variants: llama-cpp, ASR, TTS, SD, EMB, media, sandbox via nginx; slim variant: sandbox/media only) |
| `5001` | `docling-serve` |
| `8082` | `documentserver` (loopback-only) |
| `8111` | `plantuml` |
| `8091` | `searxng` (loopback-only) |

Conflicts with local dev API on `5106` and Vite dev server on `5173` — both are free of compose collisions.

---

## 5. Recommended Setup Paths

### 5a) "Just want to run the product"

1. Install Docker Desktop / WSL2 (Windows).
2. Run `start_windows.cmd` (or the `.sh` equivalent).
3. Wait for `http://localhost:5107/` — launcher opens browser.

The launcher auto-picks `cuda13`, `rocm`, or `cpu` and pulls GHCR images. No SDKs required.

### 5b) Client-only dev (UI work against dockerized API)

1. Docker stack running per 5a. For a smaller dependency set during UI work, run a split stack service subset such as `guideants-webapi-ui` plus `mssql-express` from `docker/docker-compose.cpu.yml`.
2. Node 20+ and `npm install` in `src/client`.
3. `npm run browser:dev` — connects to `http://localhost:5106/api` per `.env.development`. (Update that file or your stack to align ports if you map differently.)

### 5c) Full server dev (API on host, dependencies in Docker)

1. .NET 8 SDK on host.
2. PowerShell 7+ (PowerShell Core — works on Windows, macOS, and Linux).
3. Start dependencies only: `docker compose -f docker/docker-compose.mssql.yml up -d` (DB) and the AI services compose for whichever backend you need.
4. Create `appsettings.Development.json` from the example.
   - Ensure:
     - `DocumentServer:PublicUrl = http://localhost:8082`
     - `DocumentServer:ApiBaseUrl = http://host.docker.internal:5106`
     - `DocumentServer:JwtEnabled = false` (or true if explicitly enabling DocumentServer JWT mode)
     - if enabling JWT: `DocumentServer:JwtSecret` matches `DOCUMENTSERVER_JWT_SECRET`
5. `dotnet run --project src/server/GuideAntsApi` (or run from VS via `http` profile → `http://localhost:5106`).
6. Run client in `browser:dev` mode pointing at `:5106`.

### 5d) Image builder / docker contributor

1. Everything in 5c.
2. BuildKit enabled (`DOCKER_BUILDKIT=1` — the build scripts set this).
3. For CUDA AI image builds: NVIDIA container runtime + enough disk for multi-stage CUDA 13 images.
4. PowerShell scripts at `docker/build/build_guideants_ai.ps1` for AI backends and `docker/build/build_support_images.ps1` for MSSQL FTS, PlantUML, SearXNG, and WebAPI/UI.

---

## 6. Gaps / Things to Verify in Your Environment

A few items worth confirming explicitly before onboarding a new dev:

- **Missing scripts**: `src/client/package.json` references `../server/rebuild_server.ps1` and `../server/reset_dev_env.ps1`, but neither exists in `src/server/`. Either restore them or remove those npm scripts.
- **No pinned Node engine** in `src/client/package.json` — pin to avoid drift (Vite 6 + Vitest 4 + Electron 41 → Node 20.x or 22.x).
- **`appsettings.example.json` is not auto-copied** to `appsettings.json` — first-time devs must do this manually and replace the `SettingsSecrets` key.
- **`docker/.env` ships with stale-style local image tags** (`guideants-ai:cuda13-26132.1047` etc.) — irrelevant if you use the GHCR compose files but will fail `docker compose up` on the `local` compose files unless you build them first.
- **CUDA 13 needs NVIDIA R580+ drivers** — the Windows launcher enforces this; manual `docker compose` does not.
- **HF token must be set in UI** (`Settings → Connections → HuggingFace`); the API does *not* accept per-request token overrides per the setup guide.
- **Python**: there is no required host Python install. `src/python/pptx` runs inside the `ScriptExecutionAgent`/sandbox containers; Python 3.11 is baked into the `guideants-ai` images, including the sandbox-oriented `slim` AI variant. Script executions use per-`project + guide` venvs in the `script_agent_admin_state` volume, layered over the image's `/opt/venv` packages. Only install Python on the host if you specifically want to iterate on `src/python/pptx` outside Docker.

---

## Summary Matrix

| Lane | Mandatory | Optional |
|---|---|---|
| **Run only** | Docker + Compose, ~60 GB disk, curl, WSL2 (Win) | NVIDIA R580+ or AMD ROCm GPU, HF token |
| **Client dev** | Node 20+/22+, npm, `.env.*` files, a running API | Electron 41 (desktop), ANALYZE=true tooling |
| **Server dev** | .NET 8 SDK, PowerShell 7+ (cross-platform), `appsettings*.json`, SQL Server (containerized) | EF CLI tools for migrations, Azure Speech key (if testing Azure speech path), local/admin test accounts for role-gated endpoint checks |
| **Docker builds** | All of the above + BuildKit, GPU runtime for CUDA/ROCm image builds, dotnet publish for ScriptExecutionAgent | GHCR write access (only for publish workflows) |

---

## 7. System Guide (GuideAnts Guide)

The **System Guide** is a seeded, admin-managed in-app assistant exposed via the header flyout
(GuideAnts Guide button). It is **not** a normal user project.

| Topic | Detail |
|---|---|
| **What it is** | Two bootstrap guides (user + admin) in a hidden **system project**, each published with **App Identity** auth so signed-in GuideAnts users chat without API keys or webhooks. |
| **Visibility** | The system project is excluded from Home/Projects listings. Non-admins receive **404** on direct project URLs; admins reach it via **Settings → System Guides** (`/settings/system-guides`). |
| **Auth** | Published guides use `AuthMode = AppIdentity`. The flyout sends the same-host `GuideAnts.Auth` session cookie — **no token is minted or stored client-side**. |
| **Admin editing** | Admins open **Settings → System Guides** to edit guide instructions, publish settings (Auth tab is read-only App Identity), and workspace content. |
| **Seeding** | On startup, `GuideAntsSystemSeeder` creates one system project, two guides, two AppIdentity published rows, and persists IDs in the `GuideAntsSystem` settings section. Re-runs are idempotent. |

For implementation and acceptance details, see `docs/guideants-guide-implementation-plan.md` and
`docs/guideants-guide/STATUS.md`.
