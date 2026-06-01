# Routing Literal Audit Report (Session Baseline)

Generated: May 22, 2026  
Repository: `d:\repos\GuideAnts`

## 1) Executive Summary

This audit confirms a broad spread of hardcoded routing literals (`localhost`, loopback IPs, fixed ports, and fixed route prefixes) across runtime code, runtime config, migrations, tests, and docs.

Key counts from this session:

- Exact `http://localhost:8110`: `131` matches across `32` files (whole repo)
- `src/` non-test, non-generated (excluding `node_modules`, `dist*`, `bin`, `obj`, `.vs`, `*.Tests`, `*.IntegrationTests`):
  - `105` loopback/localhost literal matches across `39` files
- Fixed route-path literals in `GuideAntsApi` runtime (`/sandbox`, `/llama-cpp`, `/emb`, `/sd`, `/asr`, `/llama-admin`):
  - `13` matches across `7` files

The current containerized deployment works because compose correctly provides required endpoint values through environment/config (for example `http://guideants-ai:80`). The defect is that some runtime code still carries localhost defaults/fallbacks and special-case branches that can bypass or weaken the settings-driven contract.

## 2) Scope and Method

This report is based on direct repo search and targeted source inspection performed in-session.  
Primary intent: identify hardcoded routing literals and explain container-vs-local behavior.

Search dimensions used:

- Exact literal search (`http://localhost:8110`)
- Host/port literal search (`localhost:\d+`, `127.0.0.1:\d+`, `http(s)://localhost`, `tool://localhost`)
- Route segment literal search (`/sandbox`, `/llama-cpp`, `/emb`, `/sd`, `/asr`, `/llama-admin`)
- Runtime path tracing for endpoint resolution in `NotebookDockerScriptService` and `LocalServiceAdminRouting`
- Compose env override verification in `docker/docker-compose.cuda.yml`

## 3) High-Impact Findings

### 3.1 Runtime defaults hardcoded to localhost

Examples:

- `src/server/GuideAntsApi/Options/AzureDocumentIntelligenceOptions.cs`
  - `LocalServiceHostsOptions.*BaseUrl` defaults to `http://localhost:8110`
  - `DocumentIntelligenceBaseUrl` defaults to `http://localhost:5001`
- `src/server/GuideAntsApi.BackgroundJobs/Options/ServiceProviderIds.cs`
  - Background job defaults include `http://localhost:8110` and `http://localhost:5001`
- `src/server/GuideAntsApi/Services/Bootstrap/LocalServiceAutoSelector.cs`
  - fallback to `http://localhost:5001`

Impact: localhost defaults/fallbacks in runtime code can bypass strict settings validation and create inconsistent behavior between environments.

### 3.2 Compose-only routing branch in script execution service

`src/server/GuideAntsApi/Services/NotebookDockerScriptService.cs`:

- Uses `API_RUNTIME_CONTEXT=compose` branch to force container DNS endpoints
- Compose branch returns:
  - `http://guideants-ai:80/sandbox`
  - `http://plantuml:80`
- Non-compose fallback still returns localhost (`8110`/`8111`)

Impact: behavior is explicitly bifurcated by runtime context. It works, but this creates branchy route-resolution logic and duplicate endpoint truth sources.

### 3.3 Route-prefix literals spread across validation, startup, probing, and routing helpers

Notable files:

- `Configuration/ServiceRoutingStartupValidator.cs` (`/sandbox`, `/llama-cpp`)
- `Configuration/StartupConfiguration.cs` (`/llama-cpp`, `/llama-admin/`)
- `Settings/ApplicationSettingsService.RuntimeDependencies.cs` (`/llama-cpp` requirement)
- `Services/Infrastructure/InfrastructureProbeService.cs` (`/llama-cpp/health`)
- `Endpoints/LocalServiceAdminRouting.cs` (`/sd`, `/asr`, `/tts`, `/emb`)
- `Configuration/UiApplicationBuilderExtensions.cs` (`/sandbox` path handling)

Impact: path-contract updates require touching multiple files and are vulnerable to drift/regression.

### 3.4 Config and migration literals embed environment assumptions

Examples:

- `src/server/GuideAntsApi/appsettings*.json` includes localhost and loopback values
- `src/server/GuideAntsApi.DataModel/Migrations/20260409190436_AddApplicationSettingsConfigMode.cs` writes hardcoded URLs into data

Impact: migration-time literals can fossilize assumptions into persisted data and complicate future endpoint changes.

## 4) Why Container Runs Still Work Today

Container mode works by correctly supplying required values through the configuration system and compose context.

In `docker/docker-compose.cuda.yml`, `guideants-webapi-ui` sets:

- `API_RUNTIME_CONTEXT=compose`
- `LocalServiceHosts__*BaseUrl=http://guideants-ai:80` (and docling: `http://docling-serve:5001`)
- `LlamaCpp__BaseUrl=http://guideants-ai:80/llama-cpp`
- `ServiceRouting__Containers__guideants-ai__BaseUrl=http://guideants-ai:80/sandbox`

Then runtime code consumes these values:

- `NotebookDockerScriptService.ResolveScriptExecutionBaseUrl(...)`:
  - if compose: uses container DNS endpoints
  - else: may use configured override, then localhost fallback
- `LocalServiceAdminRouting.ResolveAdminBase(...)`:
  - reads `LocalServiceHosts:*BaseUrl` host and appends hardcoded admin prefix (`/sd`, `/asr`, `/tts`, `/emb`)

Conclusion: compose behavior is correct and configuration-driven. The remediation target is removing localhost defaults/fallbacks and special-case logic from runtime code so all environments rely on the same settings contract.

## 5) Risk Assessment

### Severity: High

- Cross-environment behavior divergence (compose vs non-compose vs ad-hoc local)
- Multiple endpoint truth sources (options defaults, appsettings, env vars, code branches, migrations)
- Route-contract literals duplicated across startup/validation/probe/routing flows
- Hardcoded migration URLs may preserve obsolete routing contracts in persisted state

### Severity: Medium

- Developer confusion and remediation overhead
- Increased test fragility around literal-dependent assertions

### Severity: Low

- Docs/examples using localhost for local setup are expected, but should be clearly separated from runtime defaults

## 6) Remediation Planning Baseline

### 6.1 Target state

- Single authoritative endpoint contract per service
- Zero localhost/loopback literals in runtime code defaults (except explicitly non-production developer tooling with clear boundary)
- Route prefixes centralized in one typed contract
- Migration logic uses configurable/runtime-aware templates, not fixed URLs

### 6.2 Proposed phased plan

1. Contract centralization
- Introduce a routing contract module (typed constants/options) for host keys + path prefixes.
- Replace ad-hoc string literals in:
  - `ServiceRoutingStartupValidator`
  - `StartupConfiguration`
  - `InfrastructureProbeService`
  - `LocalServiceAdminRouting`
  - `NotebookDockerScriptService`

2. Default hygiene
- Replace localhost defaults in runtime option classes with explicit placeholders/sentinels.
- Fail fast on unresolved runtime dependencies unless env/config provides usable values.

3. Configuration unification
- Consolidate resolution order and ownership:
  - deployment env vars
  - appsettings
  - runtime overrides
- Document one canonical precedence model.

4. Migration hardening
- Remove hardcoded endpoint literals from future migrations.
- Add idempotent data-fix migration/upgrade script to normalize previously persisted localhost endpoints where appropriate.

5. Guardrails
- Add CI lint/test rule to block new runtime localhost/loopback literals outside approved files.
- Keep docs/tests allowed by policy with explicit path allowlist.

## 7) Candidate Worklist (Initial)

- `src/server/GuideAntsApi/Options/AzureDocumentIntelligenceOptions.cs`
- `src/server/GuideAntsApi.BackgroundJobs/Options/ServiceProviderIds.cs`
- `src/server/GuideAntsApi/Services/NotebookDockerScriptService.cs`
- `src/server/GuideAntsApi/Endpoints/LocalServiceAdminRouting.cs`
- `src/server/GuideAntsApi/Configuration/ServiceRoutingStartupValidator.cs`
- `src/server/GuideAntsApi/Configuration/StartupConfiguration.cs`
- `src/server/GuideAntsApi/Services/Infrastructure/InfrastructureProbeService.cs`
- `src/server/GuideAntsApi/Settings/ApplicationSettingsService.RuntimeDependencies.cs`
- `src/server/GuideAntsApi.DataModel/Migrations/20260409190436_AddApplicationSettingsConfigMode.cs`

## 8) Verification Checklist for Remediation

After remediation, expected checks:

- `rg --fixed-strings "http://localhost:8110" src/server` returns only approved docs/tests (or zero in runtime paths)
- `rg --pcre2 "localhost:\\d+|127\\.0\\.0\\.1:\\d+" src/server` returns only approved local dev tooling files
- route prefixes (`/sandbox`, `/llama-cpp`, `/emb`, `/sd`, `/asr`, `/llama-admin`) originate from one shared contract source
- container run still resolves to `guideants-ai`/`docling-serve` DNS endpoints
- non-container run fails with actionable configuration errors when required endpoints are unset

## 9) Notes and Boundaries

- This report intentionally distinguishes runtime defects from tests/docs examples.
- Compose is correctly configured today; the underlying debt is runtime localhost defaults/fallbacks that should be removed so endpoint resolution remains strictly settings-driven.
- This document is intended as the baseline artifact for remediation planning and tracking.
