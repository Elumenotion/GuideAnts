# Composer Turn-Terminal Ownership: Stale-Closure & Double-Owner Cleanup

Status: Implemented (P0–P2 + P4 seam)  
Last updated: 2026-08-30  
Owner: Conversation runtime / notebook UI  
Decision context: client tools are coming to the main (notebook) chat; the design must not
deepen the split between the published-wire and first-party conversation code paths.  
Related:
- [Conversation Stream Reconnect and Server Cancel Proposal](./conversation-stream-reconnect-and-cancel-proposal.md) — the stop/cancel machinery this doc deliberately does *not* touch
- `src/client/src/contexts/ConversationContext.tsx`
- `src/client/src/contexts/conversation/useConversationActions.ts`
- `src/client/src/contexts/conversation/useStreamingEventHandler.ts`
- `src/client/src/contexts/conversation/reducer.ts`
- `src/client/src/contexts/conversation/types.ts`
- `src/client/src/components/notebook/conversations/DraftUserCell.tsx`
- `src/client/src/components/notebook/conversations/CellList.tsx`
- `src/client/src/components/notebook/conversations/ConversationPanel.tsx`
- `src/client/src/services/api.ts`
- `src/server/GuideAntsApi/Services/Conversations/Streaming/ConversationStreamEngine.cs`

## 1. Problem Summary

After a **successful** turn, the composer's (draft) user cell keeps showing the previous
turn's attachment chips. By design, attachment chips should appear in the draft cell only
when (a) the user attaches something, or (b) the draft is populated by undoing the previous
turn. The text part of the draft is cleared correctly on success; the attachments are not.

This is not an isolated bug. The composer's turn-terminal policy (finalize the streaming
cell, dispose of/restore draft text and attachments, release controllers, refresh files)
is implemented by **two owners** that are coupled through an `AbortController`, and the
owner that actually runs on the happy path reads **stale React closure state**. Both
properties hold regardless of network timing: they are structural.

## 2. Observed Symptoms

| Symptom | Repro |
|---------|-------|
| Previous turn's attachment chips remain in the draft cell after a successful turn | Send a message with an attachment (paste/drop/camera), wait for `complete`; chips persist |
| Draft *text* clears correctly on success (text/attachment asymmetry) | Same repro |
| On a turn ending in `pending_client_tool`, the draft text is silently dropped (attachments kept) | Same send path, turn ends awaiting client tool result |
| `useStreamingEventHandler.test.tsx` passes while production misbehaves | `npm run test` — the unit mounts the handler with `streamingMode: 'sending'`, so the gate is always true in tests |

## 3. Root Cause Analysis

### 3.1 Single source of truth for draft chips, shared across the conversation

Draft attachment chips are rendered from context state, not from the message list:

- `DraftUserCell.tsx:118` — `const { pendingAttachments, addPendingAttachment, removePendingAttachment } = useConversation();`
- `DraftUserCell.tsx:732` — `{pendingAttachments.map((att) => …)}`

Sent-turn chips render separately from the persisted message's `attachments`
(`CellList.tsx` `computeTurns` → `turn.attachedFiles` → `UserCell`). So the defect is
precisely: **what lands in `pendingAttachments` after a successful turn.**

### 3.2 The composer terminal policy has two owners

**Owner A — the SSE event handler** (`useStreamingEventHandler.ts`). On every terminal
event (`complete` L131, `pending_client_tool` L174, `cancelled` L209, `error` L354) it:
1. calls `deps.onStreamTerminal?.()` (L139/178/214/389) — bound to `actions.clearPendingStop`
   (`ConversationContext.tsx:431`);
2. calls `deps.abortActiveStreams?.()` (L140/179/215/390);
3. dispatches `COMPLETE_STREAMING_TURN`;
4. **if `state.streamingMode === 'sending'`** — restores draft text from
   `sendStreamStateRef.current.snapshot.draft`, keeps or clears attachments, and nulls the
   ref (e.g. L153–158 in `case 'complete'`).

**Owner B — `sendMessage`'s transport callbacks** (`useConversationActions.ts`,
`sendMessage`): `onError` (L535 guard) and `onComplete` (L584 guard) both start with
`if (sendStreamRef.current !== controller) return;`, and only then run the *unconditional*
terminal policy (`COMPLETE_STREAMING_TURN` L594, `CLEAR_ATTACHMENTS` L597,
`clearSendStreamState`, `loadNotebookFiles`). `reconcileLostSendAndStop` and
`applyComposerTerminalPolicy` (L121) are also Owner-B-only recovery paths.

The two owners are coupled by abort: `abortActiveStreams`
(`ConversationContext.tsx:92`) aborts the send controller **and sets
`sendStreamRef.current = null`**. Owner A always runs first (SSE event arrives before the
body closes). Its abort makes `reader.read()` in `sendMessageStream` reject with
`AbortError`, which `api.ts` treats as a user cancel and `return`s **without calling
`onComplete` or `onError`**. So on the normal flow, Owner B's guard fails and its body is
**dead code**. The send-path `CLEAR_ATTACHMENTS` at L597 is therefore never reached on a
successful turn.

### 3.3 The owner that does run reads stale closure state

`handleStreamingEvent` is a `useCallback` whose dependency array
(`useStreamingEventHandler.ts:462`) includes `state.streamingMode`, `state.messages`,
`state.currentTurn`, and `state.selectedAssistant`. The SSE loop inside
`sendMessageStream` invokes the `onEvent` arrow captured at `sendMessage` creation — i.e.
the handler instance from the **render before the send**, where `streamingMode` was
`'at-rest'`. `sendMessage` later dispatches `SET_STREAMING_MODE 'sending'`, which produces
a *new* handler instance, but the in-flight loop keeps calling the old one.

Consequence: every `if (state.streamingMode === 'sending')` gate in the handler (L153,
L181, L217, L374) evaluates **false for the entire duration of a locally-sent turn**. The
`complete` case therefore skips `CLEAR_ATTACHMENTS` and skips nulling
`sendStreamStateRef`. That is the reported bug.

### 3.4 Defect cluster produced by the same structure

| # | Path | Dead/affected logic | Consequence |
|---|------|--------------------|-------------|
| D1 | `complete` (L153–157) | `CLEAR_ATTACHMENTS`; `sendStreamStateRef.current = null` | **Reported bug** — previous turn's chips linger; `sendStreamStateRef` also holds a stale snapshot after every successful turn (reused by later `cancelStream`/reconcile fallbacks) |
| D2 | `pending_client_tool` (L181–189) | `SET_DRAFT(snapshot.draft)` + attachment keep | **Draft text silently lost** when a turn ends awaiting a client tool; nothing else in the flow restores it |
| D3 | `cancelled` (L217–229), `error` (L374–386) | same restore block | Server- or other-client-initiated terminal events: draft text lost, terminal policy never applied — and the handler's abort converts the transport failure into `AbortError`, which `api.ts` swallows, so Owner B's `reconcileLostSendAndStop` is never reached either |
| D4 | `complete` (L142–145) | `state.messages.find(m => m.id.startsWith('streaming-'))` | Reads the **pre-turn** messages array; the `ADD_FINAL_RESPONSE` backfill for the streamed cell almost never fires (masked because the reducer's `COMPLETE_STREAMING_TURN` also finalizes the cell) |
| D5 | `message` (L106) | `state.selectedAssistant` | Final assistant message labeled with the pre-turn assistant if the selection changes mid-turn |

The asymmetry users notice — attachments *wrongly kept* on success, but draft text
*wrongly dropped* on `pending_client_tool` — falls straight out of the same frozen gate.

### 3.5 Why the tests don't catch it

`useStreamingEventHandler.test.tsx` constructs the handler directly with
`streamingMode: 'sending'` (line 26) and asserts `CLEAR_ATTACHMENTS` on `complete`
(lines 300–318). The freeze is an **integration-level property**: the real
provider → `sendMessage` → SSE loop → handler chain is never exercised by any test. No
test mounts `ConversationProvider`, sends through the mocked `api` SSE, and asserts the
composer state after `complete`.

### 3.6 Secondary timing-dependent races (distinct from the deterministic freeze)

These do not cause D1–D5 but share the same surface and should not be made worse:

- **Stream-close vs abort-reject ordering** in `sendMessageStream`: after `complete`,
  whichever lands first — server closing the body (`done: true`) or the abort rejecting
  `reader.read()` — determines which callback path is even attempted. Today both outcomes
  hit the L584 guard and do nothing, but the path taken is a coin flip.
- **`_justCompletedStreaming` 100 ms window** (`ConversationContext.tsx:318–324`):
  refresh is suppressed for a fixed 100 ms after `COMPLETE_STREAMING_TURN`; correctness
  of post-turn refresh depends on the heuristic.
- **Stop/reconcile timers** (250 ms `stopRetryTimerRef`, `stopReconcileTimerRef`) and the
  **45 s idle timeout** (`api.ts:409` `CONVERSATION_STREAM_IDLE_TIMEOUT_MS`) around
  `requestServerCancel` — how stop failures surface is timer-dependent.
- **Multi-client terminal events**: `isForeignTurnEvent` filtering
  (`useStreamingEventHandler.ts:22–55`) depends on the `getActiveStreamTurnId()` ref;
  interplay with `stopTargetRef` identity checks is ref-juggling that is easy to regress.

## 4. Goals

1. On a **successful** turn, `pendingAttachments` is empty and `draftUserContent` is empty;
   `sendStreamStateRef` is null.
2. `pending_client_tool` is a **first-class terminal outcome**: the composer's behavior on
   it is a named, swappable policy (default today: restore-and-unlock) — not a hard-coded
   branch in the event handler — so the upcoming main-chat client tools (P4) change a
   policy, not the state machine.
3. On `pending_client_tool`, the composer is restored (draft text + attachments) from the
   send snapshot, deterministically — not via closure state.
4. Composer terminal state (draft/attachments/flags) has **exactly one owner** per
   outcome, and that owner reads only refs and reducer state — never render-captured
   `state.streamingMode`/`state.messages`.
5. The existing stop/cancel/reconcile behavior (see the reconnect & cancel proposal) keeps
   working unchanged.
6. The whole matrix (success / cancelled / error / `pending_client_tool` / local stop /
   other-client stop / idle timeout) is pinned by a provider-level integration test that
   is structurally incapable of passing with a stale closure.

## 5. Non-goals

- No server changes. The server correctly emits `complete` with `turnId` on success
  (`ConversationStreamEngine.cs:989–1014`) and never emits `[DONE]` on this endpoint.
- No changes to the stop/cancel lifecycle (`requestServerStop`,
  `completeConfirmedCancellation`, `reconcileLostSendAndStop`, the 409 retry loop) beyond
  making them the *only* composer owners where they already are.
- No redesign of turn grouping in `CellList` or of `UserCell` rendering.
- P3 items (100 ms window, `DraftUserCell` local-state collapse) are optional follow-ups,
  not part of the P0–P2 scope.
- P4 (client-tool unification) is a **sequenced future phase**, not part of the P0–P2 fix
  scope; this doc only fixes its *shape* (interfaces, policy seams, no new divergence).

## 6. Proposed Architecture

### 6.1 Overview

Three invariants replace the current "two owners + abort coupling":

**I1 — The reducer is the only place `pendingAttachments`/`draftUserContent` are mutated.**
(Already true — keep it.)

**I2 — Exactly one owner finalizes the composer per turn outcome.**
Owner = the **action layer** (`useConversationActions.ts`), driven by the **transport
layer** (`api.ts`) telling it *how the stream ended*. The SSE event handler stops owning
composer state; it keeps owning *conversation* state (streaming cell, tool calls,
progress).

**I3 — No terminal handler reads render-captured `state.*` for decisions about the live
turn.** Live-turn facts come from refs: `sendStreamStateRef` (snapshot + turnId),
`sendStreamRef`/`controller` identity, `pendingStopRef`, `activeStreamTurnIdRef`.
`state.streamingMode` is display state only.

### 6.2 Terminal outcomes and their composer policy

| Outcome | Detection | Composer policy |
|---------|-----------|-----------------|
| Success | SSE `complete` for the local turn | clear draft text + attachments; null snapshot; refresh files |
| Awaiting client tool | SSE `pending_client_tool` for the local turn | **Composer policy seam** (§6.7). Default (no client-tool runner registered, i.e. today): **restore** draft text + attachments, null `sendStreamStateRef`, unlock at-rest; turn stays cancellable/undoable server-side. With a client-tool runner registered (P4): **block** the composer with a "waiting on client tool" state, execute, then resume (§6.7/P4) |
| Cancelled (server/other client) | SSE `cancelled` for the local turn | restore draft + attachments from snapshot (message may or may not be persisted — see §6.4) |
| Error | SSE `error` for the local turn | restore draft + attachments from snapshot; surface error; if no `turnId` → existing `reconcileLostSendAndStop` |
| Local Stop confirmed | `completeConfirmedCancellation` | existing `applyComposerTerminalPolicy(true)` (clear) — **unchanged** |
| Transport failure w/o turn id | `onError` without persisted turn id | existing `reconcileLostSendAndStop` — **unchanged** |

"The local turn" = `deps.sendStreamStateRef?.current !== null` **and** the event's
`turnId` (when present) equals `sendStreamStateRef.current.turnId` or
`activeStreamTurnIdRef.current`. This is the ref-based replacement for
`state.streamingMode === 'sending'`.

### 6.3 Owner B becomes real: transport-driven completion

`api.ts` already parses SSE lines and tracks `sawTerminalEvent`
(`sendMessageStream`: `data:` loop L1419–1447; terminal set L411–416). Change:

- When a **terminal event type** (`complete` / `cancelled` / `pending_client_tool` /
  `error`) is observed, **do not keep reading the body for composer purposes**: invoke a
  new callback `onTerminal(event)` (or reuse `onComplete(event)`) exactly once, and let
  the read loop end (the server closes the body immediately after a terminal event;
  `ConversationStreamEngine.cs:1045` `writer.TryComplete()`).
- **Do not** treat the handler's abort as a user cancel any more: the abort is now only a
  cleanup primitive for navigation/unmount, issued by the provider's conversation-change
  effect, not by the event handler.

Then in `useConversationActions.ts`, the `onComplete`/`onError` bodies (L584/L535 guards)
become the single composer owner: they already hold the snapshot and are ref-based. The
existing `sendStreamRef.current !== controller` guard stays as a *stale-callback* guard
(newer turn superseded this one) — which is its correct purpose.

### 6.4 The `cancelled`/`error` persistence question

The server always persists the user message **before** emitting `turn_created`
(`ConversationService.cs:460` → `:519`), and pre-`turn_created` 409/400 failures happen
before that persistence (`NotebookConversationsEndpoints.cs:171–305`). So `turnId`
presence is a deterministic persistence oracle: `turnId` known → **clear**; no `turnId` →
**restore**. Replace today's content-match guess
(`applyComposerTerminalPolicy(Boolean(streamTurn.current))` L662; the `messagePersisted`
content-match in `reconcileLostSendAndStop`) with this oracle in P2 (§11.3). The proposal
also removes the *duplicate* restore in the event handler so the decision is made once,
by one owner.

### 6.5 DraftUserCell contract (unchanged surface, documented)

`DraftUserCell` keeps rendering `pendingAttachments` from context (§3.1) and keeping its
local `localContent`/`lastSyncedValueRef` sync. We **document** (comment at the
`pendingAttachments` consumption site) that chips are cleared by the composer owner at
send-time (§7 P0) and restored only by undo, so future readers don't re-add clearing
logic in the component. A P3 item separately proposes collapsing the three-way local
state (`localContent`, `lastSyncedValueRef`, context `draftUserContent`) into one source.

### 6.6 What `useStreamingEventHandler` keeps doing

- All non-composer dispatches: `ADD_FINAL_RESPONSE`, `COMPLETE_STREAMING_TURN` (reducer
  state only — no composer side effects), tool call/result/activity events,
  `SET_STREAMING_ERROR`, crash/recovery window events.
- Turn-scoped filtering (`isForeignTurnEvent`) — unchanged.
- It **stops** calling `abortActiveStreams()` and `onStreamTerminal()` for terminal
  events; those move to the transport-driven owner. `abortActiveStreams` remains available
  for navigation/unmount and for the Stop flow (which already uses the cancel endpoint,
  not SSE aborts, as its authority).
- The D4/D5 stale reads (`state.messages`, `state.selectedAssistant`) get fixed as a
  side effect: after the refactor, those dispatches can read from the event payload
  (which carries `content`, `turnId`) or be dropped where the reducer already does the
  same finalization.

### 6.7 Composer policy seam for `pending_client_tool` (no new code-path split)

The composer must not branch on "is this a client-tool turn?" in the event handler. Instead,
the action layer exposes a single seam:

```ts
// useConversationActions.ts
export interface ComposerTerminalPolicy {
  /** Called once per terminal outcome by the single owner (§6.3). */
  apply(outcome: TerminalOutcome): void;
}
type TerminalOutcome =
  | { kind: 'success' }
  | { kind: 'cancelled'; turnId: string | null }
  | { kind: 'error'; turnId: string | null; message: string }
  | { kind: 'pending_client_tool'; turnId: string; toolCalls: ExternalToolCallDto[] };
```

- The **default policy** (P0–P2) implements today's intended behavior: success → clear;
  `pending_client_tool` → restore + unlock (the notebook UI has no runner yet, §11.1);
  cancelled/error → clear/restore per the `turnId` persistence oracle (§11.3).
- P4 registers a **client-tool policy** that, for `pending_client_tool`, keeps
  `sendStreamStateRef` populated, blocks the composer (`streamingMode` stays non-`at-rest`
  with an `activeToolActivity`-style UI), executes the calls through the client bridge, and
  POSTs results to the notebook resume endpoint (mirroring the published wire's
  `ResumeAfterExternalToolResultsStreamAsync`, `PublishedConversationService.cs:84–200`,
  which resumes with `ResumeWithoutNewUserMessage = true`).
- The event handler and transport layer never change between the two policies — only the
  policy object passed into `useConversationActions` does. That is the concrete
  "less split between published and first-party paths" guarantee: the *lifecycle* (single
  owner, turnId oracle, snapshot semantics) is shared; only the `pending_client_tool`
  branch differs, by design.

## 7. Implementation Phases

Each phase is independently shippable and testable. P0 ships the user-facing fix even if
P1/P2 are deferred.

### Phase 0 — Clear composer state at send time (1-line fix + regression test)

In `useConversationActions.ts` `sendMessage`, immediately after snapshotting into
`sendStreamStateRef` (L446–452) and `SET_DRAFT ''` (L453), dispatch
`CLEAR_ATTACHMENTS`.

- Rationale: the snapshot already preserves the attachments for every restore-on-failure
  path (each restore path writes the snapshot back explicitly — `SET_ATTACHMENTS
  snapshot.pendingAttachments` at L224/L381 in the handler, and
  `applyComposerTerminalPolicy` L121 reads it). Clearing early makes the success path
  independent of the terminal machinery and fixes D1 immediately.
- Also null-safe: `pendingAttachments` is only read by the draft cell and by `sendMessage`
  itself (which prefers its `attachments` argument, else `state.pendingAttachments` —
  which it has already copied into `attList` at L444).
- **Regression test (provider-level, new file
  `contexts/conversation/__tests__/turnTerminal.integration.test.tsx`):**
  mount `ConversationProvider` with a mocked `api` whose `sendMessageStream` emits
  `turn_created` → `token`*n → `complete` → closes the body; drive it with fake timers or
  a real `ReadableStream`; assert after completion: `pendingAttachments` empty,
  `draftUserContent` `''`, `sendStreamStateRef` null, messages finalized. This test shape
  is what would have caught the entire cluster.

Risk: low. The only behavior change is that chips disappear at send instead of at
`complete` (imperceptible; the user cell shows the sent attachments during the turn).

### Phase 1 — Defuse the stale closure in the handler (deterministic, small)

- Add to `ConversationContext.tsx` a `streamingModeRef` (mirrored in an effect, same
  pattern as `isStreamingRef` at L117) and pass it to the handler deps.
- In `useStreamingEventHandler.ts`, replace the four
  `if (state.streamingMode === 'sending')` gates (L153/181/217/374) with
  `if (deps.streamingModeRef?.current === 'sending' && isLocalTurn(event))`, where
  `isLocalTurn(event)` = `deps.sendStreamStateRef?.current !== null` and the event's
  `turnId` matches `sendStreamStateRef.current.turnId` when both exist.
- Extract the duplicated restore block (currently copy-pasted in `complete`? no — in
  `pending_client_tool` L183–189, `cancelled` L218–229, `error` L375–386) into one
  `applyComposerTerminalPolicy(messagePersisted: boolean)` call: the action layer already
  owns that function (L121); pass `actions.applyComposerTerminalPolicy` (stable, ref-based)
  into the handler deps.
- Fix D4/D5 while in the file: `complete` backfill reads the streaming cell from
  `event.data`/reducer state at dispatch time (or drop the backfill if the reducer's
  `COMPLETE_STREAMING_TURN` (reducer `case 'COMPLETE_STREAMING_TURN'`, reducer.ts:453) already covers it — verify in the reducer, then delete),
  and `message` uses `event.data.assistantName ?? state.selectedAssistant` only for
  display fallback.

Risk: medium — touches the handler all four terminal cases share. Mitigation: the Phase-0
integration test now covers `complete`; add sibling cases for `pending_client_tool`,
`cancelled`, `error` (same harness, different event script).

### Phase 2 — Single owner: transport-driven completion (structural)

- `api.ts`: as in §6.3 — add `onTerminal` semantics; stop relying on body-close vs
  abort ordering; keep the idle-timeout and `[DONE]` handling.
- `useStreamingEventHandler.ts`: remove `abortActiveStreams`/`onStreamTerminal` calls
  from terminal cases (§6.6); composer dispatches move to the action layer's
  `onComplete`/`onError`.
- `useConversationActions.ts`: `onComplete` becomes the success/pending_client_tool
  owner using the same `applyComposerTerminalPolicy` + snapshot semantics; `onError`
  keeps `reconcileLostSendAndStop` for the no-turn-id case.
- Keep the Stop flow byte-for-byte (`requestServerStop`, `completeConfirmedCancellation`,
  409 retry) — it already treats the cancel endpoint as the authority.
- Remove now-dead code: the copy-pasted restore blocks (subsumed by Phase 1's shared
  policy) and any `sendStreamStateRef` reads that assumed the handler nulls it.

Risk: higher — this is where the reconnect-and-cancel scar tissue lives. Gate it on the
Phase-1 integration suite plus the existing `useStreamingEventHandler.test.tsx` and
`useConversationActions.test.ts` staying green, and add the multi-client scenario
(other client's `complete` broadcast must not touch this client's composer).

### Phase 3 — Optional hardening (follow-up, not blocking)

- Replace the `_justCompletedStreaming` 100 ms window with a turn-generation token
  (suppress the conversation-change refresh for the specific `turnId` being finalized).
- Collapse `DraftUserCell`'s `localContent`/`lastSyncedValueRef`/context-draft three-way
  sync into a single source of truth (context `draftUserContent` as source, editor as
  view; keep the unmount-preserve behavior).
- Consider a server-side `composerSnapshot` echo in the `complete` event so the client
  never needs to guess persistence on `cancelled` (§6.4).

### Phase 4 — Main-chat client tools (sequenced future; shape fixed here, implementation later)

Out of scope for this fix, but its **shape** is constrained by P0–P2 so it does not
re-introduce divergence:

1. **Client-tool runner (client):** a registered bridge that executes
   `external_tool_call` payloads (the notebook editor already models `client://`
   "Client Actions" tools — `toolSourceClassification.ts:3–46`,
   `openApiDescriptorBuilder.ts:166` — but has no execution path today).
2. **Notebook resume endpoint (server):** a private-conversation equivalent of the
   published wire's `ResumeAfterExternalToolResultsStreamAsync`
   (`PublishedConversationService.cs:84–200`, `ResumeWithoutNewUserMessage = true`),
   sharing the same `ConversationStreamEngine`/terminalizer path (it already does —
   both call `RunRegisteredStreamAsync`/`_streamEngine.RunStreamAsync`).
3. **Client-tool composer policy (§6.7):** registered in place of the default policy;
   blocks the composer on `pending_client_tool`, executes, resumes.
4. **`external_tool_call` handling (client):** a real `case 'external_tool_call'` in
   `useStreamingEventHandler.ts` (today it falls to `default` and is logged as unknown —
   `StreamingEvent.cs:34`), feeding the policy seam.

Guarantee: steps 1–4 add a *policy and an endpoint*; they do not touch the single-owner
terminal flow, the `turnId` oracle, or the snapshot semantics established by P0–P2. If a
future client-tool PR needs to add a `state.X === 'sending'`-style gate or a second
composer owner, that is a review blocker.

## 8. API Summary

No public API changes. Internal client changes:

| Surface | Change |
|---------|--------|
| `api.ts` `sendMessageStream` | Add `onTerminal(event)` (or widen `onComplete` signature); terminal event → exactly one terminal callback; no abort-from-handler semantics |
| `useConversationActions.ts` `sendMessage` | +`CLEAR_ATTACHMENTS` at send (P0); `onComplete`/`onError` become the composer terminal owners (P2) |
| `useStreamingEventHandler.ts` | Drop `abortActiveStreams`/`onStreamTerminal` from terminal cases (P2); ref-based local-turn gate (P1); shared composer policy call (P1) |
| `ConversationContext.tsx` | `streamingModeRef` (P1); pass `applyComposerTerminalPolicy` into handler deps (P1) |
| `reducer.ts` | No change |
| `types.ts` | Add `streamingModeRef`/`applyComposerTerminalPolicy`/`ComposerTerminalPolicy` to handler deps |
| (P4, future) notebook API | `POST .../conversations/{convoId}/turns/{turnId}/tool-results` (private equivalent of published resume) + client `external_tool_call` case |

## 9. Testing Plan

### 9.1 New provider-level integration harness (P0, reused by P1/P2)

`contexts/conversation/__tests__/turnTerminal.integration.test.tsx`:

- Real `ConversationProvider`, real reducer, real `useConversationActions`;
- `api.projects.notebooks.conversations.sendMessageStream` mocked with a
  `ReadableStream` of SSE lines driven by a test script array
  (`['turn_created', 'token', …, '<terminal>', '[close]']`);
- `notebookFiles` upload API mocked for attachment setup;
- Assertions on context state via a probe child component.

Cases (each = one scripted SSE sequence):

1. **success** → after `complete`: `pendingAttachments` `[]`, `draftUserContent` `''`,
   snapshot null, streaming cell finalized with server content. *(D1 — the reported bug)*
2. **pending_client_tool** → draft text **and** attachments restored from snapshot,
   `sendStreamStateRef` null, composer re-enabled at-rest (default policy). *(D2 — also the
   P4 baseline: assert the same harness passes a *client-tool policy* stub that blocks
   instead, proving the seam swaps behavior without touching the owner)*
3. **cancelled (local, server-confirmed)** → Stop flow: composer cleared per
   `applyComposerTerminalPolicy(true)`; no double-finalize.
4. **cancelled (no turn id)** → restore + reconcile path; `reconcileLostSendAndStop`
   invoked exactly once. *(D3)*
5. **error (no turn id)** → restore + error surfaced; reconcile invoked. *(D3)*
6. **other-client complete broadcast while local turn active** → composer untouched
   (`isForeignTurnEvent` + local-turn gate).
7. **idle timeout mid-turn** → `requestServerCancel` path unchanged; composer locked
   until confirmed stop.
8. **undo after success** → draft + attachments restored from the persisted user message
   (locks the undo contract the user stated).

### 9.2 Existing suites that must stay green

- `contexts/__tests__/useStreamingEventHandler.test.tsx` — update assertions in P1/P2
  where the composer dispatches move owners (the "clears attachments on complete only for
  the local sending stream" case becomes "clears at send time / in the action owner").
- `contexts/conversation/__tests__/useConversationActions.test.ts` — the undo restore
  case (L1257) and send snapshot cases.
- `components/notebook/conversations/__tests__/DraftUserCell.*.tsx` — chips render from
  context; no change expected.

### 9.3 Manual acceptance

1. Paste an image, send, wait for completion → no chips in draft; new chip on next paste.
2. Send a turn that ends awaiting a client tool (published-wire client tool) → draft text
   + chip restored, editable, resends the same payload.
3. Stop mid-turn (confirmed) → composer cleared; undo restores.
4. Two browsers, same conversation: complete a turn in B while A is idle → A's composer
   stays empty; A's in-flight turn is not touched by B's terminal broadcast.
5. Kill the network mid-stream (no turn id) → draft + chip restored, reconcile toast
   shown, conversation re-locks correctly.

## 10. Decisions

| # | Decision | Rationale |
|---|----------|-----------|
| 1 | Clear attachments at **send time** (P0), not at terminal | The success path must not depend on the terminal machinery at all; the snapshot already makes restore-on-failure possible; matches the stated product invariant |
| 2 | Composer terminal ownership moves to the **action layer driven by the transport** (P2), with the SSE handler owning only conversation state | Eliminates the abort-coupling that kills the fallback; one owner per outcome; handler keeps the part it does correctly (reducer dispatch, turn scoping) |
| 3 | Live-turn facts come from **refs** (`sendStreamStateRef`, `sendStreamRef`, `pendingStopRef`, `activeStreamTurnIdRef`, new `streamingModeRef`) — never render-captured `state.*` | Stale-closure class of bugs becomes structurally impossible in the hot path |
| 4 | **Do not** touch the stop/cancel lifecycle or the server | That machinery is the correct part of this area (cancel endpoint as authority); changing it widens the blast radius of a P0 fix |
| 5 | Phase the work P0 → P1 → P2, each shippable, P2 gated on the integration suite | P0 fixes the user-facing defect now; P1 removes the freeze; P2 removes the double ownership; regression risk stays contained per PR |

## 11. Open Questions (researched — resolutions below)

### 11.1 `pending_client_tool` snapshot lifetime — RESOLVED: policy seam (§6.7); default is restore-and-unlock; no notebook resume path today

Research finding: `pending_client_tool` is the **client-handled tool** terminal state
(`StreamingEvent.cs:34–35`). It fires when the run ends on a tool that the *host client*
must execute (`ThreadRun.cs:780–799` — `clientHandled.Count > 0` → `runResults.Status =
"pending_client_tool"`). A tool is "client-handled" when it comes from
`options.ClientToolDefinitions` **or** has scheme `client://`
(`ToolCaller.cs:241` → `ActionType.ClientHandled`).

Reachability from the notebook UI:
- The notebook client **never** sends `ClientToolDefinitions` (no such field in its
  `sendMessageStream` payload; `SendMessageRequest.ClientToolDefinitions` exists
  server-side but the client leaves it null).
- The notebook UI **can** define `client://` bridge tools
  (`openApiDescriptorBuilder.ts:166` → `client://${connectorKey}`, classification
  `toolSourceClassification.ts:45`).

So `pending_client_tool` is reachable in a private notebook conversation only when the
selected guide exposes a `client://` tool the model actually calls. **But the notebook
client has no resume path**: `useStreamingEventHandler.ts` has no `case 'external_tool_call'`
and no `case 'pending_client_tool'` tool-execution logic — the event falls to `default`
(logs "Unknown SSE event type"; no `content` in the payload, so no `APPEND_TOKEN`). There
is no `resume` endpoint in `NotebookConversationsEndpoints.cs`. `COMPLETE_STREAMING_TURN`
sets `streamingMode: 'at-rest'`, so the composer unlocks and the user can only **send a
new message** (new turn) or **undo** the paused turn.

Consequence for the design: there is no "client resumes the tool" path to design a
snapshot lifetime around. The correct policy is:
- On `pending_client_tool`, **restore** draft text + attachments from the snapshot (the
  user's input is not consumed into a completed turn — the turn is paused, not done), and
  **null** `sendStreamStateRef` (the turn's lifecycle is now owned by the server/undo, not
  by an in-flight send).
- The server keeps the turn cancellable (`ConversationPersistence.cs:219–223` —
  `wasPendingClientTool` is in the cancellable set), so undo/stop keep working.

This **promotes D2 from a latent bug to the primary `pending_client_tool` fix**: today the
draft is silently dropped because the `state.streamingMode === 'sending'` gate is frozen
false (D2). Per the recorded decision (client tools are coming to the main chat), restore-
and-unlock is the **default policy** implemented in P0–P2; when the client-tool runner
lands (P4), the §6.7 seam swaps in a blocking/execute/resume policy without touching the
terminal owner, the `turnId` oracle, or the snapshot semantics.

### 11.2 D4 backfill redundancy — RESOLVED: the streamed cell is finalized by the reducer; backfill is nearly dead

`convertTurnToMessages` (`streamingHelpers.ts:29–80`) emits **only** `tool`-role messages
plus an `assistant` message for the tool-step section or `finalResponse` — it does **not**
emit the streamed assistant cell. The streamed cell itself is finalized by the reducer's
`COMPLETE_STREAMING_TURN` (`reducer.ts:453`), which rewrites the `streaming-…` message's
`content` to `finalCellContent`. The handler's `ADD_FINAL_RESPONSE` backfill
(`useStreamingEventHandler.ts:145–151`) only runs when the (stale) `state.messages`
already contains a non-empty `streaming-…` cell **and** `!state.currentTurn?.finalResponse`
— and `finalResponse` is read **only** by `calculateStreamingProgress` (display) and
`convertTurnToMessages`. Nothing else consumes `finalResponse`. So the backfill is
redundant with the reducer's finalization and can be **deleted in P1** (verified no other
consumer at §11.2 grep).

### 11.3 Server-side `composerSnapshot` echo — DEFERRED, with a cheaper client-side answer

The one place the client genuinely *guesses* persistence is the `cancelled`/`error`-without-
`turnId` path (`applyComposerTerminalPolicy(Boolean(streamTurn.current))`,
`useConversationActions.ts:662`; the `messagePersisted` content-match in
`reconcileLostSendAndStop`). Research shows the server **always persists the user message
before emitting `turn_created`** (`ConversationService.cs:460` `CreateTurnAndUserMessageAsync`
→ L519 `yield return TurnCreated`). So:
- If the client has **any** `turnId` (from `turn_created`), the user message **is**
  persisted → the terminal policy should **clear** (not restore).
- A pre-`turn_created` failure (409/400 from the endpoint, `NotebookConversationsEndpoints.cs:171–305`)
  happens **before** `CreateTurnAndUserMessageAsync`, so the user message is **not**
  persisted → restore.

This means the client can replace the *content-match guess* with the **deterministic**
"did we see `turn_created`" signal (already tracked in `sendStreamStateRef.turnId`). A
server-side `composerSnapshot` echo (P3) is still the cleanest long-term fix but is no
longer *required* for correctness — the `turnId` presence is a sufficient persistence
oracle for the notebook client. Recommend: adopt the `turnId` oracle in P2; keep the
server echo as an optional P3 hardening.

**Product decision (recorded 2026-08-30):** client tools **will** be added to the main
(notebook) chat, and the design should minimize the split between published-wire and
first-party conversation paths. Consequences adopted in this doc:
- P0–P2 implement the **default policy** (restore-and-unlock on `pending_client_tool`) —
  correct for today, since no runner exists.
- The `pending_client_tool` behavior lives behind the **§6.7 policy seam**, so P4 swaps in
  a blocking/execute/resume policy without changing the terminal owner, the `turnId`
  oracle, or the snapshot semantics.
- P4 adds a notebook **resume endpoint mirroring**
  `ResumeAfterExternalToolResultsStreamAsync` (shared engine path) and a real
  `external_tool_call` SSE case.
The "decision needed" is now a recorded decision; the open work is P4 implementation,
sequenced after P0–P2.

## 12. Success Criteria

- P0: repro in §2 row 1 no longer occurs; new integration case 1 green in CI.
- P1: cases 1–5 green; no `state.streamingMode === 'sending'` gates remain in the
  handler (grep check in review).
- P2: cases 1–8 green; `useStreamingEventHandler.ts` contains no composer-state
  dispatches (`SET_DRAFT`/`SET_ATTACHMENTS`/`CLEAR_ATTACHMENTS`/`COMPLETE_STREAMING_TURN`
  with composer side effects) and no `abortActiveStreams` calls; the
  `sendMessageStream` abort-on-terminal code path is removed; existing stop/cancel tests
  unchanged and green.
- No new timing-dependent behavior: every composer mutation is triggered by a named
  outcome (transport terminal callback, confirmed stop, undo, user attach action), not by
  stream-close vs abort ordering or fixed-time windows.
- Unification: the only file that knows what `pending_client_tool` *means* for the composer
  is the policy object (§6.7); `useStreamingEventHandler.ts` and the transport layer contain
  no client-tool-specific composer logic, so P4 cannot widen the published/first-party
  split (grep review gate).


## 13. Implementation Notes (P0–P2 shipped; deviations from plan)

Implemented per §7 with the following refinements, all verified by the test suites cited
in §9 (360 files / all green; `npm run typecheck` clean):

- **P0** — `CLEAR_ATTACHMENTS` at send time in `useConversationActions.ts` `sendMessage`
  (immediately after the `SET_DRAFT ''`), exactly as specified.
- **P1 subsumed by P2 (deviation, decision §3)** — the plan's `streamingModeRef` was not
  added. All four `state.streamingMode === 'sending'` composer gates guarded *only*
  composer-state code, and P2 removes that code from the handler entirely; a ref mirroring
  `streamingMode` would have been read by nothing. The structural fix is stronger: the
  handler no longer reads any render-captured `state.*` for live-turn composer decisions
  (grep gate in §12 verified — zero `state.streamingMode === 'sending'` in the handler).
- **P2** — `api.ts sendMessageStream` now tracks the terminal SSE event and breaks the read
  loop the moment it is delivered, invoking `onComplete(terminalEventType)` exactly once
  (a body close without any terminal event is still reported as an error, so the
  reconcile path is unchanged). The handler no longer calls `abortActiveStreams()` /
  `onStreamTerminal()` and dispatches no `SET_DRAFT` / `SET_ATTACHMENTS` /
  `CLEAR_ATTACHMENTS` (§12 grep gates pass). The error case's trailing
  `setCurrentStreamController(null)` was removed: it nullled the send ref and tripped the
  owner's stale-callback guard *before* the composer restore ran (the same abort-coupling
  class this refactor eliminates); the owner's `adoptSendStream(null)` is the single
  release point.
- **`ComposerTerminalPolicy` seam (§6.7) made concrete** — `types.ts` exports
  `ComposerTerminalOutcome` / `ComposerTerminalPolicy`; `useConversationActions` exposes
  `applyComposerTerminalOutcome` (the single owner) and accepts an optional
  `composerTerminalPolicy` dep (threaded through `ConversationContext` → `ProviderProps`).
  Default (no policy registered): success → clear; cancelled/error → turnId oracle
  (clear/restore); pending_client_tool → restore-and-unlock. P4 registers a blocking/
  execute/resume policy by passing one object; the transport, the SSE handler, and the
  owner are identical for both (integration test 2b pins the swap).
- **D4 backfill deleted** (§11.2) — the `complete` case's `ADD_FINAL_RESPONSE` backfill
  (which read pre-turn `state.messages`) is gone; the reducer's `COMPLETE_STREAMING_TURN`
  finalizes the streamed cell. D5's `state.selectedAssistant` read remains only in the
  non-terminal `message` case as the documented display fallback.
- **`_justCompletedStreaming` 100 ms window, DraftUserCell local-state collapse, server
  `composerSnapshot` echo — untouched (P3, optional per §7).** The Stop/cancel lifecycle
  (`requestServerStop`, `completeConfirmedCancellation`, `reconcileLostSendAndStop`, 409
  retry) is byte-for-byte unchanged.

### New / changed surfaces

| File | Change |
|------|--------|
| `services/api.ts` | `sendMessageStream` onComplete `(terminalEventType?)`; terminal-event-driven single callback; no abort-on-terminal |
| `contexts/conversation/useConversationActions.ts` | `+CLEAR_ATTACHMENTS` at send (P0); `applyComposerTerminalOutcome` + `ComposerTerminalPolicy` seam; `onComplete` is the single composer owner (refs/snapshot only) |
| `contexts/conversation/useStreamingEventHandler.ts` | conversation-state-only on all terminal cases; no composer dispatches, no `abortActiveStreams`/`onStreamTerminal`, no `state.streamingMode === 'sending'` gates, no D4 backfill |
| `contexts/conversation/types.ts` | `ComposerTerminalOutcome`, `ComposerTerminalPolicy`, `ProviderProps.composerTerminalPolicy` |
| `contexts/ConversationContext.tsx` | threads `composerTerminalPolicy`; drops the now-unused `abortActiveStreams` (cleanup paths keep targeted aborts) |
| `components/notebook/conversations/DraftUserCell.tsx` | contract comment at the `pendingAttachments` site (§6.5) |
| `contexts/conversation/__tests__/turnTerminal.integration.test.tsx` | NEW — provider-level matrix (§9.1): success, pending_client_tool (default + seam swap), cancelled±turnId, error w/o turnId, other-client broadcast, idle timeout, undo-after-success |
| `services/__tests__/api.projects.conversations.test.ts` | terminal-event delivery contract (single callback, stop-reading, tag per outcome, abort = no callback) |
| `contexts/__tests__/useStreamingEventHandler.test.tsx` | rewritten to pin the conversation-only contract (was pinning the removed composer gates) |
| `contexts/conversation/__tests__/useConversationActions.test.ts` | `onComplete` owner routing tests (success / turn-less restore / persisted-turn clear) |

Success criteria from §12: all met. The only file that knows what `pending_client_tool`
*means for the composer* is the policy object (default inline in `applyComposerTerminalOutcome`);
`useStreamingEventHandler.ts` and the transport contain no client-tool composer logic.

### Post-implementation review: double-dispatch & file-refresh audit

1. **Double `COMPLETE_STREAMING_TURN` (handler + owner, all four terminals) — verified safe.**
   The reducer case is guarded by `if (!state.currentTurn) return state;` and is a pure
   state transform (no logs, no window events, no timers, no API). The handler's dispatch
   runs first (transport delivers `onEvent(terminal)` before `onComplete`), finalizes the
   cell, and clears `currentTurn`; the owner's second dispatch is a no-op. There are no
   analytics/telemetry hooks wired to this action, and nothing consumes the
   `streaming` flag transition. Net cost is one extra no-op dispatch per terminal — the
   owner must dispatch it unconditionally because the `pendingStopRef` early-return path
   (confirmed Stop) makes the handler's dispatch unreachable.
2. **`loadNotebookFiles` scope — was a real gap, now fixed and exactly-once per outcome.**
   The initial P2 pass left the tree refresh in the handler's `complete` case only, so
   `cancelled` / `error` / `pending_client_tool` (and the confirmed-Stop path, which
   early-returns before `onComplete`) never refreshed the tree. Fixed so **every** local
   turn-terminal outcome refreshes the file tree exactly once:
   - the send-side owner (`onComplete`) refreshes for success / cancelled / error /
     `pending_client_tool` (it early-returns only for the confirmed-Stop path, where the
     handler's terminal cases are unreachable);
   - `completeConfirmedCancellation` refreshes for the confirmed local Stop;
   - the handler's `complete` case refreshes **only for `source === 'observer'`**
     (other-client completions have no send-side owner).
   This also removes a pre-existing double refresh: the original code called
   `loadNotebookFiles` in both the handler's `complete` case and the owner's `onComplete`
   for a local success (the handler additionally fired a redundant manual
   `refresh-notebook-files` event that `loadNotebookFiles` already emits internally —
   removed). Integration test 1 pins `toHaveBeenCalledTimes(1)` on the local success
   path; tests 2/3/4/5 pin the refresh on each non-success terminal.
