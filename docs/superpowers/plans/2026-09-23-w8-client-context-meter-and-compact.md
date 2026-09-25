# W8: Client Context Meter and Compact Button Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A user composing a message sees how full their context window is, can press "Compact" to reclaim it (with before/after token feedback), sees a divider in the transcript marking where an earlier compaction happened, and gets a clear, compaction-naming message when a turn overflows — all in the notebook conversation composer (`DraftUserCell.tsx`) and transcript (`CellList.tsx`).

**Architecture:** Two new small, self-contained components (`ContextMeter`, `CompactButton`) render in `DraftUserCell`'s existing composer-footer row, fed by two fields (`conversationId`, `contextStatus`) newly exposed on the `useConversation()` context — plumbing that already exists server-side (W2/W4) and travels every conversation fetch, but was never wired past `ConversationContext.tsx`'s `refresh()` before this plan. A new `case 'compaction_boundary_marker'` in the SSE dispatcher keeps `contextStatus.boundaryTurnIndex` live during an active stream, which `CellList` also reads to render a divider positioned by matching a rendered turn's first message's `turnIndex`. `CompactButton` calls a new thin `api.projects.notebooks.conversations.compact(...)` wrapper (already-shipped `POST .../compact`, W4) and re-triggers `refresh()` on success. `chat_context_overflow` gets its own branch in the existing SSE `error` case, naming Compact as the remedy client-side (independent of W6, which is blocked on this plan and hasn't sharpened the *server* message yet).

**Tech Stack:** React 19 + TypeScript, Vite 6, Vitest 4 + React Testing Library + `@testing-library/user-event`, Tailwind CSS (utility classes only, no CSS modules/styled-components in this area of the codebase).

**Spec:** [`docs/superpowers/specs/2026-09-21-context-compaction-design.md`](../specs/2026-09-21-context-compaction-design.md) — *Client surface*, *Error handling*. Checklist: [`docs/context-compaction-plan.md`](../../context-compaction-plan.md) W8 (8.1–8.8).

## Global Constraints

- **`DraftUserCell.tsx` is already 837 lines** — per checklist 8.2, the meter and Compact button ship as their own components (`ContextMeter.tsx`, `CompactButton.tsx`), not inline markup added to this file.
- **Meter states, verbatim from the spec:** Known utilization (`contextWindowTokens` resolved), Unknown window (`contextWindowTokens` is null — render an explicit unknown state, never a guess), Compacted (`boundaryTurnIndex` set), Busy (conversation locked; compact action disabled, D8).
- **The compact endpoint already exists and is not part of this plan's scope to build** — `POST /api/projects/{projectId}/notebooks/{notebookId}/conversations/{convoId}/compact` (W4, shipped), returning `{ boundaryTurnIndex: number | null, messagesSummarized: number, estimatedTokensBefore: number | null, estimatedTokensAfter: number | null }` on 200, `404` if the conversation doesn't exist, `409` (body `{ error: string }`) if the conversation is locked. This plan only adds the client's call site and UI.
- **No server-side changes in this plan.** Everything here is under `src/client/src`.
- **Coverage gate:** `src/client/vitest.config.ts`'s `coverage` block runs with `all: true` — every new file is automatically included in the aggregate `lines: 85 / functions: 83 / branches: 80 / statements: 85` gate (`npm run test:coverage`, which the client-test CI workflow runs). An untested new component silently drags the whole gate down; there is no allowlist step needed to be *included*, only to be *excluded* (and nothing here should be excluded).
- **Server tests: not applicable to this plan.** Client tests: Vitest + React Testing Library, following the codebase's established per-component test-splitting convention (e.g. `DraftUserCell.extended.test.tsx`, `CellList.scroll.integration.test.tsx`) — new, narrowly-scoped test files rather than growing the existing broad ones.
- Never co-author commits, and only commit when the user has said to (per `CLAUDE.md` and memory `no-commits-without-permission`). The commit steps below are proposed checkpoints — ask first.

## Review Focus

- **`contextStatus` absent entirely (a conversation that has never streamed a single turn, or a provider that never reported usage).** `ContextMeter` must render the explicit "unknown" state, not throw on `null`/`undefined` or silently show `0%`. Pinned by Task 3's `renders unknown state when contextStatus is null` test.
- **The compact button pressed while the conversation is actively streaming in this same tab.** A reasonable person expects the button to already be disabled, not to fire a request that comes back 409. Pinned by Task 4's `disabled while isBusy` test (no click handler even reachable) and Task 5's wiring test asserting `isBusy` reaches the button.
- **The compact button pressed by a *different* tab/user while this tab is idle — the client doesn't know it's locked until the request comes back.** A reasonable person expects a specific "someone else is using this conversation" message, not a generic failure. Pinned by Task 4's `shows a busy message on 409` test, reusing the exact wording precedent already established for Undo's 409 (`useConversationActions.ts`).
- **The compaction-boundary SSE marker arriving for an observer who is watching a *different* turn than the one that triggered it.** Since `compaction_boundary_marker` is deliberately not in `TURN_SCOPED_EVENT_TYPES` (an observer of any turn in this conversation should learn the boundary moved), a naive implementation might filter it out like the turn-scoped events. Pinned by Task 2's test asserting the case fires regardless of `getActiveStreamTurnId()`.
- **A conversation that was compacted, then the compaction happened again later (repeated compaction) — only the transcript divider for the *current* boundary should show, not a stale one from the first compaction.** Since the divider's position is derived live from `contextStatus.boundaryTurnIndex` (a single value, not a history), a second compaction naturally moves it forward with no separate cleanup needed — but this is worth a direct assertion, not just an accident of the design. Pinned by Task 6's `marker moves to the new boundary after a second compaction` test.

---

## Findings that shaped this plan

- **`conversationId` is not exposed by `useConversation()` today**, despite being one of `ConversationProvider`'s own `ProviderProps` (`contexts/conversation/types.ts:43`) and already in scope inside `ConversationContext.tsx` (destructured at `:45`). It just was never added to the `value` object built at `:427-458`. Exposing it is a one-line addition, not new plumbing.
- **`contextStatus` (a `ConversationContextStatus`, `types/notebook.ts:334-346`) already arrives on every `api.projects.notebooks.conversations.get(...)` response** (server: `ConversationDto.ContextStatus`, merged in by `GetConversationAsync`, W2/W4) **but is read nowhere on the client** — confirmed by a zero-match grep for `.contextStatus` across all of `src/client/src`. This is genuinely greenfield UI, not an extension of partial work.
- **The reducer (`contexts/conversation/reducer.ts`) never receives the full fetched DTO as one payload** — `ConversationContext.tsx`'s `refresh()` (`:126-194`) picks individual fields off `convo` and dispatches narrow, single-purpose actions (`SET_MESSAGES`, `SET_ASSISTANT`, etc.). `contextStatus` needs its own action (`SET_CONTEXT_STATUS`) following this same narrow-action pattern, not a new "hydrate everything" action.
- **`compaction_boundary_marker` is already fully implemented and streaming from the server** (W5: `StreamingEventTypes.CompactionBoundaryMarker`, emitted exactly once by `ConversationStreamEngine.ShouldEmitCompactionBoundaryMarker` — the turn immediately after a boundary is set, never again after). The client's SSE dispatcher (`useStreamingEventHandler.ts`) has no case for it, so it falls into the `default` branch (`:424-430`), which logs it and does nothing further (the payload has no `content` field, so even the legacy-format fallback inside `default` is inert). This event is real today, not hypothetical — the plan only needs the client-side case, not any server work.
- **The exact payload is `{ turnId: string, boundaryTurnIndex: number, timestamp: string }`, camelCase** (`StreamingEvents.cs:104-116`, `JsonNamingPolicy.CamelCase`). `compaction_boundary_marker` is deliberately **not** in `TURN_SCOPED_EVENT_TYPES` (`useStreamingEventHandler.ts:24-36`) and must stay that way — an observer watching this conversation should learn the boundary moved regardless of which turn they're currently attached to.
- **There is no existing "insert a non-chat marker into the transcript" mechanism.** `MessageDto.role` (`ChatRole`) has no `'marker'`/`'divider'` variant, and `CellList.tsx`'s `computeTurns` (`:750-801`) only recognizes `'user' | 'assistant' | 'tool'` — a message with any other role is silently dropped from every turn bucket, never rendered. Rather than extending `ChatRole`/`computeTurns` (invasive, cross-cutting), this plan follows the same shape `WorkflowSection` already uses: a full-width `col-span-3` block (`CellList.tsx:927-935` is the precedent) inserted positionally in the render loop by comparing `turn.allMessages[0]?.turnIndex` (every `MessageDto` already carries `turnIndex`, `types/conversation.ts:31`) against `compactionBoundaryTurnIndex + 1` — the exact same "first turn after the boundary" semantics the server already uses (`ShouldEmitCompactionBoundaryMarker`). This needs zero changes to `ChatRole`, `MessageDto`, or `computeTurns`.
- **`CellList` receives everything via explicit props from its one real caller, `ConversationPanel.tsx`** (`ConversationPanel.tsx:82-106`) — it does not call `useConversation()` itself. So the marker's data (`compactionBoundaryTurnIndex`) needs a new prop threaded through `ConversationPanel.tsx`, not just a context read inside `CellList`.
- **A raw `lock` field is *not* needed for the "Busy" meter state.** The obvious plumbing (exposing `NotebookConversationWithMessagesDto.lock` through context) turns out to be redundant: `DraftUserCell.tsx` already computes `isBusy = Boolean(isStreaming) || isTranscribing` (`:182`) from a prop it already receives, and the compact endpoint's own `409` is the authoritative lock check regardless of any client-side guess. Reusing `isBusy` to proactively disable the button — and letting the 409 handler be the real safety net — avoids new context plumbing entirely (`lock`'s internal-only usage in `useConversationActions.ts` at lines 355-403/945-989/1090-1129 stays exactly as it is, untouched by this plan) and sidesteps a naming collision the research surfaced between a raw `lock` field and the existing-but-unread `activeStreamingUser` (itself derived from `lock.lockedByUserName`).
- **No shared number-formatting utility exists** in `src/client/src` — every component that formats a token/usage count (`InvocationTree.tsx:29-30`, `ConversationDetailPanel.tsx:63-64`, `InvocationDetailPanel.tsx:52-53`, `GuideUsagePage.tsx:305-314`) redeclares the same one-line `const formatNumber = (value: number) => new Intl.NumberFormat('en-US').format(value);` locally. `ContextMeter` follows this established convention rather than introducing a new shared util (out of scope for this plan to fix).
- **The closest real precedent for a small, API-calling, multi-state component with its own test file** is `components/guides/configTabs/PublishedGuideApiKeySection.tsx` (+ its `__tests__/PublishedGuideApiKeySection.test.tsx`): direct `api.*` import and call (no custom hook wrapper), `try/catch` + `finally` around a `useState`-tracked in-flight flag, `vi.mock('.../services/api', ...)` with only the used nested path stubbed, RTL + `@testing-library/user-event`, flat `it(...)` blocks, a `renderX()` helper. `CompactButton` follows this shape, using the toast system (`useToast`, already imported the same way in `DraftUserCell.tsx:16`) for success/error feedback instead of `PublishedGuideApiKeySection`'s inline error state — toast is the better fit here since the codebase already uses it for exactly this kind of transient, action-triggered notification (`TurnMessagesPanel.tsx:283,317-330`), and `DraftUserCell.tsx` already has a `ToastProvider` ancestor (confirmed by its own `useToast()` call at `:116`).
- **`callApi<T>` (`services/api.ts:97`) is the only HTTP helper — there is no separate `postApi`.** A no-body POST returning typed JSON is `callApi<T>(url, { method: 'POST' })`, exactly matching `generateTitle` (`:1192-1196`) and `restartLlamaRuntime` (`:1214-1218`), both in the same `conversations` object `compact` is added to.

---

## File Structure

**Created:**
- `src/client/src/components/notebook/conversations/ContextMeter.tsx`
- `src/client/src/components/notebook/conversations/__tests__/ContextMeter.test.tsx`
- `src/client/src/components/notebook/conversations/CompactButton.tsx`
- `src/client/src/components/notebook/conversations/__tests__/CompactButton.test.tsx`
- `src/client/src/components/notebook/conversations/__tests__/DraftUserCell.contextMeter.test.tsx`
- `src/client/src/components/notebook/conversations/__tests__/CellList.compactionMarker.test.tsx`

**Modified:**
- `src/client/src/types/notebook.ts` — add `ConversationCompactionResult`
- `src/client/src/services/api.ts` — add `conversations.compact(...)`
- `src/client/src/contexts/conversation/types.ts` — add `conversationId`, `contextStatus` to `ConversationContextProps`; add `contextStatus` to `ExtendedConversationState`; add `'SET_CONTEXT_STATUS'` to `ActionType`
- `src/client/src/contexts/conversation/reducer.ts` — add the `SET_CONTEXT_STATUS` case
- `src/client/src/contexts/ConversationContext.tsx` — dispatch `SET_CONTEXT_STATUS` in `refresh()`; expose `conversationId`/`contextStatus` on `value`
- `src/client/src/contexts/conversation/useStreamingEventHandler.ts` — add `case 'compaction_boundary_marker'`; add a `chat_context_overflow` branch inside `case 'error'`
- `src/client/src/contexts/conversation/__tests__/reducer.test.ts` — new tests
- `src/client/src/contexts/__tests__/useStreamingEventHandler.test.tsx` — new tests
- `src/client/src/components/notebook/conversations/DraftUserCell.tsx` — destructure new context fields; render `ContextMeter` + `CompactButton` in the footer
- `src/client/src/components/notebook/conversations/CellList.tsx` — add `compactionBoundaryTurnIndex` prop; render the boundary divider
- `src/client/src/components/notebook/conversations/ConversationPanel.tsx` — pass `compactionBoundaryTurnIndex` through to `CellList`
- `docs/context-compaction-plan.md` — tick 8.1–8.8, link this plan

---

## Task 1: Client type + `compact()` API wrapper (groundwork for 8.4)

**Files:**
- Modify: `src/client/src/types/notebook.ts`
- Modify: `src/client/src/services/api.ts`

**Interfaces:**
- Consumes: nothing new.
- Produces: `ConversationCompactionResult` (exported type); `api.projects.notebooks.conversations.compact(projectId: string, notebookId: string, convoId: string) -> Promise<ConversationCompactionResult>`, consumed by Task 4's `CompactButton`.

This task is a type + thin HTTP-wrapper addition with no branching logic — the codebase has no precedent for unit-testing an individual `api.ts` entry in isolation (confirmed: no test file targets a single wrapper function; every `api.*` call is exercised through the component that uses it, via `vi.mock('.../services/api', ...)`). Task 4's `CompactButton` tests are what exercise this wrapper's actual contract (URL, method, response shape) through a mock. This task's own verification is a successful typecheck.

- [ ] **Step 1: Add the type**

In `types/notebook.ts`, add immediately after `ConversationContextStatus` (after line 346, before `NotebookConversationWithMessagesDto`):

```ts
export interface ConversationCompactionResult {
    boundaryTurnIndex: number | null;
    messagesSummarized: number;
    estimatedTokensBefore: number | null;
    estimatedTokensAfter: number | null;
}
```

- [ ] **Step 2: Add the API wrapper**

In `services/api.ts`, inside the `conversations` object, immediately after `generateTitle` (after line 1196):

```ts
                compact: (projectId: string, notebookId: string, convoId: string) =>
                    callApi<import('../types/notebook').ConversationCompactionResult>(
                        `/projects/${projectId}/notebooks/${notebookId}/conversations/${convoId}/compact`,
                        { method: 'POST' }
                    ),
```

- [ ] **Step 3: Verify**

Run: `cd src/client && npm run typecheck`
Expected: passes clean — this only adds a type and a function, no existing call site is touched.

- [ ] **Step 4: Commit (ask first)**

```bash
git add src/client/src/types/notebook.ts src/client/src/services/api.ts
git commit -m "Adds the client compact() API wrapper and its response type"
```

---

## Task 2: Expose `conversationId`/`contextStatus` on context; live-update from the SSE boundary marker (5.1 of spec's Data flow made visible; groundwork for 8.1, 8.3, 8.6)

**Files:**
- Modify: `src/client/src/contexts/conversation/types.ts`
- Modify: `src/client/src/contexts/conversation/reducer.ts`
- Modify: `src/client/src/contexts/ConversationContext.tsx`
- Modify: `src/client/src/contexts/conversation/useStreamingEventHandler.ts`
- Test: `src/client/src/contexts/conversation/__tests__/reducer.test.ts`
- Test: `src/client/src/contexts/__tests__/useStreamingEventHandler.test.tsx`

**Interfaces:**
- Consumes: `ConversationCompactionResult`/`ConversationContextStatus` types (existing/Task 1).
- Produces: `useConversation().conversationId: string`, `useConversation().contextStatus: ConversationContextStatus | null`, both consumed by Tasks 3-6.

- [ ] **Step 1: Write the failing reducer test**

Add to `contexts/conversation/__tests__/reducer.test.ts` (append inside the existing `describe('conversation reducer', ...)` block):

```ts
  it('sets contextStatus wholesale on SET_CONTEXT_STATUS', () => {
    const status = {
      contextWindowTokens: 8192,
      estimatedPromptTokens: 4096,
      boundaryTurnIndex: null,
      estimateSource: 'ProviderUsage' as const,
      modelDeploymentId: 'gpt-4o-mini',
      contextWindowSource: 'Catalog' as const,
    };

    const next = reducer(initialState, { type: 'SET_CONTEXT_STATUS', payload: status });

    expect(next.contextStatus).toEqual(status);
  });

  it('SET_CONTEXT_STATUS with null clears a previously-known status', () => {
    const withStatus = reducer(initialState, {
      type: 'SET_CONTEXT_STATUS',
      payload: { contextWindowTokens: 100, estimatedPromptTokens: 10, boundaryTurnIndex: null, estimateSource: 'Characters' as const, modelDeploymentId: null, contextWindowSource: 'Unknown' as const },
    });

    const cleared = reducer(withStatus, { type: 'SET_CONTEXT_STATUS', payload: null });

    expect(cleared.contextStatus).toBeNull();
  });
```

- [ ] **Step 2: Run to verify it fails**

Run: `cd src/client && npx vitest run src/contexts/conversation/__tests__/reducer.test.ts`
Expected: FAIL to compile — `'SET_CONTEXT_STATUS'` is not assignable to `ActionType['type']`, and `contextStatus` doesn't exist on the reducer's returned state type.

- [ ] **Step 3: Add the type and reducer plumbing**

In `contexts/conversation/types.ts`:

Add to `ConversationContextProps` (after `pendingAttachments: PendingAttachment[];` at line 28):

```ts
  conversationId: string;
  contextStatus: ConversationContextStatus | null;
```

Add the import at the top of the file (after the existing `import type { EnhancedConversationState, ...} from '../../types/conversation';` at line 1):

```ts
import type { ConversationContextStatus } from '../../types/notebook';
```

Add `'SET_CONTEXT_STATUS'` to the `ActionType.type` union (append to the list ending `...'SET_STREAMING_MODE' | 'SET_UNDOING';` at line 95, becoming `'SET_STREAMING_MODE' | 'SET_UNDOING' | 'SET_CONTEXT_STATUS';`).

Add to `ExtendedConversationState` (after `notebookTemplate?: NotebookTemplateDto;` at line 114):

```ts
  contextStatus?: ConversationContextStatus | null;
```

In `contexts/conversation/reducer.ts`, add the case immediately after `SET_NOTEBOOK_TEMPLATE` (after line 128):

```ts
    case 'SET_CONTEXT_STATUS':
      return { ...state, contextStatus: action.payload };
```

- [ ] **Step 4: Run to verify it passes**

Run: `cd src/client && npx vitest run src/contexts/conversation/__tests__/reducer.test.ts`
Expected: PASS (existing tests + 2 new ones).

- [ ] **Step 5: Write the failing SSE-handler tests**

Add to `contexts/__tests__/useStreamingEventHandler.test.tsx`. First add a new top-level `describe` block (the existing one is scoped to `'useStreamingEventHandler error branch'`; add this as a sibling):

```ts
describe('useStreamingEventHandler compaction_boundary_marker', () => {
  it('updates contextStatus.boundaryTurnIndex, merging into an existing status', () => {
    const { handler, dispatch } = mountHandler({
      contextStatus: {
        contextWindowTokens: 8192,
        estimatedPromptTokens: 4096,
        boundaryTurnIndex: null,
        estimateSource: 'ProviderUsage',
        modelDeploymentId: 'gpt-4o-mini',
        contextWindowSource: 'Catalog',
      },
    } as any);

    handler({ type: 'compaction_boundary_marker', data: { turnId: 't1', boundaryTurnIndex: 4, timestamp: '2026-01-01T00:00:00Z' } });

    expect(dispatch).toHaveBeenCalledWith({
      type: 'SET_CONTEXT_STATUS',
      payload: {
        contextWindowTokens: 8192,
        estimatedPromptTokens: 4096,
        boundaryTurnIndex: 4,
        estimateSource: 'ProviderUsage',
        modelDeploymentId: 'gpt-4o-mini',
        contextWindowSource: 'Catalog',
      },
    });
  });

  it('builds a minimal contextStatus when none exists yet', () => {
    const { handler, dispatch } = mountHandler({ contextStatus: null } as any);

    handler({ type: 'compaction_boundary_marker', data: { turnId: 't1', boundaryTurnIndex: 2, timestamp: '2026-01-01T00:00:00Z' } });

    expect(dispatch).toHaveBeenCalledWith({
      type: 'SET_CONTEXT_STATUS',
      payload: {
        contextWindowTokens: null,
        estimatedPromptTokens: null,
        boundaryTurnIndex: 2,
        estimateSource: 'None',
        modelDeploymentId: null,
        contextWindowSource: 'Unknown',
      },
    });
  });

  it('is not turn-scoped: fires even when the event belongs to a different turn', () => {
    const { handler, dispatch } = mountHandler(
      { contextStatus: null } as any,
      { getActiveStreamTurnId: () => 'active-turn' },
    );

    handler({ type: 'compaction_boundary_marker', data: { turnId: 'some-other-turn', boundaryTurnIndex: 2, timestamp: '2026-01-01T00:00:00Z' } });

    expect(dispatch).toHaveBeenCalledWith(expect.objectContaining({ type: 'SET_CONTEXT_STATUS' }));
  });
});
```

- [ ] **Step 6: Run to verify they fail**

Run: `cd src/client && npx vitest run src/contexts/__tests__/useStreamingEventHandler.test.tsx`
Expected: FAIL — `dispatch` is never called with `SET_CONTEXT_STATUS` for any of the three new tests, since the `compaction_boundary_marker` case doesn't exist yet (falls through to `default`, which never dispatches `SET_CONTEXT_STATUS`).

- [ ] **Step 7: Implement the SSE case**

In `contexts/conversation/useStreamingEventHandler.ts`, add immediately after the `case 'system_message':` block (after line 334, before `case 'error':`):

```ts
      case 'compaction_boundary_marker':
        dispatch({
          type: 'SET_CONTEXT_STATUS',
          payload: state.contextStatus
            ? { ...state.contextStatus, boundaryTurnIndex: event.data.boundaryTurnIndex }
            : {
                contextWindowTokens: null,
                estimatedPromptTokens: null,
                boundaryTurnIndex: event.data.boundaryTurnIndex,
                estimateSource: 'None',
                modelDeploymentId: null,
                contextWindowSource: 'Unknown',
              },
        });
        break;
```

Do **not** add `'compaction_boundary_marker'` to `TURN_SCOPED_EVENT_TYPES` (lines 24-36) — per the Findings above, this event must reach the handler regardless of which turn currently owns the observer stream.

- [ ] **Step 8: Run to verify they pass**

Run: `cd src/client && npx vitest run src/contexts/__tests__/useStreamingEventHandler.test.tsx`
Expected: PASS (all existing tests + 3 new ones).

- [ ] **Step 9: Wire `refresh()` and the `value` object**

In `contexts/ConversationContext.tsx`, add a dispatch in `refresh()` immediately after the existing `dispatch({ type: 'SET_MESSAGES', payload: messages });` (after line 152):

```ts
        dispatch({ type: 'SET_CONTEXT_STATUS', payload: convo.contextStatus ?? null });
```

Add to the `value` object (after `pendingAttachments,` at line 449):

```ts
    conversationId,
    contextStatus: state.contextStatus ?? null,
```

- [ ] **Step 10: Run the full context test suite to confirm no regressions**

Run: `cd src/client && npx vitest run src/contexts/`
Expected: PASS, no regressions in `ConversationContext.provider.test.tsx`, `ConversationContext.reducer.test.ts`, `ConversationContext.streamingContracts.test.tsx`, or the reducer/handler tests above.

- [ ] **Step 11: Commit (ask first)**

```bash
git add src/client/src/contexts/conversation/types.ts src/client/src/contexts/conversation/reducer.ts src/client/src/contexts/ConversationContext.tsx src/client/src/contexts/conversation/useStreamingEventHandler.ts src/client/src/contexts/conversation/__tests__/reducer.test.ts src/client/src/contexts/__tests__/useStreamingEventHandler.test.tsx
git commit -m "Exposes conversationId and contextStatus on the conversation context"
```

---

## Task 3: `ContextMeter` component — known / unknown / compacted states (8.1, 8.3 meter half)

**Files:**
- Create: `src/client/src/components/notebook/conversations/ContextMeter.tsx`
- Test: `src/client/src/components/notebook/conversations/__tests__/ContextMeter.test.tsx`

**Interfaces:**
- Consumes: `ConversationContextStatus` (existing type, `types/notebook.ts`).
- Produces: `export default function ContextMeter({ contextStatus }: { contextStatus: ConversationContextStatus | null }): JSX.Element`, consumed by Task 5.

- [ ] **Step 1: Write the failing tests**

```tsx
import { render, screen } from '@testing-library/react';
import '@testing-library/jest-dom';
import { describe, it, expect } from 'vitest';
import ContextMeter from '../ContextMeter';
import type { ConversationContextStatus } from '../../../../types/notebook';

const knownStatus: ConversationContextStatus = {
  contextWindowTokens: 8192,
  estimatedPromptTokens: 4096,
  boundaryTurnIndex: null,
  estimateSource: 'ProviderUsage',
  modelDeploymentId: 'gpt-4o-mini',
  contextWindowSource: 'Catalog',
};

describe('ContextMeter', () => {
  it('renders an explicit unknown state when contextStatus is null', () => {
    render(<ContextMeter contextStatus={null} />);

    expect(screen.getByText(/context.*unknown/i)).toBeInTheDocument();
  });

  it('renders an explicit unknown state when contextWindowTokens is null', () => {
    render(<ContextMeter contextStatus={{ ...knownStatus, contextWindowTokens: null }} />);

    expect(screen.getByText(/context.*unknown/i)).toBeInTheDocument();
  });

  it('renders known utilization as a percentage of the window', () => {
    render(<ContextMeter contextStatus={knownStatus} />);

    expect(screen.getByText('50%')).toBeInTheDocument();
    expect(screen.getByText(/4,096/)).toBeInTheDocument();
    expect(screen.getByText(/8,192/)).toBeInTheDocument();
  });

  it('shows a compacted indicator when boundaryTurnIndex is set', () => {
    render(<ContextMeter contextStatus={{ ...knownStatus, boundaryTurnIndex: 3 }} />);

    expect(screen.getByText(/compacted/i)).toBeInTheDocument();
  });

  it('does not show a compacted indicator when never compacted', () => {
    render(<ContextMeter contextStatus={knownStatus} />);

    expect(screen.queryByText(/compacted/i)).not.toBeInTheDocument();
  });
});
```

- [ ] **Step 2: Run to verify they fail**

Run: `cd src/client && npx vitest run src/components/notebook/conversations/__tests__/ContextMeter.test.tsx`
Expected: FAIL — the module `../ContextMeter` doesn't exist.

- [ ] **Step 3: Implement**

```tsx
import type { ConversationContextStatus } from '../../../types/notebook';

interface ContextMeterProps {
  contextStatus: ConversationContextStatus | null;
}

const formatNumber = (value: number) => new Intl.NumberFormat('en-US').format(value);

export default function ContextMeter({ contextStatus }: ContextMeterProps) {
  const isUnknown =
    contextStatus == null ||
    contextStatus.contextWindowTokens == null ||
    contextStatus.estimatedPromptTokens == null;

  if (isUnknown) {
    return (
      <span className="text-xs text-gray-400" data-testid="context-meter">
        Context: unknown
      </span>
    );
  }

  const { estimatedPromptTokens, contextWindowTokens, boundaryTurnIndex } = contextStatus;
  const percent = Math.min(100, Math.round((estimatedPromptTokens / contextWindowTokens) * 100));

  return (
    <span className="text-xs text-gray-500" data-testid="context-meter">
      {boundaryTurnIndex != null && (
        <span className="mr-1 text-gray-400" title="This conversation has been compacted">
          Compacted ·
        </span>
      )}
      {formatNumber(estimatedPromptTokens)} / {formatNumber(contextWindowTokens)} tokens ({percent}%)
    </span>
  );
}
```

- [ ] **Step 4: Run to verify they pass**

Run: `cd src/client && npx vitest run src/components/notebook/conversations/__tests__/ContextMeter.test.tsx`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit (ask first)**

```bash
git add src/client/src/components/notebook/conversations/ContextMeter.tsx src/client/src/components/notebook/conversations/__tests__/ContextMeter.test.tsx
git commit -m "Adds the ContextMeter component"
```

---

## Task 4: `CompactButton` component — endpoint call, before/after feedback, 409 handling, busy disabling (8.3 busy half, 8.4, 8.5)

**Files:**
- Create: `src/client/src/components/notebook/conversations/CompactButton.tsx`
- Test: `src/client/src/components/notebook/conversations/__tests__/CompactButton.test.tsx`

**Interfaces:**
- Consumes: `api.projects.notebooks.conversations.compact` (Task 1), `useToast` (`../../common/Toast`, existing).
- Produces: `export default function CompactButton({ projectId, notebookId, conversationId, disabled, onSuccess }: CompactButtonProps): JSX.Element`, consumed by Task 5. `onSuccess?: () => void` is how the caller (Task 5) re-triggers `refresh()`.

- [ ] **Step 1: Write the failing tests**

```tsx
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import '@testing-library/jest-dom';
import { ToastProvider } from '../../../common/Toast';
import CompactButton from '../CompactButton';

vi.mock('../../../../services/api', () => ({
  api: {
    projects: {
      notebooks: {
        conversations: {
          compact: vi.fn(),
        },
      },
    },
  },
}));

import { api } from '../../../../services/api';

const renderButton = (props: Partial<React.ComponentProps<typeof CompactButton>> = {}) =>
  render(
    <CompactButton
      projectId="p1"
      notebookId="n1"
      conversationId="c1"
      {...props}
    />,
    { wrapper: ToastProvider },
  );

describe('CompactButton', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('is disabled when disabled=true and never calls the endpoint on click', async () => {
    renderButton({ disabled: true });
    const user = userEvent.setup();

    await user.click(screen.getByRole('button', { name: /compact/i }));

    expect(api.projects.notebooks.conversations.compact).not.toHaveBeenCalled();
  });

  it('calls the compact endpoint and shows before/after token feedback on success', async () => {
    vi.mocked(api.projects.notebooks.conversations.compact).mockResolvedValue({
      boundaryTurnIndex: 5,
      messagesSummarized: 12,
      estimatedTokensBefore: 5000,
      estimatedTokensAfter: 900,
    });
    const onSuccess = vi.fn();
    renderButton({ onSuccess });
    const user = userEvent.setup();

    await user.click(screen.getByRole('button', { name: /compact/i }));

    await waitFor(() => {
      expect(api.projects.notebooks.conversations.compact).toHaveBeenCalledWith('p1', 'n1', 'c1');
      expect(screen.getByText(/5,000/)).toBeInTheDocument();
      expect(screen.getByText(/900/)).toBeInTheDocument();
      expect(onSuccess).toHaveBeenCalled();
    });
  });

  it('shows a busy message on 409 without calling onSuccess', async () => {
    const conflict: any = new Error('Conversation is locked by someone-else');
    conflict.status = 409;
    vi.mocked(api.projects.notebooks.conversations.compact).mockRejectedValue(conflict);
    const onSuccess = vi.fn();
    renderButton({ onSuccess });
    const user = userEvent.setup();

    await user.click(screen.getByRole('button', { name: /compact/i }));

    await waitFor(() => {
      expect(screen.getByText(/conversation is busy/i)).toBeInTheDocument();
    });
    expect(onSuccess).not.toHaveBeenCalled();
  });

  it('shows a generic error message on any other failure', async () => {
    vi.mocked(api.projects.notebooks.conversations.compact).mockRejectedValue(new Error('boom'));
    renderButton();
    const user = userEvent.setup();

    await user.click(screen.getByRole('button', { name: /compact/i }));

    await waitFor(() => {
      expect(screen.getByText(/failed to compact/i)).toBeInTheDocument();
    });
  });
});
```

- [ ] **Step 2: Run to verify they fail**

Run: `cd src/client && npx vitest run src/components/notebook/conversations/__tests__/CompactButton.test.tsx`
Expected: FAIL — the module `../CompactButton` doesn't exist.

- [ ] **Step 3: Implement**

```tsx
import { useState } from 'react';
import { api } from '../../../services/api';
import { useToast } from '../../common/Toast';

interface CompactButtonProps {
  projectId: string;
  notebookId: string;
  conversationId: string;
  disabled?: boolean;
  onSuccess?: () => void;
}

const formatNumber = (value: number) => new Intl.NumberFormat('en-US').format(value);

export default function CompactButton({ projectId, notebookId, conversationId, disabled = false, onSuccess }: CompactButtonProps) {
  const [isCompacting, setIsCompacting] = useState(false);
  const { showToast } = useToast();

  const handleClick = async () => {
    if (disabled || isCompacting) return;
    setIsCompacting(true);
    try {
      const result = await api.projects.notebooks.conversations.compact(projectId, notebookId, conversationId);
      const before = result.estimatedTokensBefore != null ? formatNumber(result.estimatedTokensBefore) : 'unknown';
      const after = result.estimatedTokensAfter != null ? formatNumber(result.estimatedTokensAfter) : 'unknown';
      showToast({
        type: 'success',
        title: 'Conversation compacted',
        message: `Reduced from ~${before} to ~${after} estimated tokens.`,
        duration: 6000,
      });
      onSuccess?.();
    } catch (err: any) {
      if (err?.status === 409) {
        showToast({
          type: 'warning',
          title: 'Conversation is busy',
          message: 'Wait for the current response to finish and try again.',
          duration: 6000,
        });
      } else {
        showToast({
          type: 'error',
          title: 'Failed to compact conversation',
          message: err?.message || 'An unexpected error occurred.',
          duration: 6000,
        });
      }
    } finally {
      setIsCompacting(false);
    }
  };

  return (
    <button
      type="button"
      onClick={handleClick}
      disabled={disabled || isCompacting}
      className="px-2 py-1.5 text-xs text-gray-600 rounded hover:bg-gray-100 transition-colors disabled:opacity-50 disabled:cursor-not-allowed"
      title="Summarize older messages to free up context"
    >
      {isCompacting ? 'Compacting…' : 'Compact'}
    </button>
  );
}
```

- [ ] **Step 4: Run to verify they pass**

Run: `cd src/client && npx vitest run src/components/notebook/conversations/__tests__/CompactButton.test.tsx`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit (ask first)**

```bash
git add src/client/src/components/notebook/conversations/CompactButton.tsx src/client/src/components/notebook/conversations/__tests__/CompactButton.test.tsx
git commit -m "Adds the CompactButton component"
```

---

## Task 5: Wire `ContextMeter` and `CompactButton` into `DraftUserCell`'s footer (8.1)

**Files:**
- Modify: `src/client/src/components/notebook/conversations/DraftUserCell.tsx`
- Test: `src/client/src/components/notebook/conversations/__tests__/DraftUserCell.contextMeter.test.tsx`

**Interfaces:**
- Consumes: `ContextMeter` (Task 3), `CompactButton` (Task 4), `conversationId`/`contextStatus`/`refresh` from `useConversation()` (Task 2; `refresh` already existed).
- Produces: nothing new for later tasks — this is a leaf wiring task.

- [ ] **Step 1: Write the failing test**

```tsx
import React from 'react';
import { render as rtlRender, screen } from '@testing-library/react';
import { describe, it, expect, vi } from 'vitest';
import '@testing-library/jest-dom';
import DraftUserCell from '../DraftUserCell';
import { ToastProvider } from '../../../common/Toast';

const render = (ui: React.ReactElement) => rtlRender(ui, { wrapper: ToastProvider });

vi.mock('../../../../contexts/NotebookContext', () => ({
  __esModule: true,
  useNotebook: () => ({ notebook: { id: 'nb-1', projectId: 'proj-1' } }),
}));

const mockUseConversation = vi.fn();
vi.mock('../../../../contexts/ConversationContext', () => ({
  __esModule: true,
  useConversation: () => mockUseConversation(),
}));

vi.mock('../../../../services/notebookFiles', () => ({
  notebookFilesApi: { uploadFiles: vi.fn().mockResolvedValue([]) },
}));

vi.mock('../../../../services/api', () => ({
  api: { projects: { notebooks: { conversations: { compact: vi.fn() } } } },
}));

describe('DraftUserCell context meter and compact button', () => {
  it('renders the context meter and compact button using context data', () => {
    mockUseConversation.mockReturnValue({
      pendingAttachments: [],
      addPendingAttachment: vi.fn(),
      removePendingAttachment: vi.fn(),
      conversationId: 'c1',
      contextStatus: {
        contextWindowTokens: 8192,
        estimatedPromptTokens: 4096,
        boundaryTurnIndex: null,
        estimateSource: 'ProviderUsage',
        modelDeploymentId: 'gpt-4o-mini',
        contextWindowSource: 'Catalog',
      },
      refresh: vi.fn(),
    });

    render(<DraftUserCell value="" onChange={vi.fn()} onSend={vi.fn()} />);

    expect(screen.getByTestId('context-meter')).toHaveTextContent('50%');
    expect(screen.getByRole('button', { name: /compact/i })).toBeInTheDocument();
  });

  it('disables the compact button while streaming (busy)', () => {
    mockUseConversation.mockReturnValue({
      pendingAttachments: [],
      addPendingAttachment: vi.fn(),
      removePendingAttachment: vi.fn(),
      conversationId: 'c1',
      contextStatus: null,
      refresh: vi.fn(),
    });

    render(<DraftUserCell value="" onChange={vi.fn()} onSend={vi.fn()} isStreaming />);

    expect(screen.getByRole('button', { name: /compact/i })).toBeDisabled();
  });
});
```

- [ ] **Step 2: Run to verify it fails**

Run: `cd src/client && npx vitest run src/components/notebook/conversations/__tests__/DraftUserCell.contextMeter.test.tsx`
Expected: FAIL — no element with `data-testid="context-meter"` and no button named `/compact/i` exist in the current render.

- [ ] **Step 3: Implement**

In `DraftUserCell.tsx`, add imports (after the existing `import { useToast } from '../../common/Toast';` at line 16):

```tsx
import ContextMeter from './ContextMeter';
import CompactButton from './CompactButton';
```

Extend the existing `useConversation()` destructure at line 115:

```tsx
  const { pendingAttachments, addPendingAttachment, removePendingAttachment, conversationId, contextStatus, refresh } = useConversation();
```

In the footer JSX, insert the meter into the left-hand hint block (`<div className="text-sm text-gray-500">`, opening at line 752) as a new first child, before the existing `{!isReadOnly && (...Ctrl+Enter...)}` block (line 754):

```tsx
                {!isReadOnly && (
                  <span className="mr-3">
                    <ContextMeter contextStatus={contextStatus} />
                  </span>
                )}
```

Insert the button into the right-hand button cluster (`<div className="flex items-center space-x-2">`, opening at line 768), as a new first child, before the `{/* Camera button */}` block (line 769-781):

```tsx
                {!isReadOnly && conversationId && projectId && notebookId && (
                  <CompactButton
                    projectId={projectId}
                    notebookId={notebookId}
                    conversationId={conversationId}
                    disabled={isBusy}
                    onSuccess={refresh}
                  />
                )}
```

`projectId`/`notebookId` are the existing local consts (`:100-101`); `isBusy` is the existing local const (`:182`).

- [ ] **Step 4: Run to verify it passes**

Run: `cd src/client && npx vitest run src/components/notebook/conversations/__tests__/DraftUserCell.contextMeter.test.tsx`
Expected: PASS (2 tests).

Then run the full existing `DraftUserCell` test suite to confirm no regressions from the new destructure/imports:

Run: `cd src/client && npx vitest run src/components/notebook/conversations/__tests__/DraftUserCell.test.tsx src/components/notebook/conversations/__tests__/DraftUserCell.extended.test.tsx src/components/notebook/conversations/__tests__/DraftUserCell.interactions.test.tsx src/components/notebook/conversations/__tests__/DraftUserCell.camera-speech.test.tsx`
Expected: PASS — these files' `useConversation()` mocks don't include `conversationId`/`contextStatus`/`refresh`, so those destructure to `undefined`; `ContextMeter` renders its "unknown" state and the `CompactButton` guard (`conversationId && projectId && notebookId`) evaluates falsy, so nothing new renders and nothing throws.

- [ ] **Step 5: Commit (ask first)**

```bash
git add src/client/src/components/notebook/conversations/DraftUserCell.tsx src/client/src/components/notebook/conversations/__tests__/DraftUserCell.contextMeter.test.tsx
git commit -m "Wires the context meter and compact button into the composer footer"
```

---

## Task 6: Boundary marker rendered in the transcript (8.6)

**Files:**
- Modify: `src/client/src/components/notebook/conversations/CellList.tsx`
- Modify: `src/client/src/components/notebook/conversations/ConversationPanel.tsx`
- Test: `src/client/src/components/notebook/conversations/__tests__/CellList.compactionMarker.test.tsx`

**Interfaces:**
- Consumes: `contextStatus.boundaryTurnIndex` (Task 2, via `ConversationPanel`'s existing `useConversation()` call).
- Produces: `CellListProps.compactionBoundaryTurnIndex?: number | null` — no other task consumes this; it's the terminal rendering step for the boundary value.

- [ ] **Step 1: Write the failing test**

```tsx
import React from 'react';
import { render, screen } from '@testing-library/react';
import '@testing-library/jest-dom';
import CellList from '../CellList';
import { MessageDto } from '../../../../types/conversation';
import { vi, describe, it, expect } from 'vitest';
import { ToastProvider } from '../../../common/Toast';

vi.mock('../UserCell', () => ({ default: ({ content }: { content: string }) => <div data-testid="user-cell">{content}</div> }));
vi.mock('../AssistantCell', () => ({ default: ({ content }: { content: string }) => <div data-testid="assistant-cell">{content}</div> }));
vi.mock('../EditableAssistantCell', () => ({ default: () => <div data-testid="editable-assistant-cell" /> }));
vi.mock('../DraftUserCell', () => ({ default: () => <textarea data-testid="draft-input" /> }));
vi.mock('../../../services/userService', () => ({
  userService: {
    getCurrentUser: vi.fn().mockResolvedValue({ id: 'u1', name: 'Test User' }),
    getUserById: vi.fn().mockResolvedValue({ id: 'u1', name: 'Test User' }),
    isCurrentUser: vi.fn().mockResolvedValue(true),
  },
}));
vi.mock('../../../../contexts/NotebookContext', () => ({
  useNotebook: vi.fn(() => ({ notebook: { id: 'notebook-1', projectId: 'project-1', title: 'Test Notebook' }, folderTree: null, uploadFiles: vi.fn().mockResolvedValue([]) })),
}));

const baseProps = {
  isStreaming: false,
  draftUserContent: '',
  editingAssistantId: undefined,
  selectedAssistant: 'GPT',
  assistants: [{ name: 'GPT' }],
  conversationStarters: [],
  onDraftChange: vi.fn(),
  onSendMessage: vi.fn(),
  onEditAssistant: vi.fn(),
  onSaveAssistant: vi.fn(),
  onAssistantSelect: vi.fn(),
  onUndo: vi.fn(),
  editError: undefined,
  isEditLoading: false,
};

function messagesForTurns(turnIndexes: number[]): MessageDto[] {
  return turnIndexes.flatMap((turnIndex, i) => [
    { id: `u${i}`, role: 'user' as const, content: `user ${turnIndex}`, created: '2026-01-01T00:00:00Z', isEdited: false, turnIndex },
    { id: `a${i}`, role: 'assistant' as const, content: `assistant ${turnIndex}`, created: '2026-01-01T00:00:01Z', isEdited: false, turnIndex },
  ]);
}

describe('CellList compaction boundary marker', () => {
  it('renders the marker immediately before the first turn after the boundary', () => {
    render(
      <CellList {...baseProps} messages={messagesForTurns([1, 2, 3])} compactionBoundaryTurnIndex={2} />,
      { wrapper: ToastProvider },
    );

    expect(screen.getByTestId('compaction-boundary-marker')).toBeInTheDocument();
  });

  it('renders no marker when the conversation has never been compacted', () => {
    render(
      <CellList {...baseProps} messages={messagesForTurns([1, 2, 3])} compactionBoundaryTurnIndex={null} />,
      { wrapper: ToastProvider },
    );

    expect(screen.queryByTestId('compaction-boundary-marker')).not.toBeInTheDocument();
  });

  it('moves to the new boundary after a second compaction', () => {
    const { rerender } = render(
      <CellList {...baseProps} messages={messagesForTurns([1, 2, 3, 4, 5])} compactionBoundaryTurnIndex={2} />,
      { wrapper: ToastProvider },
    );
    expect(screen.getAllByTestId('compaction-boundary-marker')).toHaveLength(1);

    rerender(
      <CellList {...baseProps} messages={messagesForTurns([1, 2, 3, 4, 5])} compactionBoundaryTurnIndex={4} />,
    );

    const markers = screen.getAllByTestId('compaction-boundary-marker');
    expect(markers).toHaveLength(1);
  });
});
```

- [ ] **Step 2: Run to verify it fails**

Run: `cd src/client && npx vitest run src/components/notebook/conversations/__tests__/CellList.compactionMarker.test.tsx`
Expected: FAIL — no element with `data-testid="compaction-boundary-marker"` exists; the third test's `getAllByTestId` throws immediately since zero elements match.

- [ ] **Step 3: Implement**

In `CellList.tsx`, add to `CellListProps` (after `'data-tour-id'?: string;` at line 79):

```ts
  /** Turn index at which the conversation was last compacted, or null/undefined if never. */
  compactionBoundaryTurnIndex?: number | null;
```

Add `compactionBoundaryTurnIndex` to the component's own destructured parameter list (`CellList.tsx:283-308`), which currently ends:

```ts
  canEdit = false,
  canUndo = false,
  'data-tour-id': dataTourId
}: CellListProps) {
```

Change to:

```ts
  canEdit = false,
  canUndo = false,
  compactionBoundaryTurnIndex = null,
  'data-tour-id': dataTourId
}: CellListProps) {
```

In the render loop (`groupedTurns.map((turn, idx) => {...})`, starting at line 895), add right after `const isLastTurn = ...` (line 903):

```tsx
              const turnIndexOfThisTurn = turn.allMessages[0]?.turnIndex;
              const showCompactionMarker = compactionBoundaryTurnIndex != null
                && turnIndexOfThisTurn === compactionBoundaryTurnIndex + 1;
```

And inside the returned `<React.Fragment key={`turn-${idx}`}>` (line 906), as the first child, before `{/* User message(s) for this turn */}` (line 907):

```tsx
                  {showCompactionMarker && (
                    <div
                      key={`compaction-marker-${idx}`}
                      className="col-span-3 flex items-center gap-2 my-2 text-xs text-gray-500"
                      data-testid="compaction-boundary-marker"
                    >
                      <div className="flex-1 border-t border-gray-200" />
                      <span>Conversation compacted — earlier messages summarized</span>
                      <div className="flex-1 border-t border-gray-200" />
                    </div>
                  )}
```

In `ConversationPanel.tsx`, add `contextStatus` to the existing `useConversation()` destructure (after `onPreviewFileByPath` at line 56):

```tsx
    contextStatus,
```

And add the prop to the `<CellList>` call (after `onPreviewFileByPath={onPreviewFileByPath}` at line 105):

```tsx
        compactionBoundaryTurnIndex={contextStatus?.boundaryTurnIndex ?? null}
```

- [ ] **Step 4: Run to verify it passes**

Run: `cd src/client && npx vitest run src/components/notebook/conversations/__tests__/CellList.compactionMarker.test.tsx`
Expected: PASS (3 tests).

Then run the full `CellList` test suite to confirm no regressions:

Run: `cd src/client && npx vitest run src/components/notebook/conversations/__tests__/CellList.test.tsx src/components/notebook/conversations/__tests__/CellList.scroll.integration.test.tsx src/components/notebook/conversations/__tests__/CellList.turnGrouping.integration.test.tsx src/components/notebook/conversations/__tests__/CellList.extended.integration.test.tsx src/components/notebook/conversations/__tests__/CellList.user-cache.integration.test.tsx src/components/notebook/conversations/__tests__/CellList.messages.integration.test.tsx`
Expected: PASS — `compactionBoundaryTurnIndex` is optional and every existing test omits it, so `showCompactionMarker` is always `false` for them.

- [ ] **Step 5: Commit (ask first)**

```bash
git add src/client/src/components/notebook/conversations/CellList.tsx src/client/src/components/notebook/conversations/ConversationPanel.tsx src/client/src/components/notebook/conversations/__tests__/CellList.compactionMarker.test.tsx
git commit -m "Renders a transcript divider at the compaction boundary"
```

---

## Task 7: Sharpen `chat_context_overflow` on the client (8.7)

**Files:**
- Modify: `src/client/src/contexts/conversation/useStreamingEventHandler.ts`
- Test: `src/client/src/contexts/__tests__/useStreamingEventHandler.test.tsx`

**Interfaces:**
- Consumes: nothing new.
- Produces: nothing consumed by other tasks — terminal.

The server's current message for this code (`StreamingErrorEnvelope.cs:102`) is `"The request was too large for the model's context window. Retry with a smaller message or a different approach."` — accurate but doesn't name the actual remedy this feature ships. Sharpening the *server* text is W6.4, which is sequenced after this plan (W6 is blocked on W8). This task adds a client-side override so users get the better message now, independent of W6's timing.

- [ ] **Step 1: Write the failing test**

Add to `contexts/__tests__/useStreamingEventHandler.test.tsx`, inside the existing `describe('useStreamingEventHandler error branch', ...)` block:

```ts
  it('names Compact as the remedy for chat_context_overflow', () => {
    const { handler, showToast } = mountHandler();

    handler({
      type: 'error',
      data: {
        code: 'chat_context_overflow',
        message: "The request was too large for the model's context window. Retry with a smaller message or a different approach.",
        type: 'ChatContextOverflowException',
        promptTokens: 9000,
        contextSize: 8192,
      },
    });

    expect(showToast).toHaveBeenCalledWith(expect.objectContaining({
      type: 'error',
      title: 'Context window full',
      message: expect.stringMatching(/compact/i),
    }));
  });
```

- [ ] **Step 2: Run to verify it fails**

Run: `cd src/client && npx vitest run src/contexts/__tests__/useStreamingEventHandler.test.tsx`
Expected: FAIL — `chat_context_overflow` currently falls to the final generic `else`, producing `title: 'Conversation Error'`, not `'Context window full'`.

- [ ] **Step 3: Implement**

In `useStreamingEventHandler.ts`'s `case 'error':` block, add a new branch before the final `else` (i.e. insert before `} else {` at line 409, after the `AttachmentNotReadyException` branch ending at line 408):

```ts
          } else if (errorCode === 'chat_context_overflow') {
            showToast({
              type: 'error',
              title: 'Context window full',
              message: 'This conversation is too large for the model to process. Press Compact in the composer to summarize older messages and free up space, then try again.',
              duration: 10000,
            });
```

- [ ] **Step 4: Run to verify it passes**

Run: `cd src/client && npx vitest run src/contexts/__tests__/useStreamingEventHandler.test.tsx`
Expected: PASS (all existing tests + the 4 added across Task 2 and this task).

- [ ] **Step 5: Commit (ask first)**

```bash
git add src/client/src/contexts/conversation/useStreamingEventHandler.ts src/client/src/contexts/__tests__/useStreamingEventHandler.test.tsx
git commit -m "Names Compact as the remedy for a full-context streaming error"
```

---

## Task 8: Full verification (8.8 rollup)

- [ ] **Step 1: Typecheck and full client test run with coverage**

Run: `cd src/client && npm run test:coverage`
Expected: passes typecheck, all tests pass, and the aggregate coverage gate (`lines: 85`, `functions: 83`, `branches: 80`, `statements: 85`) is met. If a new file drags a metric below its threshold, add the missing test case(s) — do not lower the thresholds in `vitest.config.ts`.

- [ ] **Step 2: Manual smoke check in the browser (if a running dev server is available)**

Run `npm run browser:dev` from `src/client` against a running API (see `docs/developer-config-guide.md`), open a notebook conversation, and confirm: (a) the meter shows "Context: unknown" before any turn has completed usage data, then a percentage after one completes; (b) the Compact button is visible and disabled while a message is streaming; (c) pressing Compact on an idle conversation shows a success toast with before/after numbers and the meter picks up the "Compacted" indicator. If no running stack is available in this environment, skip this step and say so explicitly rather than claiming it passed.

- [ ] **Step 3: Tick W8 in the checklist**

In `docs/context-compaction-plan.md`, tick 8.1–8.8 and add a line under W8 linking this plan, matching the "Implementation plan written" pattern used for W1–W5.

---

## Self-review

**Spec coverage.**
- 8.1 (meter in the composer footer, beside Send and the Ctrl+Enter hint) → Task 5, inserted into the exact footer row (`DraftUserCell.tsx:751-823`) the spec names.
- 8.2 (extract as their own components) → Tasks 3-4 (`ContextMeter`, `CompactButton`), consumed (not inlined) by Task 5.
- 8.3 (four meter states) → known/unknown/compacted covered by Task 3's `ContextMeter` tests; busy covered by Task 4's `disabled=true` test plus Task 5's `isStreaming` wiring test.
- 8.4 (compact button wired to the endpoint, before/after feedback) → Task 4.
- 8.5 (409 handling) → Task 4's `shows a busy message on 409` test.
- 8.6 (boundary marker in the transcript) → Task 6.
- 8.7 (clear, actionable `chat_context_overflow` message) → Task 7.
- 8.8 (tests: all four meter states, button, 409 path, marker; coverage gate) → distributed across Tasks 3-6, rolled up in Task 8.
- Spec's *Data flow* ("Next turn -> ... attach conversation_recall tool") — the recall-tool attachment itself is explicitly W7's job (`docs/context-compaction-plan.md`); this plan only wires the client-visible parts of that same diagram (the meter/marker), not the tool.
- Spec's *Client surface → Extract, don't inline* — satisfied by Tasks 3-4.

**Known limitations, deliberately accepted.**
- The button doesn't proactively know about a lock held by a *different* tab/user until it tries and gets a 409 (Findings: the raw `lock` DTO field is not plumbed through context). This is a UX trade-off, not a correctness gap — the 409 handler is the authoritative check either way, and D8 only requires the action be blocked, not predicted.
- `CompactButton`'s toast-based feedback doesn't persist in the composer after the toast times out (6s). If a persistent inline confirmation is wanted later, `PublishedGuideApiKeySection`'s inline-error-state pattern is the alternative precedent already researched.
- The transcript marker shows only the *current* boundary, not a history of every past compaction (Review Focus item 4, explicitly tested in Task 6). This matches D3 (the boundary is a single, monotonic value) and the checklist's literal wording ("Boundary marker," singular).
- `ContextMeter`'s percentage calculation uses simple division with no calibration awareness (`estimateSource` is read by `ContextMeter` only to decide nothing — it's not rendered). If a future workstream wants to show "~" for character-estimated tokens (as the server DTO's `estimateSource` field anticipates), that's a small, additive change to `ContextMeter`, not a redesign.

**Placeholder scan.** Every code step has complete, real code — no "TODO," "similar to Task N," or unshown logic. Every exact line number referenced (`DraftUserCell.tsx:751-823`, `CellList.tsx:895-958`, `ConversationContext.tsx:126-194,427-458`, `useStreamingEventHandler.ts:24-431`, `types.ts`/`reducer.ts` line numbers) was read directly from the current branch during this plan's research, not recalled from memory or another plan's text — including one case (`conversationId`'s actual location) where an initial research pass's claim was wrong and was caught and corrected by reading the file directly.

**Type consistency.** `ConversationCompactionResult` (Task 1) is used identically in `CompactButton`'s mock setup (Task 4) and its real usage (Task 5's wiring, indirectly via `CompactButton`). `ConversationContextStatus` (existing type) flows unchanged from `ConversationContext.tsx`'s `value` object (Task 2) through `ContextMeter`'s props (Task 3) and `ConversationPanel.tsx`'s `contextStatus?.boundaryTurnIndex` read (Task 6) — no shape is redefined or renamed along the way. `SET_CONTEXT_STATUS`'s payload shape matches between the reducer case (Task 2), the SSE handler's two dispatch sites (Task 2), and `refresh()`'s dispatch (Task 2).
