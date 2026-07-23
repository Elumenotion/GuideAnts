# Phase 3 — Deploy Scripts

**Branch:** `feature/azure-deploy-slim`  
**Depends on:** Phase 2 `DONE`  
**Blocks:** Phase 4

---

## Mission

Ship cross-platform deploy automation: secret generation, orchestrated deploy, SQL
migrations, SearXNG config seed, and day-2 `manage.ps1`. **No committed secrets.**

Adapt Waterfall `deploy.ps1` flow (infra → apps → SQL user → migrations → KV connection
string → force revision → file upload) but remove ACR build and Entra steps.

---

## Read first

- `docs/azure-deploy-execution/DECISIONS.md` A8, A11
- Waterfall `src/server/Azure/deploy.ps1` (structure reference only)
- Waterfall `upload-files.ps1`, `manage.ps1`
- `docs/auth-flow.md` (JWT signing key requirements)
- `src/server/GuideAntsApi/appsettings.example.json` (`SettingsSecrets`, `Jwt`)

---

## Preconditions

- [ ] Phase 2 apps deploy and reach Running state with manually injected secrets (smoke)
- [ ] `dotnet ef` tool available or installable in deploy script

---

## Guardrails

- **No default secret parameter values** in `deploy.ps1` (unlike Waterfall).
- Secrets generated once, written to Key Vault; deploy reads from KV or secure prompt.
- `generate-secrets` must produce:
  - `Jwt__SigningKey` — ≥ 32 chars random
  - `SettingsSecrets__Keys__{keyId}` — base64 16/24/32-byte AES key
  - `ScriptExecution__AgentToken` + `AdminToken` — shared random strings
  - `DocumentServer__JwtSecret` — random string
- SQL admin password: prompt or `-SqlAdminPassword` param (never default).
- Migrations use SQL admin; runtime app uses MI connection string.

---

## Tasks

### 1. `deploy/azure/scripts/generate-secrets.ps1` + `.sh`

```text
Outputs (stdout or writes to Key Vault):
  jwt-signing-key
  settings-secrets-key-azure-deploy
  script-agent-token
  script-agent-admin-token
  documentserver-jwt-secret
```

Also write `SettingsSecrets__ActiveKeyId=azure-deploy` for app env.

### 2. `deploy/azure/deploy.ps1` + `deploy.sh`

Parameters (mirror Waterfall ergonomics):

| Param | Default | Purpose |
|-------|---------|---------|
| `EnvironmentName` | `dev` | |
| `Location` | `East US 2` | |
| `AppNamePrefix` | `guideants` | RG naming |
| `GhcrOwner` | `elumenotion` | Image org |
| `ImageTag` | `main` | Aligns with compose |
| `CustomDomain` | `""` | Public URL + CORS |
| `SqlAdminPassword` | *(required)* | Migrations only |
| `SubscriptionId` | current context | |
| `SkipMigrations` | false | |
| `OnlyInfra` | false | Phase 1 only |
| `OnlyApps` | false | Phase 2 only (infra must exist) |
| `SqlAadAdminObjectId` | `""` | Optional AAD SQL admin |

**Main() flow:**

```text
1. Test-Prerequisites (az, docker not required)
2. Generate-Secrets → Key Vault
3. Deploy-Infrastructure (main.bicep)
4. Deploy-ContainerApps (apps.bicep) with domain + image params
5. Set-SqlDatabase (AAD admin optional, MI user for app)
6. Apply-DatabaseMigrations (dotnet ef, admin connection)
7. Update-KeyVaultConnectionString (MI client ID)
8. Force-NewRevision-WebApiApp
9. Upload-SearXngConfig (from docker/volumes/searxng/config/)
10. Show-DeploymentSummary
```

### 3. Migration command

```powershell
dotnet ef database update `
  --project src/server/GuideAntsApi.DataModel/GuideAntsApi.DataModel.csproj `
  --startup-project src/server/GuideAntsApi/GuideAntsApi.csproj `
  --connection "<admin connection string>"
```

Add temporary SQL firewall rule for deployer IP (`Ensure-FirewallRuleForCurrentIP`).

### 4. `upload-searxng-config.ps1`

Upload `docker/volumes/searxng/config/*` to `searxng-config` share with retry logic
(learn from Waterfall `SMB_LOCK_TROUBLESHOOTING.md`).

### 5. `manage.ps1`

Port Waterfall operations: `scale`, `logs`, `status`, `restart`, `update` (image tag bump).

### 6. Consumer parameter files

- `parameters.example.json` — infra params, no secrets
- `parameters-container-apps.example.json` — apps params including `customDomain`, `imageTag`
- Document copy-to-`parameters.local.json` workflow in README

---

## Files in scope

| Action | Path |
|--------|------|
| Add | `deploy/azure/deploy.ps1` |
| Add | `deploy/azure/deploy.sh` |
| Add | `deploy/azure/scripts/generate-secrets.ps1` |
| Add | `deploy/azure/scripts/generate-secrets.sh` |
| Add | `deploy/azure/scripts/upload-searxng-config.ps1` |
| Add | `deploy/azure/manage.ps1` |

---

## Self-verification

```powershell
./deploy/azure/deploy.ps1 `
  -EnvironmentName dev `
  -AppNamePrefix guideants `
  -ImageTag main `
  -CustomDomain "" `
  -SqlAdminPassword "<generated>"
```

- [ ] Full flow completes
- [ ] Migrations applied to `guideants` database
- [ ] Web UI loads at ACA FQDN
- [ ] deploy-gate §2.3–2.4 passes

---

## Definition of Done

- [ ] Phase 3 gate (orchestration §4.4) passes
- [ ] `STATUS.md` updated: Phase 3 → `DONE`
- [ ] Grep confirms no hardcoded secrets in scripts

---

## Report-back

```text
PHASE 3 COMPLETE
- deploy.ps1 / deploy.sh: pass
- generate-secrets: pass
- Migrations: pass (database guideants)
- SearXNG seed: pass
- Summary URL: <fqdn>
- Deviations: <none | list>
```
