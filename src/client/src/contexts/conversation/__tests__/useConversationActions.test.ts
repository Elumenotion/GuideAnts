import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import type { ExtendedConversationState } from '../types';
import { createMockMessage, createMockAssistantMessage } from '../../../test/conversationContextTestUtils';

vi.mock('../../../utils/notebookAuth', () => ({
  ensureValidTokensForTemplate: vi.fn().mockResolvedValue({ needsAuth: false, missingProviders: [] }),
}));

vi.mock('../runtimeChecks', () => ({
  checkRuntimeStatus: vi.fn().mockResolvedValue({ state: 'ready' }),
  getRuntimeBlockingMessage: vi.fn().mockReturnValue('Runtime not ready'),
  dispatchRuntimeStatusWindowEvent: vi.fn(),
}));

vi.mock('../../../services/userService', () => ({
  userService: {
    getCurrentUser: vi.fn().mockResolvedValue({ id: 'user-1', name: 'Test User' }),
  },
}));

import { useConversationActions } from '../useConversationActions';
import { api } from '../../../services/api';
import { ensureValidTokensForTemplate } from '../../../utils/notebookAuth';
import {
  checkRuntimeStatus,
  getRuntimeBlockingMessage,
  dispatchRuntimeStatusWindowEvent,
} from '../runtimeChecks';
import { userService } from '../../../services/userService';

const PROJECT_ID = 'proj-1';
const NOTEBOOK_ID = 'nb-1';
const CONVERSATION_ID = 'convo-1';

function createBaseState(overrides: Partial<ExtendedConversationState> = {}): ExtendedConversationState {
  return {
    messages: [],
    isStreaming: false,
    selectedAssistant: 'Claude',
    draftUserContent: '',
    assistants: [{ name: 'Claude', id: 'asst-1' }],
    pendingAttachments: [],
    userProfiles: {},
    ...overrides,
  };
}

function createDeps(overrides: Partial<Parameters<typeof useConversationActions>[2]> = {}) {
  const inflightRuntimeChecksRef = { current: new Set<string>() };
  const runtimeReadyCacheRef = { current: new Set<string>() };
  const setCurrentStreamController = vi.fn();

  return {
    projectId: PROJECT_ID,
    notebookId: NOTEBOOK_ID,
    conversationId: CONVERSATION_ID,
    handleStreamingEvent: vi.fn(),
    showToast: vi.fn(),
    loadNotebookFiles: vi.fn().mockResolvedValue(undefined),
    currentStreamController: null as AbortController | null,
    setCurrentStreamController,
    observerStreamController: null as AbortController | null,
    setObserverStreamController: vi.fn(),
    inflightRuntimeChecksRef,
    runtimeReadyCacheRef,
    activeStreamTurnId: null as string | null,
    setActiveStreamTurnId: vi.fn(),
    getActiveStreamTurnId: vi.fn(() => null),
    refreshConversation: vi.fn().mockResolvedValue(undefined),
    assistantByName: {
      Claude: { name: 'Claude', avatarUrl: '', id: 'asst-1' },
    },
    ...overrides,
  };
}

function mountActions(
  stateOverrides: Partial<ExtendedConversationState> = {},
  depsOverrides: Partial<ReturnType<typeof createDeps>> = {},
) {
  const dispatch = vi.fn();
  const state = createBaseState(stateOverrides);
  const deps = createDeps(depsOverrides);

  const { result, rerender } = renderHook(
    ({ s, d }) => useConversationActions(dispatch, s, d),
    { initialProps: { s: state, d: deps } },
  );

  return { actions: result.current, dispatch, state, deps, rerender };
}

describe('useConversationActions', () => {
  let dispatchEventSpy: ReturnType<typeof vi.spyOn>;

  beforeEach(() => {
    vi.clearAllMocks();
    dispatchEventSpy = vi.spyOn(window, 'dispatchEvent').mockImplementation(() => true);
    vi.mocked(ensureValidTokensForTemplate).mockResolvedValue({ needsAuth: false, missingProviders: [] });
    vi.mocked(checkRuntimeStatus).mockResolvedValue({ state: 'ready' });
    const conversations = api.projects.notebooks.conversations as Record<string, unknown>;
    conversations.sendMessageStream = vi.fn().mockResolvedValue(undefined);
    conversations.observeConversationEvents = vi.fn().mockResolvedValue(undefined);
    conversations.cancelTurn = vi.fn().mockResolvedValue(undefined);
    conversations.editMessage = vi.fn().mockResolvedValue({});
    conversations.undoLast = vi.fn().mockResolvedValue({});
    conversations.get = vi.fn().mockResolvedValue({ messages: [] });
  });

  afterEach(() => {
    dispatchEventSpy.mockRestore();
  });

  describe('sendMessage', () => {
    it('returns early when _isUndoing is true', async () => {
      const { actions } = mountActions({ _isUndoing: true });

      await act(async () => {
        await actions.sendMessage('hello');
      });

      expect(api.projects.notebooks.conversations.sendMessageStream).not.toHaveBeenCalled();
    });

    it('shows auth toast and returns when tokens are missing', async () => {
      vi.mocked(ensureValidTokensForTemplate).mockResolvedValue({
        needsAuth: true,
        missingProviders: [{ id: 'openai' }],
      } as any);

      const { actions, deps } = mountActions();

      await act(async () => {
        await actions.sendMessage('hello');
      });

      expect(deps.showToast).toHaveBeenCalledWith(expect.objectContaining({
        type: 'error',
        title: 'Authentication Required',
        message: expect.stringContaining('openai'),
      }));
      expect(api.projects.notebooks.conversations.sendMessageStream).not.toHaveBeenCalled();
    });

    it('dispatches streaming setup and calls sendMessageStream on success', async () => {
      const { actions, dispatch, deps } = mountActions();

      await act(async () => {
        await actions.sendMessage('hello world');
      });

      expect(dispatch).toHaveBeenCalledWith({ type: 'SET_STREAMING_MODE', payload: { mode: 'sending' } });
      expect(dispatch).toHaveBeenCalledWith({ type: 'START_STREAMING_TURN' });
      expect(dispatch).toHaveBeenCalledWith(expect.objectContaining({ type: 'ADD_MESSAGE' }));
      expect(api.projects.notebooks.conversations.sendMessageStream).toHaveBeenCalledWith(
        PROJECT_ID,
        NOTEBOOK_ID,
        CONVERSATION_ID,
        expect.objectContaining({ instructions: 'hello world', assistantName: 'Claude' }),
        deps.handleStreamingEvent,
        expect.any(Function),
        expect.any(Function),
        expect.any(AbortSignal),
        expect.objectContaining({ requestServerCancel: expect.any(Function) }),
      );
      expect(deps.setCurrentStreamController).toHaveBeenCalled();
    });

    it('returns when runtime check is deduplicated (null)', async () => {
      vi.mocked(checkRuntimeStatus).mockResolvedValue(null);
      const { actions } = mountActions();

      await act(async () => {
        await actions.sendMessage('hello');
      });

      expect(api.projects.notebooks.conversations.sendMessageStream).not.toHaveBeenCalled();
    });

    it('shows toast when runtime is failed', async () => {
      vi.mocked(checkRuntimeStatus).mockResolvedValue({ state: 'failed' });
      const { actions, deps } = mountActions();

      await act(async () => {
        await actions.sendMessage('hello');
      });

      expect(deps.showToast).toHaveBeenCalledWith(expect.objectContaining({
        title: 'Local Runtime Error',
      }));
      expect(api.projects.notebooks.conversations.sendMessageStream).not.toHaveBeenCalled();
    });

    it('shows toast when runtime is invalid', async () => {
      vi.mocked(checkRuntimeStatus).mockResolvedValue({ state: 'invalid' });
      const { actions, deps } = mountActions();

      await act(async () => {
        await actions.sendMessage('hello');
      });

      expect(deps.showToast).toHaveBeenCalledWith(expect.objectContaining({
        title: 'Incompatible Local Models',
      }));
      expect(getRuntimeBlockingMessage).toHaveBeenCalled();
    });

    it('shows toast when runtime check throws', async () => {
      vi.mocked(checkRuntimeStatus).mockRejectedValue(new Error('check failed'));
      const { actions, deps } = mountActions();

      await act(async () => {
        await actions.sendMessage('hello');
      });

      expect(deps.showToast).toHaveBeenCalledWith(expect.objectContaining({
        title: 'Runtime Error',
        message: expect.stringContaining('check failed'),
      }));
    });

    it('handles 409 ROUTING_MODEL_NOT_READY', async () => {
      const error = Object.assign(new Error('conflict'), {
        status: 409,
        body: { code: 'ROUTING_MODEL_NOT_READY', detail: 'No model', action: 'Open Settings' },
      });
      vi.mocked(api.projects.notebooks.conversations.sendMessageStream).mockRejectedValue(error);

      const { actions, dispatch, deps } = mountActions();

      await act(async () => {
        await actions.sendMessage('hello');
      });

      expect(dispatch).toHaveBeenCalledWith({
        type: 'SET_STREAMING_ERROR',
        payload: 'No model',
      });
      expect(deps.showToast).toHaveBeenCalledWith(expect.objectContaining({
        title: 'No Chat Model',
        message: 'Open Settings',
      }));
    });

    it('handles 409 OAUTH_RECONNECT_REQUIRED', async () => {
      const error = Object.assign(new Error('conflict'), {
        status: 409,
        body: { code: 'OAUTH_RECONNECT_REQUIRED', providers: ['google', 'github'] },
      });
      vi.mocked(api.projects.notebooks.conversations.sendMessageStream).mockRejectedValue(error);

      const { actions, dispatch, deps } = mountActions();

      await act(async () => {
        await actions.sendMessage('hello');
      });

      expect(dispatch).toHaveBeenCalledWith({
        type: 'SET_STREAMING_ERROR',
        payload: expect.stringContaining('google, github'),
      });
      expect(deps.showToast).toHaveBeenCalledWith(expect.objectContaining({
        title: 'Reconnect Required',
      }));
    });

    it('handles 409 with runtime status not ready', async () => {
      const error = Object.assign(new Error('conflict'), {
        status: 409,
        body: { runtimeStatus: { state: 'failed' } },
      });
      vi.mocked(api.projects.notebooks.conversations.sendMessageStream).mockRejectedValue(error);

      const { actions, deps } = mountActions();

      await act(async () => {
        await actions.sendMessage('hello');
      });

      expect(dispatchRuntimeStatusWindowEvent).toHaveBeenCalledWith('asst-1', { state: 'failed' });
      expect(deps.showToast).toHaveBeenCalledWith(expect.objectContaining({
        title: 'Local Runtime Error',
      }));
    });

    it('handles 400 model-not-loaded error', async () => {
      const error = Object.assign(new Error('bad request'), {
        status: 400,
        body: { message: 'the server does not have a model loaded' },
      });
      vi.mocked(api.projects.notebooks.conversations.sendMessageStream).mockRejectedValue(error);

      const { actions } = mountActions();

      await act(async () => {
        await actions.sendMessage('hello');
      });

      expect(dispatchRuntimeStatusWindowEvent).toHaveBeenCalledWith('asst-1', { state: 'requires_load' });
    });

    it('handles generic send failure', async () => {
      vi.mocked(api.projects.notebooks.conversations.sendMessageStream).mockRejectedValue(
        new Error('network down'),
      );

      const { actions, dispatch, deps } = mountActions();

      await act(async () => {
        await actions.sendMessage('hello');
      });

      expect(dispatch).toHaveBeenCalledWith({
        type: 'SET_STREAMING_ERROR',
        payload: 'network down',
      });
      expect(deps.showToast).toHaveBeenCalledWith(expect.objectContaining({
        title: 'Chat Request Failed',
      }));
    });

    it('handles stream onError AbortError without completing the turn or force refresh', async () => {
      let onError: ((err: Error) => void) | undefined;
      vi.mocked(api.projects.notebooks.conversations.sendMessageStream).mockImplementation(
        async (_p, _n, _c, _payload, _onEvent, errCb) => {
          onError = errCb;
        },
      );

      const { actions, dispatch, deps } = mountActions();

      await act(async () => {
        await actions.sendMessage('hello');
      });

      act(() => {
        onError?.(Object.assign(new Error('aborted'), { name: 'AbortError' }));
      });

      expect(dispatch).not.toHaveBeenCalledWith({ type: 'COMPLETE_STREAMING_TURN' });
      expect(dispatch).not.toHaveBeenCalledWith({ type: 'FINALIZE_STREAMING_MESSAGE', payload: {} });
      expect(deps.refreshConversation).not.toHaveBeenCalled();
      expect(deps.showToast).not.toHaveBeenCalled();
      const errorDispatches = dispatch.mock.calls.filter(
        ([action]) => action.type === 'SET_STREAMING_ERROR' && typeof action.payload === 'string' && action.payload.length > 0,
      );
      expect(errorDispatches).toHaveLength(0);
    });

    it('handles stream onError StreamIdleTimeoutError without force refresh but with error UI', async () => {
      let onError: ((err: Error) => void) | undefined;
      vi.mocked(api.projects.notebooks.conversations.sendMessageStream).mockImplementation(
        async (_p, _n, _c, _payload, _onEvent, errCb) => {
          onError = errCb;
        },
      );

      const { actions, dispatch, deps } = mountActions();

      await act(async () => {
        await actions.sendMessage('hello');
      });

      act(() => {
        onError?.(Object.assign(
          new Error('The conversation stream stopped sending data. The server is no longer answering this request.'),
          { name: 'StreamIdleTimeoutError' },
        ));
      });

      expect(dispatch).toHaveBeenCalledWith({ type: 'FINALIZE_STREAMING_MESSAGE', payload: {} });
      expect(dispatch).toHaveBeenCalledWith({ type: 'COMPLETE_STREAMING_TURN' });
      expect(dispatch).toHaveBeenCalledWith({
        type: 'SET_STREAMING_ERROR',
        payload: 'The conversation stream stopped sending data. The server is no longer answering this request.',
      });
      expect(deps.refreshConversation).not.toHaveBeenCalled();
      expect(deps.showToast).toHaveBeenCalledWith(expect.objectContaining({
        title: 'Chat Request Failed',
      }));
    });

    it('handles stream onError generic failure', async () => {
      let onError: ((err: Error) => void) | undefined;
      vi.mocked(api.projects.notebooks.conversations.sendMessageStream).mockImplementation(
        async (_p, _n, _c, _payload, _onEvent, errCb) => {
          onError = errCb;
        },
      );

      const { actions, dispatch, deps } = mountActions();

      await act(async () => {
        await actions.sendMessage('hello');
      });

      act(() => {
        onError?.(new Error('stream broke'));
      });

      expect(dispatch).toHaveBeenCalledWith({
        type: 'SET_STREAMING_ERROR',
        payload: 'stream broke',
      });
      expect(deps.showToast).toHaveBeenCalledWith(expect.objectContaining({
        title: 'Chat Request Failed',
      }));
      expect(deps.refreshConversation).toHaveBeenCalledWith({ force: true });
    });

    it('invokes onComplete callback to clear attachments and refresh files', async () => {
      let onComplete: (() => void) | undefined;
      vi.mocked(api.projects.notebooks.conversations.sendMessageStream).mockImplementation(
        async (_p, _n, _c, _payload, _onEvent, _onError, completeCb) => {
          onComplete = completeCb;
        },
      );

      const { actions, dispatch, deps } = mountActions();

      await act(async () => {
        await actions.sendMessage('hello');
      });

      await act(async () => {
        onComplete?.();
      });

      expect(dispatch).toHaveBeenCalledWith({ type: 'COMPLETE_STREAMING_TURN' });
      expect(dispatch).toHaveBeenCalledWith({ type: 'CLEAR_ATTACHMENTS' });
      expect(deps.loadNotebookFiles).toHaveBeenCalled();
    });
  });

  describe('editAssistantMessage', () => {
    it('sets edit error when message is not found', async () => {
      const { actions, dispatch } = mountActions({ messages: [] });

      await act(async () => {
        await actions.editAssistantMessage('missing-id', 'new text');
      });

      expect(dispatch).toHaveBeenCalledWith({ type: 'SET_EDIT_ERROR', payload: 'Message not found' });
      expect(dispatch).toHaveBeenCalledWith({ type: 'SET_EDIT_LOADING', payload: false });
    });

    it('optimistically updates and persists edit', async () => {
      const original = createMockAssistantMessage({ id: 'msg-1', content: 'old' });
      const { actions, dispatch } = mountActions({ messages: [original] });

      await act(async () => {
        await actions.editAssistantMessage('msg-1', 'new content');
      });

      expect(dispatch).toHaveBeenCalledWith(expect.objectContaining({
        type: 'UPDATE_MESSAGE',
        payload: expect.objectContaining({
          id: 'msg-1',
          updates: expect.objectContaining({ content: 'new content', isEdited: true }),
        }),
      }));
      expect(api.projects.notebooks.conversations.editMessage).toHaveBeenCalledWith(
        PROJECT_ID, NOTEBOOK_ID, CONVERSATION_ID, 'msg-1', 'new content',
      );
      expect(userService.getCurrentUser).toHaveBeenCalled();
    });

    it('rolls back on edit failure', async () => {
      const original = createMockAssistantMessage({ id: 'msg-1', content: 'old' });
      vi.mocked(api.projects.notebooks.conversations.editMessage).mockRejectedValue(new Error('save failed'));
      const { actions, dispatch } = mountActions({ messages: [original] });

      await act(async () => {
        await actions.editAssistantMessage('msg-1', 'new content');
      });

      expect(dispatch).toHaveBeenCalledWith({
        type: 'UPDATE_MESSAGE',
        payload: { id: 'msg-1', updates: original },
      });
      expect(dispatch).toHaveBeenCalledWith({ type: 'SET_EDITING', payload: 'msg-1' });
      expect(dispatch).toHaveBeenCalledWith({
        type: 'SET_EDIT_ERROR',
        payload: 'Failed to save message. Please try again.',
      });
    });
  });

  describe('editing helpers', () => {
    it('startEditingAssistant sets editing id', () => {
      const { actions, dispatch } = mountActions();

      act(() => {
        actions.startEditingAssistant('msg-42');
      });

      expect(dispatch).toHaveBeenCalledWith({ type: 'SET_EDITING', payload: 'msg-42' });
      expect(dispatch).toHaveBeenCalledWith({ type: 'SET_EDIT_ERROR', payload: undefined });
    });

    it('cancelEditingAssistant clears editing state', () => {
      const { actions, dispatch } = mountActions();

      act(() => {
        actions.cancelEditingAssistant();
      });

      expect(dispatch).toHaveBeenCalledWith({ type: 'SET_EDITING', payload: undefined });
      expect(dispatch).toHaveBeenCalledWith({ type: 'SET_EDIT_LOADING', payload: false });
    });
  });

  describe('cancelStream', () => {
    it('calls cancelTurn and does not abort the send SSE controller', () => {
      const controller = new AbortController();
      const abortSpy = vi.spyOn(controller, 'abort');
      const { actions, dispatch } = mountActions(
        { isStreaming: true },
        {
          currentStreamController: controller,
          activeStreamTurnId: 'turn-1',
          getActiveStreamTurnId: vi.fn(() => 'turn-1'),
        },
      );

      act(() => {
        actions.cancelStream();
      });

      expect(abortSpy).not.toHaveBeenCalled();
      expect(api.projects.notebooks.conversations.cancelTurn).toHaveBeenCalledWith(
        PROJECT_ID, NOTEBOOK_ID, CONVERSATION_ID, 'turn-1',
      );
      expect(dispatch).toHaveBeenCalledWith({ type: 'SET_CANCELLING', payload: true });
    });

    it('calls cancelTurn without aborting when only observing', () => {
      const observerController = new AbortController();
      const abortSpy = vi.spyOn(observerController, 'abort');
      const { actions, dispatch } = mountActions(
        { isStreaming: true, streamingMode: 'observing' },
        {
          observerStreamController: observerController,
          activeStreamTurnId: 'turn-2',
          getActiveStreamTurnId: vi.fn(() => 'turn-2'),
        },
      );

      act(() => {
        actions.cancelStream();
      });

      expect(api.projects.notebooks.conversations.cancelTurn).toHaveBeenCalledWith(
        PROJECT_ID, NOTEBOOK_ID, CONVERSATION_ID, 'turn-2',
      );
      expect(abortSpy).not.toHaveBeenCalled();
      expect(dispatch).toHaveBeenCalledWith({ type: 'SET_CANCELLING', payload: true });
    });

    it('posts cancelTurn when Stop is clicked before turn_created then the turn id arrives', () => {
      const controller = new AbortController();
      const abortSpy = vi.spyOn(controller, 'abort');
      const { actions, dispatch } = mountActions(
        { isStreaming: true },
        {
          currentStreamController: controller,
          activeStreamTurnId: null,
          getActiveStreamTurnId: vi.fn(() => null),
        },
      );

      act(() => {
        actions.cancelStream();
      });

      expect(api.projects.notebooks.conversations.cancelTurn).not.toHaveBeenCalled();
      expect(abortSpy).not.toHaveBeenCalled();
      expect(dispatch).toHaveBeenCalledWith({ type: 'SET_CANCELLING', payload: true });

      act(() => {
        actions.onTurnIdAssigned('turn-late');
      });

      expect(api.projects.notebooks.conversations.cancelTurn).toHaveBeenCalledWith(
        PROJECT_ID, NOTEBOOK_ID, CONVERSATION_ID, 'turn-late',
      );
      expect(abortSpy).not.toHaveBeenCalled();
    });
  });

  describe('reattachIfStreaming', () => {
    it('opens observer SSE when conversation is still streaming', async () => {
      const setObserverStreamController = vi.fn();
      const setActiveStreamTurnId = vi.fn();
      const { actions, dispatch } = mountActions(
        {},
        { setObserverStreamController, setActiveStreamTurnId },
      );

      await act(async () => {
        await actions.reattachIfStreaming({
          activeTurn: { turnId: 'turn-1', status: 'streaming', turnIndex: 1 },
          lock: { lockedByUserName: 'Alice' },
          streamingPreview: { messageId: 'msg-1', content: 'partial', turnIndex: 1 },
          assistantName: 'Claude',
        });
      });

      expect(setActiveStreamTurnId).toHaveBeenCalledWith('turn-1');
      expect(dispatch).toHaveBeenCalledWith({
        type: 'SET_STREAMING_MODE',
        payload: { mode: 'observing', activeUser: { userId: '', userName: 'Alice' } },
      });
      expect(api.projects.notebooks.conversations.observeConversationEvents).toHaveBeenCalled();
      expect(setObserverStreamController).toHaveBeenCalled();
    });

    it('does not open observer SSE while the send stream is still attached', async () => {
      const sendController = new AbortController();
      const { actions } = mountActions(
        {},
        { currentStreamController: sendController },
      );

      await act(async () => {
        await actions.reattachIfStreaming({
          activeTurn: { turnId: 'turn-1', status: 'streaming', turnIndex: 1 },
        });
      });

      expect(api.projects.notebooks.conversations.observeConversationEvents).not.toHaveBeenCalled();
    });
  });

  describe('undoLastTurn', () => {
    it('returns early when _isUndoing', async () => {
      const { actions } = mountActions({ _isUndoing: true, messages: [createMockMessage()] });

      await act(async () => {
        await actions.undoLastTurn();
      });

      expect(api.projects.notebooks.conversations.undoLast).not.toHaveBeenCalled();
    });

    it('returns early when no user messages exist', async () => {
      const { actions } = mountActions({ messages: [createMockAssistantMessage()] });

      await act(async () => {
        await actions.undoLastTurn();
      });

      expect(api.projects.notebooks.conversations.undoLast).not.toHaveBeenCalled();
    });

    it('removes last turn optimistically and calls API', async () => {
      const userMsg = createMockMessage({ id: 'u1', content: 'undo me' });
      const { actions, dispatch } = mountActions({
        messages: [userMsg, createMockAssistantMessage()],
        draftUserContent: '',
      });

      await act(async () => {
        await actions.undoLastTurn();
      });

      expect(dispatch).toHaveBeenCalledWith({ type: 'REMOVE_LAST_TURN' });
      expect(dispatch).toHaveBeenCalledWith({ type: 'SET_DRAFT', payload: 'undo me' });
      expect(api.projects.notebooks.conversations.undoLast).toHaveBeenCalled();
      expect(dispatch).toHaveBeenCalledWith({ type: 'SET_UNDOING', payload: false });
    });

    it('restores messages on undo failure with 409 message', async () => {
      const userMsg = createMockMessage({ content: 'keep' });
      const messages = [userMsg, createMockAssistantMessage()];
      vi.mocked(api.projects.notebooks.conversations.undoLast).mockRejectedValue({ status: 409 });

      const { actions, dispatch, deps } = mountActions({ messages, draftUserContent: 'draft' });

      await act(async () => {
        await actions.undoLastTurn();
      });

      expect(dispatch).toHaveBeenCalledWith({ type: 'SET_MESSAGES', payload: messages });
      expect(dispatch).toHaveBeenCalledWith({ type: 'SET_DRAFT', payload: 'draft' });
      expect(deps.showToast).toHaveBeenCalledWith(expect.objectContaining({
        title: 'Undo Failed',
        message: expect.stringContaining('busy'),
      }));
    });

    it('shows generic undo failure toast', async () => {
      const userMsg = createMockMessage();
      vi.mocked(api.projects.notebooks.conversations.undoLast).mockRejectedValue(new Error('fail'));

      const { actions, deps } = mountActions({ messages: [userMsg] });

      await act(async () => {
        await actions.undoLastTurn();
      });

      expect(deps.showToast).toHaveBeenCalledWith(expect.objectContaining({
        message: 'Could not undo the last message. Please try again.',
      }));
    });
  });

  describe('setStreamingMode', () => {
    it('fetches last user message when entering observing mode', async () => {
      const lastUser = createMockMessage({ id: 'observer-user', content: 'observed' });
      vi.mocked(api.projects.notebooks.conversations.get).mockResolvedValue({
        messages: [lastUser, createMockAssistantMessage()],
      } as any);

      const { actions, dispatch } = mountActions();

      await act(async () => {
        await actions.setStreamingMode('observing', { userId: 'u1', userName: 'Alice' });
      });

      expect(dispatch).toHaveBeenCalledWith({ type: 'ADD_MESSAGE', payload: lastUser });
      expect(dispatch).toHaveBeenCalledWith({
        type: 'SET_STREAMING_MODE',
        payload: { mode: 'observing', activeUser: { userId: 'u1', userName: 'Alice' } },
      });
    });
  });

  describe('misc actions', () => {
    it('setSelectedAssistant dispatches and checks runtime', async () => {
      const { actions, dispatch } = mountActions();

      await act(async () => {
        await actions.setSelectedAssistant('Claude');
      });

      expect(dispatch).toHaveBeenCalledWith({ type: 'SET_ASSISTANT', payload: 'Claude' });
      expect(checkRuntimeStatus).toHaveBeenCalled();
    });

    it('setDraftUserContent dispatches SET_DRAFT', () => {
      const { actions, dispatch } = mountActions();

      act(() => {
        actions.setDraftUserContent('draft text');
      });

      expect(dispatch).toHaveBeenCalledWith({ type: 'SET_DRAFT', payload: 'draft text' });
    });

    it('addPendingAttachment and removePendingAttachment dispatch attachment actions', () => {
      const { actions, dispatch } = mountActions();
      const att = { notebookFileId: 'f1', fileName: 'doc.pdf', uploadType: 'document' as const };

      act(() => {
        actions.addPendingAttachment(att);
        actions.removePendingAttachment('f1');
      });

      expect(dispatch).toHaveBeenCalledWith({ type: 'ADD_ATTACHMENT', payload: att });
      expect(dispatch).toHaveBeenCalledWith({ type: 'REMOVE_ATTACHMENT', payload: 'f1' });
    });
  });
});
