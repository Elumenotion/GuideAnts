# Phase 2 — Container Apps

**Branch:** `feature/azure-deploy-slim`  
**Depends on:** Phase 1 `DONE`  
**Blocks:** Phase 3

---

## Mission

Deploy all **6** container apps for the azure-slim profile. Translate
`docker/docker-compose.ghcr-slim.yml` env wiring to ACA, using **`guideants-webapi-ui-slim`**
(not `webapi-ui-mssql`) with Azure SQL connection from Key Vault.

---

## Read first

- `docs/azure-deploy-execution/DECISIONS.md` A4–A9, A12
- `docker/docker-compose.ghcr-slim.yml` (source contract)
- `docker/.env` (DocumentServer image)
- Waterfall `modules/container-apps.bicep` (ingress, secrets, volume patterns)
- `docs/azure-deploy-execution/deploy-gate.md`

---

## Preconditions

- [ ] Phase 1 infra deployed to test subscription (or what-if validated)
- [ ] GHCR images pullable: `guideants-webapi-ui-slim:main`, `guideants-ai-slim:main`, etc.

---

## Guardrails

- **One external app:** `guideants-webapi-ui` only.
- **DocumentServer on by default** (`DocumentServer__Enabled=true`).
- **No Entra** env vars.
- **No ACR** — image refs are `ghcr.io/{owner}/...` and `quay.io/...`.
- Default `imageTag` param: **`main`**.
- `ASPNETCORE_ENVIRONMENT=Production` (not Development).
- Local AI URLs point to disabled stub (`http://127.0.0.1:9`) like slim compose.
- Script tokens: single Key Vault secret referenced by API, AI, and plantuml apps.

---

## Tasks

### 1. Create `deploy/azure/apps.bicep`

Resource-group scope. Reference existing resources from Phase 1 (same naming convention
as Waterfall `container-apps-deploy.bicep`).

Parameters:

| Param | Default | Notes |
|-------|---------|-------|
| `environmentName` | `dev` | |
| `appNamePrefix` | `guideants` | |
| `ghcrOwner` | `elumenotion` | Public org |
| `imageTag` | `main` | Aligns with compose |
| `customDomain` | `''` | See §3 |
| `documentServerEnabled` | `true` | A7 — on by default |

### 2. Image map (from compose + `.env`)

| Service | Image |
|---------|-------|
| webapi-ui | `ghcr.io/{owner}/guideants-webapi-ui-slim:{tag}` |
| guideants-ai | `ghcr.io/{owner}/guideants-ai-slim:{tag}` |
| docling | `quay.io/docling-project/docling-serve-cpu:v1.21.0` |
| plantuml | `ghcr.io/{owner}/guideants-plantuml:{tag}` |
| searxng | `ghcr.io/{owner}/guideants-searxng:{tag}` |
| documentserver | `ghcr.io/euro-office/documentserver:latest` |

### 3. Env wiring — `guideants-webapi-ui`

Translate from slim compose lines 94–131. Key mappings:

| Compose | ACA |
|---------|-----|
| `ConnectionStrings__DefaultConnection` | Key Vault `sql-connection-string` |
| `Jwt__SigningKey` | Key Vault `jwt-signing-key` |
| `SettingsSecrets__*` | Key Vault |
| `ALLOWED_ORIGINS` | `https://{customDomain}` or `*` if empty |
| `SearXngSearch__BaseUrl` | `http://searxng` (internal) |
| `LocalServiceHosts__DocumentIntelligenceBaseUrl` | `http://docling-serve:5001` |
| `LocalServiceHosts__MediaBaseUrl` | `http://guideants-ai` |
| `ServiceRouting__Containers__guideants-ai__BaseUrl` | `http://guideants-ai/sandbox` |
| `ServiceRouting__Containers__plantuml__BaseUrl` | `http://plantuml` |
| `DocumentServer__Enabled` | `true` |
| `DocumentServer__InternalUrl` | `http://documentserver` |
| `DocumentServer__ApiBaseUrl` | `https://{customDomain or webapi FQDN}` |
| `DocumentServer__JwtEnabled` | `true` in Azure profile |
| `ScriptExecution__*` | Key Vault tokens |
| `API_RUNTIME_CONTEXT` | `azure-slim` |

Remove: `MSSQL_*`, `ACCEPT_EULA`, embedded SQL vars.

### 4. Env wiring — sidecars

**guideants-ai:** FILE_STORAGE_ROOT, script tokens, admin state dir, volume mount for
`contentfiles` + `script-agent-state`.

**plantuml:** FILE_STORAGE_ROOT, script token, contentfiles mount.

**searxng:** config + data volume mounts; `FORCE_OWNERSHIP=true`.

**docling-serve:** copy tuning env from compose (workers, log level, boot load).

**documentserver:** JWT_ENABLED=true, JWT_SECRET from KV, ALLOW_PRIVATE_IP_ADDRESS=true.

### 5. Volume mounts (Azure Files)

Register storage env + mount per app:

| App | Shares |
|-----|--------|
| webapi-ui | contentfiles |
| guideants-ai | contentfiles, script-agent-state |
| plantuml | contentfiles |
| searxng | searxng-config, searxng-data |

### 6. Custom domain (`-CustomDomain`)

When non-empty:

- Bind custom domain on `guideants-webapi-ui` ingress (document cert steps if not automatable in Bicep v1).
- Set `ALLOWED_ORIGINS=https://{domain}`.
- Set `DocumentServer__ApiBaseUrl=https://{domain}` (or `https://api.{domain}` if consumer uses subdomain — document convention: **single domain** for v1).

When empty:

- Use default `https://guideants-webapi-ui.{defaultDomain}`.
- `ALLOWED_ORIGINS=*` acceptable for dev; document tightening for production.

### 7. Resources

All apps: `minReplicas: 1`, consumption profile.

Suggested CPU/memory starting points (tune during deploy-gate):

| App | CPU | Memory |
|-----|-----|--------|
| webapi-ui | 2 | 4Gi |
| guideants-ai | 1 | 2Gi |
| docling-serve | 2 | 4Gi |
| plantuml | 0.5 | 1Gi |
| searxng | 1 | 2Gi |
| documentserver | 2 | 4Gi |

---

## Files in scope

| Action | Path |
|--------|------|
| Add | `deploy/azure/apps.bicep` |
| Add | `deploy/azure/modules/container-apps.bicep` |
| Add | `deploy/azure/parameters-container-apps.example.json` |

---

## Self-verification

- [ ] Deploy apps against Phase 1 infra
- [ ] `az containerapp list` shows 6 apps Running
- [ ] deploy-gate §2.2 passes

---

## Definition of Done

- [ ] Phase 2 gate (orchestration §4.3) passes
- [ ] deploy-gate container startup green
- [ ] `STATUS.md` updated: Phase 2 → `DONE`

---

## Report-back

```text
PHASE 2 COMPLETE
- Apps deployed: 6/6 Running
- DocumentServer: enabled, healthy
- Custom domain test: <skipped | pass with domain X>
- Web UI FQDN: <url>
- Deviations: <none | list>
```
