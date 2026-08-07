# GuideAnts Azure Deployment (azure-slim)

Deploy GuideAnts to **Azure Container Apps** with **Azure SQL** and public **GHCR** images.
This is the cloud equivalent of `docker/docker-compose.ghcr-slim.yml` — cloud AI via Settings,
no local llama/ASR/TTS/SD containers.

**Estimated deploy time:** 15–25 minutes (first run).

## Prerequisites

| Requirement | Notes |
|-------------|-------|
| Azure subscription | **Contributor** on the target resource group is sufficient for deploy |
| Empty resource group | **Must exist before deploy** — see [Resource group naming](#resource-group-naming). AZBuilder and similar roles usually cannot create RGs; ask an admin if needed. |
| [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli) | `az login` completed |
| PowerShell 7+ **or** Bash | Windows: `pwsh`; Linux/macOS: `bash` |
| `dotnet ef` global tool | Installed automatically by deploy script if missing |
| `openssl` (Bash path only) | For `generate-secrets.sh` |

**GHCR images must be public** (default org: `elumenotion`). No registry credentials are configured.

### Resource group naming

Bicep is resource-group scoped; the deploy script does **not** create the RG. It targets a fixed name derived from the same flags you pass to deploy:

```text
rg-{AppNamePrefix}-{EnvironmentName}
```

| Deploy flag | Default | Role in RG name |
|-------------|---------|-----------------|
| `-AppNamePrefix` / `--app-name-prefix` | `guideants` | Middle segment |
| `-EnvironmentName` / `--environment-name` | `dev` | Final segment |

Examples:

| Deploy command args | Required RG name |
|---------------------|------------------|
| (defaults) or `-EnvironmentName dev -AppNamePrefix guideants` | `rg-guideants-dev` |
| `-EnvironmentName staging` | `rg-guideants-staging` |
| `-AppNamePrefix myco -EnvironmentName prod` | `rg-myco-prod` |

Create that exact group (and use the same location you will pass as `-Location` / `--location`, default `East US 2`) before running deploy. If the names do not match, deploy exits with “resource group does not exist.”

## Quick start

```powershell
cd deploy/azure

# 1. Create the resource group — name must match AppNamePrefix + EnvironmentName below
#    Pattern: rg-{AppNamePrefix}-{EnvironmentName}
az group create --name rg-guideants-dev --location "East US 2"

# 2. Copy parameter files (optional — deploy script accepts CLI flags)
Copy-Item parameters.example.json parameters.local.json

# 3. Deploy (generates secrets, provisions infra + apps, runs migrations)
#    These two flags must match the RG created in step 1:
./deploy.ps1 `
  -EnvironmentName dev `
  -AppNamePrefix guideants `
  -ImageTag main `
  -CustomDomain "" `
  -SqlAdminPassword 'G4-Deploy!xK9mQ2vL'
```

SQL password rules: 8+ chars, upper/lower/number/symbol; must **not** contain the login name (`sqladmin`) or angle brackets `<` `>` (Windows `az.cmd` redirection).

Bash equivalent:

```bash
# RG name must match --app-name-prefix + --environment-name (defaults: guideants + dev)
az group create --name rg-guideants-dev --location "East US 2"
chmod +x deploy.sh scripts/*.sh
./deploy.sh --environment-name dev --app-name-prefix guideants --image-tag main --sql-admin-password 'G4-Deploy!xK9mQ2vL'
```

When complete, the script prints your application URL.

## Post-deploy

1. Open the URL → **Register** → first user becomes **Admin**.
2. **Settings → Connections** → add OpenAI, Azure OpenAI, Anthropic, or another provider.
3. Create a project and run a chat smoke test.
4. **DocumentServer** is enabled by default for in-app Office document editing.

## Parameters

| Parameter | Default | Purpose |
|-----------|---------|---------|
| `-EnvironmentName` | `dev` | Environment suffix (`rg-guideants-dev`) |
| `-Location` | `East US 2` | Azure region |
| `-AppNamePrefix` | `guideants` | Resource naming prefix |
| `-GhcrOwner` | `elumenotion` | GHCR organization for GuideAnts images |
| `-ImageTag` | `main` | Image tag (see below) |
| `-CustomDomain` | `""` | Public HTTPS domain (optional) |
| `-SqlAdminPassword` | *(required on first deploy)* | SQL admin for Phase 1 + migrations. On redeploy, omit to read `sql-admin-password` from Key Vault. Not used with `-OnlyApps`. |
| `-SkipMigrations` | false | Skip `dotnet ef database update` |
| `-OnlyInfra` | false | Phase 1 only (no container apps) |
| `-OnlyApps` | false | Phase 2 only (infra must already exist) |
| `-SqlAadAdminObjectId` | `""` | Optional AAD object ID for SQL admin |

Example files: `parameters.example.json`, `parameters-container-apps.example.json`.
Copy to `*.local.json` for local overrides (gitignored).

## Image tags

| Tag | When to use |
|-----|-------------|
| `main` | Default — matches compose `docker-compose.ghcr-slim.yml` |
| `latest` | Published from `main` branch releases |
| Semver (`v1.2.3`) | Pin to a GitHub Release |
| `sha-<commit>` | Pin to a specific commit build |

Images pulled (no local build):

| Service | Image |
|---------|-------|
| Web API + UI | `ghcr.io/{owner}/guideants-webapi-ui-slim:{tag}` |
| AI sandbox | `ghcr.io/{owner}/guideants-ai-slim:{tag}` |
| PlantUML | `ghcr.io/{owner}/guideants-plantuml:{tag}` |
| SearXNG | `ghcr.io/{owner}/guideants-searxng:{tag}` |
| Docling | `quay.io/docling-project/docling-serve-cpu:v1.29.0` |
| DocumentServer | `ghcr.io/euro-office/documentserver:latest` |

## Custom domain

1. Deploy with `-CustomDomain app.example.com`.
2. Create a **CNAME** record: `app.example.com` → ACA FQDN (printed in summary).
3. In Azure Portal → Container App `guideants-webapi-ui` → **Custom domains** → add domain and bind managed certificate.
4. Deploy sets `ALLOWED_ORIGINS=https://app.example.com` and `DocumentServer__ApiBaseUrl` to match.

Without a custom domain, the default ACA FQDN is used and `ALLOWED_ORIGINS=*` (tighten for production).

## Architecture

```
Internet → guideants-webapi-ui (external :8080)
              ├── guideants-ai (internal)
              ├── docling-serve (internal :5001)
              ├── plantuml (internal)
              ├── searxng (internal :8080)
              └── documentserver (internal :80)
Azure SQL (guideants) ← managed identity via Key Vault
Azure Files: contentfiles, searxng-config, searxng-data, script-agent-state
```

See [ARCHITECTURE.md](./ARCHITECTURE.md) for details.

## Day-2 operations

**Safe app-only update** (new image tag, no SQL/password/secret changes):

```powershell
./deploy.ps1 -OnlyApps -SkipMigrations -ImageTag main
```

This redeploys container apps only. It does **not** regenerate Key Vault secrets, reset the SQL admin password, run migrations, or rewrite `sql-connection-string`.

### Scoped venv repair

Scoped Python venvs live on the `script-agent-state` share. A venv created before the mount had `mfsymlinks` can never resolve its interpreter, so it has to be rebuilt.

`guideants-ai` handles this itself: on every start it checks each `python-venv` for a resolvable interpreter and `pyvenv.cfg`, deletes only the ones that fail, and leaves healthy venvs alone. Deleted venvs rebuild on next script run. If the mount has no symlink support it changes nothing rather than risk deleting good venvs.

Deploy is not involved and cannot fail because of it.

**Image-only bump** without Bicep (single apps):

```powershell
./manage.ps1 -Operation update -ImageTag v1.0.0
```

**Do not** run a full deploy (without `-OnlyApps`) against an existing environment unless you intend to reconcile infrastructure. Phase 1 reapplies the SQL admin password and bootstrap secrets from parameters — wrong `-SqlAdminPassword` breaks migrations and can lock out `sqladmin`.

```powershell
# Status of all apps
./manage.ps1 -Operation status

# Tail web API logs (live stream — not Log Analytics)
./manage.ps1 -Operation logs -AppName guideants-webapi-ui -Follow

# Bump image tag
./manage.ps1 -Operation update -ImageTag v1.0.0

# Restart all apps
./manage.ps1 -Operation restart
```

## Troubleshooting

| Symptom | Likely cause | Fix |
|---------|--------------|-----|
| Image pull 401/404 | Wrong tag or private GHCR package | Verify tag exists; confirm package is public |
| SQL connection fail after redeploy | Wrong `-SqlAdminPassword` on full deploy, or `sql-connection-string` missing `User ID={clientId}` | Use `-OnlyApps -SkipMigrations` for routine updates. For full deploy, pass the **original** password or omit it so deploy reads `sql-admin-password` from Key Vault. |
| Port 8080 already in use | `ASPNETCORE_URLS` set to `:8080` conflicts with nginx in `webapi-ui-slim` image | Use `ASPNETCORE_URLS=http://127.0.0.1:8081`; ACA ingress stays on 8080 |
| Key Vault `setSecret` Forbidden | Deployer not in vault access policies (re-run Phase 1 with deployer signed in) | Re-run full deploy; confirm `az ad signed-in-user show` matches deployer passed to Bicep |
| File share mount fail | Storage account key mount not ready | Wait 2–5 min; redeploy apps |
| docling CrashLoop | Insufficient memory | Increase CPU/memory in `modules/container-apps.bicep` |
| documentserver unhealthy | JWT mismatch | Ensure secrets generated once; force new revision |
| searxng empty | Config not seeded | Run `scripts/upload-searxng-config.ps1 -ResourceGroupName rg-guideants-dev` |
| Python venv / scoped execute fails (`Permission denied` on `lib64`) | `script-agent-state` SMB mount missing `mfsymlinks` | Re-run `deploy.ps1 -OnlyApps -SkipMigrations` so the mount picks up `mfsymlinks`. `guideants-ai` repairs unusable scoped venvs itself on next start — deploy does not touch the share. |
| First scoped venv very slow on cold share | Azure Files latency for `python -m venv` + pip | Expected; venv is durable on the share after first create |
| First request after idle is slow | Apps scaled to zero and/or SQL paused | Expected; wait for cold start + SQL resume, or `manage.ps1 -Operation scale -MinReplicas 1` |
| No historical logs in portal Logs blade | CAE log destination is null (not saved) | Use `manage.ps1 -Operation logs -Follow` (live stream); console is not ingested into LA |

## Cleanup

```bash
az group delete --name rg-guideants-dev --yes --no-wait
```

**Failed partial deploy:** if Phase 1 failed on an older template that used Key Vault RBAC role assignments, delete the resource group and redeploy with the current templates. Key Vault permission model cannot be switched from RBAC to access policies in place.

## Cost notes

Defaults favor **low cost at rest** (scale-to-zero + SQL auto-pause + no console log ingest):

| Resource | At rest (idle) | Notes |
|----------|----------------|-------|
| Azure SQL GP serverless | ~storage only after 15 min pause | Resumes on first connection (~tens of seconds) |
| 6 Container Apps (`minReplicas=0`) | ~$0 compute when scaled to zero | Cold starts on first request after idle |
| Log Analytics | ~$0 from ACA console | CAE logs destination is null (not saved); use live log stream |
| Azure Files + storage | ~$5–20/mo | Depends on content size |

**Active usage** bills ACA vCPU/memory while replicas run and SQL compute while the database is resumed. DocumentServer and Docling are heavy on cold start — first Office/doc request after idle can take a while.

Raise always-on capacity when needed:

```powershell
./manage.ps1 -Operation scale -MinReplicas 1 -MaxReplicas 3
```

## Security

- **No secrets in git.** `generate-secrets` creates random values stored in Key Vault.
- **No Entra app login** — first-party JWT only; register first user as Admin.
- **SQL admin password** is for migrations only; the app uses managed identity at runtime.
- Local parameter files (`parameters.local.json`, `secrets.env`) are gitignored.

## Compose parity

Source contract: `docker/docker-compose.ghcr-slim.yml`. Intentional differences:

| Compose | Azure |
|---------|-------|
| `MSSQL_*`, embedded SQL | Azure SQL GP serverless (auto-pause) |
| `ASPNETCORE_ENVIRONMENT=Development` | `Production` |
| `webapi-ui-mssql` image | `webapi-ui-slim` + external SQL |
| Local AI URLs `127.0.0.1:9` | Same (local AI disabled) |

## Related docs

- [Setup guide (post-deploy configuration)](../../docs/setup-guide.md)
- [Auth flow (first-user bootstrap)](../../docs/auth-flow.md)
- [Execution orchestration](../../docs/azure-deploy-execution/00-orchestration.md)
