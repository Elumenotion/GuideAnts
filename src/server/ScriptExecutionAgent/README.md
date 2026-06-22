# ScriptExecutionAgent

A lightweight .NET HTTP server that can be injected into any Linux container to provide script execution capabilities via HTTP API.

## Overview

The ScriptExecutionAgent is a bolt-on component that:
- Runs on port 8081 inside any container
- Provides HTTP API for executing Python, Bash, and PowerShell scripts
- Uses the container's native interpreters
- Works with Azure Container Apps (no Docker exec dependency)

## API Endpoints

### POST /execute
Executes a script and returns the result.

**Request:**
```json
{
  "script": "print('Hello, World!')",
  "scriptType": "Python",
  "projectId": "11111111-1111-1111-1111-111111111111",
  "notebookId": "22222222-2222-2222-2222-222222222222",
  "guideId": "33333333-3333-3333-3333-333333333333",
  "workingDirectory": "/app/ContentFiles/my-project/notebooks/22222222-2222-2222-2222-222222222222/Output",
  "environment": {
    "MY_API_TOKEN": "per-run-secret-value"
  }
}
```

**Response:**
```json
{
  "standardOutput": "Hello, World!\n",
  "standardError": ""
}
```

### GET /health
Health check endpoint.

**Response:**
```
OK
```

### GET /files?directory={path}&projectId={guid}&notebookId={guid}
Lists files in a directory.

**Response:**
```json
["file1.py", "file2.txt", "output.csv"]
```

### Admin API
The admin API is disabled by default. Images that opt in expose it under `/admin/*` inside the agent, which is reachable as `/sandbox/admin/*` through the GuideAnts AI nginx gateway.

All admin requests require `X-Script-Agent-Admin-Token`.

- `GET /admin/health`
- `GET /admin/requirements`
- `PUT /admin/requirements`
- `GET /admin/apt-packages`
- `PUT /admin/apt-packages`
- `GET /admin/setup-status`
- `GET /admin/install-scripts`
- `PUT /admin/install-scripts`
- `POST /admin/apply`
- `GET /admin/apply/jobs/{jobId}`

Scoped `install-scripts.json` stores ordered setup scripts (`Python` or `Bash`) that run after pip requirements during apply and on replay. Each script's last status is recorded in `applied-state.json`.

`POST /admin/apply` runs synchronous preflight (requirements/apt validation plus pip/apt dry-run when installs are needed) and returns `400` on preflight failure. When preflight passes it returns `202 Accepted` with a `jobId` and starts background apply work detached from the HTTP request (default timeout: 60 minutes via `SCRIPT_EXECUTION_ADMIN_APPLY_TIMEOUT_MINUTES`). Poll `GET /admin/apply/jobs/{jobId}` for `queued`, `running`, `succeeded`, or `failed` status. Preflight timeout defaults to 120 seconds (`SCRIPT_EXECUTION_ADMIN_APPLY_PREFLIGHT_TIMEOUT_SECONDS`).

`GET`/`PUT /admin/requirements` and `POST /admin/apply` operate on global state unless both `projectId` and `guideId` query parameters are provided. Scoped requests affect the shared `project + guide` Python venv used by all matching notebooks.

## Execution Scope And Environment

Every execution request must include `ProjectId`, `NotebookId`, and `WorkingDirectory`. The GuideAnts API also sends `GuideId`; when omitted for compatibility, the agent falls back to `NotebookId` as the scope key.

Python runtime state is scoped by `project + guide`:

```text
{SCRIPT_EXECUTION_SCOPE_STATE_ROOT}/
  project-{projectId:N}/
    guide-{guideId:N}/
      python-venv/
      requirements.txt
      applied-state.json
```

All notebooks in the same project that use the same guide share that venv. Scoped requirements are applied into the scoped venv before Python execution. If a scoped `requirements.txt` does not exist, the agent uses the global admin `requirements.txt` as the fallback requirements source for that scope.

Scoped venvs extend the image-provided Python runtime instead of replacing it. By default on Linux, the agent links the scoped venv to `/opt/venv` site-packages with a `.pth` file. Packages installed into the scoped venv remain first on `sys.path`, and baked image packages remain available as a fallback. Set `SCRIPT_EXECUTION_BASE_PYTHON_VENV` to override the base runtime venv path or leave it unset on non-AI images.

The optional `environment` object on `/execute` is a per-run injection surface. Values are placed only into the launched script process environment; they are not persisted by the ScriptExecutionAgent and are not written to scope state.

Validation rules:

- At most 128 environment entries per request.
- Each value must be 64 KiB or smaller.
- Names must match `[A-Za-z_][A-Za-z0-9_]*`.
- Reserved runtime keys are rejected, including `PATH`, `HOME`, `PYTHONPATH`, loader/preload keys, `SCRIPT_EXECUTION_AGENT_TOKEN`, and `SCRIPT_EXECUTION_ADMIN_TOKEN`.
- Names starting with `SCRIPT_EXECUTION_` or `GUIDEANTS_` are rejected.

The child process receives a curated base environment:

- `PATH`, `HOME`, `LANG`, `LC_ALL`
- `GUIDEANTS_PROJECT_ID`, `GUIDEANTS_GUIDE_ID`
- `VIRTUAL_ENV` when the scoped venv exists
- selected runtime/cache variables required by GPU and model tooling, such as CUDA/ROCm visibility variables, Hugging Face/Torch cache paths, Playwright browser path, and certificate bundle variables

It does not inherit the full container or agent process environment.

## Credential Handling

Credential persistence is intentionally not owned by the ScriptExecutionAgent. The API tier is responsible for resolving any credentials for the current `project + guide` boundary and, when needed, passing them as `/execute.environment` values for that single run.

Current implementation status:

- The ScriptExecutionAgent supports secure per-run environment hydration.
- The GuideAnts API sends `GuideId` and has a `ResolveExecutionEnvironmentAsync(...)` integration point.
- No agent-side credential file or credential store exists.
- Until the API-owned credential service is wired into that integration point, no credentials are injected by default.

Scripts can read their own process environment, so any injected credential is available to the executed script. The hardening goal is to avoid persistent credential files and to reduce cross-process environment inspection from sibling scripts.

## Admin State Persistence

GuideAnts AI images bake the admin mechanism into the image and enable it with image-level `ENV SCRIPT_EXECUTION_ADMIN_API_ENABLED=true`. Other images that embed the agent remain disabled by default unless they explicitly opt in.

In the compose stacks, `guideants-ai` mounts `script_agent_admin_state` at:

```text
/var/lib/guideants/script-agent-admin
```

That volume stores:

- global `requirements.txt`
- global `apt-packages.txt`
- global `applied-state.json`
- scoped state under `scopes/project-{projectId:N}/guide-{guideId:N}/`

The volume survives container restart and normal `docker compose down` / `up`. It is removed by `docker compose down -v` or manual volume deletion.

## Container Integration

### Option 1: Multi-stage Dockerfile
```dockerfile
# Use ScriptExecutionAgent as base
FROM script-execution-agent AS agent

# Your existing container
FROM your-base-image
COPY --from=agent /app /app/script-agent

# Start both your app and the agent
CMD ["sh", "-c", "dotnet /app/script-agent/ScriptExecutionAgent.dll & your-app-command"]
```

### Option 2: Add to Existing Dockerfile
```dockerfile
FROM your-base-image

# Install .NET runtime
RUN apt-get update && apt-get install -y dotnet-runtime-8.0

# Copy ScriptExecutionAgent
COPY ScriptExecutionAgent/ /app/script-agent/

# Expose agent port
EXPOSE 8081

# Start both services
CMD ["sh", "-c", "dotnet /app/script-agent/ScriptExecutionAgent.dll & your-app-command"]
```

### Option 3: Sidecar Pattern
```yaml
# docker-compose.yml
services:
  your-app:
    image: your-app-image
    # ... your app config
    
  script-agent:
    image: script-execution-agent
    volumes:
      - ./ContentFiles:/app/ContentFiles
    ports:
      - "80:80"
```

## Environment Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `ASPNETCORE_URLS` | `http://+:80` | URL to listen on |
| `FILE_STORAGE_ROOT` | _(required)_ | Root path that bounds all script/listing file operations |
| `SCRIPT_EXECUTION_AGENT_TOKEN` | _(required when strict token mode is enabled)_ | Shared token expected in `X-Script-Agent-Token` |
| `SCRIPT_EXECUTION_REQUIRE_TOKEN` | `true` | Require `X-Script-Agent-Token` on `/execute` and `/files` |
| `SCRIPT_EXECUTION_ENABLE_IDENTITY_ISOLATION` | `true` | Enable Linux notebook identity + `setpriv` execution/listing |
| `SCRIPT_EXECUTION_ALLOW_OWNERSHIP_FALLBACK` | `false` in production (`true` in Development if not set) | Allow compatibility fallback if Linux ownership hardening fails |
| `SCRIPT_EXECUTION_SCOPE_STATE_ROOT` | `FILE_STORAGE_ROOT/.guideants/script-execution` | Root for project+guide scoped runtime state (venv + requirements apply state) |
| `SCRIPT_EXECUTION_SCOPE_PYTHON_VENV_DIR` | `python-venv` | Relative path (within scope root) for the Python virtual environment |
| `SCRIPT_EXECUTION_PYTHON_BOOTSTRAP` | _(unset)_ | Optional bootstrap interpreter command used for `python -m venv` |
| `SCRIPT_EXECUTION_REQUIRE_SCOPED_VENV` | `true` on Linux | Fail Python execution if the project+guide venv cannot be created/applied |
| `SCRIPT_EXECUTION_BASE_PYTHON_VENV` | `/opt/venv` on Linux | Base image venv whose site-packages are exposed as fallback imports inside scoped venvs |
| `SCRIPT_EXECUTION_PRIVACY_WRAPPER` | `/usr/local/bin/ga-script-exec` | Optional Linux wrapper that sets `PR_SET_DUMPABLE=0` before launching script interpreters |
| `SCRIPT_EXECUTION_ADMIN_API_ENABLED` | `false` | Enable `/admin/*` routes |
| `SCRIPT_EXECUTION_ADMIN_TOKEN` | _(required when admin API enabled)_ | Shared token expected in `X-Script-Agent-Admin-Token` |
| `SCRIPT_EXECUTION_ADMIN_STATE_DIR` | `/var/lib/guideants/script-agent-admin` on Linux | Volume-backed admin state root |
| `SCRIPT_EXECUTION_ADMIN_FAIL_OPEN` | `false` | Continue startup after admin reconcile failure |
| `SCRIPT_EXECUTION_ADMIN_APPLY_TIMEOUT_MINUTES` | `60` | Background apply job timeout |
| `SCRIPT_EXECUTION_ADMIN_APPLY_PREFLIGHT_TIMEOUT_SECONDS` | `120` | Synchronous preflight timeout for `POST /admin/apply` |

## Supported Script Types

- **Python**: Uses `python` command
- **Bash**: Uses `bash` command  
- **PowerShell**: Uses `pwsh` command

## Usage Example

```csharp
// In your main API
var scriptExecutionUrl = "http://guideants-ai";
var request = new
{
    Script = "print('Hello from Python!')",
    ScriptType = "Python",
    ProjectId = "11111111-1111-1111-1111-111111111111",
    NotebookId = "22222222-2222-2222-2222-222222222222",
    GuideId = "33333333-3333-3333-3333-333333333333",
    WorkingDirectory = "/app/ContentFiles/my-project/notebooks/22222222-2222-2222-2222-222222222222/Output",
    Environment = new Dictionary<string, string>
    {
        ["MY_API_TOKEN"] = "<per-run-value>"
    }
};

using var httpClient = new HttpClient();
httpClient.DefaultRequestHeaders.Add("X-Script-Agent-Token", "<shared-token>");
var json = JsonSerializer.Serialize(request);
var content = new StringContent(json, Encoding.UTF8, "application/json");

var response = await httpClient.PostAsync($"{scriptExecutionUrl}/execute", content);
var result = await JsonSerializer.DeserializeAsync<ScriptExecutionResult>(await response.Content.ReadAsStreamAsync());
```

## Building

```bash
# Build the agent image
docker build -t script-execution-agent .

# Or use the build script
./build-script-agent.ps1
```

## Testing

```bash
# Test the agent
./test-script-execution.ps1

# Manual testing
curl http://localhost/health
curl -X POST http://localhost/execute \
  -H "Content-Type: application/json" \
  -H "X-Script-Agent-Token: your-shared-token" \
  -d '{"script":"print(\"test\")","scriptType":"Python","projectId":"11111111-1111-1111-1111-111111111111","notebookId":"22222222-2222-2222-2222-222222222222","guideId":"33333333-3333-3333-3333-333333333333","workingDirectory":"/app/ContentFiles/my-project/notebooks/22222222-2222-2222-2222-222222222222/Output"}'
```

## Azure Container Apps

For Azure Container Apps deployment:

1. **Build and push the agent image** to your container registry
2. **Add the agent to your container app** using multi-stage Dockerfile
3. **Configure internal networking** between container apps
4. **Update your main API** to use HTTP instead of Docker exec

## Security Considerations

- The agent runs on internal port 80 and is intended for internal network use.
- `/execute` and `/files` require `X-Script-Agent-Token` when token enforcement is enabled.
- `/admin/*` is not mapped unless `SCRIPT_EXECUTION_ADMIN_API_ENABLED=true` and uses a separate `X-Script-Agent-Admin-Token`.
- `ProjectId` + `NotebookId` are mandatory and validated for every execution/listing request.
- Python runtime state is scoped to `ProjectId + GuideId` (falls back to notebook scope when `GuideId` is omitted for execution compatibility).
- Scoped Python venvs extend the configured base image venv (`/opt/venv` by default on Linux) rather than replacing baked image packages.
- Credentials are API-owned and may be supplied to `/execute` as per-run `environment` values; the agent does not persist credential files.
- Child process environments are allowlisted and do not inherit agent tokens or admin tokens.
- In GuideAnts AI images, script launches go through `ga-script-exec`, which sets `PR_SET_DUMPABLE=0` before exec to reduce `/proc/<pid>/environ` exposure between sibling processes.
- Compose drops `SYS_PTRACE` from `guideants-ai` to reduce debugger-style process inspection.
- `requirements.txt` is validated for blocked install sources/options before it is applied to a scoped venv.
- `apt-packages.txt` is validated as package names only; admin apply reconciles the managed set by removing packages no longer listed and installing listed packages via `apt-get`.
- Paths are canonicalized and rejected if they escape `FILE_STORAGE_ROOT` or notebook scope.
- Reparse-point (symlink/junction) pivots in the authorized path chain are rejected.
- On Linux, script/listing operations run under notebook-scoped low-privilege identities via `setpriv`.
- When a notebook has registered host-folder mounts (`.guideants/mounts.json`), execution automatically uses compatibility mode (no notebook identity isolation) to avoid host-mount permission drift.

## Troubleshooting

### Agent Not Starting
```bash
# Check if .NET runtime is installed
dotnet --version

# Check agent logs
docker logs your-container
```

### Script Execution Fails
```bash
# Check if interpreter is available
python --version
bash --version
pwsh --version

# Check working directory permissions
ls -la /app/ContentFiles/
```

### Network Issues
```bash
# Test connectivity
curl http://container-name:8081/health

# Check container networking
docker network ls
docker network inspect your-network
``` 
