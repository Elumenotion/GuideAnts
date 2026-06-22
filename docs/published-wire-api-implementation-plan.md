# Published Wire APIs — Implementation Plan

Last updated: 2026-06-22

## Summary

Implement a published-guide API surface that is OpenAI-compatible at:

- `/api/published/openai/{pubId}/v1`

The implementation must use existing provider routing, enforce published-guide
auth and cost policy, and meter all wire calls to the published guide's
project/notebook even when a call is outside conversations.

## Compatibility Scope

v1 scope is OpenAI-style APIs only (no provider-native pass-through):

- `GET /models`
- `POST /chat/completions`
- `POST /responses`
- `POST /embeddings`
- `POST /images/generations`
- `POST /audio/transcriptions`
- `POST /audio/speech`

## Locked Decisions

- Base URL is `/api/published/openai/{pubId}/v1`.
- Client `model` values are published aliases, never raw provider model IDs.
- Default aliases are `guide`, `embeddings`, `image`, `transcription`,
  `speech`.
- Provider selection is internal:
  - chat uses published guide/default chat resolution;
  - non-chat uses `ServiceModes`.
- Auth honors `PublishedGuide.AuthMode`.
- API key auth accepts both `Authorization: Bearer <key>` and
  `x-guideants-apikey: <key>`.
- Unknown OpenAI request fields are ignored.
- Known-but-unsupported capabilities return OpenAI-shaped `400`.
- Billing-period language becomes monthly limit (UTC calendar month) because no
  subscription billing-period model exists.
- Usage events must carry `SourceChannel = wire_api`, `PublishedGuideId`,
  `ExternalRequestId`, optional `ExternalUserIdentity`.

## Phase Plan

## Phase 0 — Pre-flight and baseline

Mission: capture the current truth and prevent regression drift during execution.

Tasks:

- Capture `git status`, current branch, and known dirty files.
- Run baseline gates:
  - `cd src/server; dotnet build GuideAntsApi.sln`
  - `cd src/server; dotnet test GuideAntsApi.sln`
  - `cd src/client; npm run build`
  - `cd src/client; npm test -- --run`
- Capture local CodeQL baseline using the repo-local baseline-vs-current method.
  - Full all-language scan once in Phase 0.
- Record known failures/flakes in execution `STATUS.md`.
- Confirm EF tooling works for migrations.

Gate:

- Baseline is recorded (it does not need to be perfect).
- Any known failures are explicitly classified before Phase 1 starts.

## Phase 1 — Data model and usage schema

Mission: add storage shape only.

Tasks:

- Add `WireApiConfigJson` (or equivalent) on `PublishedGuide`.
- Add server/client DTO support for:
  - `wireApiConfig.enabled`
  - `profile`
  - endpoint flags
  - alias map
  - max request sizes
- Extend `UsageEvent` with:
  - `PublishedGuideId`
  - `SourceChannel`
  - `ExternalRequestId`
  - `ExternalUserIdentity`
- Add `UsageCategory.Embeddings = 9` in data model and usage package.
- Add indexes:
  - `PublishedGuideId + Created`
  - `SourceChannel + Created`
  - `ExternalRequestId`
- Generate migration and verify fresh + existing DB apply.

Guardrails:

- No endpoint behavior in this phase.
- Existing usage calls compile without semantic change.
- New fields are nullable/backward compatible.

## Phase 2 — Published API execution context

Mission: create shared auth/cost/context for all wire endpoints.

Tasks:

- Add `PublishedApiExecutionContext`.
- Add resolver/service that resolves `{pubId}`, loads active `PublishedGuide`,
  validates auth, enforces cost limits, checks wire API enabled, and writes
  request metadata into context.
- Support API key auth from bearer and `x-guideants-apikey`.
- Support webhook auth from bearer and `X-Published-Auth`.
- Keep app identity cookie behavior unchanged.
- Add OpenAI-shaped error helper for auth failure, endpoint disabled, model
  alias missing, provider not ready, request too large, and limit exceeded.

Guardrails:

- No anonymous fallback for auth-required guides.
- Reuse existing token validation/crypto paths.
- Do not log keys/tokens/raw auth headers.
- Do not alter MCP/conversation auth except low-risk shared helper extraction.

## Phase 3 — Usage recorder and metering wrappers

Mission: make non-conversation usage impossible to miss.

Tasks:

- Extend `IUsageRecorder.RecordAsync` with optional published/source/request
  fields without breaking existing call sites.
- Add `RecordEmbeddingsAsync`.
- Add `PublishedWireUsageRecorder` (or equivalent) wrapper requiring project,
  notebook, published guide, source channel, external request id, and operation.
- Update published STT path to meter via the same wrapper.
- Add wire metadata schema:
  - endpoint
  - alias
  - provider model/service mode
  - status
  - request bytes
  - input count
  - output count

Guardrails:

- Every successful billable wire call writes at least one usage event.
- Usage write failures are surfaced as server errors for wire APIs.
- Conversation usage behavior stays unchanged except added attribution fields.

## Phase 4 — Wire API handlers

Mission: implement OpenAI-compatible handlers.

Tasks:

- Add endpoint group `/api/published/openai/{pubId}/v1`.
- Implement endpoint behavior:
  - `/models` returns enabled aliases only
  - `/chat/completions` and `/responses` use published conversation execution
  - `/embeddings` uses provider-routed `IEmbeddingService`
  - `/images/generations` uses existing image routing
  - `/audio/transcriptions` uses `ISpeechTranscriptionService`
  - `/audio/speech` uses `ISpeechSynthesisService`
- Add request size validation per endpoint.
- Add OpenAI-like response adapters (IDs, timestamps, objects, choices, usage,
  errors).
- Ship non-streaming first; add streaming only after non-streaming tests are
  green.

Guardrails:

- No provider hardcoding in endpoint handlers.
- No raw internal model IDs exposed unless intentionally aliased.
- Unsupported OpenAI features return explicit OpenAI-shaped errors.

## Phase 5 — Cost limits and reporting

Mission: enforce and visualize costs.

Tasks:

- Enforce daily UTC and monthly UTC limits in `PublishedGuideCostLimitService`.
- Rename UI copy from billing-period limit to monthly limit unless a real
  subscription period service is introduced.
- Include non-conversation events in guide usage totals.
- Keep conversation drilldowns conversation-only.
- Add API usage reporting grouped by source channel, endpoint, alias,
  provider/service mode, status family, events, and charge.
- Add source filters: conversation, published chat, MCP, wire API.

Guardrails:

- Do not force non-conversation events into conversation panels.
- Owner/project usage totals keep including all usage.
- Cost queries use indexed notebook/published/date paths.

## Phase 6 — Publishing UI

Mission: enable safe admin operation of wire APIs.

Tasks:

- Add `APIs` tab in `PublishGuideDialog`.
- Add controls for enablement, endpoint toggles, alias mapping, max request
  sizes, base URL copy, auth-header summary, curl/OpenAI SDK examples.
- Show readiness states:
  - enabled
  - disabled
  - missing provider/service mode
  - missing chat model
  - auth mode unsuitable for server-to-server SDK use
- Align General/Auth/API copy so `AuthMode` remains the single source of truth.
- Preserve one-time API key display behavior.
- Add last-rotated metadata if not already present; otherwise defer to a small
  follow-up migration.

Guardrails:

- Do not add a new auth mode.
- Do not expose provider secrets/service credentials.
- Reuse current tab/dialog visual patterns.

## Phase 7 — Docs, examples, SDK compatibility

Mission: make the feature usable by external clients.

Tasks:

- Add admin docs for base URL, auth headers, endpoint matrix, alias behavior,
  unsupported fields, and cost attribution.
- Add examples for curl, OpenAI JS SDK, OpenAI Python SDK.
- Add troubleshooting docs for:
  - provider not configured
  - endpoint disabled
  - cost limit exceeded
  - auth failed
  - unsupported feature

Gate:

- Docs match actual routes and error names.
- Example calls are smoke-tested where practical.

## Phase 8 — Final acceptance and hardening

Mission: close regressions and acceptance gaps.

Tasks:

- Run full server/client build and tests.
- Run final full CodeQL diff gate (all languages).
- Run manual/live acceptance:
  - API key guide with OpenAI SDK chat call
  - webhook guide auth fail/success
  - anonymous guide (if enabled)
  - OpenRouter-backed chat route
  - one configured non-chat service-mode route (local/HF/OpenAI)
  - cost-limit exceeded response
  - usage appears in guide API usage view
- Update `STATUS.md` final matrix and defer list.

## Global Test Gate (after every phase)

- `cd src/server; dotnet build GuideAntsApi.sln`
- `cd src/server; dotnet test GuideAntsApi.sln`
- `cd src/client; npm run build`
- `cd src/client; npm test -- --run`

Acceptance rules:

- No new failures vs baseline.
- No weakened tests.
- No new secrets.
- No swallowed `401`, `403`, `404`, or usage-write errors.
- CodeQL policy:
  - Phases 2, 4, and 6 run changed-scope CodeQL (`scripts/run-codeql-changed.ps1`).
  - Phase 8 runs full all-language baseline-vs-current diff.

## Final Definition of Done

- Endpoint contracts are stable and OpenAI-shaped.
- No unmetered successful wire calls.
- No provider hardcoding in handlers.
- Auth/cost behavior matches published guide config.
- UI can enable and verify the wire API surface.
- Reporting clearly separates wire API usage from conversations.

## Explicit Gaps Covered

- Published STT usage recording is currently inconsistent.
- Embeddings are missing first-class usage categorization.
- Billing-period limit exists but is not enforced.
- Guide usage reporting is conversation-heavy and lacks a dedicated API usage
  view.
- Publishing UI has auth wording inconsistencies.
- Provider capability differences need explicit validation and error shaping.
- Wire API calls need request-level grouping independent of conversations.
