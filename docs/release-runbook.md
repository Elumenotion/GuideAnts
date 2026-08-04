# GuideAnts release runbook

Operator guide for cutting a GuideAnts release: publish GHCR images, pin digests into the installer zip, and ship a GitHub Release.

## Mental model

| Layer | What it is | Mutability |
|-------|------------|------------|
| Git tag / GitHub Release | Source + installer zip (`guideants-installer-<tag>.zip`) | Immutable once published |
| GHCR `:main` | Floating **update channel** used by installers to detect newer images | Moves as you publish |
| GHCR `:latest` / build tags | Convenience / build identity (`26215.1050`, etc.) | Build tags immutable; `latest` moves |
| `installer/docker/images.env` | Digest pins baked into the release zip | Immutable in the zip; rewritten locally if the user accepts an update |

Release packaging does **not** leave users on floating `:main`. The zip pins exact digests. On later launches, the installer compares those digests to `:main` and offers an update.

Related:

- Workflow: `.github/workflows/package-installer-release.yml`
- Pin generator: `installer/scripts/generate-release-image-pins.sh`
- Local AI/support push: `docker/push-ghcr-guideants-ai.ps1` (or `.sh`)
- Installer behavior: `installer/README.md` → “Release image pins and updates”

## Packages pinned per release

`generate-release-image-pins.sh` resolves digests for `:main` on:

| Env key | GHCR package |
|---------|--------------|
| `GA_WEBAPI_UI_MSSQL_GHCR_IMAGE` | `guideants-webapi-ui-mssql` |
| `GA_WEBAPI_UI_SLIM_GHCR_IMAGE` | `guideants-webapi-ui-slim` |
| `GA_MSSQL_IMAGE` | `mssql2025-express-fts` |
| `GA_AI_SLIM_GHCR_IMAGE` | `guideants-ai-slim` |
| `GA_AI_CPU_GHCR_IMAGE` | `guideants-ai-cpu` |
| `GA_AI_CUDA_GHCR_IMAGE` | `guideants-ai-cuda13` |
| `GA_AI_ROCM_GHCR_IMAGE` | `guideants-ai-rocm` |
| `GA_AI_VULKAN_GHCR_IMAGE` | `guideants-ai-vulkan` |
| `GA_PLANTUML_GHCR_IMAGE` | `guideants-plantuml` |
| `GA_SEARXNG_GHCR_IMAGE` | `guideants-searxng` |

Third-party images (DocLing, DocumentServer) stay on their compose defaults / version pins and are not part of this pin file.

## Prerequisites

- Write access to the GitHub repo and GHCR (`ghcr.io/<owner>/…`)
- Docker + Buildx on the machine used for local AI/support pushes
- `gh` CLI authenticated (for creating the release), or use the GitHub UI
- All images you intend to ship already published to `:main` **before** you publish the GitHub Release (the pin job reads registry metadata at publish time)

## Release checklist

### 1. Choose the version tag

Use a clear tag, for example `v1.2.3`. That string becomes:

- the Git tag / GitHub Release name
- `GA_RELEASE_TAG` inside `images.env`
- the installer asset name `guideants-installer-v1.2.3.zip`
- optional GHCR tag when you pass `-ReleaseTag` to the push script

### 2. Land the code on `main`

Merge everything that belongs in this release. Image publish workflows and the installer zip both assume the tag points at the commit you want customers to run.

### 3. Publish / refresh GHCR `:main`

Ensure every package in the pin table above points `:main` at the builds you want locked.

**Web API images (CI):**

| Package | Workflow |
|---------|----------|
| `guideants-webapi-ui-slim` | `.github/workflows/publish-slim-image.yml` |
| `guideants-webapi-ui-mssql` | `.github/workflows/publish-mssql-image.yml` |

Run via `workflow_dispatch`, or push to `main` on the paths those workflows watch. Confirm both jobs finished green.

**AI + support images (local push, typical operator path):**

```powershell
cd docker
.\push-ghcr-guideants-ai.ps1 -ReleaseTag v1.2.3
```

```bash
cd docker
./push-ghcr-guideants-ai.sh --release-tag v1.2.3
```

This pushes each AI variant plus PlantUML, MSSQL FTS, and SearXNG with:

- immutable build tag (for example `26215.1050`)
- `:main` (update channel)
- `:latest`
- optional `:v1.2.3` when `-ReleaseTag` / `--release-tag` is set

**AI images (CI alternative):** `.github/workflows/publish-guideants-ai-images.yml` (`workflow_dispatch`, variant filter).

**Verify `:main` digests look right:**

```bash
docker buildx imagetools inspect ghcr.io/elumenotion/guideants-webapi-ui-slim:main
docker buildx imagetools inspect ghcr.io/elumenotion/guideants-ai-cpu:main
# …repeat for packages you care about
```

Optional dry-run of the pin file (does not publish anything):

```bash
./installer/scripts/generate-release-image-pins.sh v1.2.3 elumenotion main
# writes installer/docker/images.env (gitignored); review then delete if you do not want it locally
```

### 4. Create and publish the GitHub Release

From an updated `main` (or the release commit):

```bash
git tag -a v1.2.3 -m "GuideAnts v1.2.3"
git push origin v1.2.3
gh release create v1.2.3 --title "GuideAnts v1.2.3" --notes-file path/to/notes.md
```

Or create the release in the GitHub UI from that tag and **publish** it (draft releases do not trigger packaging).

### 5. Wait for `Package Installer Release Asset`

On `release: published`, `.github/workflows/package-installer-release.yml`:

1. Checks out the release tag
2. Runs `generate-release-image-pins.sh <tag> <owner> main`
3. Zips `installer/` **including** `docker/images.env`
4. Uploads `guideants-installer-<tag>.zip` to the release (`--clobber`)

Confirm:

- workflow is green
- release assets list includes `guideants-installer-v1.2.3.zip`
- unzip listing contains `docker/images.env`

```bash
gh release download v1.2.3 -p 'guideants-installer-*.zip'
unzip -l guideants-installer-v1.2.3.zip | grep images.env
unzip -p guideants-installer-v1.2.3.zip docker/images.env | head
```

### 6. Smoke the installer zip

On a clean machine (or clean Docker image cache):

1. Unzip the asset
2. Run `./guideants.ps1` or `./guideants.sh` (use `--yes` for non-interactive)
3. Confirm first pull uses digest refs from `images.env`
4. Confirm health at `http://localhost:5107/`

Optional update-path check (after you know `:main` has moved past the release pins):

1. Publish a newer build to `:main`
2. Rerun the launcher
3. Expect “Updates available… Update now before starting?”
4. Accept and confirm `images.env` digests were rewritten

## What customers experience

| Event | Behavior |
|-------|----------|
| First install from release zip | Pull pinned digests; start stack |
| Relaunch, `:main` unchanged | No pull; reuse local digests |
| Relaunch, `:main` moved | Prompt to update (default Yes); `--yes` auto-accepts |
| Decline update | Keep release pins; start with current local images |
| Accept update | Pull `:main`, rewrite local `images.env` pins, start |

Volumes (DB, content, models) persist across image updates.

## Failure modes

| Symptom | Likely cause | Fix |
|---------|--------------|-----|
| Release workflow fails resolving a digest | Package missing or private on GHCR; `:main` not published yet | Publish that package to `:main`, re-run workflow or re-publish release asset |
| Zip missing `images.env` | Pin step failed or zip excludes the file | Check workflow logs; do not ship the release until the pin file is present |
| Installer pulls wrong AI backend image | Old zip / wrong pin keys | Confirm compose uses `GA_AI_*_GHCR_IMAGE` per backend; regenerate pins |
| Update never offered | Comparing pin to itself, or channel unreachable | Confirm `GA_UPDATE_CHANNEL=main` and registry inspect works for `:main` |
| Vulkan pull fails | Unpublished / unavailable Vulkan package | Build locally and use `--compose local`, or choose another backend |

Re-running the packaging workflow after fixing `:main` is enough if the GitHub Release already exists: re-run the workflow for that release tag, then confirm the asset was `--clobber` replaced.

## Do / don’t

**Do**

- Publish all required `:main` images **before** publishing the GitHub Release
- Pass `-ReleaseTag` when pushing local AI/support images so GHCR has a human tag matching the release
- Smoke the downloaded zip, not only a git checkout of `installer/`

**Don’t**

- Expect `git archive` of `installer/` alone to include pins (`images.env` is generated at release time and gitignored)
- Move or retag an already-shipped digest under a customer’s pin (pins are digests; retagging `:main` is fine and is how updates are offered)
- Commit a local `installer/docker/images.env` from a dry-run into `main` unless you intentionally want the tree pinned
