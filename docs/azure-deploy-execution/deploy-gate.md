# Deploy Gate — GuideAnts Azure Slim

Companion to `00-orchestration.md`. Run after **Phase 2** (container apps) and at
**final acceptance** (full script path including migrations).

This gate proves deploy automation works end-to-end: infrastructure provisions, images
pull from public GHCR, containers start, and the web UI is reachable. It does **not**
require functional AI provider configuration (that is post-deploy UI work).

---

## 1. Gate intent

Pass when all are true:

- Azure deployment completes without error.
- All **6** container apps reach `Running` provisioning state.
- `guideants-webapi-ui` external endpoint returns HTTP **200** (or **302** to UI).
- EF migrations applied successfully (Phase 3 gate).
- No container app revision stuck in `Failed` after 15 minutes.

---

## 2. Checks

### 2.1 Infrastructure deploy (Phase 1)

```powershell
cd deploy/azure
az deployment sub create `
  --location "East US 2" `
  --template-file main.bicep `
  --parameters @parameters.example.json `
  --parameters sqlAdminPassword="<generated>" `
  --what-if
```

- [ ] `what-if` succeeds with no blocking errors.
- [ ] Resource group `rg-{prefix}-{env}` created with SQL, storage, KV, CAE, VNet.

### 2.2 Container apps deploy (Phase 2)

```powershell
az deployment group create `
  --resource-group rg-guideants-dev `
  --template-file apps.bicep `
  --parameters @parameters-container-apps.example.json `
  --parameters customDomain="" imageTag="main"
```

- [ ] All apps provisioned:

| App | Expected state |
|-----|----------------|
| `guideants-webapi-ui` | Running, external ingress |
| `guideants-ai` | Running, internal |
| `docling-serve` | Running, internal |
| `plantuml` | Running, internal |
| `searxng` | Running, internal |
| `documentserver` | Running, internal |

```powershell
az containerapp list -g rg-guideants-dev -o table
az containerapp show -n guideants-webapi-ui -g rg-guideants-dev --query properties.runningStatus
```

### 2.3 Full script deploy (Phase 3)

```powershell
./deploy.ps1 -EnvironmentName dev -AppNamePrefix guideants -ImageTag main -CustomDomain ""
```

- [ ] `generate-secrets` ran (or secrets pre-exist in Key Vault).
- [ ] Migrations: `dotnet ef database update` exit code 0.
- [ ] SearXNG config uploaded to file share.
- [ ] Script prints application URL.

### 2.4 Reachability

```powershell
$fqdn = az containerapp show -n guideants-webapi-ui -g rg-guideants-dev `
  --query properties.configuration.ingress.fqdn -o tsv
curl -sI "https://$fqdn/" | Select-String "HTTP/"
```

- [ ] Response is 200 or 302 (not 502/503 after warm-up window).

### 2.5 Custom domain (when `-CustomDomain` set)

- [ ] ACA custom domain binding documented or automated.
- [ ] `ALLOWED_ORIGINS` includes `https://{CustomDomain}`.
- [ ] `DocumentServer__ApiBaseUrl` uses public HTTPS URL (not internal hostname).

### 2.6 Negative cases

- [ ] Deploy with invalid image tag fails clearly (image pull error surfaced).
- [ ] Deploy without generated secrets fails at Key Vault / app startup (no silent dev defaults).

---

## 3. Gate failure triage

| Symptom | Likely cause |
|---------|----------------|
| Image pull 401/404 | GHCR tag typo or package not public |
| SQL connection fail | MI client ID not in connection string; migration firewall |
| File share mount fail | SMB role assignment not propagated |
| docling CrashLoop | Insufficient memory; increase resources |
| documentserver unhealthy | JWT secret mismatch; increase start_period |
| searxng empty config | Seed upload skipped or wrong share mount path |

---

## 4. Evidence capture

Record commands and outputs in `acceptance-evidence.md` § Deploy gate.
