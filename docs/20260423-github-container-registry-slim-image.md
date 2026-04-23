# GitHub Container Registry Slim Image

Last updated: 2026-04-23

## What this adds

This repo now has a first-pass GitHub publishing path for the slim container:

- Docker target: `runtime-slim`
- Local compose entrypoint: `docker/docker-compose.slim.yml`
- GitHub workflow: `.github/workflows/publish-slim-image.yml`
- Local Windows build command: `pwsh -File ./docker/build/build_webapi_ui.ps1 -Flavor Slim`

The published image name is:

- `ghcr.io/elumenotion/guideants-webapi-ui-slim`

## First-time GitHub process

1. Push these workflow changes to `main`.
2. Open GitHub for the `Elumenotion/GuideAnts` repository.
3. Go to **Actions**.
4. If your repository or organization restricts workflow token permissions, confirm Actions can write packages.
5. Run **Publish Slim Container Image** manually once with **Run workflow**.
6. After the run succeeds, open **Packages** on the repository or owner page.
7. Confirm the package `guideants-webapi-ui-slim` exists.

The workflow publishes these tag styles:

- `main`
- `latest` for `main`
- `sha-<commit>`
- pushed Git tag names

## How the workflow works

The workflow does four things:

1. Checks out the repo.
2. Builds `src/client/dist-browser` with `npm run browser:build:docker`.
3. Builds Docker target `runtime-slim` from `docker/build/webapi-ui/Dockerfile`.
4. Pushes the image to GitHub Container Registry (`ghcr.io`) using the workflow `GITHUB_TOKEN`.

## Local test loop

Build the slim image locally:

```powershell
pwsh -File .\docker\build\build_webapi_ui.ps1 -Flavor Slim -NoRecreate
```

Start the slim compose stack:

```powershell
docker compose -f .\docker\docker-compose.slim.yml up -d
```

Pull the published GitHub image explicitly:

```powershell
docker pull ghcr.io/elumenotion/guideants-webapi-ui-slim:main
```

Use the published image in slim compose:

```powershell
$env:GA_WEBAPI_UI_SLIM_IMAGE = 'ghcr.io/elumenotion/guideants-webapi-ui-slim:main'
docker compose -f .\docker\docker-compose.slim.yml up -d
```

If the package stays private, authenticate to `ghcr.io` before `docker pull`. If you later switch the package to public visibility, pulls can be simpler for local testing and onboarding.

## Current phase limitation

This is the Phase 1 image/publish path from `slim-api-container-proposal.md`, not the full cloud-only contract yet.

The image currently seeds placeholder loopback URLs for:

- `LlamaCpp__BaseUrl`
- `ServiceRouting__Containers__guideants-ai__BaseUrl`

That keeps startup validation satisfied until the optional-dependency contract work lands. The slim image can boot without `guideants-ai`, but local-runtime features are still expected to return unavailable or connection-failure behavior until Phase 2 code changes are completed.

## Runtime notes

- The slim image expects `MSSQL_SA_PASSWORD` at runtime.
- `docker/docker-compose.slim.yml` provides a local default for convenience; override it outside local dev.
- SQL Server data is persisted in the named volume `guideants_slim_mssql`.
- Content files are persisted in the named volume `guideants_slim_content_files`.
