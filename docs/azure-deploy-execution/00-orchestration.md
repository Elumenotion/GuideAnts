# GuideAnts Azure Deploy — Execution & Orchestration Guide

Last updated: 2026-07-23

This is the **conductor** document for public-repo Azure Container Apps deployment
automation. Consumers clone GuideAnts, run scripts from `deploy/azure/`, and get a
working **azure-slim** stack in their own Azure subscription.

The work adapts proven patterns from Waterfall's private `src/server/Azure` Bicep, but
**does not port it wholesale**: GuideAnts uses **GHCR public images**, **no Entra login**,
**more sidecar containers**, and **`docker-compose.ghcr-slim.yml`** as the env contract.

> **Delivery model: one PR**
>
> All phases land on one feature branch (e.g. `feature/azure-deploy-slim`).
> Phases are **logical milestones** for implementation and gate checks, not separate
> merges. Do not open a PR until **final acceptance** (section 6) is green on the
> complete tree.

> **Audience split**
>
> - **Implementer / reviewer** read this file, [`DECISIONS.md`](./DECISIONS.md),
>   [`STATUS.md`](./STATUS.md), [`deploy-gate.md`](./deploy-gate.md), and
>   [`acceptance-evidence.md`](./acceptance-evidence.md).
> - **Phase work** is defined in `task-phase-*.md` briefs. Execute phases in order;
>   each phase's Definition of Done must pass before starting the next.

---

## 0. How to use this folder

| File | Purpose |
|------|---------|
| `00-orchestration.md` (this) | Scope, phase order, gates, deviation protocol, final acceptance. |
| `DECISIONS.md` | Locked decisions (A1–A12) + frozen invariants. Single source of truth. |
| `STATUS.md` | Living ledger: baseline, per-phase state, gate results, deviations. |
| `deploy-gate.md` | Proves deploy completes and all container apps reach running state. |
| `task-phase-1-bicep-infra.md` | Core Bicep: RG, VNet, SQL, storage, KV, CAE, observability. |
| `task-phase-2-container-apps.md` | Container apps module: 6 services, env from slim compose, domain support. |
| `task-phase-3-deploy-scripts.md` | `deploy.ps1`/`deploy.sh`, `generate-secrets`, migrations, SearXNG seed. |
| `task-phase-4-docs-acceptance.md` | Consumer README, compose parity check, acceptance evidence. |
| `acceptance-evidence.md` | Captured commands/outputs for the single PR. |

Each task brief: Mission → Read first → Preconditions → Guardrails → Tasks → Files in
scope → Self-verification → Definition of Done → Report-back contract.

---

## 1. Problem statement (why this work exists)

| Gap | Impact |
|-----|--------|
| GuideAnts is Docker Compose–first | Self-hosters have no first-party Azure path in the public repo. |
| Waterfall Azure is private + different shape | Entra, ACR builds, 3 apps — not copy-pasteable. |
| Compose slim stack ≠ ACA | Embedded SQL, bind mounts, and localhost service URLs need translation. |
| Public repo constraints | No committed secrets; consumers need parameterized, documented deploy. |

**Target:** A consumer with Azure CLI + subscription runs one script, gets a running
GuideAnts instance at their domain (or ACA default URL), registers as Admin, configures
cloud AI in Settings.

---

## 2. Pre-flight (once, before Phase 1)

- [ ] **`DECISIONS.md` is LOCKED** (A1–A12). No implementation until decisions are filled.
- [ ] **Read Waterfall reference** (patterns only, do not copy secrets):
  - `D:\Elumenotion\repos\waterfall\src\server\Azure\main.bicep`
  - `modules/database.bicep`, `storage.bicep`, `key-vault.bicep`, `deploy.ps1` (MI + migrations flow)
- [ ] **Read GuideAnts compose contract:**
  - `docker/docker-compose.ghcr-slim.yml`
  - `docker/.env` (DocumentServer image, SearXNG paths)
  - `src/server/GuideAntsApi/appsettings.example.json`
- [ ] **Capture baseline** in `STATUS.md`:
  - `cd src/server && dotnet build GuideAntsApi.sln`
  - `cd src/server && dotnet test GuideAntsApi.sln`
- [ ] **Inventory GHCR images + tags** from `.github/workflows/publish-*.yml`:
  - Default consumer tag: `main`
  - Also supported: `latest`, release semver, `sha-*`
- [ ] Feature branch from updated `main` per repo branch-safety rules.
- [ ] Confirm GHCR packages are **public** (or document visibility requirement).

---

## 3. Dependency graph (implementation order — one branch)

```text
Phase 1  Bicep infrastructure
         (RG, VNet, Log Analytics, App Insights, SQL S2, storage shares,
          Key Vault shell, Container Apps Environment)
              │
              ▼
Phase 2  Container apps
         (6 ACA apps, env wiring from slim compose, domain params,
          Azure Files mounts, DocumentServer on by default)
              │
              ▼
Phase 3  Deploy scripts
         (generate-secrets, deploy.ps1/sh, migrations, SearXNG seed,
          manage.ps1, .gitignore for local params)
              │
              ▼
Phase 4  Docs + acceptance
         (deploy/azure/README.md, compose parity validation, deploy-gate evidence)
```

**Rules:**

- A phase is not done until its gate (section 4) passes on the current branch.
- **Do not** merge partial phases to `main`. One PR when section 6 is complete.
- Phases are sequential — Phase 2 depends on infra outputs from Phase 1.

---

## 4. Verification gates

### 4.1 Global invariants (every phase)

- [ ] `dotnet build GuideAntsApi.sln` — 0 errors; warnings not worse than baseline.
- [ ] `dotnet test GuideAntsApi.sln` — no new failures vs baseline.
- [ ] **No secrets in git:** grep deploy scripts for API keys, `sk_live`, passwords — none committed.
- [ ] **No Entra app login config** in Bicep env blocks (`AzureAd__*` absent).
- [ ] **Matches `DECISIONS.md`.**
- [ ] **Compose parity:** every env var in slim compose (except SQL-in-container and
      localhost stubs) has a Bicep/script equivalent or documented intentional omission.

### 4.2 Phase 1 — Bicep infrastructure

- [ ] `deploy/azure/main.bicep` deploys at subscription scope without container apps.
- [ ] SQL module matches Waterfall S2 SKU and MI outputs.
- [ ] Storage module creates shares: `contentfiles`, `searxng-config`, `searxng-data`,
      `script-agent-state`.
- [ ] Key Vault RBAC enabled; no secret values in Bicep parameters committed to repo.
- [ ] `az deployment sub create --what-if` succeeds for example parameters.

### 4.3 Phase 2 — Container apps

- [ ] `deploy/azure/apps.bicep` deploys all 6 container apps.
- [ ] Only `guideants-webapi-ui` has `external: true` ingress.
- [ ] `guideants-webapi-ui-slim` image (not `webapi-ui-mssql`).
- [ ] DocumentServer enabled with JWT secret ref from Key Vault.
- [ ] `-CustomDomain` sets `ALLOWED_ORIGINS` and public API URL env vars correctly.
- [ ] Internal URLs use ACA service discovery (`http://{app-name}` or environment default domain).
- [ ] **deploy-gate** passes (section 4.5) when run against a test subscription.

### 4.4 Phase 3 — Deploy scripts

- [ ] `generate-secrets.ps1` / `generate-secrets.sh` produces JWT, SettingsSecrets, script tokens, DocumentServer JWT.
- [ ] `deploy.ps1` and `deploy.sh` support:
  - `-EnvironmentName`, `-Location`, `-AppNamePrefix`
  - `-CustomDomain` (empty allowed)
  - `-ImageTag` (default `main`)
  - `-GhcrOwner` (default `elumenotion`)
  - `-SkipMigrations`, `-OnlyInfra`, `-OnlyApps`
- [ ] EF migrations run once with SQL admin; runtime uses MI connection string in KV.
- [ ] SearXNG config uploaded to `searxng-config` share from `docker/volumes/searxng/config/`.
- [ ] Post-deploy summary prints URL, "register first user", "configure Connections in Settings".

### 4.5 Deploy gate (summary)

Defined in [`deploy-gate.md`](./deploy-gate.md). Pass when:

- `az deployment` completes without error.
- All 6 container apps report `Running` / healthy provisioning state.
- `guideants-webapi-ui` external FQDN responds (HTTP 200 or redirect to UI).
- No container app stuck in `Provisioning` / `Failed` after 15 minutes.

### 4.6 Phase 4 — Docs + acceptance

- [ ] `deploy/azure/README.md` — consumer-facing 15-minute guide.
- [ ] `parameters.example.json` — no secrets; documents all knobs.
- [ ] Compose parity script or checklist documented in `acceptance-evidence.md`.
- [ ] `acceptance-evidence.md` complete for the single PR.

---

## 5. Deviation & failure protocol

When a gate fails, **stop the line** — do not start the next phase.

1. **Classify** in `STATUS.md`:
   - `bicep what-if fail` → fix module/parameters.
   - `image pull fail` → GHCR visibility, tag typo, or platform mismatch.
   - `container failed` → env wiring, secret ref, or file share mount.
   - `migration fail` → SQL firewall, admin password, FTS on Azure SQL.
   - `secret committed` → revert immediately; rotate if ever pushed.
   - `scope creep` → revert or update brief + DECISIONS.
2. Fix in the **owning phase**; re-run the **full** gate for that phase.
3. Record attempt + fix in `STATUS.md` deviation log.
4. Do not land partial work on `main`.

---

## 6. Final acceptance (single PR ready)

The job is complete only when **all** hold:

- [ ] Phases 1–4 marked `DONE` in `STATUS.md`.
- [ ] **deploy-gate** green on final tree (test subscription deploy).
- [ ] Consumer README accurate: prerequisites, cost warning, domain setup, first-user register.
- [ ] No secrets in diff; `.gitignore` covers local parameter files.
- [ ] DocumentServer deployed and healthy by default.
- [ ] Custom domain path tested OR documented with ACA cert binding steps.
- [ ] `acceptance-evidence.md` captured.
- [ ] One PR opened with full diff; user reviews and merges after CI green.

---

## 7. Report-back contract (final handoff to user)

```text
GUIDEANTS AZURE DEPLOY — FINAL REPORT
Branch: <branch>
PR: <url or "ready to open">

BASELINE:
- Server build/test: <pass + counts>

PHASES:
- Phase 1 Bicep infra: <DONE + notes>
- Phase 2 Container apps: <DONE + notes>
- Phase 3 Deploy scripts: <DONE + notes>
- Phase 4 Docs + acceptance: <DONE + notes>

DEPLOY GATE:
- All 6 apps Running: <pass/fail>
- Web UI reachable: <pass/fail + URL>
- Migrations applied: <pass/fail>

INVARIANTS:
- No committed secrets: <pass/fail>
- No Entra login config: <pass/fail>
- Compose parity: <pass/fail>
- DocumentServer default on: <pass/fail>

DEVIATIONS: <none | list from STATUS.md>

FILES ADDED (high level):
- deploy/azure/...
- docs/azure-deploy-execution/...

RECOMMENDED POST-MERGE SMOKE (consumer):
1. Run deploy.sh with -CustomDomain or default FQDN
2. Register first user → Admin
3. Settings → Connections → add OpenAI/Azure provider
4. Create project + chat smoke test
```
