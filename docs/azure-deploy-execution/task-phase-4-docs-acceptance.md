# Phase 4 — Docs + Acceptance

**Branch:** `feature/azure-deploy-slim`  
**Depends on:** Phase 3 `DONE`  
**Blocks:** PR open

---

## Mission

Complete consumer-facing documentation, compose parity validation, and
`acceptance-evidence.md`. Open **one PR** with full diff.

---

## Read first

- `docs/azure-deploy-execution/00-orchestration.md` §6
- `docs/azure-deploy-execution/deploy-gate.md`
- `docs/setup-guide.md` (cross-link for post-deploy configuration)
- `docs/auth-flow.md` (first-user bootstrap)

---

## Preconditions

- [ ] Phases 1–3 `DONE`
- [ ] deploy-gate green on test subscription

---

## Guardrails

- README is consumer-facing — no internal Waterfall references.
- Document estimated Azure monthly cost range (SQL S2 + 6 ACA apps + storage + logs).
- Document third-party images (Docling, DocumentServer) and licenses.
- No secrets in docs or example files.

---

## Tasks

### 1. `deploy/azure/README.md`

Sections:

1. **Prerequisites** — Azure subscription, Azure CLI, PowerShell 7+ or Bash, `dotnet ef` for migrations
2. **Quick start** — copy parameters, run generate-secrets, run deploy
3. **Parameters** — table of all knobs including `-CustomDomain`, `-ImageTag`, `-GhcrOwner`
4. **Image tags** — explain `main` (compose default), `latest`, release tags from GitHub Releases
5. **Custom domain** — DNS CNAME to ACA, cert binding steps
6. **Post-deploy**
   - Open URL → Register → first user is Admin
   - Settings → Connections → configure OpenAI/Azure/etc.
   - DocumentServer enabled by default
7. **Troubleshooting** — image pull, SQL firewall, SMB locks, container logs
8. **Cleanup** — `az group delete`
9. **Cost notes** — Log Analytics can be expensive; link to cost tips

### 2. Compose parity checklist

Create script or markdown table mapping every env var in
`docker/docker-compose.ghcr-slim.yml` → Bicep/script equivalent.

Flag intentional omissions:

| Compose var | Azure handling |
|-------------|----------------|
| `MSSQL_*` | Replaced by Azure SQL |
| `LocalServiceHosts__*Url=http://127.0.0.1:9` | Same (local AI disabled) |
| `ASPNETCORE_ENVIRONMENT=Development` | `Production` |

### 3. Link from main docs

Add entry to `docs/setup-guide.md` § "Cloud deployment" pointing to `deploy/azure/README.md`.

### 4. `acceptance-evidence.md`

Capture full deploy-gate outputs (see template file).

### 5. Final PR

- Branch: `feature/azure-deploy-slim`
- Title: `feat(deploy): add Azure Container Apps slim deployment for public consumers`
- Include `docs/azure-deploy-execution/` + `deploy/azure/`

---

## Files in scope

| Action | Path |
|--------|------|
| Add | `deploy/azure/README.md` |
| Add | `deploy/azure/ARCHITECTURE.md` (optional service diagram) |
| Edit | `docs/setup-guide.md` (cloud deploy link) |
| Complete | `docs/azure-deploy-execution/acceptance-evidence.md` |

---

## Definition of Done

- [ ] Phase 4 gate (orchestration §4.6) passes
- [ ] Final acceptance checklist (orchestration §6) complete
- [ ] `STATUS.md` updated: Phase 4 → `DONE`
- [ ] PR ready to open

---

## Report-back

```text
PHASE 4 COMPLETE
- README: deploy/azure/README.md
- Setup guide link: added
- Compose parity: <N>/<N> vars mapped
- acceptance-evidence.md: complete
- PR: ready to open
```
