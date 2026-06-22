# ConversationService Composition Plan

## Problem Summary

`ConversationService` has become a large orchestration class that owns multiple unrelated responsibilities:

- conversation list/detail projections
- conversation create/rename/delete/edit/undo commands
- distributed and in-process conversation locking
- streaming lifecycle and SSE event forwarding
- chat runner invocation
- turn and message persistence
- assistant history construction and assistant-switch filtering
- attachment validation and conversion to chat content
- thinking block persistence/rendering
- generated-file URL rewriting
- usage recording

The file is currently over 3,000 lines. The largest risk area is the streaming path, especially `SendMessageStreamToConversationAsync` and `StartChatRunnerBackgroundTask`, because it mixes lock handling, event emission, callback processing, database writes, turn status changes, usage recording, and cleanup.

## Holistic Folder Review

The composition work should be framed as an organization pass over the conversation runtime, not only as a `ConversationService` split. The rest of `src/server/GuideAntsApi/Services/Conversations` already contains adjacent or duplicated responsibilities that will affect the shape of the extraction:

- `PublishedConversationService` mirrors much of the private streaming path: turn/message persistence, tool callbacks, thinking block persistence, usage recording, attachment handling, and generated-file URL rewriting.
- `PublishedAssistantHistoryBuilder`, `ConversationService`, `ConversationManager`, and `Agent` all contain versions of duplicate assistant message filtering or database-message-to-chat-message conversion.
- `ConversationService` and `PublishedConversationService` both contain URL rewriting helpers with different private-vs-published policies.
- `TurnManager` exists, but the current streaming path creates and completes `ConversationTurn` rows directly. `IMessageManager` exists as an interface but does not appear to have a production implementation in this folder.
- Many callback paths open fresh scopes through `IServiceScopeFactory`, which is sensible for long-running stream callbacks, but the database ownership is currently spread across orchestration code.
- `ConversationBroadcastHub`, `DistributedConversationLockService`, and `LockCleanupBackgroundService` are cohesive infrastructure pieces and should stay mostly independent from query/command/streaming extraction.

The main organizational risk is adding new services beside old abstractions without deciding which layer owns turns, messages, history, and streaming persistence. The refactor should converge duplicate concepts rather than simply moving code into more files.

## Organization Recommendations

### Keep facades stable, split internals by runtime concern

Keep `IConversationService` and `IPublishedConversationService` stable at first so endpoint churn stays low. Internally, organize around reusable conversation runtime collaborators:

```text
Services/Conversations/
  Facades/
    ConversationService.cs
    PublishedConversationService.cs
  Queries/
    ConversationQueryService.cs
    UserConversationQueryService.cs
  Commands/
    ConversationCommandService.cs
    ConversationUndoService.cs
  Streaming/
    ConversationStreamOrchestrator.cs
    ConversationStreamRunner.cs
    PublishedConversationStreamRunner.cs
    StreamingEventFactory.cs
    StreamingEventSink.cs
  Persistence/
    ConversationTurnStore.cs
    ConversationMessageStore.cs
    ConversationLockCoordinator.cs
  Mapping/
    ConversationMessageMapper.cs
    ConversationHistoryBuilder.cs
    ThinkingBlockMapper.cs
  Attachments/
    ConversationAttachmentService.cs
    AttachmentMessageBuilder.cs
    AttachmentMarkdownChatPolicy.cs
  Content/
    AssistantContentSanitizer.cs
    PrivateConversationFileUrlPolicy.cs
    PublishedConversationFileUrlPolicy.cs
  Infrastructure/
    ConversationBroadcastHub.cs
    DistributedConversationLockService.cs
    LockCleanupBackgroundService.cs
```

This folder shape can be introduced incrementally. Do not move every file at once; move files when the extracted service makes the ownership boundary real.

### Make published/private differences explicit

Private and published conversations should share the same underlying concepts where possible:

- history filtering
- message and DTO mapping
- thinking block persistence/rendering
- stream event payload construction
- turn/message persistence primitives
- usage recording
- attachment row persistence and file-to-chat-content conversion

They should differ through explicit policy objects or small mode-specific collaborators:

- user identity and authenticated ownership checks
- published guide/assistant resolution
- private vs published file URL rewriting
- client-tool resume behavior
- broadcast/lock behavior, if published conversations intentionally do not use the same lock/broadcast lifecycle

This avoids a common failure mode: cleaning up `ConversationService` while leaving `PublishedConversationService` as a second copy of the old design.

### Reconcile legacy managers before adding stores

Before introducing `ConversationTurnStore` and `ConversationMessageStore`, decide the fate of:

- `TurnManager`
- `ITurnManager`
- `IMessageManager`
- duplicated `ToChatMessage` helpers in `ConversationService`, `ConversationManager`, and `Agent`

Recommended direction:

- Move current database turn/message write primitives into `ConversationTurnStore` and `ConversationMessageStore`.
- Keep `ConversationManager` only for current-state/cache concerns if still needed.
- Remove unused `IMessageManager`, or implement it as the new message store if callers still need that name.
- Deprecate or narrow `ITurnManager` once the streaming path no longer depends on direct turn writes.

### Keep scope creation at persistence boundaries

The streaming code often creates fresh scopes inside callbacks. That is probably the correct lifetime behavior, but it should be hidden behind persistence collaborators. Orchestrators and runners should not need to know how many scopes are opened to update a partial assistant message or mark a turn cancelled.

Prefer verbs like:

- `CreateTurnAsync`
- `CreateUserMessageAsync`
- `StartAssistantMessageAsync`
- `AppendOrFinalizeAssistantMessageAsync`
- `CreateToolMessageAsync`
- `SetTurnStatusAsync`
- `PersistRunOutputAsync`
- `PersistThinkingBlocksAsync`
- `PruneIncompleteToolCallsAsync`

This keeps callback code readable while preserving fresh-DbContext safety.

## Current Safety Net

Before decomposition, we added characterization coverage around the highest-risk behavior.

### Added real streaming persistence coverage

`src/server/GuideAntsApi.IntegrationTests/Services/Conversations/ConversationServiceIntegrationTests.cs`

New test:

- `SendMessageStream_Persists_turn_messages_usage_and_releases_lock`

This drives the real SSE endpoint with the real `ConversationService` and the integration test fake chat client. It verifies:

- stream completes successfully
- assistant and usage events are emitted
- a `ConversationTurn` is created with `Status = completed`
- `ChatRunOutputJson` and `UsageJson` are persisted
- user message is persisted with a user id
- assistant message is persisted, finalized, attributed to the assistant, and no longer streaming
- chat usage is recorded against the assistant message
- the conversation lock is released

### Tightened user conversation ownership coverage

Existing test changed:

- `GetUserConversations_excludes_deleted_projects_and_applies_search_sort_paging`

The test now goes through `/api/conversations` with an authenticated user and verifies that:

- deleted project conversations are excluded
- another user's live conversation is excluded
- search, sort, and paging still work

This exposed and fixed an existing bug in `ConversationService.GetUserConversationsAsync`: it selected conversations from all messages instead of filtering by the authenticated user's `UserId`.

## Behavior Fix Included With Tests

`src/server/GuideAntsApi/Services/Conversations/ConversationService.cs`

`GetUserConversationsAsync` now resolves `ICurrentUserService` and filters:

```csharp
db.NotebookConversationMessages
    .Where(m => m.UserId == currentUser.UserId)
    .Select(m => m.NotebookConversationId)
    .Distinct();
```

This is intentionally small, but it matters for composition because the future query component should preserve user-scoped behavior.

## Verification

Targeted checks passed:

```powershell
dotnet test src/server/GuideAntsApi.IntegrationTests/GuideAntsApi.IntegrationTests.csproj --filter "FullyQualifiedName~ConversationServiceIntegrationTests"
```

Result:

- 30 passed

```powershell
dotnet test src/server/GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~ConversationService"
```

Result:

- 29 passed

## Proposed Composition

Keep `IConversationService` as a public facade at first. This minimizes endpoint churn while allowing internal extraction behind the existing contract.

### ConversationQueryService

Owns read-only projections:

- `GetConversationByIdAsync`
- `GetConversationWithMessagesAsync`
- `GetListAsync`
- `GetUserConversationsAsync`

This service should keep the optimized SQL projections and `READ UNCOMMITTED` behavior where currently used.

### ConversationCommandService

Owns simple conversation mutations:

- `CreateConversationAsync`
- `RenameConversationAsync`
- `DeleteConversationAsync`
- `EditMessageAsync`

This is comparatively low risk and can be extracted early.

### ConversationUndoService

Owns undo behavior:

- acquire/release undo lock
- remove messages and turns from a target turn onward
- broadcast `turn_removed`

This should use the existing lock behavior as a contract. Undo currently has special orphaned-lock recovery semantics that must be preserved.

### ConversationStreamOrchestrator

Owns the top-level streaming flow:

- validate request
- acquire distributed lock
- acquire local conversation semaphore
- broadcast lock/turn/started/unlock/complete lifecycle events
- create stream context
- create turn and user message
- process attachments
- initialize the stream runner
- forward channel events to caller and observers
- mark turn completed/cancelled

This service should be thin and should not directly persist individual assistant/tool callback messages.

### ConversationStreamRunner

Owns the callback-heavy chat runner path currently inside `StartChatRunnerBackgroundTask`:

- invoke `ChatRunner.RunThread`
- handle streaming progress callbacks
- create/update/finalize assistant messages
- create tool messages
- sanitize generated content before persisting/broadcasting
- emit usage/thinking/error/cancelled events to the stream channel

This is the riskiest extraction and should be done after pure helpers and persistence helpers are carved out.

For published conversations, either add a sibling published runner or introduce a small stream-mode policy. Avoid forcing private and published flows into one runner if the result is a large conditional class.

### ConversationTurnStore

Owns turn and message persistence primitives:

- create next turn
- create user message
- set turn status
- update `LastUpdated`
- persist `ChatRunOutputJson`, `UsageJson`, `FilesCreated`, and `FilesModified`
- finalize partial assistant messages
- prune incomplete tool calls after cancellation

This should remove repeated "attach stub, update content/status, update turn.LastUpdated" blocks from the streaming runner.

This store should also absorb the equivalent repeated persistence blocks from `PublishedConversationService`. If it only serves private conversations, the duplicate persistence problem remains.

### ConversationHistoryBuilder

Owns chat history construction:

- `PrepareMessagesForAssistantAsync`
- assistant-switch detection
- assistant-switch handoff message
- tool-call filtering
- database message to `ChatMessage` conversion

This should converge with `PublishedAssistantHistoryBuilder`, which already contains similar filtering behavior for published conversations.

It should also replace duplicate message conversion/filtering used by `ConversationManager` and `Agent` where feasible.

### ConversationMessageMapper

Owns DTO and chat-message mapping:

- `NotebookConversationMessage` to `MessageDto`
- `NotebookConversation` to `ConversationDto`
- tool-call DTO parsing
- duplicate assistant message filtering
- thinking block DTO expansion
- `NotebookConversationMessage` to `ChatMessage`

This can be extracted early because much of it is pure transformation logic.

It should include the duplicate assistant-message filtering rule, or delegate that rule to a single shared helper, so DTO projection, assistant history, agent context, and published history stay consistent.

### ConversationAttachmentService

Owns conversation-specific attachment coordination:

- validate attachments belong to the notebook
- add `MessageAttachment` rows
- load `NotebookFile`
- call `AttachmentMessageBuilder`

`AttachmentMessageBuilder` should remain the shared file-to-chat-content converter.

### AssistantContentSanitizer

Owns generated-content URL normalization:

- `sandbox:/...` URL conversion
- sandbox path normalization
- filename-to-URL map extraction from tool output
- assistant content URL rewriting
- cache-busting query parameter append

This is pure enough to extract early and test directly.

Use a private/published file URL policy so shared parsing and cache-busting logic does not fork into two static helper sets.

### ConversationUsageReporter

Owns usage recording:

- chat completion usage
- tool call usage
- cancelled-turn marker usage
- idempotency checks for tool usage

This keeps billing/reporting attribution concerns out of streaming mechanics.

### StreamingEventFactory / StreamingEventSink

Owns event creation and channel writes:

- role to `StreamingEventTypes` mapping
- assistant/tool/user payload construction
- throttled channel writes
- error/cancelled/usage event payloads

This reduces duplicated anonymous payload construction and makes stream event behavior easier to test.

## Recommended Extraction Order

1. Extract shared pure helpers first:
   - `AssistantContentSanitizer`
   - private/published file URL policies
   - `ConversationMessageMapper`
   - duplicate assistant-message filtering helper
   - `ThinkingBlockMapper`
   - `StreamingEventFactory`

2. Move tests off private reflection where extracted helpers make direct tests possible:
   - event type mapping
   - assistant-switch history filtering
   - duplicate assistant filtering
   - URL sanitization
   - thinking block DTO/rendering behavior

3. Extract read/query and simple command services:
   - `ConversationQueryService`
   - `UserConversationQueryService`
   - `ConversationCommandService`

4. Extract assistant history and attachment coordination:
   - `ConversationHistoryBuilder`
   - `ConversationAttachmentService`
   - use these from both private and published paths where behavior should match

5. Reconcile turn/message abstractions:
   - decide whether `TurnManager`, `ITurnManager`, and `IMessageManager` are retired, narrowed, or replaced
   - introduce `ConversationTurnStore`
   - introduce `ConversationMessageStore` if message writes remain large enough to justify separation

6. Extract usage recording:
   - `ConversationUsageReporter`
   - include both private and published tool/chat/cancelled-turn usage behavior

7. Extract undo:
   - `ConversationUndoService`
   - preserve private orphaned-lock recovery semantics
   - decide whether published undo should share the same implementation or remain a smaller published command path

8. Extract streaming last:
   - `ConversationStreamOrchestrator`
   - `ConversationStreamRunner`
   - published runner or stream-mode policy
   - `StreamingEventSink`

The streaming extraction should preserve the new characterization test and add smaller targeted tests for cancellation, tool calls, and URL sanitization before moving the callback body.

## Remaining Test Gaps Before Streaming Extraction

Add these before or during the streaming split:

- cancellation finalizes partial assistant messages and marks turn cancelled
- cancellation prunes incomplete tool calls
- lock/unlock/complete event ordering for observers
- tool-call assistant message and tool result persistence in a real streaming run
- tool usage event recording and idempotency
- generated-file URL rewriting from tool output
- thinking block persistence and stream emission
- attachment send path persists message attachments and includes file content in chat history

## Success Criteria

The refactor is successful when:

- `ConversationService` is a thin facade over composed collaborators
- `PublishedConversationService` is also a thin facade, or at least no longer duplicates core private streaming persistence/mapping/sanitization logic
- endpoint contracts remain unchanged
- the real streaming characterization test stays green
- query/command integration tests stay green
- ownership filtering for `/api/conversations` remains enforced
- streaming behavior is covered by focused tests instead of private-method reflection
- no extracted service depends on unrelated concerns such as URL rewriting plus usage recording plus lock lifecycle in the same class
- there is a single source of truth for duplicate assistant filtering, message-to-chat-message conversion, thinking block rendering, and generated-file URL rewriting
- turn/message write ownership is clear; `TurnManager`, `ITurnManager`, and `IMessageManager` are either intentionally retained with narrow responsibilities or removed/replaced
