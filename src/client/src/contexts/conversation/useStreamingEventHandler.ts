import { useCallback } from 'react';
import type { MessageDto, StreamingMessage } from '../../types/conversation';
import { api } from '../../services/api';
import { DEFAULT_CONVERSATION_TITLE } from '../../constants/conversation';
import type { ActionType, ExtendedConversationState } from './types';

interface StreamingEventDeps {
  loadNotebookFiles: () => Promise<void>;
  loadConversations: () => Promise<void>;
  conversations: any[];
  showToast: (opts: any) => void;
  projectId: string;
  notebookId: string;
  conversationId: string;
  setCurrentStreamController: (c: AbortController | null) => void;
}

export function useStreamingEventHandler(
  dispatch: React.Dispatch<ActionType>,
  state: ExtendedConversationState,
  deps: StreamingEventDeps,
): (event: { type: string; data: any }) => void {
  const {
    loadNotebookFiles, loadConversations, conversations, showToast,
    projectId, notebookId, conversationId, setCurrentStreamController,
  } = deps;

  return useCallback((event: { type: string; data: any }) => {
    if (['user_message', 'tool_result', 'assistant_message'].includes(event.type)) {
      console.log('🔥 SSE Event:', event.type, event.data);
    }

    if (!event.data) {
      console.warn('SSE event received with no data:', event.type);
      return;
    }

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

      case 'complete':
        {
          const streamingMsg = state.messages.find(m =>
            m.role.toLowerCase() === 'assistant' && m.id.startsWith('streaming-')
          );
          if (streamingMsg && streamingMsg.content && !state.currentTurn?.finalResponse) {
            dispatch({ type: 'ADD_FINAL_RESPONSE', payload: {
              role: 'assistant',
              content: streamingMsg.content,
              timestamp: new Date()
            } as StreamingMessage });
          }
          dispatch({ type: 'COMPLETE_STREAMING_TURN' });

          console.log('📄 [SSE complete] Triggering loadNotebookFiles');
          loadNotebookFiles().catch(error => {
            console.error('Failed to refresh notebook files after conversation turn:', error);
          });

          try { window.dispatchEvent(new Event('refresh-notebook-files')); } catch {}

          try {
            const convoMeta = (conversations || []).find(c => c.id === conversationId);
            if (convoMeta && convoMeta.title === DEFAULT_CONVERSATION_TITLE) {
              api.projects.notebooks.conversations
                .generateTitle(projectId, notebookId, conversationId)
                .then(() => {
                  loadConversations().catch(() => {});
                  try { window.dispatchEvent(new Event('refresh-conversations')); } catch {}
                })
                .catch(err => console.warn('Auto title generation failed:', err));
            }
          } catch (e) {
            // Best-effort
          }
        }
        break;

      case 'cancelled':
        console.log('🔥 Stream was cancelled, preserving partial content');
        dispatch({ type: 'FINALIZE_STREAMING_MESSAGE', payload: {} });
        dispatch({ type: 'COMPLETE_STREAMING_TURN' });
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
          dispatch({ type: 'ADD_TOOL_RESULT', payload: {
            toolCallId,
            content: toolContent,
            timestamp: new Date(event.data.timestamp || new Date())
          }});
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
          console.error('Streaming error received:', event.data);

          const errorMessage = event.data?.message || 'An error occurred during the conversation';
          const errorAction = event.data?.action;
          const displayMessage = errorAction ? `${errorMessage}\n\n${errorAction}` : errorMessage;
          const errorType = event.data?.type;
          // Server-populated `code` field. Currently one of:
          //   'local_llm_oom'       — classified CUDA/OOM response body from llama-server
          //   'local_llm_crashed'   — 5xx without OOM markers, or mid-stream socket drop
          //   'local_llm_not_ready' — 400 "no model loaded"; runtime is up but idle
          // See GuideAntsApi/Services/Conversations/StreamingErrorEnvelope.cs.
          const errorCode = event.data?.code;

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

          setCurrentStreamController(null);
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
  }, [loadNotebookFiles, showToast, conversations, loadConversations, projectId, notebookId, conversationId]);
}
