# Phase 1 — Bicep Infrastructure

**Branch:** `feature/azure-deploy-slim` (same branch for all phases)  
**Depends on:** Pre-flight complete; `DECISIONS.md` A1–A12 LOCKED  
**Blocks:** Phase 2

---

## Mission

Create `deploy/azure/` Bicep modules for core Azure infrastructure. **No container apps**
in this phase — mirror Waterfall's split where `main.bicep` provisions platform services
first.

---

## Read first

- `docs/azure-deploy-execution/00-orchestration.md` §4.2
- `docs/azure-deploy-execution/DECISIONS.md` A2, A3, A10
- Waterfall reference (patterns only):
  - `waterfall/src/server/Azure/main.bicep`
  - `modules/core-infrastructure.bicep`
  - `modules/database.bicep`
  - `modules/storage.bicep`
  - `modules/key-vault.bicep`
  - `modules/container-apps-environment.bicep`

---

## Preconditions

- [ ] Baseline build/test captured in `STATUS.md`
- [ ] Feature branch created from updated `main`

---

## Guardrails

- Do **not** deploy container apps yet (Phase 2).
- Do **not** commit secret values — only `@secure()` parameters and Key Vault secret *names*.
- SQL SKU: **GP serverless** (`GP_S_Gen5`, max 1 vCore, min 0.5, auto-pause 15 min).
- Database name default: **`guideants`** (not `AntArmyProjects`).
- No `AzureAd__*` or Stripe/Postmark parameters.
- App name prefix default: **`guideants`** (not Waterfall's `aqm`).

---

## Tasks

### 1. Create directory layout

```text
deploy/azure/
  main.bicep                    # subscription scope
  parameters.example.json
  modules/
    core-infrastructure.bicep   # VNet, Log Analytics, App Insights
    database.bicep              # Azure SQL (Waterfall pattern)
    storage.bicep               # File shares
    key-vault.bicep             # Vault + secret placeholders
    container-apps-environment.bicep
  .gitignore                    # parameters.local.json, secrets.env
```

### 2. `main.bicep` modules

Wire modules in dependency order. Outputs needed by Phase 2:

| Output | Consumer |
|--------|----------|
| `resourceGroupName` | scripts |
| `containerAppsEnvironmentId` | apps.bicep |
| `keyVaultName` | apps.bicep |
| `storageAccountName` | apps.bicep |
| `sqlServerFqdn` | migrations script |
| `sqlDatabaseName` | migrations script |

### 3. `database.bicep`

Port from Waterfall with these changes:

| Waterfall | GuideAnts |
|-----------|-----------|
| `sqlDatabaseName = 'AntArmyProjects'` | `sqlDatabaseName = 'guideants'` (param, default `guideants`) |
| `appNamePrefix` default `aqm` | default `guideants` |
| AAD admin param | keep optional `sqlAadAdminObjectId` for deployer migrations |

### 4. `storage.bicep`

Create Azure Files shares:

| Share | Quota (GB) | Mount target |
|-------|------------|--------------|
| `contentfiles` | 100 | `/app/ContentFiles` |
| `searxng-config` | 1 | `/etc/searxng` |
| `searxng-data` | 10 | `/var/cache/searxng` |
| `script-agent-state` | 10 | `/var/lib/guideants/script-agent-admin` |

### 5. `key-vault.bicep`

Secret **slots** (values populated by `generate-secrets` script, not Bicep deploy params):

- `jwt-signing-key`
- `settings-secrets-key-{keyId}`
- `script-agent-token`
- `script-agent-admin-token`
- `documentserver-jwt-secret`
- `sql-connection-string` (updated post-deploy with MI client ID)

Optional: accept bootstrap secrets as secure params on first deploy only (like Waterfall),
but **no defaults in scripts**.

### 6. Observability

Cost-optimized from day one (learn from Waterfall `cost-optimized-recreation-plan.md`):

- Log Analytics: 30-day retention (PerGB2018 minimum); ACA console destination `null` (live stream only).
- App Insights linked to workspace.
- Optional `data-collection-rules.bicep` for basic logs tier (stretch).

---

## Files in scope

| Action | Path |
|--------|------|
| Add | `deploy/azure/main.bicep` |
| Add | `deploy/azure/parameters.example.json` |
| Add | `deploy/azure/modules/*.bicep` |
| Add | `deploy/azure/.gitignore` |

---

## Self-verification

```powershell
az deployment sub create --location "East US 2" `
  --template-file deploy/azure/main.bicep `
  --parameters @deploy/azure/parameters.example.json `
  --parameters sqlAdminPassword="<test-only>" `
  --what-if
```

- [ ] What-if clean
- [ ] No container app resources in template
- [ ] SQL outputs match Waterfall shape

---

## Definition of Done

- [ ] Phase 1 gate (orchestration §4.2) passes
- [ ] `STATUS.md` updated: Phase 1 → `DONE`
- [ ] No secrets committed

---

## Report-back

```text
PHASE 1 COMPLETE
- main.bicep modules: <list>
- SQL database name: guideants
- Storage shares: contentfiles, searxng-config, searxng-data, script-agent-state
- what-if: pass/fail
- Deviations: <none | list>
```
