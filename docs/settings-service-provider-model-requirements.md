# Settings Requirements: Service -> Provider -> Model

Last updated: 2026-04-30

This document defines the non-chat service editor requirements for the Settings
**Services** tab. It is the normative baseline for service editor UX and data behavior.

## 1. Model and ownership

1. The hierarchy is `service -> provider -> model/runtime options`.
2. Provider selection is subordinate to a specific service.
3. Provider fields are provider-scoped; they do not bleed across providers.
4. Service-level fields are only for true cross-provider behavior.
5. Chat routing is out of scope for this document.

## 2. Settings IA context

The Services tab lives inside the 7-tab Settings IA:

1. Overview
2. Personalization
3. Connections
4. Models & Runtime
5. Services
6. Infrastructure
7. Telemetry

Responsibilities split:

- **Services**: non-chat service provider and runtime behavior editing.
- **Connections**: credential/connection section editing.
- **Models & Runtime**: catalog/runtime profiles/local llama runtime operations.
- **Infrastructure**: runtime-owned dependency visibility/probes.

## 3. Service editor interaction contract

1. Every non-chat service editor renders, in order:
   - Service header (persisted active provider + readiness)
   - Provider selector for that service
   - Provider-specific settings
   - Service-level settings (if any)
   - Action row (save/reset/refresh pattern as applicable)
2. Active provider label in header reflects persisted server state.
3. Draft provider selection before save is shown as draft/edit context, not persisted active state.
4. Provider switch preserves in-session per-provider drafts.
5. Validation runs only for visible operative fields of the selected provider.
6. Hidden provider fields do not block save for current provider.
7. Runtime-owned dependencies are shown as operational dependencies (not free-form edits) unless explicitly editable.
8. Secret fields use non-revealing UI with `hasValue` semantics.
9. Unavailable local-model capability states (e.g., list route unavailable) must show operator copy, not raw JSON dump.
10. Local model actions are disabled/omitted when capability is unavailable.

## 4. Services in scope

Non-chat services managed by Services tab:

- Embeddings
- Image Generation
- Document Intelligence
- Speech Transcription
- Speech Synthesis

Each service editor must:

1. Offer only provider options declared for that service contract.
2. Enforce provider-specific required fields.
3. Keep provider-specific values isolated.
4. Surface readiness status and validation blockers in-context.

## 5. Provider compatibility and schema behavior

1. Service editors derive allowed providers from server contracts, not ad-hoc section-name guesses.
2. Provider-model compatibility must remain explicit and validated.
3. Non-operative persisted fields are hidden or clearly marked non-operative.
4. Validation errors are field-specific and actionable.

## 6. Save, concurrency, and reliability

1. Save operations use row-version optimistic concurrency behavior.
2. Stale writes return `409` with explicit reload/reapply behavior.
3. Service editor save should not mutate unrelated provider drafts.
4. Provider switching does not drop unsaved draft values for other providers.

## 7. Local operation semantics

For services that expose local model/runtime operations:

1. Operations are explicit and stateful (download/select/load/unload/remove as applicable).
2. Destructive actions require confirmation.
3. Operation status is visible and deterministic.
4. Capability absence is treated as deployment capability mismatch, not unexpected app failure.

## 8. Non-regression expectations

1. Service-specific copy remains service-specific (no generic chat/LLM wording on non-chat editors).
2. Connections tab remains the only credential ownership surface.
3. Infrastructure tab remains the runtime dependency ownership surface.
4. Service editors remain the only non-chat active-provider ownership surface.
