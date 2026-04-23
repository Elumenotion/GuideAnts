# Service editor closure — acceptance checklist

This document records verification against `docs/settings-service-provider-model-requirements.md` after the service → provider → model editor work.

**Date:** 2026-04-21  
**Scope:** Non-chat service editors on the Settings **Services** tab, service editor API validation, local model operations panels.

**Chat note:** Image generation (local SD and similar) does not consume a chat completion on the diffusion path. Assistants that invoke image tools are still **chat-driven**; the outer LLM turn is subject to the global **Default Chat Model** / `IChatModelResolver` behavior documented in [default-chat-models.md](default-chat-models.md).

## Global (§5.1)

| # | Criterion | Status | Notes |
|---|-----------|--------|--------|
| 1 | No false helper copy for the active service | **Pass** | Copy is provider-scoped per editor (Embeddings, Document Intelligence, Image Generation, Speech Transcription, Speech Synthesis). |
| 2 | Each editor is tailored, not a generic routing matrix | **Pass** | Image and Speech use dedicated layouts; Embeddings/DocIntel use `ServiceEditorBase` with provider-scoped runtime sections. |
| 3 | Provider picker is service-constrained | **Pass** | Still driven by `GET /api/settings/services/{id}` contracts. |
| 4 | Invalid combinations cannot be saved | **Pass** | Client `validateOperativeProviderFields` + server `ValidateProviderFieldUpdate` reject bad URLs, unknown fields, non-operative fields, enum mismatches. |
| 5 | Switching providers preserves drafts | **Pass** | `useServiceEditorDraft` unchanged; controller re-seeds on load. |
| 6 | No raw provider section names as a user-editable primitive | **Pass** | Provider selector uses display names / ids from DTOs. |
| 7 | Validation scoped to visible operative fields | **Pass** | `ProviderFieldsSection` + metadata `operative` filter. |
| 8 | Hidden fields do not block save | **Pass** | Non-operative fields are not in operative list; server rejects explicit writes to diagnostic fields. |
| 9 | Non-operative fields hidden or labeled | **Pass** | Diagnostic fields excluded from operative rendering; legacy fields remain in metadata as non-operative where applicable. |
| 10 | Active provider label vs draft | **Pass** | `ServiceEditorShell` unchanged. |
| 11 | Secrets use `hasValue` | **Pass** | `SecretInput` + DTOs. |
| 12 | Local model 404 / unavailable | **Pass** | `listOutcome` + curated copy; actions gated when unavailable. |

## Per-service

| Service | Status | Notes |
|---------|--------|--------|
| Embeddings §5.2 | **Pass** | Runtime behavior copy; validation; rebuild action preserved. |
| Image §5.3 | **Pass** | Profile inference + size lists; `LocalOutputFormat` persisted and applied on local SD path in `NotebookImageService`. Bundle manager now exposes engine liveness + loaded bundle id, explicit **Load engine** / **Unload engine** controls backed by `/local-models/{load,unload}`, a hot-swap **Activate bundle** flow that restarts `sd-server` in place (no `guideants-ai` restart required), and compact icon row actions with accessible labels/tooltips. |
| Document Intelligence §5.4 | **Pass** | Throughput vs parse cost; Docling copy. |
| Speech Transcription §5.5 | **Pass** | Behavior copy; local ops panel for non-cloud. |
| Speech Synthesis §5.6 | **Pass** | SSML vs local plain-text copy; local ops panel for non-cloud. |

## Automated tests added

- Client: `src/client/src/pages/settings/state/__tests__/serviceEditorValidation.test.ts`
- Client: `src/client/src/pages/settings/editors/image-generation/__tests__/ImageBundleManager.test.tsx` (hot-swap via Activate bundle; Load engine; Unload engine — replaces the old "restart required" banner test).
- Server: `src/server/GuideAntsApi.Tests/Settings/ServiceEditorUpdateValidationTests.cs`

## Manual follow-ups (optional)

- End-to-end UI test for provider switch + save on each service.
- Extend `/local-models/unload` to Speech Transcription and Speech Synthesis once those sub-services grow a `/admin/unload` handler (today the C# proxy already forwards the call; the sub-service returns 404 until implemented).
