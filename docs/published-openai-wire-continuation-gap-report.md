# Published Wire API Continuation and Tool Bridging Requirements

Date: 2026-06-25

## Purpose

GuideAnts exposes published guide endpoints that emulate provider wire APIs while still running requests through the GuideAnts conversation system. These endpoints must preserve the behavior expected by provider clients and also preserve GuideAnts-specific behavior such as guide instructions, notebook/project context, internal tools, tracing, persistence, and usage accounting.

This document defines the required continuation and tool-calling behavior for these published wire endpoints:

- OpenAI Chat Completions-compatible endpoint
- OpenAI Responses-compatible endpoint
- Anthropic Messages-compatible endpoint

The goal is to prevent a logical client conversation from being split across multiple internal GuideAnts conversations and to ensure tool calls/results resume the correct pending internal turn.

## Problem statement

Provider clients do not all identify conversation state the same way.

OpenAI Chat Completions is caller-managed: each request contains a `messages` array, and the caller continues a conversation by resending the prior conversation messages along with the new user message. The published Chat Completions endpoint must therefore recover the existing internal GuideAnts conversation by matching the full client-visible transcript supplied in `messages`.

OpenAI Responses supports multiple state mechanisms: an explicit `conversation` object, `previous_response_id`, and manual replay of prior input/output items. The published Responses endpoint must support all intended state mechanisms if it is meant to behave like the current Responses API state model.

Anthropic Messages supports message-shaped transcripts and provider message ids. Its continuation behavior provides the baseline pattern for transcript matching and tool callback resumption, but the implementation should not remain Anthropic-specific.

When continuation resolution is incomplete, the service can create a new internal GuideAnts conversation for a second turn that should have continued an existing one. If that second turn involves tools, the callback may then fail because the returned tool id belongs to a pending invocation in a different internal conversation or turn.

## Definitions

| Term | Meaning |
| --- | --- |
| Published wire endpoint | A GuideAnts HTTP endpoint that accepts and returns provider-shaped API payloads for a published guide. |
| Internal conversation | The persisted GuideAnts `NotebookConversation` and its turns/messages. |
| Provider transcript | The conversation history supplied in the provider's request shape, such as Chat Completions `messages` or Responses `input` items. |
| Client-visible transcript | The subset of the conversation the external provider client can see and replay. It may omit GuideAnts-internal tool messages, guide reminders, or internal context. |
| Guide concerns | Guide instructions, notebook/project context, local command caveats, internal tools, tracing, persistence, usage accounting, and caller scope. |
| Tool bridge | The translation layer that accepts provider-shaped tool definitions/results, runs GuideAnts' internal tool orchestration, and emits provider-shaped tool calls/responses. |
| Pending client tool | A tool call that GuideAnts has emitted to the external client and is waiting for the client to satisfy. |

## Required external behavior

### OpenAI Chat Completions

The Chat Completions-compatible endpoint must treat the caller's `messages` array as the conversation state supplied by the client.

Required behavior:

- Parse the full `messages` array into a normalized transcript.
- Split the latest user message into the new instruction for the next internal turn.
- Use the prior messages as the replayed prefix to find an existing internal GuideAnts conversation.
- Match the full replayed prefix transcript against persisted internal messages, not just one assistant message.
- Use a latest assistant message only to narrow candidate conversations before full transcript verification.
- Continue the matched internal conversation when the replay is a confident match.
- Start a new internal conversation only when no confident match exists.
- Avoid injecting the replayed prefix as duplicate seed messages when an internal conversation is found.
- Preserve normal Chat Completions tool-call semantics: provider tool definitions in, assistant `tool_calls` out, trailing `tool` messages back in as callbacks.

Chat Completions should not use `chatcmpl_*` ids as conversation ids. Standard Chat Completions continuation is transcript replay, not response-id chaining.

### OpenAI Responses

The Responses-compatible endpoint must implement the current Responses state model used by provider clients.

Continuation mechanisms should be resolved in this order:

1. Explicit `conversation` parameter.
2. `previous_response_id`.
3. Manual replay through `input` items/messages.

Required `conversation` behavior:

- Accept a `conversation` parameter on `OpenAiResponsesRequest`.
- Maintain a durable mapping between external OpenAI-style conversation ids and internal GuideAnts conversation ids.
- Validate notebook scope and caller identity before resolving the mapped conversation.
- Continue the mapped internal conversation without adding replayed prefix items as duplicate seed history.
- If both `conversation` and `previous_response_id` are supplied, verify they refer to the same internal conversation.
- Return a provider-compatible invalid request or not found error when an explicit `conversation` id is missing, inaccessible, or inconsistent with `previous_response_id`.

Required `previous_response_id` behavior:

- Continue resolving `previous_response_id` through stable response ids mapped to persisted assistant messages, such as `resp_<assistantMessageId>`.
- Verify notebook scope, caller identity, and latest-assistant/branch validity.
- Reject invalid, inaccessible, or stale explicit response ids.
- Do not silently fall back to transcript matching when an explicit `previous_response_id` is invalid.

Required manual replay behavior:

- When neither `conversation` nor `previous_response_id` is supplied, parse `input` into a normalized replayed transcript.
- Match the full replayed prefix transcript against persisted internal messages.
- Continue the matched internal conversation when the replay is a confident match.
- Start a new internal conversation with the replayed prefix only when no confident match exists.
- Avoid duplicate seed messages when an existing internal conversation is resolved.

Required tool behavior:

- Parse Responses `tools` into internal tool definitions.
- Emit Responses-shaped function/tool call output items when waiting on the client.
- Accept trailing `function_call_output` or equivalent tool-result items as callbacks for pending client tools.
- Treat historical replayed tool outputs as transcript context, not active callbacks.

### Anthropic Messages

The Messages-compatible endpoint should use the same provider-neutral continuation and tool bridge primitives as the OpenAI endpoints.

Required behavior:

- Continue by explicit provider message id when a valid latest assistant message id is supplied.
- Continue by full replayed transcript matching when no explicit id is available.
- Use provider message ids and tool ids only as stable references, not as substitutes for scope checks.
- Preserve Anthropic-shaped tool use/result translation while sharing the same internal pending-tool semantics as OpenAI endpoints.

## Transcript matching requirements

A provider-neutral transcript resolver should be used by all published wire endpoints that accept replayed conversation history.

The resolver must:

- Normalize provider messages/items into a common transcript model containing role, text content, assistant tool call ids, and tool result ids.
- Include provider roles that affect model behavior, such as `system`, `developer`, `user`, `assistant`, and `tool`, where present.
- Compare against persisted GuideAnts conversation messages after normalizing text and tool ids.
- Match the full client-visible replayed prefix, not a single message.
- Allow persisted internal-only messages that the client could not have replayed.
- Require the matched conversation to belong to the same published notebook/guide scope.
- Require the matched turn to belong to the same caller identity. Published callers always carry an identity: an authenticated internal user id, or an API-key-derived external caller identity for otherwise anonymous callers. The resolver must scope every candidate to that identity and must never cross-attach to another caller's conversation.
- Require the replayed latest assistant candidate to correspond to the latest persisted assistant message in the internal conversation.
- Return no match instead of guessing when the replayed prefix is empty, ambiguous, or lacks enough evidence.

The resolver may use the latest assistant text and tool call ids as a database candidate filter, but final resolution must be based on the normalized transcript.

### Candidate selection and performance

Full transcript matching is cheap because the candidate set is bounded and evaluation short-circuits on the first mismatch:

- Restrict candidates to the published notebook/guide scope and the resolved caller identity (internal user id or API-key-derived external identity).
- Restrict candidates to conversations with activity in the previous 60 minutes (most recent persisted message created within the window). Conversations idle longer than the window are not eligible for replay continuation and fall through to new-conversation creation.
- Order candidates by most recent activity first (turn index, then created, descending) so the likely match is evaluated first.
- Compare the replayed prefix against each candidate message by message and abandon that candidate as soon as any message mismatches, moving on to the next candidate. Per-request cost stays proportional to the first matching or quickly rejected candidates, not the full notebook history.
- Still require the latest replayed assistant candidate to correspond to the latest persisted assistant message before accepting a match.

## Tool bridge requirements

The published wire layer must bridge provider tool calling without bypassing GuideAnts' internal guide execution model.

Inbound request handling:

- Parse provider tool definitions into internal `ChatToolDefinition` values.
- Preserve function names, descriptions, parameter schemas, and supported tool choice controls.
- Merge client-provided tools with GuideAnts guide/internal tools according to the existing internal tool policy.
- Keep guide instructions, notebook context, local command caveats, tracing, persistence, and usage reporting active for every provider request.
- Treat replayed historical tool outputs as transcript context unless they are trailing callback items for the active pending turn.

Outbound assistant handling:

- When the internal runner needs a client-side tool, emit provider-shaped tool calls with stable ids.
- Persist those ids on the internal assistant message/turn.
- Mark the internal turn as waiting for a client tool and keep it pending until the callback arrives.
- Return provider-compatible finish reasons or output item types for pending tools.

Inbound callback handling:

- Resolve the pending internal conversation and turn from callback tool ids and, when supplied, explicit Responses state.
- Append provider tool results to the same internal turn.
- Resume the internal runner without replaying already-satisfied server-side or client-side tool invocations.
- Return either a final provider-shaped assistant response or the next provider-shaped pending tool call.

## Current implementation assessment

Primary implementation file:

- `src/server/GuideAntsApi/Endpoints/PublishedOpenAiWireEndpoints.cs`

Supporting implementation files:

- `src/server/GuideAntsApi/Services/Conversations/PublishedConversationService.cs`
- `src/server/AntRunner.Chat/AntRunner.Chat/ThreadRun.cs`

Current behavior and gaps:

| Capability | Current status | Required change |
| --- | --- | --- |
| Chat Completions replay continuation | The request is parsed into `ClientPromptParts`, but non-tool requests do not resolve an existing internal conversation from the replayed `messages` prefix. | Add full transcript resolution before `ExecuteConversationAsync`. |
| Chat Completions response id continuation | `chatcmpl_*` ids are generated, but they are not a valid thread continuation mechanism. | No id-based Chat continuation should be added. Use transcript replay. |
| Responses `previous_response_id` | Existing path resolves `resp_<assistantMessageId>` and avoids duplicate prefix injection when resolved. | Keep this path and preserve invalid/stale id errors. |
| Responses `conversation` | `OpenAiResponsesRequest` does not model `conversation`. | Add request support and durable external-to-internal conversation mapping. |
| Responses manual replay | Parsed input can produce prefix messages, but replay matching is not used when no explicit state id is supplied. | Add full transcript replay resolution as fallback after explicit state mechanisms. |
| Anthropic transcript continuation | Existing resolver is Anthropic-named but mostly provider-neutral in behavior. | Rename/generalize and reuse across provider endpoints. |
| Tool callback parsing | Chat and Responses trailing tool callback parsing exists. | Preserve and expand provider-specific regression coverage. |
| Mixed server/client tool resume | Shared runner/service paths are intended to skip already-satisfied tools and preserve pending client tool state. | Add OpenAI Chat and Responses tests that prove the behavior through provider wire shapes. |

## Required implementation changes

### 1. Introduce a provider-neutral transcript model

Create or rename the current Anthropic-specific transcript model into a common model, for example:

- `WireTranscriptMessage`
- `BuildWireTranscriptHistory`
- `ResolveConversationFromTranscriptAsync`

The resolver should accept normalized `ChatMessage` values produced by each provider parser and return a matching internal `NotebookConversationId` only when the full replayed prefix confidently matches persisted history.

### 2. Use transcript resolution in Chat Completions

In `PostChatCompletionsAsync`, resolve the replayed prefix before creating or executing a conversation.

Expected control flow:

```csharp
var clientPrompt = BuildOpenAiChatClientPrompt(request.Messages);
var instructions = clientPrompt.UserPrompt;

var existingConversationId = await ResolveConversationFromTranscriptAsync(
    context,
    clientPrompt.PrefixMessages,
    db,
    httpContext.RequestAborted);

conversation = await ExecuteConversationAsync(
    publishedConversationService,
    db,
    context,
    instructions,
    httpContext.RequestAborted,
    existingConversationId: existingConversationId,
    clientMessages: existingConversationId.HasValue ? null : clientPrompt.PrefixMessages,
    clientToolDefinitions: clientToolDefinitions);
```

This makes Chat Completions behave like a normal caller-managed Chat API: the external client owns the replayed transcript, and GuideAnts maps that transcript to internal state.

### 3. Add full Responses state support

Add `conversation` to `OpenAiResponsesRequest` and implement a durable mapping for external conversation ids.

Recommended mapping fields:

- Provider or wire protocol name, such as `openai_responses`.
- External conversation id, such as `conv_<id>`.
- Internal `NotebookConversationId`.
- Published guide or notebook id.
- Internal user id or external caller identity.
- Created timestamp and last-used timestamp.

Responses resolution should then use this order:

1. Resolve and validate `conversation` when supplied.
2. Resolve and validate `previous_response_id` when supplied.
3. If both are supplied, ensure they refer to the same internal conversation.
4. If neither is supplied, resolve from manual replayed input transcript.
5. If no manual replay match is found, create a new internal conversation.

### 4. Preserve guide-aware execution on every path

All continuation paths must eventually call the same guide-aware execution path used for new published guide turns. Continuation resolution should choose the internal conversation; it should not replace guide instruction injection, context building, internal tool routing, tracing, persistence, or usage reporting.

When a conversation is resolved from explicit state or transcript replay, pass `clientMessages: null` so the client replay is not duplicated into the model context. When no conversation is resolved, pass the replayed prefix as seed messages for the new internal conversation.

### 5. Complete provider tool bridge parity

Ensure Chat Completions, Responses, and Anthropic Messages all satisfy the same internal tool bridge contract:

- Provider tool definitions are converted to internal tool definitions.
- Internal pending client tools are emitted in provider shape.
- Provider callback tool results are appended to the pending internal turn.
- Resumption skips already-satisfied tools.
- The pending turn stays pending until the client callback arrives.
- Historical replayed tool outputs do not trigger an active callback path.

## Test plan

Add or update tests in:

- `src/server/GuideAntsApi.Tests/Endpoints/PublishedOpenAiWireHandlersTests.cs`
- `src/server/GuideAntsApi.IntegrationTests/Services/Conversations/PublishedConversationStreamingTests.cs`

Chat Completions tests:

- A second request with replayed `user -> assistant -> user` messages continues the first internal conversation.
- Repeated assistant text across multiple conversations does not attach to the wrong conversation when the full transcript differs.
- Replayed history that omits server-internal tool messages still matches the persisted conversation.
- Resolved continuation does not seed duplicate prefix messages into the next model call.
- Historical non-trailing tool messages do not trigger a pending-tool resume.
- Client tool definitions are bridged while guide concerns still reach the internal runner.
- Pending client tool callback resumes the same internal conversation and does not create a new one.

Responses tests:

- `conversation` continues the mapped internal conversation.
- `conversation` plus `previous_response_id` succeeds only when both refer to the same internal conversation.
- `conversation` returns an error when missing, inaccessible, or scoped to a different caller.
- `previous_response_id` continuation remains deterministic and rejects stale/non-latest ids.
- Invalid `previous_response_id` does not fall back to transcript matching.
- Manual replay without `conversation` or `previous_response_id` continues the existing internal conversation.
- Resolved manual replay does not seed duplicate prefix messages.
- Manual replay with prior function call output tolerates persisted server-side tool messages.
- Function tool definitions/results are bridged while guide concerns still reach the internal runner.

Shared transcript-resolution tests:

- A replayed prefix continues a candidate conversation active within the last 60 minutes.
- A replayed prefix that only matches a conversation idle longer than 60 minutes starts a new internal conversation instead of continuing it.
- A replayed prefix is never attached to a conversation owned by a different caller identity (different internal user, or different API-key-derived external identity), even when the transcript text is identical.
- Candidate evaluation short-circuits on the first message mismatch and resolves the correct conversation when an earlier-ordered candidate diverges.

Shared tool-resume tests:

- A server-executed tool call followed by a client-executed tool call does not fail with a stale pending-tool error.
- Already-satisfied tool calls are skipped when resuming after client tool output.
- Turn status remains `pending_client_tool` until the callback arrives.
- Trace data remains available after provider wire API errors.

## Priority

| Priority | Item | Rationale |
| --- | --- | --- |
| P0 | Chat Completions full transcript continuation | Chat continuation is caller-managed through `messages`; this is the normal thread mechanism. |
| P0 | Responses `conversation` support | Full Responses state compatibility requires explicit persistent conversations. |
| P0 | Responses manual replay fallback | Responses clients can choose manual item replay instead of explicit state ids. |
| P0 | Provider tool bridge parity | Multi-turn tool workflows require tool ids/results to resume the correct internal pending turn. |
| P1 | Provider-neutral transcript resolver refactor | Shared semantics reduce drift across Chat, Responses, and Messages. |
| P1 | OpenAI-specific mixed tool regression coverage | Shared code paths need proof through each provider's wire shape. |

## Acceptance criteria

The implementation is complete when all of the following are true:

- Chat Completions resolves caller-managed `messages` replay to the existing internal conversation by matching the full client-visible transcript.
- Chat Completions creates a new internal conversation only when the replayed prefix has no confident match.
- Transcript resolution only considers conversations within the caller's identity/notebook scope that have been active in the previous 60 minutes, and never cross-attaches to another caller's conversation.
- Responses supports explicit `conversation`, `previous_response_id`, and manual replay continuation.
- Responses returns errors for invalid, inaccessible, inconsistent, or stale explicit state references.
- Anthropic Messages, OpenAI Chat Completions, and OpenAI Responses all use provider-neutral transcript resolution for replayed conversation history.
- After any resolved continuation, the next run receives the new user instruction plus persisted internal history, not duplicated client replay history.
- Tool definitions, assistant tool calls, and callback tool results are bridged for Chat, Responses, and Messages without dropping guide instructions, guide context, tracing, persistence, usage accounting, or internal tools.
- Tool-result callbacks for Chat, Responses, and Messages resume the pending internal turn without creating a new conversation.
- Mixed server/client tool execution does not produce stale pending-tool errors.
- Trace display remains available and does not spin indefinitely after a published wire API error.

## Non-goals

- Do not use assistant text alone as a conversation key.
- Do not add synthetic thread keys based on repeated assistant content.
- Do not treat `chatcmpl_*` response ids as conversation ids.
- Do not silently recover from explicitly invalid `previous_response_id` or inaccessible `conversation` values.
- Do not bypass guide-aware execution because the external protocol supplies its own transcript or tools.

## Summary

The published wire APIs must behave like provider-compatible protocol adapters over the GuideAnts conversation engine.

For Chat Completions, the client-owned `messages` transcript is the thread. GuideAnts must map that full transcript to the correct internal conversation before running the next turn.

For Responses, compatibility requires all current state surfaces: `conversation`, `previous_response_id`, and manual replay.

For all provider wire APIs, continuation and tool bridging are inseparable. The endpoint must identify the correct internal conversation, inject guide concerns, expose provider-shaped tool calls, accept provider-shaped tool results, and resume the same pending internal turn.
