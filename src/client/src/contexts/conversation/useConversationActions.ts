import { useCallback, useRef } from 'react';
import type { MessageDto, PendingAttachment } from '../../types/conversation';
import { api } from '../../services/api';
import { uploadTypeToServer } from '../../utils/attachments';
import { userService } from '../../services/userService';
import { ensureValidTokensForTemplate } from '../../utils/notebookAuth';
import { checkRuntimeStatus, getRuntimeBlockingMessage, dispatchRuntimeStatusWindowEvent } from './runtimeChecks';
import type { ActionType, ExtendedConversationState, StreamingMode } from './types';

interface ActionDeps {
  projectId: string;
  notebookId: string;
  conversationId: string;
  handleStreamingEvent: (event: { type: string; data: any }) => void;
  showToast: (opts: any) => void;
  loadNotebookFiles: () => Promise<void>;
  currentStreamController: AbortController | null;
  setCurrentStreamController: (c: AbortController | null) => void;
  observerStreamController: AbortController | null;
  setObserverStreamController: (c: AbortController | null) => void;
  sendStreamRef: React.MutableRefObject<AbortController | null>;
  observerStreamRef: React.MutableRefObject<AbortController | null>;
  inflightRuntimeChecksRef: React.MutableRefObject<Set<string>>;
  runtimeReadyCacheRef: React.MutableRefObject<Set<string>>;
  assistantByName: Record<string, { name: string; model?: string; avatarUrl: string; id?: string }>;
  activeStreamTurnId: string | null;
  setActiveStreamTurnId: (turnId: string | null) => void;
  getActiveStreamTurnId: () => string | null;
  refreshConversation: (options?: { force?: boolean }) => Promise<void>;
}

export function useConversationActions(
  dispatch: React.Dispatch<ActionType>,
  state: ExtendedConversationState,
  deps: ActionDeps,
) {
  const {
    projectId, notebookId, conversationId,
    handleStreamingEvent, showToast, loadNotebookFiles,
    setCurrentStreamController,
    setObserverStreamController,
    sendStreamRef, observerStreamRef,
    inflightRuntimeChecksRef, runtimeReadyCacheRef, assistantByName,
    activeStreamTurnId, setActiveStreamTurnId, getActiveStreamTurnId, refreshConversation,
  } = deps;

  const pendingStopRef = useRef(false);
  const stopPostedForTurnRef = useRef<string | null>(null);

  const clearPendingStop = useCallback(() => {
    pendingStopRef.current = false;
    stopPostedForTurnRef.current = null;
  }, []);

  const adoptSendStream = useCallback((controller: AbortController | null) => {
    setCurrentStreamController(controller);
  }, [setCurrentStreamController]);

  const adoptObserverStream = useCallback((controller: AbortController | null) => {
    setObserverStreamController(controller);
  }, [setObserverStreamController]);

  const abortObserverStream = useCallback(() => {
    const existing = observerStreamRef.current;
    if (existing && !existing.signal.aborted) {
      existing.abort();
    }
    setObserverStreamController(null);
  }, [observerStreamRef, setObserverStreamController]);

  const requestServerStop = useCallback((turnId: string) => {
    if (stopPostedForTurnRef.current === turnId) {
      return;
    }

    stopPostedForTurnRef.current = turnId;
    void api.projects.notebooks.conversations
      .cancelTurn(projectId, notebookId, conversationId, turnId)
      .catch(err => console.warn('Failed to request server stream cancellation:', err));
  }, [projectId, notebookId, conversationId]);

  const onTurnIdAssigned = useCallback((turnId: string) => {
    if (pendingStopRef.current) {
      requestServerStop(turnId);
    }
  }, [requestServerStop]);

  const sendMessage = useCallback(
    async (content: string, attachments: PendingAttachment[] = []) => {
      if (state._isUndoing) {
        return;
      }

      const isRuntimeNotReadyError = (error: any): boolean => {
        const bodyText = (() => {
          const body = error?.body;
          if (typeof body === 'string') return body;
          if (body && typeof body === 'object') {
            const pieces = [body.error, body.message, body.detail]
              .filter((v: unknown) => typeof v === 'string') as string[];
            return pieces.join(' ');
          }
          return '';
        })();
        const raw = `${error?.message ?? ''} ${bodyText}`.toLowerCase();
        return raw.includes('model is not loaded')
          || raw.includes('no model is loaded')
          || raw.includes('does not have a model loaded')
          || raw.includes('server has no model loaded');
      };

      const authStatus = await ensureValidTokensForTemplate(state.notebookTemplate || null, projectId);

      if (authStatus.needsAuth) {
        const missingProviderNames = authStatus.missingProviders.map(p => p.id).join(', ');
        showToast({
          type: 'error',
          title: 'Authentication Required',
          message: `Please reconnect these services: ${missingProviderNames}`
        });
        return;
      }

      const assistantName = state.selectedAssistant || (() => { throw new Error('No assistant selected when sending message'); })();
      const assistant = (state.assistants || []).find((candidate: any) => candidate?.name === assistantName);

      // Optimistic render: paint the user's message and the placeholder immediately,
      // before the runtime preflight. Every early-return below must roll this back.
      const attList = (attachments && attachments.length > 0) ? attachments : (state.pendingAttachments ?? []);

      const userMessage: MessageDto = {
        id: `tmp-${Date.now()}`,
        role: 'user',
        content,
        created: new Date().toISOString(),
        isEdited: false,
        attachments: attList.map(a => ({
          notebookFileId: a.relativePath ? (a.relativePath || a.notebookFileId) : a.notebookFileId,
          fileName: a.fileName,
          fileType: a.uploadType as any,
          fileSize: 0,
          type: 'Referenced' as const
        }))
      } as MessageDto;

      const placeholderAssistant: MessageDto = {
        id: `streaming-${Date.now()}`,
        role: 'assistant',
        content: '',
        created: new Date().toISOString(),
        isEdited: false,
        assistantName: state.selectedAssistant || undefined,
        streaming: true,
      } as MessageDto;

      // Reset stream-cancellation state BEFORE the optimistic dispatches flip
      // isStreaming to true. The Stop button renders on isStreaming alone (no
      // currentTurn guard), so it's clickable for the entire runtime-preflight
      // window below; if that reset ran after the preflight (as it used to),
      // a Stop click during the preflight would queue pendingStopRef, and this
      // clearPendingStop() would then wipe it out before onTurnIdAssigned ever
      // saw it — silently discarding the stop. Also clear any stale turn id
      // from a previous send here so an early Stop on this new send can't
      // target the wrong turn (see stale-active-stream-turn-id.md).
      dispatch({ type: 'SET_CANCELLING', payload: false });
      dispatch({ type: 'SET_STREAMING_ERROR', payload: undefined });
      clearPendingStop();
      setActiveStreamTurnId?.(null);

      dispatch({ type: 'SET_STREAMING_MODE', payload: { mode: 'sending' } });
      dispatch({ type: 'SET_DRAFT', payload: '' });
      dispatch({ type: 'ADD_MESSAGE', payload: userMessage });
      dispatch({ type: 'ADD_MESSAGE', payload: placeholderAssistant });

      const rollbackOptimisticSend = () => {
        // REMOVE_LAST_TURN pops trailing messages through the last user message —
        // exactly the placeholder + user message added above.
        dispatch({ type: 'REMOVE_LAST_TURN' });
        dispatch({ type: 'SET_DRAFT', payload: content });
        dispatch({ type: 'SET_STREAMING_MODE', payload: { mode: 'at-rest' } });
        // A Stop click during the runtime preflight (window between the
        // optimistic dispatches above and this rollback) sets _isCancelling
        // true; undo that here so it can't leak into the next send attempt.
        dispatch({ type: 'SET_CANCELLING', payload: false });
      };

      if (assistant?.id) {
        try {
          const runtimeStatus = await checkRuntimeStatus(projectId, notebookId, assistant.id, inflightRuntimeChecksRef.current, runtimeReadyCacheRef.current);
          if (!runtimeStatus) {
            rollbackOptimisticSend();
            return;
          }
          if (runtimeStatus.state !== 'ready') {
            if (runtimeStatus.state === 'failed' || runtimeStatus.state === 'invalid') {
              showToast({
                type: 'error',
                title: runtimeStatus.state === 'invalid' ? 'Incompatible Local Models' : 'Local Runtime Error',
                message: getRuntimeBlockingMessage(runtimeStatus)
              });
            }
            rollbackOptimisticSend();
            return;
          }
        } catch (error: any) {
          console.error('Failed to check local runtime status:', error);
          showToast({
            type: 'error',
            title: 'Runtime Error',
            message: `Failed to check model runtime status: ${error.message}`
          });
          rollbackOptimisticSend();
          return;
        }
      }

      // Send exclusively owns token appends for this turn.
      abortObserverStream();

      const controller = new AbortController();
      adoptSendStream(controller);

      dispatch({ type: 'START_STREAMING_TURN' });

      // activeStreamTurnId is assigned from the SSE `turnId` event once the
      // stream opens (see useStreamingEventHandler); no need to pre-fetch
      // the conversation snapshot just to learn it a few hundred ms early.
      // A Stop click that races ahead of that event is queued via
      // pendingStopRef/onTurnIdAssigned and re-issued once the id lands
      // (pre-existing behavior, unchanged by this removal).

      try {
        await api.projects.notebooks.conversations.sendMessageStream(
          projectId,
          notebookId,
          conversationId,
          {
            instructions: content,
            assistantName: state.selectedAssistant || (() => { throw new Error('No assistant selected when sending message'); })(),
            attachments: attList.map(a => ({
              notebookFileId: a.relativePath ? null : a.notebookFileId,
              relativePath: a.relativePath ?? null,
              uploadType: uploadTypeToServer(a.uploadType),
            })),
          } as any,
          handleStreamingEvent,
          (error) => {
            const isUserCancel = error.name === 'AbortError';
            const isIdleTimeout = error.name === 'StreamIdleTimeoutError';

            if (isUserCancel) {
              console.log('SSE client disconnected; server run may continue');
              adoptSendStream(null);
              if (!pendingStopRef.current) {
                dispatch({ type: 'SET_CANCELLING', payload: false });
              }
              return;
            }

            if (isIdleTimeout) {
              console.log('Stream idle timeout - finalizing partial content without force refresh');
              clearPendingStop();
              dispatch({ type: 'SET_STREAMING', payload: false });
              dispatch({ type: 'SET_CANCELLING', payload: false });
              dispatch({ type: 'FINALIZE_STREAMING_MESSAGE', payload: {} });
              dispatch({ type: 'CONVERT_STREAMING_IDS' });
              dispatch({ type: 'COMPLETE_STREAMING_TURN' });
              adoptSendStream(null);
              setActiveStreamTurnId?.(null);
              const streamErrorMessage = error.message || 'The conversation stream stopped sending data.';
              dispatch({ type: 'SET_STREAMING_ERROR', payload: streamErrorMessage });
              showToast({
                type: 'error',
                title: 'Chat Request Failed',
                message: streamErrorMessage,
              });
              return;
            }

            const reconcile = async () => {
              if (refreshConversation) {
                try {
                  await refreshConversation({ force: true });
                } catch (reconcileError) {
                  console.warn('Failed to reconcile conversation after stream error:', reconcileError);
                }
              }
            };

            console.error('Streaming error:', error);
            void reconcile();
            clearPendingStop();
            runtimeReadyCacheRef.current.clear();
            dispatch({ type: 'SET_STREAMING', payload: false });
            dispatch({ type: 'SET_CANCELLING', payload: false });
            dispatch({ type: 'FINALIZE_STREAMING_MESSAGE', payload: {} });
            dispatch({ type: 'CONVERT_STREAMING_IDS' });
            dispatch({ type: 'COMPLETE_STREAMING_TURN' });
            adoptSendStream(null);
            setActiveStreamTurnId?.(null);
            const streamErrorMessage = error.message || 'Chat request failed';
            dispatch({ type: 'SET_STREAMING_ERROR', payload: streamErrorMessage });
            showToast({
              type: 'error',
              title: 'Chat Request Failed',
              message: streamErrorMessage
            });
          },
          () => {
            clearPendingStop();
            dispatch({ type: 'COMPLETE_STREAMING_TURN' });
            dispatch({ type: 'SET_CANCELLING', payload: false });
            adoptSendStream(null);
            dispatch({ type: 'CLEAR_ATTACHMENTS' });
            try { window.dispatchEvent(new Event('refresh-notebook-toolbar')); } catch {}
            console.log('📄 [onComplete] Triggering loadNotebookFiles');
            loadNotebookFiles().catch(error => {
              console.error('Failed to refresh notebook files after conversation turn:', error);
            });
            try { window.dispatchEvent(new Event('refresh-notebook-files')); } catch {}
          },
          controller.signal,
          {
            requestServerCancel: async () => {
              const turnId = getActiveStreamTurnId();
              if (turnId) {
                await api.projects.notebooks.conversations
                  .cancelTurn(projectId, notebookId, conversationId, turnId);
              }
              controller.abort();
            },
          },
        );
      } catch (error: any) {
        if (error instanceof Error && (error.name === 'AbortError' || error.message.includes('aborted'))) {
          adoptSendStream(null);
          if (!pendingStopRef.current) {
            dispatch({ type: 'SET_CANCELLING', payload: false });
          }
          return;
        }

        console.error('Send message failed', error);
        runtimeReadyCacheRef.current.clear();
        clearPendingStop();
        dispatch({ type: 'SET_STREAMING', payload: false });
        dispatch({ type: 'SET_CANCELLING', payload: false });
        dispatch({ type: 'FINALIZE_STREAMING_MESSAGE', payload: {} });
        dispatch({ type: 'CONVERT_STREAMING_IDS' });
        dispatch({ type: 'COMPLETE_STREAMING_TURN' });
        adoptSendStream(null);

        if (error.status === 409) {
          const body = error.body;

          if (body?.code === 'ROUTING_MODEL_NOT_READY') {
            const action = body.action || 'Open Settings and configure a default chat model.';
            dispatch({ type: 'SET_STREAMING_ERROR', payload: body.detail || 'No chat model is configured.' });
            showToast({
              type: 'error',
              title: 'No Chat Model',
              message: action,
              duration: 8000
            });
            try { window.dispatchEvent(new Event('refresh-notebook-toolbar')); } catch {}
            return;
          }

          if (body?.code === 'OAUTH_RECONNECT_REQUIRED') {
            const providers: string[] = Array.isArray(body.providers) ? body.providers : [];
            const providerList = providers.length > 0 ? providers.join(', ') : 'one or more providers';
            const message = `Reconnect OAuth for ${providerList} before continuing.`;
            dispatch({ type: 'SET_STREAMING_ERROR', payload: message });
            showToast({
              type: 'error',
              title: 'Reconnect Required',
              message,
              duration: 8000
            });
            return;
          }

          const runtimeStatus = body?.runtimeStatus;
          if (runtimeStatus?.state && runtimeStatus.state !== 'ready') {
            dispatchRuntimeStatusWindowEvent(assistant?.id, runtimeStatus);
            if (runtimeStatus.state === 'failed' || runtimeStatus.state === 'invalid') {
              showToast({
                type: 'error',
                title: runtimeStatus.state === 'invalid' ? 'Incompatible Local Models' : 'Local Runtime Error',
                message: getRuntimeBlockingMessage(runtimeStatus)
              });
            }
            return;
          }
        }

        if (error?.status === 400 && isRuntimeNotReadyError(error)) {
          dispatchRuntimeStatusWindowEvent(assistant?.id, { state: 'requires_load' });
          return;
        }

        const errorMsg = error instanceof Error ? error.message : 'Chat request failed';
        dispatch({ type: 'SET_STREAMING_ERROR', payload: errorMsg });
        try { window.dispatchEvent(new Event('refresh-notebook-toolbar')); } catch {}
        showToast({
          type: 'error',
          title: 'Chat Request Failed',
          message: errorMsg
        });
      }
    },
    [state._isUndoing, state.selectedAssistant, state.assistants, state.notebookTemplate, projectId, notebookId, conversationId, handleStreamingEvent, state.pendingAttachments, loadNotebookFiles, showToast, abortObserverStream, adoptSendStream, clearPendingStop, getActiveStreamTurnId, refreshConversation, runtimeReadyCacheRef, setActiveStreamTurnId]
  );

  const editAssistantMessage = useCallback(
    async (messageId: string, content: string) => {
      dispatch({ type: 'SET_EDIT_LOADING', payload: true });
      dispatch({ type: 'SET_EDIT_ERROR', payload: undefined });

      const originalMessage = state.messages.find(m => m.id === messageId);
      if (!originalMessage) {
        dispatch({ type: 'SET_EDIT_ERROR', payload: 'Message not found' });
        dispatch({ type: 'SET_EDIT_LOADING', payload: false });
        return;
      }

      let currentUser: any = null;
      try {
        currentUser = await userService.getCurrentUser();
        if (currentUser && currentUser.id && !state.userProfiles?.[currentUser.id]) {
          dispatch({
            type: 'SET_USER_PROFILES',
            payload: { [currentUser.id]: currentUser }
          });
        }
      } catch (error) {
        console.warn('Failed to get current user for optimistic update:', error);
      }

      const updatedMessage = {
        ...originalMessage,
        content,
        isEdited: true,
        lastEditedAt: new Date().toISOString(),
        userId: currentUser?.id,
        originalContent: originalMessage.originalContent || originalMessage.content,
      };
      dispatch({
        type: 'UPDATE_MESSAGE',
        payload: { id: messageId, updates: updatedMessage }
      });
      dispatch({ type: 'SET_EDITING', payload: undefined });

      try {
        await api.projects.notebooks.conversations.editMessage(projectId, notebookId, conversationId, messageId, content);
      } catch (err) {
        console.error('Edit message failed', err);
        dispatch({
          type: 'UPDATE_MESSAGE',
          payload: { id: messageId, updates: originalMessage }
        });
        dispatch({ type: 'SET_EDITING', payload: messageId });
        dispatch({ type: 'SET_EDIT_ERROR', payload: 'Failed to save message. Please try again.' });
      } finally {
        dispatch({ type: 'SET_EDIT_LOADING', payload: false });
      }
    },
    [projectId, notebookId, conversationId, state.messages, state.userProfiles]
  );

  const startEditingAssistant = useCallback((messageId: string) => {
    dispatch({ type: 'SET_EDITING', payload: messageId });
    dispatch({ type: 'SET_EDIT_ERROR', payload: undefined });
  }, []);

  const cancelEditingAssistant = useCallback(() => {
    dispatch({ type: 'SET_EDITING', payload: undefined });
    dispatch({ type: 'SET_EDIT_ERROR', payload: undefined });
    dispatch({ type: 'SET_EDIT_LOADING', payload: false });
  }, []);

  const setStreamingMode = useCallback(async (mode: StreamingMode, activeUser?: { userId: string; userName: string }) => {
    console.log('🔥 setStreamingMode called:', mode, activeUser);

    if (mode === 'observing') {
      console.log('🔥 Fetching user message for observer');
      try {
        const convo = await api.projects.notebooks.conversations.get(projectId, notebookId, conversationId);
        console.log('🔥 Fetched conversation:', convo?.messages?.length, 'messages');

        if (convo && convo.messages && convo.messages.length > 0) {
          console.log('🔥 Message roles:', convo.messages.map((m: any) => m.role));

          const lastUserMessage = convo.messages
            .filter((m: any) => m.role && m.role.toLowerCase() === 'user')
            .sort((a: any, b: any) => {
              const aTime = new Date(a.created || a.timestamp || a.createdAt || 0).getTime();
              const bTime = new Date(b.created || b.timestamp || b.createdAt || 0).getTime();
              return bTime - aTime;
            })[0];

          console.log('🔥 Last user message:', lastUserMessage);

          if (lastUserMessage) {
            console.log('🔥 Adding user message to conversation');
            dispatch({ type: 'ADD_MESSAGE', payload: lastUserMessage });
          } else {
            console.log('⚠️ No user message found in conversation');
          }
        }
      } catch (error) {
        console.error('Failed to fetch user message for observer:', error);
      }
    }

    console.log('🔥 Setting streaming mode to:', mode);
    dispatch({ type: 'SET_STREAMING_MODE', payload: { mode, activeUser } });
  }, [projectId, notebookId, conversationId, dispatch]);

  const reattachIfStreaming = useCallback(async (convo: {
    activeTurn?: { turnId: string; status: string; turnIndex: number } | null;
    lock?: { lockedByUserName: string } | null;
    streamingPreview?: { messageId: string; content: string; turnIndex: number } | null;
    assistantName?: string | null;
    messages?: MessageDto[];
  }) => {
    if (!convo.activeTurn || convo.activeTurn.status !== 'streaming') {
      return;
    }

    if (sendStreamRef.current && !sendStreamRef.current.signal.aborted) {
      return;
    }

    if (observerStreamRef.current && !observerStreamRef.current.signal.aborted) {
      return;
    }

    const activeUser = convo.lock
      ? { userId: '', userName: convo.lock.lockedByUserName }
      : undefined;

    setActiveStreamTurnId(convo.activeTurn.turnId);
    dispatch({
      type: 'SET_STREAMING_MODE',
      payload: { mode: 'observing', activeUser },
    });

    if (convo.streamingPreview) {
      const previewId = `streaming-${convo.streamingPreview.messageId}`;
      const knownMessages = convo.messages ?? state.messages;
      const hasPreview = knownMessages.some(m => m.id === previewId);
      if (!hasPreview) {
        const placeholder: MessageDto = {
          id: previewId,
          role: 'assistant',
          content: convo.streamingPreview.content,
          created: new Date().toISOString(),
          isEdited: false,
          streaming: true,
          assistantName: convo.assistantName || state.selectedAssistant || undefined,
          turnIndex: convo.streamingPreview.turnIndex,
        } as MessageDto;
        dispatch({ type: 'ADD_MESSAGE', payload: placeholder });
      }
    }

    // Claim ownership synchronously before the fetch starts so a concurrent reattach cannot open a second socket.
    const controller = new AbortController();
    adoptObserverStream(controller);

    void api.projects.notebooks.conversations.observeConversationEvents(
      projectId,
      notebookId,
      conversationId,
      handleStreamingEvent,
      (error) => {
        if (error.name === 'AbortError') {
          return;
        }
        console.error('Observer stream error:', error);
        dispatch({ type: 'SET_STREAMING_ERROR', payload: error.message || 'Observer stream failed' });
      },
      () => {
        if (observerStreamRef.current === controller) {
          adoptObserverStream(null);
        }
        setActiveStreamTurnId(null);
        dispatch({ type: 'SET_CANCELLING', payload: false });
        clearPendingStop();
      },
      controller.signal,
    );
  }, [
    projectId,
    notebookId,
    conversationId,
    handleStreamingEvent,
    dispatch,
    setActiveStreamTurnId,
    adoptObserverStream,
    clearPendingStop,
    state.messages,
    state.selectedAssistant,
  ]);

  const cancelStream = useCallback(() => {
    dispatch({ type: 'SET_CANCELLING', payload: true });
    pendingStopRef.current = true;

    const turnId = getActiveStreamTurnId() ?? activeStreamTurnId;
    if (turnId) {
      requestServerStop(turnId);
    }
  }, [activeStreamTurnId, getActiveStreamTurnId, requestServerStop, dispatch]);

  const undoLastTurn = useCallback(async () => {
    if (state._isUndoing) {
      return;
    }

    const hasUserMessage = state.messages.some(m => (m.role || '').toLowerCase() === 'user');
    if (!hasUserMessage) {
      console.warn('No user messages to undo');
      return;
    }

    // Finalize any in-flight streaming state first; COMPLETE_STREAMING_TURN must run before we
    // flag the undo as in-progress so it can't clear the flag we are about to set.
    dispatch({ type: 'FINALIZE_STREAMING_MESSAGE', payload: {} });
    dispatch({ type: 'CONVERT_STREAMING_IDS' });
    dispatch({ type: 'COMPLETE_STREAMING_TURN' });

    dispatch({ type: 'SET_UNDOING', payload: true });

    const originalMessages = [...state.messages];
    const previousDraft = state.draftUserContent || '';

    const lastUserMessage = [...state.messages]
      .slice()
      .reverse()
      .find(m => (m.role || '').toLowerCase() === 'user');
    const undoneUserContent = (lastUserMessage?.content ?? '').toString();

    dispatch({ type: 'REMOVE_LAST_TURN' });
    dispatch({ type: 'SET_DRAFT', payload: undoneUserContent });

    try {
      await api.projects.notebooks.conversations.undoLast(projectId, notebookId, conversationId);
    } catch (err: any) {
      console.error('Undo turn failed', err);
      dispatch({ type: 'SET_MESSAGES', payload: originalMessages });
      dispatch({ type: 'SET_DRAFT', payload: previousDraft });
      showToast({
        type: 'error',
        title: 'Undo Failed',
        message: err?.status === 409
          ? 'The conversation is busy right now. Wait for the current response to finish and try again.'
          : 'Could not undo the last message. Please try again.'
      });
    } finally {
      dispatch({ type: 'SET_UNDOING', payload: false });
    }
  }, [projectId, notebookId, conversationId, state.messages, state._isUndoing, showToast]);

  const setSelectedAssistant = useCallback(async (name: string) => {
    dispatch({ type: 'SET_ASSISTANT', payload: name });

    const assistant = assistantByName[name];
    if (assistant?.id) {
      checkRuntimeStatus(projectId, notebookId, assistant.id, inflightRuntimeChecksRef.current, runtimeReadyCacheRef.current).catch(() => {});
    }
  }, [projectId, notebookId, assistantByName]);

  const setDraftUserContent = useCallback((text: string) => dispatch({ type: 'SET_DRAFT', payload: text }), []);

  const addPendingAttachment = useCallback((att: PendingAttachment) => {
    dispatch({ type: 'ADD_ATTACHMENT', payload: att });
  }, []);

  const removePendingAttachment = useCallback((fileId: string) => {
    dispatch({ type: 'REMOVE_ATTACHMENT', payload: fileId });
  }, []);

  return {
    sendMessage,
    editAssistantMessage,
    startEditingAssistant,
    cancelEditingAssistant,
    setStreamingMode,
    reattachIfStreaming,
    onTurnIdAssigned,
    clearPendingStop,
    cancelStream,
    undoLastTurn,
    setSelectedAssistant,
    setDraftUserContent,
    addPendingAttachment,
    removePendingAttachment,
  };
}
