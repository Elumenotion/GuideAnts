# GuideAnts Telemetry and Visibility Configuration

Last updated: 2026-04-30

This document summarizes current telemetry controls and investigation paths.

## 1. Settings surface

Use `Settings -> Telemetry` to configure API logging levels.

- Writes to DB-backed `Telemetry` settings section.
- Changes are applied without requiring API/container restart.
- Scope is API process logging; runtime containers still use container logs.

## 2. Primary visibility sources

- API structured logs (`ILogger<T>` categories)
- Background job state/logs
- Usage and invocation records in DB
- Settings Infrastructure probes
- Container logs (`guideants-webapi-ui`, `guideants-ai`, `searxng`, `docling-serve`, SQL Server)

## 3. Recommended baseline

Use targeted category overrides instead of globally raising `Default` to verbose levels.
Keep framework noise lower than product-domain categories in normal operations.

## 4. Investigation playbooks

### Chat routing issues

Raise routing categories and verify:

- requested model id
- resolved catalog model id
- provider id
- stable routing problem details code/action fields

### Local llama runtime issues

Raise llama + infrastructure probe categories and verify:

- `LlamaCpp:BaseUrl` from Infrastructure
- runtime health endpoints
- runtime inventory alias/artifact state

### Background jobs stuck/failing

Raise background-job categories and inspect job queue lifecycle events:

- enqueue
- claim
- retry
- completion/failure

### Service-specific issues (images/speech/documents/search)

Raise only relevant service categories and compare with corresponding runtime/container logs.

## 5. Infrastructure probes

Infrastructure tab probes runtime dependency keys and URL reachability.
Use these probes before deep-diving into code when failures appear network/config related.

## 6. Related docs

- Setup and troubleshooting: [setup-guide.md](setup-guide.md)
- Settings architecture: [settings-page-provider-model-llama-redesign.md](settings-page-provider-model-llama-redesign.md)
- Requirements baseline: [settings-and-llama-completion-requirements.md](settings-and-llama-completion-requirements.md)
