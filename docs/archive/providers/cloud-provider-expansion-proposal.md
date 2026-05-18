# Cloud Provider Expansion Proposal

> Historical - not current implementation guidance.
>
> Superseded by:
> - [setup-guide.md](../../setup-guide.md)
> - [chat-providers-architecture-and-extensibility.md](../../chat-providers-architecture-and-extensibility.md)
> - [settings-architecture.md](../../settings-architecture.md)
> - [settings-and-llama-completion-requirements.md](../../settings-and-llama-completion-requirements.md)

Date: 2026-04-24

## Status

This document replaces the previously referenced cloud provider expansion proposal that was missing from the repository.

Current implementation status:
- Complete: provider IDs and settings section names remain stable for `google-vertex-chat`, `hf-inference-chat`, and `openrouter-chat`.
- Complete: chat dispatch now uses provider-native Google Vertex, Hugging Face Router, and OpenRouter adapters instead of OpenAI pass-through wrappers.
- Complete: runtime validation remains in the existing routing/readiness surfaces.
- Complete: OpenRouter image generation/edit now uses the chat-completions multimodal path.
- Complete: Google, Hugging Face, and OpenRouter speech/image request payloads use named DTOs instead of anonymous provider payloads.
- Complete: ASR validation cross-wiring between Google and Hugging Face has been removed.
- Complete: focused adapter, routing, image, transcription, and synthesis regression tests cover the remediated paths.
- Follow-up: Google credential loading still uses `GoogleCredential.FromStream(...)` in a few places and should be migrated to the newer factory-based API in a future cleanup.

## Locked Contracts

These public identifiers are intentionally preserved:
- Chat providers: `google-vertex-chat`, `hf-inference-chat`, `openrouter-chat`
- Settings sections: `GoogleVertexAi`, `HuggingFace`, `OpenRouter`

These synchronization points must remain aligned:
- `IChatTargetValidator.KnownProviders`
- `RoutingChatCompletionClientFactory.ParseProvider`
- `RoutingReadinessService` provider-to-section mapping
- client settings/provider section mapping

## Chat Adapters

### Google Vertex
- Uses Vertex AI `generateContent` and `streamGenerateContent?alt=sse`.
- Reads native config from `GoogleVertexAi:ProjectId`, `Location`, plus either `ApiKey` or `ServiceAccountJson`.
- Maps system/developer messages into `systemInstruction`.
- Maps assistant tool calls to Google function calls and tool messages to function responses.
- Returns normalized finish reasons and token usage in `ChatCompletionResponse`.

### Hugging Face
- Uses the Hugging Face Router `/chat/completions` endpoint directly.
- Reads native config from `HuggingFace:Token` and optional `RouterBaseUrl`.
- Preserves tool definitions, tool calls, streaming deltas, and token usage mapping.

### OpenRouter
- Uses `/chat/completions` directly for chat and multimodal image generation/edit.
- Reads native config from `OpenRouter:ApiKey`, optional `BaseUrl`, `HttpReferer`, and `AppTitle`.
- Preserves tool definitions, tool calls, streaming deltas, and token usage mapping.

## Non-Chat Provider Rules

- Hugging Face non-chat remains on task-specific inference APIs.
- OpenRouter image generation/edit uses multimodal chat payloads, not `/images/generations` or `/images/edits`.
- Unsupported provider/model capability combinations must fail explicitly.

## Verification Snapshot

Focused verification completed on 2026-04-24:
- `RoutingChatCompletionClientFactoryTests`
- `ProviderNativeChatClientTests`
- `NotebookImageServiceTests`
- `SpeechTranscriptionServiceTests`
- `SpeechSynthesisServiceTests`

Result:
- Passed: 40
- Failed: 0

