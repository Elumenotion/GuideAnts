# GitHub Container Registry Web API Image Flavors

Last updated: 2026-04-24

## What this adds

This repo now has two published web application container flavors:

- `slim`
  Standalone API + UI container without bundled SQL Server.
- `mssql`
  API + UI container with embedded SQL Server Express + FTS.

Repo assets:

- Docker targets: `runtime-slim`, `runtime-mssql`
- Local compose entrypoints: `docker/docker-compose.slim.yml`, `docker/docker-compose.mssql.yml`
- GitHub workflows: `.github/workflows/publish-slim-image.yml`, `.github/workflows/publish-mssql-image.yml`
- Local Windows build commands:
  `pwsh -File ./docker/build/build_webapi_ui.ps1 -Flavor Slim`
  `pwsh -File ./docker/build/build_webapi_ui.ps1 -Flavor Mssql`

Published image names:

- `ghcr.io/elumenotion/guideants-webapi-ui-slim`
- `ghcr.io/elumenotion/guideants-webapi-ui-mssql`

## First-time GitHub process

1. Push these workflow changes to `main`.
2. Open GitHub for the `Elumenotion/GuideAnts` repository.
3. Go to **Actions**.
4. If your repository or organization restricts workflow token permissions, confirm Actions can write packages.
5. Run **Publish Slim Container Image** and/or **Publish MSSQL Container Image** manually once with **Run workflow**.
6. After the run succeeds, open **Packages** on the repository or owner page.
7. Confirm the expected package exists.

Each workflow publishes these tag styles:

- `main`
- `latest` for `main`
- `sha-<commit>`
- pushed Git tag names

## How the workflow works

Each workflow does four things:

1. Checks out the repo.
2. Builds `src/client/dist-browser` with `npm run browser:build:docker`.
3. Builds either Docker target `runtime-slim` or `runtime-mssql` from `docker/build/webapi-ui/Dockerfile`.
4. Pushes the image to GitHub Container Registry (`ghcr.io`) using the workflow `GITHUB_TOKEN`.

## Local test loop

Build the standalone slim image locally:

```powershell
pwsh -File .\docker\build\build_webapi_ui.ps1 -Flavor Slim -NoRecreate
```

Start the standalone slim compose stack:

```powershell
docker compose -f .\docker\docker-compose.slim.yml up -d
```

Build the bundled-SQL mssql image locally:

```powershell
pwsh -File .\docker\build\build_webapi_ui.ps1 -Flavor Mssql -NoRecreate
```

Start the bundled-SQL mssql compose stack:

```powershell
docker compose -f .\docker\docker-compose.mssql.yml up -d
```

Pull the published GitHub images explicitly:

```powershell
docker pull ghcr.io/elumenotion/guideants-webapi-ui-slim:main
docker pull ghcr.io/elumenotion/guideants-webapi-ui-mssql:main
```

Use the published image in standalone slim compose:

```powershell
$env:GA_WEBAPI_UI_SLIM_IMAGE = 'ghcr.io/elumenotion/guideants-webapi-ui-slim:main'
docker compose -f .\docker\docker-compose.slim.yml up -d
```

Use the published image in bundled-SQL mssql compose:

```powershell
$env:GA_WEBAPI_UI_MSSQL_IMAGE = 'ghcr.io/elumenotion/guideants-webapi-ui-mssql:main'
docker compose -f .\docker\docker-compose.mssql.yml up -d
```

If the package stays private, authenticate to `ghcr.io` before `docker pull`. If you later switch the package to public visibility, pulls can be simpler for local testing and onboarding.

## Current phase limitation

This is the Phase 1 image/publish path from `slim-api-container-proposal.md`, not the full cloud-only contract yet.

The image currently seeds placeholder loopback URLs for:

- `LlamaCpp__BaseUrl`
- `ServiceRouting__Containers__guideants-ai__BaseUrl`

That keeps startup validation satisfied until the optional-dependency contract work lands. The slim image can boot without `guideants-ai`, but local-runtime features are still expected to return unavailable or connection-failure behavior until Phase 2 code changes are completed.

## Runtime notes

- `slim` no longer bundles SQL Server. Provide `ConnectionStrings__DefaultConnection` or override `GA_SQL_CONNECTION_STRING` in `docker/docker-compose.slim.yml`.
- `docker/docker-compose.slim.yml` defaults to `host.docker.internal:1434`, which is intended for local Docker Desktop scenarios with an external SQL Server listening on that published port.
- `mssql` expects `MSSQL_SA_PASSWORD` at runtime and persists SQL state in `guideants_mssql_runtime_state`.
- Content files are persisted in `guideants_slim_content_files` for `slim` and `guideants_mssql_content_files` for `mssql`.
