import { useCallback } from 'react';
import type { MessageDto, PendingAttachment } from '../../types/conversation';
import { api } from '../../services/api';
import { uploadTypeToServer } from '../../utils/attachments';
import { userService } from '../../services/userService';
import { collectOAuthTokensForTemplate, ensureValidTokensForTemplate } from '../../utils/notebookAuth';
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
  inflightRuntimeChecksRef: React.MutableRefObject<Set<string>>;
  runtimeReadyCacheRef: React.MutableRefObject<Set<string>>;
  assistantByName: Record<string, { name: string; model?: string; avatarUrl: string; id?: string }>;
}

export function useConversationActions(
  dispatch: React.Dispatch<ActionType>,
  state: ExtendedConversationState,
  deps: ActionDeps,
) {
  const {
    projectId, notebookId, conversationId,
    handleStreamingEvent, showToast, loadNotebookFiles,
    currentStreamController, setCurrentStreamController,
    inflightRuntimeChecksRef, runtimeReadyCacheRef, assistantByName,
  } = deps;

  const sendMessage = useCallback(
    async (content: string, attachments: PendingAttachment[] = []) => {
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

      if (assistant?.id) {
        try {
          const runtimeStatus = await checkRuntimeStatus(projectId, notebookId, assistant.id, inflightRuntimeChecksRef.current, runtimeReadyCacheRef.current);
          if (!runtimeStatus) {
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
            return;
          }
        } catch (error: any) {
          console.error('Failed to check local runtime status:', error);
          showToast({
            type: 'error',
            title: 'Runtime Error',
            message: `Failed to check model runtime status: ${error.message}`
          });
          return;
        }
      }

      dispatch({ type: 'SET_STREAMING_MODE', payload: { mode: 'sending' } });
      dispatch({ type: 'SET_CANCELLING', payload: false });
      dispatch({ type: 'SET_STREAMING_ERROR', payload: undefined });
      dispatch({ type: 'SET_DRAFT', payload: '' });

      const controller = new AbortController();
      setCurrentStreamController(controller);

      const attList = (attachments && attachments.length > 0) ? attachments : (state.pendingAttachments ?? []);

      dispatch({ type: 'START_STREAMING_TURN' });

      const userMessage: MessageDto = {
        id: `tmp-${Date.now()}`,
        role: 'user',
        content,
        created: new Date().toISOString(),
        isEdited: false,
        attachments: attList.map(a => ({
          notebookFileId: a.notebookFileId,
          fileName: a.fileName,
          fileType: a.uploadType as any,
          fileSize: 0,
          type: 'Referenced' as const
        }))
      } as MessageDto;
      dispatch({ type: 'ADD_MESSAGE', payload: userMessage });

      const placeholderAssistant: MessageDto = {
        id: `streaming-${Date.now()}`,
        role: 'assistant',
        content: '',
        created: new Date().toISOString(),
        isEdited: false,
        assistantName: state.selectedAssistant || undefined,
        streaming: true,
      } as MessageDto;
      dispatch({ type: 'ADD_MESSAGE', payload: placeholderAssistant });

      try {
        const oauthTokens = collectOAuthTokensForTemplate(state.notebookTemplate || null, projectId);

        await api.projects.notebooks.conversations.sendMessageStream(
          projectId,
          notebookId,
          conversationId,
          {
            instructions: content,
            assistantName: state.selectedAssistant || (() => { throw new Error('No assistant selected when sending message'); })(),
            attachments: attList.map(a => ({
              notebookFileId: a.notebookFileId,
              uploadType: uploadTypeToServer(a.uploadType),
            })),
          } as any,
          handleStreamingEvent,
          (error) => {
            if (error.name === 'AbortError' || error.message.includes('aborted')) {
              console.log('Stream was cancelled by user - finalizing partial content');
              dispatch({ type: 'FINALIZE_STREAMING_MESSAGE', payload: {} });
              dispatch({ type: 'COMPLETE_STREAMING_TURN' });
              dispatch({ type: 'SET_CANCELLING', payload: false });
              setCurrentStreamController(null);
              return;
            }

            console.error('Streaming error:', error);
            runtimeReadyCacheRef.current.clear();
            dispatch({ type: 'SET_STREAMING', payload: false });
            dispatch({ type: 'SET_CANCELLING', payload: false });
            dispatch({ type: 'FINALIZE_STREAMING_MESSAGE', payload: {} });
            dispatch({ type: 'CONVERT_STREAMING_IDS' });
            dispatch({ type: 'COMPLETE_STREAMING_TURN' });
            setCurrentStreamController(null);
            const streamErrorMessage = error.message || 'Chat request failed';
            dispatch({ type: 'SET_STREAMING_ERROR', payload: streamErrorMessage });
            showToast({
              type: 'error',
              title: 'Chat Request Failed',
              message: streamErrorMessage
            });
          },
          () => {
            dispatch({ type: 'COMPLETE_STREAMING_TURN' });
            dispatch({ type: 'SET_CANCELLING', payload: false });
            setCurrentStreamController(null);
            dispatch({ type: 'CLEAR_ATTACHMENTS' });
            try { window.dispatchEvent(new Event('refresh-notebook-toolbar')); } catch {}
            console.log('📄 [onComplete] Triggering loadNotebookFiles');
            loadNotebookFiles().catch(error => {
              console.error('Failed to refresh notebook files after conversation turn:', error);
            });
            try { window.dispatchEvent(new Event('refresh-notebook-files')); } catch {}
          },
          controller.signal,
          oauthTokens
        );
      } catch (error: any) {
        if (error instanceof Error && (error.name === 'AbortError' || error.message.includes('aborted'))) {
          dispatch({ type: 'FINALIZE_STREAMING_MESSAGE', payload: {} });
          dispatch({ type: 'COMPLETE_STREAMING_TURN' });
          dispatch({ type: 'SET_CANCELLING', payload: false });
          setCurrentStreamController(null);
          return;
        }

        console.error('Send message failed', error);
        runtimeReadyCacheRef.current.clear();
        dispatch({ type: 'SET_STREAMING', payload: false });
        dispatch({ type: 'SET_CANCELLING', payload: false });
        dispatch({ type: 'FINALIZE_STREAMING_MESSAGE', payload: {} });
        dispatch({ type: 'CONVERT_STREAMING_IDS' });
        dispatch({ type: 'COMPLETE_STREAMING_TURN' });
        setCurrentStreamController(null);

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
    [state.selectedAssistant, state.assistants, state.notebookTemplate, projectId, notebookId, conversationId, handleStreamingEvent, state.pendingAttachments, loadNotebookFiles, showToast]
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

  const cancelStream = useCallback(() => {
    if (currentStreamController) {
      dispatch({ type: 'SET_CANCELLING', payload: true });
      currentStreamController.abort();
      setCurrentStreamController(null);

      setTimeout(() => {
        if (state.isStreaming) {
          console.log('🔥 Forcing finalization of cancelled stream');
          dispatch({ type: 'FINALIZE_STREAMING_MESSAGE', payload: {} });
          dispatch({ type: 'COMPLETE_STREAMING_TURN' });
          dispatch({ type: 'SET_CANCELLING', payload: false });
        }
      }, 500);
    }
  }, [currentStreamController, projectId, notebookId, conversationId, state.isStreaming]);

  const undoLastTurn = useCallback(async () => {
    const hasUserMessage = state.messages.some(m => (m.role || '').toLowerCase() === 'user');
    if (!hasUserMessage) {
      console.warn('No user messages to undo');
      return;
    }

    dispatch({ type: 'FINALIZE_STREAMING_MESSAGE', payload: {} });
    dispatch({ type: 'CONVERT_STREAMING_IDS' });
    dispatch({ type: 'COMPLETE_STREAMING_TURN' });

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
    } catch (err) {
      console.error('Undo turn failed', err);
      dispatch({ type: 'SET_MESSAGES', payload: originalMessages });
      dispatch({ type: 'SET_DRAFT', payload: previousDraft });
    }
  }, [projectId, notebookId, conversationId, state.messages]);

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
    cancelStream,
    undoLastTurn,
    setSelectedAssistant,
    setDraftUserContent,
    addPendingAttachment,
    removePendingAttachment,
  };
}
