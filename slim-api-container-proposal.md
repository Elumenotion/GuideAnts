# Slim API Container Proposal

Last updated: 2026-04-23

## Goal

Create a `slim` container variant for the main GuideAnts experience that is
self-contained for:

- SQL Server
- the ASP.NET API
- the bundled browser UI
- local file storage

Everything else should become optional:

- `guideants-ai`
- `docling-serve`
- `searxng`
- `plantuml`

The app should remain usable in three modes:

1. **Cloud only**: one container, no local AI/runtime sidecars.
2. **Mixed**: the slim container plus selected optional local services.
3. **Full local stack**: the current compose layout.

## Current state

The active Docker build path for the web app is:

- `docker/build/webapi-ui/Dockerfile`
- `docker/build/build_webapi_ui.ps1`

That image already bundles the API and browser UI into one runtime image.
`docker/build/api/Dockerfile` still exists, but it is not the build path the
current compose stack uses, so the `slim` variant should extend the
`webapi-ui` build, not revive the older `api` image.

Today the runtime is still split operationally:

- SQL Server runs in `mssql-express`
- the web app runs in `guideants-webapi-ui`
- local AI/runtime features run in `guideants-ai`
- document extraction runs in `docling-serve-*`
- web search/browser rendering runs in `searxng`
- diagram rendering runs in `plantuml`

There are also two important hard assumptions that work against a true
cloud-only/self-contained mode:

1. `docker/docker-compose.yml` makes `guideants-webapi-ui` depend on both
   `mssql-express` and `guideants-ai`.
2. `ServiceRoutingStartupValidator` currently requires both
   `LlamaCpp:BaseUrl` and `ServiceRouting:Containers:guideants-ai:BaseUrl`
   even if local runtime features are intentionally absent.

That means the docs already describe "cloud-only onboarding", but the
container/runtime contract still treats `guideants-ai` as quasi-required.

## Proposed shape

Introduce a second image flavor:

- `guideants-webapi-ui` = current full web app image
- `guideants-webapi-ui-slim` = web app image with embedded SQL Server Express + FTS

`guideants-webapi-ui-slim` should contain:

- SQL Server 2025 Express with FTS
- ASP.NET 8 runtime
- GuideAnts API publish output
- bundled browser UI
- `ffmpeg`
- a startup script that launches SQL Server, waits for readiness, then starts the API

It should not require these to exist at boot:

- `guideants-ai`
- `docling-serve`
- `searxng`
- `plantuml`

Those services can still be attached later by configuration and network
connectivity.

## Recommended build approach

Do this as a **new flavor of the active webapi-ui build**, not a new primary
build system.

### Dockerfile strategy

Preferred approach:

- keep `docker/build/webapi-ui/Dockerfile` as the shared build file
- add a second runtime target such as `runtime-slim`
- base `runtime-slim` on `mssql2025-express-fts` or the same upstream SQL
  Server Ubuntu image used by `docker/build/mssql-fts/Dockerfile`
- install the ASP.NET runtime and `ffmpeg` into that SQL-based image
- copy the existing API publish output and UI bundle into it

This keeps the API/UI publish logic identical across full and slim variants
and limits the new work to the runtime stage.

### Entrypoint strategy

Add a dedicated slim entrypoint script, for example:

- `docker/build/webapi-ui/entrypoint-slim.sh`

Responsibilities:

1. ensure SQL Server directories/permissions are ready
2. start `sqlservr`
3. wait until `sqlcmd` returns success
4. export `ConnectionStrings__DefaultConnection` pointing to `localhost,1433`
5. start `dotnet GuideAntsApi.dll`
6. forward shutdown signals to both processes

Use `tini` or an equivalent minimal init so process handling is predictable.

## Recommended compose strategy

Do not force the slim mode into the existing full-stack compose file as the
default path. Keep the full stack stable and add a new compose entrypoint for
the self-contained mode.

Recommended files:

- `docker/docker-compose.yml`
  Current full stack, kept intact.
- `docker/docker-compose.slim.yml`
  New self-contained main experience.
- optional later: `docker/docker-compose.addons.yml`
  Sidecars that can be layered onto slim when local services are wanted.

### Slim compose behavior

The slim compose file should start one primary service:

- `guideants-webapi-ui-slim`

Recommended mounts:

- one named SQL volume mounted at `/var/opt/mssql`
- one content-files bind mount or named volume mounted at `/app/ContentFiles`

Recommended ports:

- publish `5107:8080`
- do **not** publish SQL Server by default
- optionally allow an override to publish `1434:1433` for debugging/admin work

## Configuration contract changes

This is the most important application change. The slim image is only useful
if missing local sidecars means "feature unavailable" rather than "startup
failure".

### Make optional dependencies truly optional

Treat these config sections as optional:

- `LlamaCpp:BaseUrl`
- `ServiceRouting:Containers:guideants-ai:BaseUrl`
- `LocalServiceHosts:*`
- `SearXngSearch:BaseUrl`
- `BrowserRendering:BaseUrl`
- `ServiceRouting:Containers:plantuml:BaseUrl`

Expected behavior when missing:

- startup still succeeds
- the relevant feature surfaces as `Not configured`, `Unavailable`, or a
  stable runtime error
- settings pages disable or hide actions that require the missing dependency

### Specific code changes implied

1. Relax `ServiceRoutingStartupValidator`
   - only validate `guideants-ai`/`llama-cpp` URLs when those features are configured
   - keep path-shape validation (`/sandbox`, `/llama-cpp`) when values are present

2. Relax defaults in appsettings/bootstrap
   - stop relying on fake default local URLs to satisfy startup
   - let absence mean "this local capability is not installed"

3. Make web-search/browser-rendering clients degrade cleanly
   - nullable `BaseUrl` options
   - clear runtime errors instead of null-reference or connection-noise behavior

4. Make local-runtime endpoints explicit about disabled state
   - model inventory/load/download endpoints should return a stable disabled/unavailable response when no local runtime base URL is configured

5. Make the Settings UI reflect optional infrastructure
   - cloud-only users should not see local runtime failures as blockers
   - Overview/Infrastructure should distinguish `Not configured` from `Unreachable`

## What belongs in "main experience"

The slim container should optimize for the flows that still matter with only
cloud providers configured:

- app bootstrapping
- login/user/project/notebook flows
- chat against cloud models
- settings management
- file upload/storage
- DB-backed application settings
- migrations on first start

These should remain outside the slim default:

- local llama runtime
- local ASR/TTS/embeddings/image generation
- local docling
- searxng search/browser rendering
- plantuml execution
- any Docker-exec-dependent helper behavior

That means `docker.io` is a good candidate to remove from the slim runtime
image unless we confirm it is part of the default user journey.

`ffmpeg` should likely remain in slim because it supports content/media flows
that still matter even when the transcription provider is cloud-hosted.

## Rollout plan

### Phase 1: Image and compose

- add `runtime-slim` target and slim entrypoint
- add `build_webapi_ui.ps1 -Flavor Slim` or a dedicated slim build script
- publish `GA_WEBAPI_UI_SLIM_IMAGE`
- add `docker-compose.slim.yml`

### Phase 2: Optional-dependency contract

- relax startup validation
- make optional base URLs nullable
- return stable disabled-state API responses
- update Settings UI to treat missing local services as optional

### Phase 3: Mixed mode

- allow slim container to join the same Docker network as optional sidecars
- document how to attach `guideants-ai`, `docling-serve`, `searxng`, and `plantuml`
- verify cloud and local providers can coexist cleanly

### Phase 4: Hardening

- add a real API health endpoint for container health checks
- verify graceful shutdown of both `sqlservr` and `dotnet`
- load test first-boot migration path
- validate upgrade behavior with existing DB volume data

## Acceptance criteria

The proposal is complete when we can say all of the following are true:

1. `docker compose -f docker/docker-compose.slim.yml up -d` starts the main
   app with one primary container.
2. First boot creates the SQL catalog and applies migrations automatically.
3. The app is usable with only cloud providers configured.
4. Missing `guideants-ai`, `docling`, `searxng`, and `plantuml` do not block
   startup.
5. Attaching optional local containers later does not require rebuilding the
   slim image.
6. Settings clearly distinguishes `Not configured` from `Unreachable`.

## Recommendation

Build the slim experience around **`guideants-webapi-ui` as the active
application container** and treat SQL Server as the only dependency absorbed
into that image. Do not fold `guideants-ai` into slim, and do not make the
old `docker/build/api` Dockerfile the new center of gravity.

That gives us the architecture we want:

- one self-contained container for the main product experience
- optional local sidecars for enhanced/offline workflows
- clean cloud-only or mixed deployments without changing the app model
