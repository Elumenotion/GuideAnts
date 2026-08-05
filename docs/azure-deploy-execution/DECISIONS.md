# GuideAnts Azure Deploy — Locked Decisions

Last updated: 2026-08-04  
Status: **LOCK before Phase 1**

This file freezes design decisions for public-repo Azure Container Apps deployment.
The `notebook-sync-execution` folder is the structural template; **this** document is
the contract for this work.

Rules:

- If a decision is `UNDECIDED`, blocked phases do not start.
- Changing a locked decision mid-implementation requires updating affected code/docs and
  re-running gates from Phase 1.
- Do not reinterpret locked values in task briefs.

---

## Part A — Locked decisions (A1–A12)

| ID | Decision | Resolved value | Blocks |
|----|----------|----------------|--------|
| A1 | **Delivery** | **One PR** on a single feature branch after all phases and gates pass. Phases are milestones, not separate merges. | all |
| A2 | **IaC stack** | **Bicep only** (same approach as Waterfall `src/server/Azure`). No Terraform/azd in v1. | 1, 2 |
| A3 | **Azure SQL** | **General Purpose serverless** (`GP_S_Gen5`, max 1 vCore, min 0.5), **auto-pause 15 min**, 32 GB max, public access + Azure-services firewall rule, managed-identity connection string in Key Vault. Database name default **`guideants`**. SQL auditing to Azure Monitor **disabled** (keeps at-rest log cost near zero). | 1, 3 |
| A4 | **Deployment profile** | **`azure-slim` only** for v1: `guideants-webapi-ui-slim` + Azure SQL (not `webapi-ui-mssql`). Cloud AI via UI settings; no local llama/ASR/TTS/SD containers. | 2 |
| A5 | **Container registry** | **GHCR public** (`ghcr.io/elumenotion/*`). No ACR build phase. ACA pulls without registry credentials. | 2, 3 |
| A6 | **Image tags** | Align with existing publish workflows and compose defaults: **`main`** default tag; support **`latest`** (main branch publishes) and **semver/git tag** overrides via deploy parameters. Image names match `docker-compose.ghcr-slim.yml` env vars (`GA_*_GHCR_IMAGE`). | 2, 3 |
| A7 | **DocumentServer** | **Deployed by default** (`DocumentServer__Enabled=true`). Image: `ghcr.io/euro-office/documentserver:latest` (from `docker/.env`). JWT enabled in Azure profile with generated shared secret. | 2 |
| A8 | **Custom domain** | Deploy scripts accept **`-CustomDomain`** (and derive `ALLOWED_ORIGINS`, `DocumentServer__ApiBaseUrl`, public API URL env). Empty → default ACA FQDN only. | 2, 3 |
| A9 | **Scaling** | All container apps: **minReplicas=0** (scale-to-zero), consumption workload profile. Cold starts expected after idle; use `manage.ps1 -Operation scale` to raise mins if needed. | 2 |
| A10 | **Persistence** | Azure Files SMB shares: **`contentfiles`**, **`searxng-config`**, **`searxng-data`**, **`script-agent-state`**. SearXNG config seeded from repo on first deploy. | 1, 2, 3 |
| A11 | **Secrets** | **Never committed.** `generate-secrets` script creates JWT signing key, `SettingsSecrets` AES key, script-agent tokens, DocumentServer JWT; stored in Key Vault. No hardcoded defaults in deploy scripts (unlike Waterfall). | 3 |
| A12 | **Auth model** | **No Entra app login.** First-party JWT cookie only. Deploy does not configure `AzureAd__*`. Post-deploy: consumer registers first user → Admin. | 2, 3 |

---

## Part B — Frozen invariants

- **Compose contract:** `docker/docker-compose.ghcr-slim.yml` is the source of truth for
  service graph and env wiring (minus embedded SQL and localhost placeholders).
- **External ingress:** only `guideants-webapi-ui` is publicly reachable (HTTPS).
- **Internal services:** `guideants-ai`, `docling-serve`, `plantuml`, `searxng`,
  `documentserver` use internal ACA ingress.
- **Script tokens:** `ScriptExecution__AgentToken` / `AdminToken` must match across
  `guideants-webapi-ui`, `guideants-ai`, and `plantuml`.
- **FTS:** EF migrations conditionally enable full-text on `DocumentChunks`; Azure SQL
  General Purpose (including serverless) supports FTS.
- **Logging:** CAE `appLogsConfiguration.destination = null` — rich stdout/stderr via live
  log stream only; no console ingest into Log Analytics. (String `'none'` fails create preflight.)
- **Third-party images:** `quay.io/docling-project/docling-serve-cpu` and
  `ghcr.io/euro-office/documentserver` — documented in consumer README; not mirrored to GHCR.
- **No image build in deploy:** consumers pull prebuilt GHCR images only.
- **Public repo safety:** `parameters.local.json`, `secrets.env`, and deployment outputs
  are gitignored.

---

## Part C — Explicit non-goals (v1)

- Entra ID / Microsoft SSO for app login.
- ACR or consumer-local image builds in deploy path.
- `azure-cpu` / GPU local-AI on Container Apps.
- Stripe, Postmark, or billing infrastructure.
- Azure Front Door / WAF (custom domain via ACA managed cert only).
- Multi-environment CI deploy from GuideAnts repo (consumer runs scripts locally).
- Automated post-deploy functional tests beyond container startup / health.

---

## Part D — Service inventory (azure-slim)

| ACA name | Image | Ingress |
|----------|-------|---------|
| `guideants-webapi-ui` | `ghcr.io/elumenotion/guideants-webapi-ui-slim:{tag}` | External :8080 |
| `guideants-ai` | `ghcr.io/elumenotion/guideants-ai-slim:{tag}` | Internal |
| `docling-serve` | `quay.io/docling-project/docling-serve-cpu:v1.29.0` | Internal |
| `plantuml` | `ghcr.io/elumenotion/guideants-plantuml:{tag}` | Internal |
| `searxng` | `ghcr.io/elumenotion/guideants-searxng:{tag}` | Internal |
| `documentserver` | `ghcr.io/euro-office/documentserver:latest` | Internal |

---

## Part E — Decision ledger

| ID | Status | Date | Notes |
|----|--------|------|-------|
| A1 | LOCKED | 2026-07-23 | One PR |
| A2 | LOCKED | 2026-07-23 | Bicep only |
| A3 | LOCKED | 2026-08-04 | GP serverless + auto-pause (was S2; cost-at-rest) |
| A4 | LOCKED | 2026-07-23 | Slim + Azure SQL |
| A5 | LOCKED | 2026-07-23 | Public GHCR |
| A6 | LOCKED | 2026-07-23 | `main` default tag |
| A7 | LOCKED | 2026-07-23 | DocumentServer on |
| A8 | LOCKED | 2026-07-23 | `-CustomDomain` input |
| A9 | LOCKED | 2026-08-04 | minReplicas=0 scale-to-zero (was 1) |
| A10 | LOCKED | 2026-07-23 | SearXNG + content shares |
| A11 | LOCKED | 2026-07-23 | Generated secrets only |
| A12 | LOCKED | 2026-07-23 | JWT auth, no Entra |
