# GuideAnts Azure Deploy — Acceptance Evidence

Captured during Phase 4 / final acceptance.

---

## Baseline (pre-change)

### Server build

```text
cd src/server && dotnet build GuideAntsApi.sln
Result: pass — 0 errors, 14 warnings
Date: 2026-07-23
```

### Server tests

```text
cd src/server && dotnet test GuideAntsApi.sln
Result: pass — 2460 passed, 7 skipped, 0 failed
  GuideAntsApi.Tests: 2133
  ScriptExecutionAgent.Tests: 71 (7 skipped)
  GuideAntsApi.IntegrationTests: 256
Date: 2026-07-23
```

---

## Phase gates

### Phase 1 — Bicep infrastructure

```text
az bicep build --file deploy/azure/main.bicep
Result: pass (no errors)

az deployment sub create --what-if ...
Result: not run (requires user-approved test subscription deploy)
```

Validated:
- [x] `main.bicep` compiles
- [x] No container app resources in Phase 1 template
- [x] SQL database name default: `guideants`
- [x] Storage shares: contentfiles, searxng-config, searxng-data, script-agent-state

### Phase 2 — Container apps

```text
az bicep build --file deploy/azure/apps.bicep
Result: pass (no errors)

az containerapp list -g rg-guideants-dev -o table
Result: pending (requires live deploy)
```

Validated in template:
- [x] 6 apps defined: guideants-webapi-ui, guideants-ai, docling-serve, plantuml, searxng, documentserver
- [x] Only guideants-webapi-ui has external ingress
- [x] Image: guideants-webapi-ui-slim (not webapi-ui-mssql)
- [x] DocumentServer enabled by default
- [x] No AzureAd__* env vars

### Phase 3 — Full deploy script

```text
./deploy/azure/deploy.ps1 -EnvironmentName dev -AppNamePrefix guideants -ImageTag main
Result: pending (requires test subscription + SqlAdminPassword)
```

Script inventory:
- [x] deploy.ps1 / deploy.sh
- [x] scripts/generate-secrets.ps1 / .sh
- [x] scripts/upload-searxng-config.ps1
- [x] manage.ps1

---

## Deploy gate

### All apps Running

| App | Status | Revision |
|-----|--------|----------|
| guideants-webapi-ui | pending | |
| guideants-ai | pending | |
| docling-serve | pending | |
| plantuml | pending | |
| searxng | pending | |
| documentserver | pending | |

### Web UI reachable

```text
curl -sI https://<fqdn>/
Result: pending (requires live deploy)
```

### Migrations

```text
dotnet ef database update ...
Result: pending (requires live deploy)
```

### Custom domain (if tested)

```text
Domain: not tested (documented in deploy/azure/README.md)
ALLOWED_ORIGINS: set via -CustomDomain param in Bicep
DocumentServer__ApiBaseUrl: set via -CustomDomain param in Bicep
```

---

## Compose parity

Source: `docker/docker-compose.ghcr-slim.yml`

| Compose env / setting | Azure equivalent | Status |
|---------------------|------------------|--------|
| `GA_AI_SLIM_GHCR_IMAGE` | `ghcr.io/{owner}/guideants-ai-slim:{tag}` | mapped |
| `DOCLING_SERVE_*` | docling-serve container env block | mapped |
| `GA_DOCUMENTSERVER_IMAGE` | `ghcr.io/euro-office/documentserver:latest` | mapped |
| `GA_WEBAPI_UI_MSSQL_GHCR_IMAGE` | `guideants-webapi-ui-slim` + Azure SQL | replaced |
| `MSSQL_*`, `ACCEPT_EULA` | Azure SQL S2 | intentional omission |
| `ASPNETCORE_ENVIRONMENT=Development` | `Production` | intentional change |
| `API_RUNTIME_CONTEXT` | `azure-slim` | mapped |
| `ALLOWED_ORIGINS` | `*` or `https://{customDomain}` | mapped |
| `FileStorage__Path` | `/app/ContentFiles` + Azure Files | mapped |
| `SearXngSearch__BaseUrl` | `http://searxng` | mapped |
| `LocalServiceHosts__*Url=127.0.0.1:9` | same | mapped |
| `LocalServiceHosts__MediaBaseUrl` | `http://guideants-ai` | mapped |
| `LocalServiceHosts__DocumentIntelligenceBaseUrl` | `http://docling-serve` | mapped |
| `DocumentServer__*` | KV secrets + env | mapped |
| `ScriptExecution__*` | KV secrets | mapped |
| `ServiceRouting__Containers__*` | internal ACA URLs | mapped |
| `SettingsSecrets__*` | KV `settings-secrets-key-azure-deploy` | mapped |
| `Jwt__SigningKey` | KV `jwt-signing-key` | mapped |
| `HF_TOKEN` | not set (optional, UI settings) | intentional omission |
| `GA_SCRIPT_AGENT_*` | KV script tokens | mapped |
| `FORCE_OWNERSHIP` (searxng) | same | mapped |
| `PLANTUML_LIMIT_SIZE` | same | mapped |

Mapped: 28/30 env vars; 2 intentional omissions (MSSQL block, HF_TOKEN).

---

## Security audit

```text
grep -ri "sk_live|password|apikey" deploy/azure/scripts deploy/azure/*.ps1
Result: clean — only parameter references, no hardcoded secrets
```

---

## Post-deploy consumer smoke (manual)

```text
1. Open https://<fqdn>/
2. Register first user → Admin role
3. Settings → Connections → add provider
4. Create project + notebook + chat turn
Result: pending (requires live deploy)
```

---

## Files added

```text
deploy/azure/
  main.bicep
  apps.bicep
  parameters.example.json
  parameters-container-apps.example.json
  .gitignore
  README.md
  ARCHITECTURE.md
  deploy.ps1
  deploy.sh
  manage.ps1
  modules/
    core-infrastructure.bicep
    database.bicep
    storage.bicep
    key-vault.bicep
    container-apps-environment.bicep
    container-apps.bicep
  scripts/
    generate-secrets.ps1
    generate-secrets.sh
    upload-searxng-config.ps1

docs/setup-guide.md (cloud deploy link)
docs/azure-deploy-execution/STATUS.md (updated)
docs/azure-deploy-execution/acceptance-evidence.md (this file)
```
