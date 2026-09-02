import { describe, it, expect, beforeEach, vi } from 'vitest';
import { render, act, waitFor } from '@testing-library/react';
import React from 'react';
import { MemoryRouter } from 'react-router';

// ---------------------------------------------------------------------------
// Provider-level turn-terminal integration harness.
//
// Mounts the REAL ConversationProvider (reducer + useConversationActions +
// useStreamingEventHandler) and drives scripted SSE turns through a mocked
// api.sendMessageStream that mirrors the real transport contract:
//   * onEvent is invoked for every SSE event, INCLUDING the terminal one, and
//   * onComplete(terminalEventType) is invoked exactly once when the body closes.
//
// That full provider -> sendMessage -> SSE loop -> handler -> action-owner chain
// is where the stale-closure freeze lived. It is structurally impossible for a
// render-captured state.* to reach the composer terminal policy here: the single
// owner reads only refs (sendStreamStateRef / streamTurn) and the snapshot. Each
// test below is one scripted terminal outcome from the proposal's matrix (§9.1).
// ---------------------------------------------------------------------------

let captured: {
  onEvent?: (event: { type: string; data: any }) => void;
  onError?: (error: Error) => void;
  onComplete?: (terminalEventType?: string) => void;
  requestServerCancel?: () => void;
} = {};

const mockApiGet = vi.fn();
const mockCancelTurn = vi.fn();
const mockUndoLast = vi.fn();
const mockLoadNotebookFiles = vi.fn().mockResolvedValue(undefined);
const mockShowToast = vi.fn();

vi.mock('../../../services/api', () => ({
  api: {
    projects: {
      notebooks: {
        conversations: {
          get: (...args: any[]) => mockApiGet(...args),
          sendMessageStream: vi.fn(
            async (_p: string, _n: string, _c: string, _payload: any, onEvent: any, onError: any, onComplete: any, _signal: any, streamControl?: any) => {
              captured = { onEvent, onError, onComplete, requestServerCancel: streamControl?.requestServerCancel };
              // The real transport settles when the body closes (after the terminal event).
              // Here we settle immediately so `await ctx.sendMessage(...)` completes; the test
              // then drives the captured onEvent/onComplete callbacks explicitly to mirror the
              // terminal delivery.
              return Promise.resolve();
            },
          ),
          cancelTurn: (...args: any[]) => mockCancelTurn(...args),
          undoLast: (...args: any[]) => mockUndoLast(...args),
          checkLlamaRuntime: vi.fn().mockResolvedValue({ state: 'ready' }),
          editMessage: vi.fn().mockResolvedValue({}),
          getAll: vi.fn().mockResolvedValue([]),
        },
        getNotebook: vi.fn().mockResolvedValue({}),
      },
      notebookTemplates: {
        getAll: vi.fn().mockResolvedValue([]),
        getAssistants: vi.fn().mockResolvedValue([]),
      },
      assistants: {
        getConversationStarters: vi.fn().mockResolvedValue([]),
      },
      folders: {
        getFolderTree: vi.fn().mockResolvedValue({}),
      },
    },
  },
}));

vi.mock('../../../utils/notebookAuth', () => ({
  ensureValidTokensForTemplate: vi.fn().mockResolvedValue({ needsAuth: false, missingProviders: [] }),
}));

vi.mock('../../runtimeChecks', () => ({
  checkRuntimeStatus: vi.fn().mockResolvedValue({ state: 'ready' }),
  getRuntimeBlockingMessage: vi.fn().mockReturnValue('Runtime not ready'),
  dispatchRuntimeStatusWindowEvent: vi.fn(),
  getNotebookRuntimeReadyCache: vi.fn(() => new Set<string>()),
  clearNotebookRuntimeReadyCache: vi.fn(),
}));

vi.mock('../../NotebookContext', () => ({
  useNotebook: () => ({ loadNotebookFiles: mockLoadNotebookFiles }),
  NotebookProvider: ({ children }: { children: React.ReactNode }) => children,
}));

vi.mock('../../../components/common/Toast', () => ({
  useToast: () => ({ showToast: mockShowToast }),
  ToastProvider: ({ children }: { children: React.ReactNode }) => children,
}));

vi.mock('../../../services/userService', () => ({
  userService: {
    getCurrentUser: vi.fn().mockResolvedValue({ id: 'user-1', name: 'Test User', email: 't@example.com' }),
    getUserById: vi.fn().mockResolvedValue({ id: 'user-1', name: 'Test User', email: 't@example.com' }),
  },
}));

vi.mock('../../../services/authService', () => ({
  authService: { getAccessToken: vi.fn().mockResolvedValue('mock-token') },
}));

import { ConversationProvider, useConversation } from '../../ConversationContext';
import type { ComposerTerminalPolicy } from '../../conversation/types';

const PROJECT_ID = 'test-project';
const NOTEBOOK_ID = 'test-notebook';
const CONVERSATION_ID = 'test-conversation';
const ASSISTANTS = [{ name: 'Demo Guide', model: 'gpt-4', avatarUrl: '/a.png', id: 'assistant-1' }];

function StateProbe({ onState }: { onState: (s: any) => void }) {
  const ctx: any = useConversation();
  React.useEffect(() => { onState(ctx); });
  return null;
}

let lastState: any = null;

function mountProvider(policy?: ComposerTerminalPolicy) {
  const wrapper = ({ children }: { children: React.ReactNode }) => (
    <MemoryRouter>
      <ConversationProvider
        projectId={PROJECT_ID}
        notebookId={NOTEBOOK_ID}
        conversationId={CONVERSATION_ID}
        guideId="guide-1"
        assistants={ASSISTANTS}
        composerTerminalPolicy={policy}
      >
        <StateProbe onState={(s: any) => { lastState = s; }} />
        {children}
      </ConversationProvider>
    </MemoryRouter>
  );
  render(<>{/* probe is inside the provider */}</>, { wrapper });
  return wrapper;
}

interface SendOpts {
  message: string;
  attachments?: any[];
  turnId?: string | null; // null => do NOT emit turn_created (no persisted turn)
}

interface TerminalOpts {
  type: 'complete' | 'cancelled' | 'error' | 'pending_client_tool';
  extraEvents?: Array<{ type: string; data: any }>;
  beforeTerminal?: () => void; // e.g. ctx.cancelStream()
}

// Drive a full send through the mocked transport, then deliver a scripted terminal
// outcome exactly the way the real api.ts does (terminal onEvent, then one onComplete).
async function driveTurn(ctx: any, opts: SendOpts, terminal: TerminalOpts) {
  // Faithful to the real composer: the draft text the user typed is the same string that is
  // sent, so the send snapshot (which preserves state.draftUserContent) captures it. Set the
  // context draft first, then use the freshly-created sendMessage (its closure reads the
  // updated draft).
  await act(async () => {
    lastState.setDraftUserContent(opts.message);
  });
  await waitFor(() => expect(lastState.draftUserContent).toBe(opts.message));

  await act(async () => {
    await lastState.sendMessage(opts.message, opts.attachments ?? []);
  });

  await act(async () => {
    if (opts.turnId) {
      captured.onEvent?.({ type: 'turn_created', data: { turnId: opts.turnId } });
    }
    for (const e of terminal.extraEvents ?? []) {
      captured.onEvent?.(e);
    }
    terminal.beforeTerminal?.();
    captured.onEvent?.({
      type: terminal.type,
      data: opts.turnId ? { turnId: opts.turnId } : {},
    });
    captured.onComplete?.(terminal.type);
  });
}

describe('turn-terminal composer ownership (provider-level)', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    captured = {};
    lastState = null;
    mockApiGet.mockResolvedValue({ messages: [], assistantName: 'Demo Guide' });
    mockCancelTurn.mockResolvedValue(undefined);
    mockUndoLast.mockResolvedValue(undefined);
  });

  async function initialized(): Promise<any> {
    mountProvider();
    await waitFor(() => expect(lastState?.isInitialized).toBe(true));
    return lastState;
  }

  it('1. success: composer is empty after complete and the cell is finalized', async () => {
    const ctx = await initialized();

    const chip = { notebookFileId: 'path:Data/photo.png', relativePath: 'Data/photo.png', fileName: 'photo.png', uploadType: 'image' as const };
    await driveTurn(ctx, { message: 'with a photo', attachments: [chip], turnId: 'turn-1' }, {
      type: 'complete',
      extraEvents: [{ type: 'token', data: { contentDelta: 'Working on it…' } }],
    });

    // D1 (the reported bug): the previous turn's chips must not linger after a
    // successful turn. This test would fail against the pre-fix handler because its
    // `state.streamingMode === 'sending'` gate was frozen false for the whole turn.
    await waitFor(() => {
      expect(lastState.pendingAttachments).toEqual([]);
    });
    expect(lastState.draftUserContent).toBe('');
    // Composer back at rest; the streaming cell was finalized (no streaming-* id left).
    expect(lastState.streamingMode).toBe('at-rest');
    expect(lastState.isStreaming).toBe(false);
    expect(lastState.messages.filter((m: any) => String(m.id).startsWith('streaming-'))).toEqual([]);
    // The file tree refreshed EXACTLY ONCE on the local success path: the send-side owner
    // does it (the handler's complete case now refreshes only for observer completions, so
    // there is no double fetch + broadcast).
    expect(mockLoadNotebookFiles).toHaveBeenCalledTimes(1);
  });

  it('2. pending_client_tool: default policy keeps the composer empty and re-enables at rest', async () => {
    const ctx = await initialized();

    const chip = { notebookFileId: 'file-1', fileName: 'notes.md', uploadType: 'text' as const };
    await driveTurn(ctx, { message: 'follow-up text', attachments: [chip], turnId: 'turn-2' }, {
      type: 'pending_client_tool',
    });

    // A pending_client_tool terminal carries a turnId: the input was persisted (it is in
    // the transcript) and the turn is resumable, so this is an end-of-turn. Rule 1: the
    // draft stays EMPTY -- undo is the only restore path -- and the chips are cleared
    // with the send. The composer still unlocks at-rest.
    await waitFor(() => {
      expect(lastState.draftUserContent).toBe('');
    });
    expect(lastState.pendingAttachments).toEqual([]);
    expect(lastState.streamingMode).toBe('at-rest');
    expect(lastState.isStreaming).toBe(false);
    expect(lastState._isCancelling).toBeFalsy();
    // File tree still refreshes on a paused (client-tool) turn — tools may register files.
    expect(mockLoadNotebookFiles).toHaveBeenCalled();
  });

  it('2b. pending_client_tool: the policy seam swaps composer behavior without touching the owner', async () => {
    // The P4 guarantee: a registered client-tool policy receives the outcome from the SAME
    // single owner and decides the composer's behavior. Under the default policy (test 2)
    // the owner leaves the composer empty; under this stub policy the owner must delegate
    // and must not apply its own draft/attachment decisions.
    const policy: ComposerTerminalPolicy = { apply: vi.fn() };
    const wrapper = ({ children }: { children: React.ReactNode }) => (
      <MemoryRouter>
        <ConversationProvider
          projectId={PROJECT_ID}
          notebookId={NOTEBOOK_ID}
          conversationId={CONVERSATION_ID}
          guideId="guide-1"
          assistants={ASSISTANTS}
          composerTerminalPolicy={policy}
        >
          <StateProbe onState={(s: any) => { lastState = s; }} />
          {children}
        </ConversationProvider>
      </MemoryRouter>
    );
    render(<></>, { wrapper });
    await waitFor(() => expect(lastState?.isInitialized).toBe(true));

    const chip = { notebookFileId: 'file-9', fileName: 'x.md', uploadType: 'text' as const };
    await driveTurn(lastState, { message: 'tool turn', attachments: [chip], turnId: 'turn-seam' }, {
      type: 'pending_client_tool',
    });

    // The owner delegated the outcome to the registered policy…
    expect(policy.apply).toHaveBeenCalledTimes(1);
    expect(policy.apply).toHaveBeenCalledWith({
      kind: 'pending_client_tool',
      turnId: 'turn-seam',
    });
    // …and did NOT run the default restore (the stub policy owns the composer now).
    expect(lastState.draftUserContent).toBe('');
    expect(lastState.pendingAttachments).toEqual([]);
    // The turn still finalized conversation state regardless of policy.
    expect(lastState.streamingMode).toBe('at-rest');
    expect(lastState.isStreaming).toBe(false);
  });

  it('3. cancelled (server-confirmed local Stop): composer cleared, no double finalize', async () => {
    const ctx = await initialized();

    const chip = { notebookFileId: 'file-3', fileName: 'c.md', uploadType: 'text' as const };
    await driveTurn(ctx, { message: 'long answer', attachments: [chip], turnId: 'turn-3' }, {
      type: 'cancelled',
      beforeTerminal: () => { ctx.cancelStream(); },
    });

    // The Stop endpoint is the authority; the confirmed cancellation cleared the composer.
    expect(mockCancelTurn).toHaveBeenCalledWith(PROJECT_ID, NOTEBOOK_ID, CONVERSATION_ID, 'turn-3');
    await waitFor(() => {
      expect(lastState.isStreaming).toBe(false);
      expect(lastState.isCancelling).toBeFalsy();
    });
    // Persisted (turnId known) → the turnId oracle clears the attachments.
    expect(lastState.pendingAttachments).toEqual([]);
    expect(lastState.streamingMode).toBe('at-rest');
    // A cancelled turn still refreshes the file tree (tools may have registered files).
    expect(mockLoadNotebookFiles).toHaveBeenCalled();
  });

  it('4. cancelled with no turn id: restore the composer, nothing to stop server-side', async () => {
    const ctx = await initialized();

    const chip = { notebookFileId: 'file-4', fileName: 'd.md', uploadType: 'text' as const };
    await driveTurn(ctx, { message: 'lost send', attachments: [chip], turnId: null }, {
      type: 'cancelled',
    });

    // No turnId → the message was never persisted (pre-turn_created failure) → the owner
    // restores the draft + chips from the snapshot (D3).
    await waitFor(() => {
      expect(lastState.draftUserContent).toBe('lost send');
    });
    expect(lastState.pendingAttachments).toEqual([chip]);
    // No turn id → there is nothing to stop; the owner finalizes locally.
    expect(mockCancelTurn).not.toHaveBeenCalled();
    expect(lastState.streamingMode).toBe('at-rest');
    expect(mockLoadNotebookFiles).toHaveBeenCalled();
  });

  it('5. error with no turn id: restore the composer and surface the error', async () => {
    const ctx = await initialized();

    const chip = { notebookFileId: 'file-5', fileName: 'e.md', uploadType: 'text' as const };
    await driveTurn(ctx, { message: 'errored send', attachments: [chip], turnId: null }, {
      type: 'error',
    });

    await waitFor(() => {
      expect(lastState.draftUserContent).toBe('errored send');
    });
    expect(lastState.pendingAttachments).toEqual([chip]);
    expect(mockCancelTurn).not.toHaveBeenCalled();
    expect(lastState.streamingMode).toBe('at-rest');
    // An errored turn still refreshes the file tree (tools may have registered files).
    expect(mockLoadNotebookFiles).toHaveBeenCalled();
  });

  it('6. an other-client complete broadcast does not touch the local turn or composer', async () => {
    const ctx = await initialized();

    const chip = { notebookFileId: 'file-6', fileName: 'f.md', uploadType: 'text' as const };
    await act(async () => {
      await ctx.sendMessage('local turn', [chip]);
    });
    await act(async () => {
      captured.onEvent?.({ type: 'turn_created', data: { turnId: 'turn-local' } });
    });

    // A second client completes a DIFFERENT turn: turn-scoped filtering drops it, and the
    // per-turn transport delivers no terminal for OUR turn — so the local composer is
    // untouched (isForeignTurnEvent + local-turn ownership).
    await act(async () => {
      captured.onEvent?.({ type: 'complete', data: { turnId: 'turn-foreign' } });
    });

    expect(lastState.isStreaming).toBe(true);
    expect(lastState.streamingMode).toBe('sending');

    // Now the LOCAL turn completes: the composer finalizes exactly once.
    await act(async () => {
      captured.onEvent?.({ type: 'complete', data: { turnId: 'turn-local' } });
      captured.onComplete?.('complete');
    });
    await waitFor(() => {
      expect(lastState.streamingMode).toBe('at-rest');
    });
    expect(lastState.pendingAttachments).toEqual([]);
  });

  it('7. idle timeout mid-turn routes through the confirmed Stop boundary', async () => {
    const ctx = await initialized();

    await act(async () => {
      await lastState.sendMessage('will stall', []);
    });
    await act(async () => {
      captured.onEvent?.({ type: 'turn_created', data: { turnId: 'turn-idle' } });
    });

    // Idle timeout is a transport failure, not a terminal event: the same confirmed Stop
    // boundary as the button (requestServerStop) finalizes the workflow — unchanged behavior.
    // The confirmed cancelTurn resolves quickly, so real timers suffice (no fake timers needed,
    // which would otherwise freeze the provider's init effects).
    await act(async () => {
      captured.onError?.(Object.assign(
        new Error('The conversation stream stopped sending data. The server is no longer answering this request.'),
        { name: 'StreamIdleTimeoutError' },
      ));
    });

    expect(mockCancelTurn).toHaveBeenCalledWith(PROJECT_ID, NOTEBOOK_ID, CONVERSATION_ID, 'turn-idle');
    await waitFor(() => {
      expect(lastState.isStreaming).toBe(false);
    });
    expect(lastState.isCancelling).toBeFalsy();
  });

  it('8. undo after success restores the draft + chips from the persisted user message', async () => {
    // The server's conversation already holds the (completed) turn's user message; the client
    // is at rest. Undo reads the LAST user message from state and restores it into the
    // composer — locking the undo contract the user stated.
    mockApiGet.mockResolvedValue({
      messages: [{
        id: 'm-user',
        role: 'user',
        content: 'undo me',
        created: new Date().toISOString(),
        isEdited: false,
        attachments: [{
          relativePath: 'Data/photo.png',
          fileName: 'photo.png',
          uploadType: 'ImageFile',
          fileType: 'image',
        }],
      }, {
        id: 'm-asst',
        role: 'assistant',
        content: 'done',
        created: new Date().toISOString(),
        isEdited: false,
        assistantName: 'Demo Guide',
      }],
      assistantName: 'Demo Guide',
    });
    mountProvider();
    await waitFor(() => expect(lastState?.isInitialized).toBe(true));
    // Ensure the provider's initial refresh has loaded the persisted messages.
    await waitFor(() => expect(lastState.messages.length).toBeGreaterThanOrEqual(2));

    // Undo (using the freshest hook instance each tick to avoid a stale closure) restores the
    // draft text and the attachment chip from the persisted user message.
    await act(async () => {
      lastState.undoLastTurn();
    });
    await waitFor(() => {
      expect(lastState.draftUserContent).toBe('undo me');
    });
    expect(lastState.pendingAttachments.length).toBe(1);
    expect(lastState.pendingAttachments[0].fileName).toBe('photo.png');
    expect(mockUndoLast).toHaveBeenCalled();
    // The turn's messages were removed from the transcript.
    expect(lastState.messages.some((m: any) => m.id === 'm-user')).toBe(false);
  });
});
