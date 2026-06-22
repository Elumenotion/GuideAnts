# ScriptExecutionAgent Admin API Requirements And Plan

Status: implemented for admin runtime, scoped venvs, and API-owned per-run environment hydration  
Date: 2026-06-19  
Last updated: 2026-06-20

## Locked Requirements

1. Add an admin API for ScriptExecutionAgent configuration management.
2. The mechanism must be baked into the container image (not ad-hoc runtime patching).
3. Admin state files must be persisted in a Docker volume.
4. Startup must validate persisted admin state and apply it when needed.
5. Credential persistence must be API-owned; ScriptExecutionAgent receives only per-run environment values and does not persist credentials.
6. Admin API must be disabled by default for all images that embed ScriptExecutionAgent.
7. Admin API is enabled only for GuideAnts AI images via image definition.
8. Admin API auth must be separate from execution auth (separate token/header).
9. Python execution environment must be scoped by `project + guide`.
10. All notebooks in the same project that use the same guide share one Python venv.
11. Environment variables and secrets must exist at `project + guide/assistant` scope before the API resolves them into per-run execution environment values.

## Implementation Status (2026-06-20)

### Completed in current branch

1. Script execution payload now includes `GuideId` from API to ScriptExecutionAgent.
2. ScriptExecutionAgent validates optional `GuideId` and resolves runtime scope by `projectId + guideId`.
3. Python execution uses scoped venv path under scope root when available.
4. ScriptExecutionAgent accepts validated per-run environment values from the API and injects them into only the launched child process.
5. Admin API endpoints (`/admin/*`) are mapped only when `SCRIPT_EXECUTION_ADMIN_API_ENABLED=true`.
6. Admin API auth uses separate `X-Script-Agent-Admin-Token` / `SCRIPT_EXECUTION_ADMIN_TOKEN`.
7. Startup admin state initialization validates `requirements.txt` and `apt-packages.txt`, seeds missing defaults, and applies changed packages/requirements.
8. AI Dockerfiles copy baked admin assets and enable the admin API at image-definition level.
9. Compose variants mount a dedicated `script_agent_admin_state` volume for `guideants-ai` only.
10. Tests cover admin route disabled behavior, admin auth, requirement validation, per-run environment injection, and blocked inherited agent token leakage.
11. GuideAnts AI images include `ga-script-exec`, a native wrapper that sets `PR_SET_DUMPABLE=0` before launching script interpreters.
12. The GuideAnts API persists project-bounded guide/assistant environment variables, encrypts secret values at rest with the SettingsSecrets encryption mechanism, masks secret values on read, preserves masked secrets on edit, and injects resolved values per script invocation.
13. Scoped Python venvs extend the base image runtime venv (`/opt/venv` by default on Linux) instead of replacing baked image packages.

## Implemented Contract

### Feature Gates

- `SCRIPT_EXECUTION_ADMIN_API_ENABLED` (default: `false`)
- `SCRIPT_EXECUTION_ADMIN_TOKEN` (required when admin API enabled)
- `SCRIPT_EXECUTION_ADMIN_STATE_DIR` (default: `/var/lib/guideants/script-agent-admin`)
- `SCRIPT_EXECUTION_ADMIN_FAIL_OPEN` (default: `false`)
- `SCRIPT_EXECUTION_SCOPE_STATE_ROOT` (default: `FILE_STORAGE_ROOT/.guideants/script-execution`)
- `SCRIPT_EXECUTION_SCOPE_PYTHON_VENV_DIR` (default: `python-venv`)
- `SCRIPT_EXECUTION_PYTHON_BOOTSTRAP` (optional interpreter command for `-m venv`)
- `SCRIPT_EXECUTION_REQUIRE_SCOPED_VENV` (strict mode for scoped venv requirement)
- `SCRIPT_EXECUTION_BASE_PYTHON_VENV` (default on Linux: `/opt/venv`)
- `SCRIPT_EXECUTION_PRIVACY_WRAPPER` (default in AI image: `/usr/local/bin/ga-script-exec`)

### Persisted State (Volume-Backed)

- `requirements.txt`
- `apt-packages.txt`
- `applied-state.json` (hashes, timestamps, last apply result)
- Optional backups under `backups/`

### Project + Guide Scope Boundary

- Scope key: `projectId + guideId`
- Shared by notebooks that match both IDs
- Scoped venv packages are installed first, then the configured base image venv site-packages are available as fallback imports.
- Example scope tree:
  - `.../project-{projectId}/guide-{guideId}/python-venv`
  - `.../project-{projectId}/guide-{guideId}/requirements.txt`
  - `.../project-{projectId}/guide-{guideId}/applied-state.json`

### Admin API Surface (only when enabled)

- `GET /admin/health`
- `GET /admin/requirements`
- `PUT /admin/requirements`
- `GET /admin/apt-packages`
- `PUT /admin/apt-packages`
- `POST /admin/apply`

### Script Execution Request Scope Keys

- `ProjectId` (required)
- `NotebookId` (required)
- `GuideId` (required by current API caller; agent accepts optional for compatibility)
- `Environment` (optional dictionary of API-resolved per-run values)

### Per-Run Environment Contract

- The agent accepts up to 128 environment entries per `/execute` request.
- Each value may be at most 64 KiB.
- Names must match `[A-Za-z_][A-Za-z0-9_]*`.
- Reserved names and prefixes are rejected, including `PATH`, `HOME`, loader/preload keys, `PYTHONPATH`, `SCRIPT_EXECUTION_*`, and `GUIDEANTS_*`.
- The child process receives a curated environment rather than the agent/container environment.
- Injected values are visible to the launched script process but are not persisted by the agent.

### Environment And Credential Boundary

- ScriptExecutionAgent does not store, read, or write credential files.
- API-owned environment/credential persistence stores values at `(ProjectId, AssistantId)` scope. The same guide or assistant can have different values in different projects.
- Secret values are stored as encrypted `encv2::...` payloads using the same SettingsSecrets key ring used by application settings and OAuth token storage.
- Secret values are returned to clients as the masked sentinel `••••••••`; submitting that sentinel preserves the stored value instead of overwriting it.
- For script execution in a notebook, the API hydrates the notebook guide scope by merging the project-bounded guide configuration plus the project-bounded configurations for that guide's crew members, then passes those values to `/execute.environment` only for the run that needs them.
- The script agent validates names and limits again before launching the child process.

## Implementation Plan

### Phase 1: Agent Runtime Gate And Models

Status: complete

1. Update `src/server/ScriptExecutionAgent/Program.cs`:
   - Read admin feature gate env vars.
   - Map `/admin/*` routes only when `SCRIPT_EXECUTION_ADMIN_API_ENABLED=true`.
   - Return route-not-found behavior (`404`) when disabled.
2. Add strict admin auth middleware/helper:
   - Header: `X-Script-Agent-Admin-Token`
   - Enforced only for admin routes.
3. Keep `GuideId` in request contract and preserve `project + guide` runtime boundary behavior.
4. Ensure scoped Python venvs are overlays over the image-provided Python runtime, not replacements.

### Phase 2: Reconcile Engine (Baked Into Image)

Status: complete

1. Add image-baked admin assets under `docker/build/guideants-ai/script-agent-admin/`:
   - `reconcile.sh`
   - default seed files
2. Reconcile behavior:
   - Validate `requirements.txt` policy (blocked patterns, optional pinning rules).
   - Validate `apt-packages.txt` package names only.
   - Apply `apt-get install` only when apt package hash changes.
   - Apply `pip install -r` into `/opt/venv` only when requirements hash changes.
   - Persist apply result and hashes in `applied-state.json`.

### Phase 3: EntryPoint Startup Validation And Apply

Status: complete

1. In `docker/build/guideants-ai/entrypoint.sh` and `entrypoint.slim.sh`:
   - Seed defaults into state dir if missing.
   - Run reconcile startup flow before launching ScriptExecutionAgent.
   - Respect `SCRIPT_EXECUTION_ADMIN_FAIL_OPEN`.
2. Log startup status with redacted values.

### Phase 4: Volume Persistence

Status: complete

1. Add a dedicated state volume mount on `guideants-ai`:
   - `/var/lib/guideants/script-agent-admin`
2. Add corresponding volume declarations in compose variants.
3. Keep model volume (`ai_local_models`) separate from admin state volume.

### Phase 5: Enable Only In AI Image Definitions

Status: complete

1. In GuideAnts AI Dockerfiles only:
   - Set `ENV SCRIPT_EXECUTION_ADMIN_API_ENABLED=true`
   - Copy baked admin assets.
2. Do not set this `ENV` in non-AI images that include ScriptExecutionAgent (for example PlantUML/sandbox images), so admin API remains off by default there.

### Phase 6: Credentials Management

Status: implemented

1. Keep credential persistence outside the ScriptExecutionAgent container.
2. API resolves environment variables and secrets from the project-bounded notebook guide scope, including that guide's crew member configurations, and sends only per-run environment values to `/execute`.
3. Agent validates environment variable names, rejects reserved/dangerous keys, and limits count/value size.
4. Agent launches child processes with a curated environment rather than inheriting the agent/container environment.
5. GuideAnts AI images launch scripts through `ga-script-exec`, which sets `PR_SET_DUMPABLE=0` before `execvp`.
6. Compose drops `SYS_PTRACE` from the `guideants-ai` container.

### Phase 7: Testing

Status: complete for agent and compose validation

1. Agent tests:
   - `/admin/*` returns `404` when disabled.
   - `/admin/*` enforces admin token when enabled.
2. Startup/reconcile checks:
   - Entry point scripts validate with `bash -n`.
   - Requirements and apt package validation are covered by admin endpoint tests.
3. Execution environment tests:
   - Per-run values are visible to the script.
   - Agent/admin tokens are not inherited by the script.
   - Reserved environment keys are rejected.
4. Smoke package candidates for apply test:
   - apt: `jq`
   - pip: `humanize`

## Acceptance Criteria

1. Non-AI images with ScriptExecutionAgent do not expose `/admin/*` unless explicitly enabled.
2. GuideAnts AI images expose `/admin/*` only when admin token is configured.
3. Apt packages and Python requirements persist across `down/up` without `-v`.
4. Startup enforces validation and deterministic apply behavior.
5. Python execution for notebooks sharing the same `(projectId, guideId)` uses the same scoped venv while retaining imports from the base image venv.
6. Environment variables and secrets are resolved by the API from the project-bounded notebook guide scope, including crew member configurations, and sent only as per-run environment values.
7. Apt smoke package candidate: `jq`; pip smoke package candidate: `humanize`.

## Verificationfix 

1. `dotnet test src/server/ScriptExecutionAgent.Tests/ScriptExecutionAgent.Tests.csproj -c Release --nologo`
   - Passed: 54
   - Skipped: 3 (2 Linux-only mount identity tests; 1 local host lacks working `python -m venv`)
2. `dotnet test src/server/GuideAntsApi.Tests/GuideAntsApi.Tests.csproj -c Release --nologo --filter "FullyQualifiedName~NotebookDockerScriptServiceTests"`
   - Passed: 7
3. `docker compose config --quiet` passed for CPU, CUDA, ROCm, slim, GHCR CPU/CUDA/ROCm/slim, and API-only local CPU/CUDA compose variants.
4. `bash -n` passed for `docker/build/guideants-ai/script-agent-admin/reconcile.sh`, `entrypoint.sh`, and `entrypoint.slim.sh`.
5. `git diff --check` passed with line-ending warnings only.
