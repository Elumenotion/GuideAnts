# GuideAnts Telemetry and Visibility Configuration

Last updated: 2026-07-21

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

### Full outbound chat provider requests

To print the full ThreadRun request bound for chat providers (messages, tools, model, sampling) in the API console:

1. Open `Settings → Telemetry`
2. Set **Chat providers** to **Investigating** (`Debug`) or **Verbose** (`Trace`)
3. Save — applies without restart

Look for: `ThreadRun outbound chat request. Round=… Request={…}` under category `AntRunner.Chat.ThreadRun` (controlled by Telemetry key `AntRunnerChat` → `Logging:LogLevel:AntRunner.Chat`).

This is provider-agnostic (same log for OpenAI, Anthropic, Gemini, Hugging Face, OpenRouter, and llama). Prompt Trace in Usage remains the persisted UI drill-down for the same round payloads.

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
- Settings architecture: [settings-architecture.md](settings-architecture.md)
- Requirements baseline: [settings-and-llama-completion-requirements.md](settings-and-llama-completion-requirements.md)

