# GuideAnts Azure Deployment (azure-slim)

Deploy GuideAnts to **Azure Container Apps** with **Azure SQL** and public **GHCR** images.
This is the cloud equivalent of `docker/docker-compose.ghcr-slim.yml` — cloud AI via Settings,
no local llama/ASR/TTS/SD containers.

**Estimated deploy time:** 15–25 minutes (first run).

## Prerequisites

| Requirement | Notes |
|-------------|-------|
| Azure subscription | Owner or Contributor on the target subscription |
| [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli) | `az login` completed |
| PowerShell 7+ **or** Bash | Windows: `pwsh`; Linux/macOS: `bash` |
| `dotnet ef` global tool | Installed automatically by deploy script if missing |
| `openssl` (Bash path only) | For `generate-secrets.sh` |

**GHCR images must be public** (default org: `elumenotion`). No registry credentials are configured.

## Quick start

```powershell
cd deploy/azure

# 1. Copy parameter files (optional — deploy script accepts CLI flags)
Copy-Item parameters.example.json parameters.local.json

# 2. Deploy (generates secrets, provisions infra + apps, runs migrations)
./deploy.ps1 `
  -EnvironmentName dev `
  -AppNamePrefix guideants `
  -ImageTag main `
  -CustomDomain "" `
  -SqlAdminPassword "<strong-password>"
```

Bash equivalent:

```bash
chmod +x deploy.sh scripts/*.sh
./deploy.sh --environment-name dev --image-tag main --sql-admin-password '<strong-password>'
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
| `-SqlAdminPassword` | *(required)* | SQL admin — migrations only; runtime uses managed identity |
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
| Docling | `quay.io/docling-project/docling-serve-cpu:v1.21.0` |
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
              └── documentserver (internal :8000)
Azure SQL (guideants) ← managed identity via Key Vault
Azure Files: contentfiles, searxng-config, searxng-data, script-agent-state
```

See [ARCHITECTURE.md](./ARCHITECTURE.md) for details.

## Day-2 operations

```powershell
# Status of all apps
./manage.ps1 -Operation status

# Tail web API logs
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
| SQL connection fail | MI user not created or wrong connection string | Re-run deploy; check Key Vault `sql-connection-string` has `User ID={clientId}` |
| File share mount fail | SMB role not propagated | Wait 2–5 min; redeploy apps |
| docling CrashLoop | Insufficient memory | Increase CPU/memory in `modules/container-apps.bicep` |
| documentserver unhealthy | JWT mismatch | Ensure secrets generated once; force new revision |
| searxng empty | Config not seeded | Run `scripts/upload-searxng-config.ps1 -ResourceGroupName rg-guideants-dev` |
| Migration fail | Firewall blocks your IP | Deploy adds temporary rule; check `sqlcmd`/EF can reach server |

## Cleanup

```bash
az group delete --name rg-guideants-dev --yes --no-wait
```

## Cost notes

Approximate monthly cost (varies by region and usage):

| Resource | Estimate |
|----------|----------|
| Azure SQL S2 (50 DTU) | ~$75/mo |
| 6 Container Apps (minReplicas=1) | ~$150–300/mo |
| Log Analytics (30-day retention) | ~$20–100/mo depending on log volume |
| Azure Files + storage | ~$5–20/mo |

Log Analytics is often the surprise line item. Consider table-level retention tuning after deploy.

## Security

- **No secrets in git.** `generate-secrets` creates random values stored in Key Vault.
- **No Entra app login** — first-party JWT only; register first user as Admin.
- **SQL admin password** is for migrations only; the app uses managed identity at runtime.
- Local parameter files (`parameters.local.json`, `secrets.env`) are gitignored.

## Compose parity

Source contract: `docker/docker-compose.ghcr-slim.yml`. Intentional differences:

| Compose | Azure |
|---------|-------|
| `MSSQL_*`, embedded SQL | Azure SQL Standard S2 |
| `ASPNETCORE_ENVIRONMENT=Development` | `Production` |
| `webapi-ui-mssql` image | `webapi-ui-slim` + external SQL |
| Local AI URLs `127.0.0.1:9` | Same (local AI disabled) |

## Related docs

- [Setup guide (post-deploy configuration)](../../docs/setup-guide.md)
- [Auth flow (first-user bootstrap)](../../docs/auth-flow.md)
- [Execution orchestration](../../docs/azure-deploy-execution/00-orchestration.md)
