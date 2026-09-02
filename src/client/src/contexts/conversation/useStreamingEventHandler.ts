import { useCallback } from 'react';
import type { MessageDto, StreamingMessage, StreamingToolActivity } from '../../types/conversation';
import type { ActionType, ExtendedConversationState, SendStreamState } from './types';

interface StreamingEventDeps {
  loadNotebookFiles: () => Promise<void>;
  showToast: (opts: any) => void;
  projectId: string;
  notebookId: string;
  conversationId: string;
  setCurrentStreamController: (c: AbortController | null) => void;
  setActiveStreamTurnId?: (turnId: string | null) => void;
  getActiveStreamTurnId?: () => string | null;
  refreshConversation?: (options?: { force?: boolean }) => Promise<void>;
  // sendStreamStateRef / pendingStopRef are still read here, but only for NON-composer
  // decisions: turn_created ownership (source === 'observer') and the stop-pending early
  // return. Composer terminal state is owned by the action layer (see P2).
  sendStreamStateRef?: React.MutableRefObject<SendStreamState | null>;
  pendingStopRef?: React.MutableRefObject<boolean>;
}

/** Events that must not mutate the live UI when they belong to a different turn. */
const TURN_SCOPED_EVENT_TYPES = new Set([
  'assistant_message',
  'token',
  'error',
  'cancelled',
  'complete',
  'pending_client_tool',
  'tool_result',
  'tool_error',
  'streaming_progress',
  'message',
  'usage',
]);

function isForeignTurnEvent(
  event: { type: string; data: any },
  getActiveStreamTurnId?: () => string | null,
): boolean {
  if (!TURN_SCOPED_EVENT_TYPES.has(event.type)) {
    return false;
  }

  const eventTurnId = event.data?.turnId;
  if (typeof eventTurnId !== 'string' || !eventTurnId) {
    return false;
  }

  const activeTurnId = getActiveStreamTurnId?.() ?? null;
  if (!activeTurnId) {
    return false;
  }

  return eventTurnId !== activeTurnId;
}

export function useStreamingEventHandler(
  dispatch: React.Dispatch<ActionType>,
  state: ExtendedConversationState,
  deps: StreamingEventDeps,
): (
  event: { type: string; data: any },
  source?: 'send' | 'observer',
) => void {
  const {
    loadNotebookFiles, showToast,
    projectId, notebookId, conversationId, setCurrentStreamController,
  } = deps;

  return useCallback((event: { type: string; data: any }, source?: 'send' | 'observer') => {
    if (['user_message', 'tool_result', 'assistant_message'].includes(event.type)) {
      console.log('🔥 SSE Event:', event.type, event.data);
    }

    if (!event.data) {
      console.warn('SSE event received with no data:', event.type);
      return;
    }

    if (isForeignTurnEvent(event, deps.getActiveStreamTurnId)) {
      console.debug(
        'Ignoring stream event for non-active turn',
        event.type,
        event.data?.turnId,
        'active=',
        deps.getActiveStreamTurnId?.(),
      );
      return;
    }

    const stopPending = deps.pendingStopRef?.current ?? state._isCancelling;

    switch (event.type) {
      case 'message':
        if (!event.data.content) {
          console.warn('Message event received with no content');
          return;
        }
        {
          const finalMessage: MessageDto = {
            id: `msg-${Date.now()}`,
            role: event.data.role || 'assistant',
            content: event.data.content,
            created: new Date().toISOString(),
            isEdited: false,
            assistantName: state.selectedAssistant || (() => { throw new Error('No assistant selected for streaming message'); })()
          } as MessageDto;
          dispatch({ type: 'FINALIZE_STREAMING_MESSAGE', payload: finalMessage });
          dispatch({ type: 'ADD_FINAL_RESPONSE', payload: {
            role: 'assistant',
            content: event.data.content,
            timestamp: new Date(event.data.timestamp || new Date())
          } as StreamingMessage });
        }
        break;

      case 'usage':
        console.log('Usage:', event.data);
        break;

      case 'streaming_progress':
        if (event.data.toolActivity?.name) {
          const activity = event.data.toolActivity;
          dispatch({ type: 'SET_ACTIVE_TOOL_ACTIVITY', payload: {
            ...activity,
            timestamp: new Date(activity.timestamp || new Date())
          } as StreamingToolActivity });
        }
        break;

      case 'complete':
        {
          if (stopPending) {
            // A terminal SSE event is not the Stop acknowledgement. The cancel endpoint is the
            // authoritative boundary because it also confirms durable terminalization and lock
            // release. Keep the local workflow stopping until its HTTP response arrives.
            return;
          }
          deps.setActiveStreamTurnId?.(null);
          // Conversation state only: finalize the streamed cell. Composer terminal state
          // (draft/attachments/snapshot) is owned by the action layer's onComplete, which the
          // transport invokes for this same terminal event — so no render-captured state.* is
          // read here and the D4 stale `state.messages` backfill is gone (the reducer's
          // COMPLETE_STREAMING_TURN already rewrites the streamed cell to its final content).
          dispatch({ type: 'COMPLETE_STREAMING_TURN' });

          // Fast-register completes on the server before SSE `complete`. Refresh the notebook
          // file tree so sidebar/serving gate pick up newly registered paths without a hard
          // reload — but ONLY for observer completions: the send-side owner
          // (useConversationActions onComplete) already refreshes the tree for every local
          // terminal outcome, and refreshing here too would double the fetch + broadcast.
          // (loadNotebookFiles emits the refresh-notebook-files event itself, so no manual
          // dispatch is needed.)
          if (source === 'observer') {
            console.log('📄 [SSE complete] Triggering loadNotebookFiles (observer)');
            loadNotebookFiles().catch(error => {
              console.error('Failed to refresh notebook files after conversation turn:', error);
            });
          }

          // Title is set server-side on first turn complete; refresh sidebar when a turn finishes.
          try { window.dispatchEvent(new Event('refresh-conversations')); } catch {}
        }
        break;

      case 'pending_client_tool':
        if (stopPending) {
          return;
        }
        deps.setActiveStreamTurnId?.(null);
        // Conversation state only. Composer restore (draft + chips from the send snapshot)
        // is the action layer's job via onComplete('pending_client_tool') — the default
        // policy restores-and-unlocks; a registered client-tool policy would block instead.
        // This handler never decides composer behavior for the client-tool outcome.
        dispatch({ type: 'FINALIZE_STREAMING_MESSAGE', payload: {} });
        dispatch({ type: 'COMPLETE_STREAMING_TURN' });
        dispatch({ type: 'SET_STREAMING', payload: false });
        dispatch({ type: 'SET_CANCELLING', payload: false });
        break;

      case 'turn_created':
        if (event.data?.turnId) {
          // An observer receives every conversation broadcast. While this client is sending,
          // accepting an observer's turn_created would make a pending Stop target a different
          // client's turn. The send endpoint is the only authority for the sending turn.
          if (source === 'observer' && deps.sendStreamStateRef?.current) {
            return;
          }
          deps.setActiveStreamTurnId?.(event.data.turnId);
        }
        break;

      case 'cancelled':
        if (stopPending) {
          return;
        }
        console.log('🔥 Stream was cancelled, preserving partial content');
        deps.setActiveStreamTurnId?.(null);
        // Conversation state only. Composer restore/clear is the action layer's
        // onComplete('cancelled') call, which applies the turnId persistence oracle:
        // turnId present → message persisted → clear; absent → restore the snapshot.
        dispatch({ type: 'FINALIZE_STREAMING_MESSAGE', payload: {} });
        dispatch({ type: 'COMPLETE_STREAMING_TURN' });
        dispatch({ type: 'SET_STREAMING', payload: false });
        dispatch({ type: 'SET_CANCELLING', payload: false });
        // Cancelled tool ERROR results may land after optimistic unlock cleared currentTurn;
        // reload so the transcript shows them like any other tool error.
        if (deps.refreshConversation) {
          void deps.refreshConversation({ force: true }).catch(err =>
            console.warn('Failed to refresh conversation after cancel:', err));
        }
        break;

      case 'user_message':
        console.log('🔥 Skipping user_message SSE event (already added in sendMessage)');
        break;

      case 'tool_result':
        {
          console.log('🔥 Processing tool_result event:', event.data);

          const toolContent = event.data.content || '';
          const toolCallId = event.data.toolCallId;

          if (event.data.functionName === 'GeneratePodcast' || event.data.functionName?.includes('GeneratePodcast')) {
            loadNotebookFiles().catch(err => console.error('Refresh after GeneratePodcast failed:', err));
          }

          if (!toolCallId) {
            console.warn('tool_result event missing toolCallId, skipping');
            return;
          }

          const functionName = event.data.functionName || 'unknown_function';
          const toolCall = {
            id: toolCallId,
            name: functionName,
            arguments: event.data.arguments || event.data.parameters || '{}',
            status: 'executing' as const,
            timestamp: new Date(event.data.timestamp || new Date())
          };

          console.log('🔥 Dispatching ENSURE_TOOL_CALL for:', toolCallId);
          dispatch({ type: 'ENSURE_TOOL_CALL', payload: toolCall });

          console.log('🔥 Dispatching ADD_TOOL_RESULT for:', toolCallId);
          const isError = typeof toolContent === 'string'
            && toolContent.trimStart().startsWith('ERROR:');
          dispatch({ type: 'ADD_TOOL_RESULT', payload: {
            toolCallId,
            content: toolContent,
            isError,
            timestamp: new Date(event.data.timestamp || new Date())
          }});
        }
        break;

      case 'token':
        if (event.data.contentDelta) {
          dispatch({ type: 'APPEND_TOKEN', payload: { contentDelta: event.data.contentDelta } });
        }
        break;

      case 'assistant_message':
        {
          console.log('🔥 Processing assistant_message event:', {
            hasToolCalls: !!(event.data.tool_calls && event.data.tool_calls.length > 0),
            hasContentDelta: !!(event.data.contentDelta && event.data.contentDelta.trim()),
            contentDeltaLength: event.data.contentDelta?.length || 0
          });

          if (state.streamingMode === 'observing') {
            const hasStreamingMessage = state.messages.some(m =>
              m.role.toLowerCase() === 'assistant' && m.id.startsWith('streaming-')
            );

            if (!hasStreamingMessage) {
              console.log('🔥 Observer receiving first assistant_message - creating streaming placeholder');
              const observerPlaceholderAssistant: MessageDto = {
                id: `streaming-${Date.now()}`,
                role: 'assistant',
                content: '',
                created: new Date().toISOString(),
                isEdited: false,
                streaming: true,
              } as MessageDto;
              dispatch({ type: 'ADD_MESSAGE', payload: observerPlaceholderAssistant });
            }
          }

          if (event.data.contentDelta) {
            dispatch({ type: 'APPEND_TOKEN', payload: { contentDelta: event.data.contentDelta } });
          }

          if (event.data.tool_calls && event.data.tool_calls.length > 0) {
            const toolCalls = event.data.tool_calls.map((tc: any) => ({
              id: tc.id,
              name: tc.function.name,
              arguments: tc.function.arguments,
              status: 'executing' as const,
              timestamp: new Date(event.data.timestamp || new Date())
            }));

            console.log('🔥 Adding tool calls to turn:', toolCalls.map((tc: any) => ({ id: tc.id, name: tc.name })));
            dispatch({ type: 'SET_TOOL_CALLS', payload: toolCalls });
          }
        }
        break;

      case 'tool_error':
        {
          const errorContent = event.data.content || 'Unknown tool error';
          dispatch({ type: 'ADD_TOOL_ERROR', payload: {
            toolCallId: event.data.toolCallId || `tool-${Date.now()}`,
            content: errorContent,
            timestamp: new Date(event.data.timestamp || new Date())
          }});
        }
        break;

      case 'system_message':
        console.log('System message:', event.data);
        break;

      case 'error':
        {
          if (stopPending) {
            return;
          }
          console.error('Streaming error received:', event.data);

          const errorMessage = event.data?.message || 'An error occurred during the conversation';
          const errorAction = event.data?.action;
          const displayMessage = errorAction ? `${errorMessage}\n\n${errorAction}` : errorMessage;
          const errorType = event.data?.type;
          // Server-populated `code` field. Currently one of:
          //   'local_llm_oom'       — classified CUDA/OOM response body from llama-server
          //   'local_llm_crashed'   — 5xx without OOM markers, or mid-stream socket drop
          //   'local_llm_not_ready' — 400 "no model loaded"; runtime is up but idle
          //   'local_llm_timeout'    — inference deadline expired; automatic recovery started
          //   'local_llm_recovering' — automatic recovery currently owns the model
          // See GuideAntsApi/Services/Conversations/StreamingErrorEnvelope.cs.
          const errorCode = event.data?.code;

          // Conversation state only. Composer restore/clear is the action layer's
          // onComplete('error') call, which applies the same turnId persistence oracle as
          // the cancelled path (turnId present → clear; absent → restore the snapshot).
          deps.setActiveStreamTurnId?.(null);
          dispatch({ type: 'SET_STREAMING_ERROR', payload: displayMessage });
          dispatch({ type: 'SET_STREAMING', payload: false });
          dispatch({ type: 'SET_CANCELLING', payload: false });
          dispatch({ type: 'FINALIZE_STREAMING_MESSAGE', payload: {} });
          dispatch({ type: 'CONVERT_STREAMING_IDS' });
          dispatch({ type: 'COMPLETE_STREAMING_TURN' });

          if (errorCode === 'local_llm_oom' || errorCode === 'local_llm_crashed') {
            // Hand off to the notebook-level crash modal. We *don't* raise a toast here — the
            // modal is the user-visible error surface in this branch, and a stacked toast just
            // adds noise while the user is already looking at a red dialog. The reason payload
            // is passed through so the modal copy can distinguish OOM from generic crashes.
            window.dispatchEvent(new CustomEvent('llama-runtime-crashed', {
              detail: {
                reason: event.data?.reason || (errorCode === 'local_llm_oom' ? 'OutOfMemory' : 'Crashed'),
                message: displayMessage,
                upstreamDetail: event.data?.innerMessage || null,
                code: errorCode
              }
            }));
          } else if (errorCode === 'local_llm_not_ready') {
            // Runtime is up, just no model loaded. Route through the *same* event the notebook-
            // level check uses so exactly one code path opens the load dialog. No restart — the
            // process is healthy, and killing it would be wasteful. No toast — the dialog is the
            // error surface here.
            window.dispatchEvent(new CustomEvent('llama-runtime-requires-load', {
              detail: {
                runtimeStatus: { state: 'requires_load' },
                // assistantId is intentionally omitted: the notebook page preserves the last
                // targeted assistant in its own ref, so we don't need to re-plumb it through
                // the SSE payload. NotebookDetails.handleRequiresLoad falls back to its existing
                // targetAssistantId when detail.assistantId is undefined.
                assistantId: undefined
              }
            }));
          } else if (errorCode === 'local_llm_timeout' || errorCode === 'local_llm_recovering') {
            showToast({
              type: 'warning',
              title: 'Local Model Recovering',
              message: displayMessage,
              duration: 10000
            });
          } else if (errorType === 'AttachmentNotReadyException') {
            showToast({
              type: 'warning',
              title: 'Attachment Still Processing',
              message: errorMessage,
              duration: 10000
            });
          } else {
            showToast({
              type: 'error',
              title: 'Conversation Error',
              message: displayMessage,
              duration: 8000
            });
          }
          // Note: the send controller is released by the action owner (adoptSendStream(null)),
          // not here. Nulling it here would trip the owner's stale-callback guard
          // (sendStreamRef.current !== controller) before it restored the composer on error —
          // the same abort-coupling this refactor removes.
        }
        break;

      default:
        console.log('Unknown SSE event type:', event.type, event.data);
        if (event.type && typeof event.data === 'object' && event.data.content) {
          console.log('Attempting to handle unknown event as legacy format');
          dispatch({ type: 'APPEND_TOKEN', payload: { contentDelta: event.data.content } });
        }
        break;
    }
  }, [loadNotebookFiles, showToast, projectId, notebookId, conversationId, setCurrentStreamController, deps, state.messages, state.currentTurn, state.streamingMode, state.selectedAssistant, state._isCancelling, dispatch]);
}
