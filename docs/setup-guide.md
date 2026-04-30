# GuideAnts Setup Guide

Last updated: 2026-04-30

This is the operator-facing setup source of truth for Settings and AI onboarding.
Use this guide for first install, first-launch configuration, and troubleshooting.

## 1. What is current

GuideAnts runs as a Docker-based stack with configuration split between:

- Runtime/environment config (compose/appsettings/env), and
- DB-backed application settings edited in the Settings UI.

The Settings UI now has **seven** top-level tabs, in this order:

1. Overview
2. Personalization
3. Connections
4. Models & Runtime
5. Services
6. Infrastructure
7. Telemetry

This order comes from `SettingsTabNavigation.tsx` and is the canonical UI IA.

## 2. First launch and Add AI Services Wizard

On Home (`/`), the Add AI Services Wizard opens automatically when either condition is true:

- No configured connection sections (`readinessStatus === configured` for connection sections), or
- No catalog models exist.

Auto-open is skipped when:

- The user has set `Don't auto-open this again on this device`, or
- Startup probe calls fail.

Wizard facts (as built):

- Entry points: auto-open on Home and manual `Setup Wizard` button on Home.
- Providers currently supported: `Microsoft Foundry` and `Google Gemini`.
- Steps: `Provider`, `Connection details`, `Models`, `Optional services`, `Finish`.
- Footer actions are always visible: `Not now`, `Configure manually`, `Back`, `Next`, `Finish`.
- `Finish` from a non-final step validates/saves current step and jumps to `Finish` step.
- Wizard only closes when `Finish` is clicked on the final step.

Details: [add-ai-services-wizard.md](add-ai-services-wizard.md)

## 2a. Bootstrap seeding (first startup)

On first startup, after EF migrations and application settings bootstrap,
the system seeds required data from `Resources/bootstrap/`:

**Required guides and assistants** — imported via the existing
guide/assistant export/import service. Seeds include Creative Guide,
The Guide Guide, and their crew member assistants (Conversation Title
Generator, Read Web, Search, Media Creator, Diagrams, Code Executor,
Conversation User Proxy). All seeds omit explicit model fields so they
inherit the operator's configured default chat model.

**Runtime profiles** — the three template profiles required by R-6.7 and
R-8.1 are seeded directly into the `RuntimeProfiles` table:

- `qwen3_5` — Qwen 3.5 family (non-thinking general defaults)
- `qwen3_6` — Qwen 3.6 family (current default recommendation)
- `gemma4` — Gemma 4 family

All seeding is idempotent: if an entity with the same name (guides/assistants)
or profile ID (runtime profiles) already exists, the seed is skipped.
User modifications are never overwritten.

## 3. Recommended setup flow

1. Open **Connections** and save required credentials.
2. Open **Models & Runtime** and add at least one chat model.
3. Open **Services** and select/activate provider per non-chat service.
4. Check **Overview** status pills for chat + non-chat readiness.
5. Use **Infrastructure** probes if runtime endpoints are unreachable.
6. Use **Telemetry** to raise log levels for investigations.

Use **Personalization** for user profile details only.

## 4. Connections and token ownership

Hugging Face token ownership is single-path:

- `Settings -> Connections -> HuggingFace -> Token`

The token is resolved server-side from `HuggingFace:Token` via `IHuggingFaceTokenResolver`.
There are no per-request token overrides in the Settings model-download flow.

## 5. Models & Runtime and llama specifics

`Models & Runtime` is the operator home for:

- Catalog models
- Runtime profiles (three templates seeded at first boot — see §2a)
- Local llama runtime inventory/actions

Llama download behavior:

- API delegates download and alias registration to `guideants-ai` admin endpoints.
- `LlamaModelManagementOptions` currently includes `AllowOverwrite` only.
- Runtime alias/files are runtime-owned; the API does not directly manage host model directories.

Details: [llama-model-download-and-runtime-management.md](llama-model-download-and-runtime-management.md)

## 6. Infrastructure runtime dependencies

Infrastructure tab exposes runtime-owned dependency keys with source and probe support.
Current keys are:

- `LlamaCpp:BaseUrl`
- `LocalServiceHosts:SpeechTranscriptionBaseUrl`
- `LocalServiceHosts:SpeechSynthesisBaseUrl`
- `LocalServiceHosts:ImageGenerationBaseUrl`
- `LocalServiceHosts:EmbeddingsBaseUrl`
- `LocalServiceHosts:MediaBaseUrl`
- `LocalServiceHosts:DocumentIntelligenceBaseUrl`

If local runtime calls fail, validate these keys first, then run probes.

## 7. Common troubleshooting

### Wizard did not auto-open on a fresh environment

- Confirm browser local storage does not contain dismissal key:
  `guideants.firstLaunch.addAiServicesWizard.dismissed.v1`
- Confirm `GET /api/settings/sections` and `GET /api/settings/models` both succeed.

### Cloud setup works, local runtime actions fail

- Verify `LlamaCpp:BaseUrl` and `LocalServiceHosts:*` values.
- Run Infrastructure probes.
- Check `guideants-ai` health and logs.

### Model creation/download fails due to Hugging Face auth

- Save `HuggingFace.Token` in Connections.
- Retry model add/download.

### Service shows Not ready

- Open Services editor for that capability.
- Validate active provider fields and save provider activation.
- Re-check Overview.

## 8. Developer and deep-dive docs

Read in this order:

1. [settings-page-provider-model-llama-redesign.md](settings-page-provider-model-llama-redesign.md)
2. [settings-and-llama-completion-requirements.md](settings-and-llama-completion-requirements.md)
3. [settings-service-provider-model-requirements.md](settings-service-provider-model-requirements.md)
4. [default-chat-models.md](default-chat-models.md)
5. [llama-model-download-and-runtime-management.md](llama-model-download-and-runtime-management.md)
6. [add-ai-services-wizard.md](add-ai-services-wizard.md)
7. [telemetry-configuration.md](telemetry-configuration.md)
