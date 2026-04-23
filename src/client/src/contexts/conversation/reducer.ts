import type { MessageDto, EnhancedConversationState, StreamingProgress, StreamingMessage, StreamingToolResult } from '../../types/conversation';
import type { ActionType, ExtendedConversationState, StreamingMode } from './types';
import { generateTurnId, appendAssistantStepChunk, convertTurnToMessages, calculateStreamingProgress } from './streamingHelpers';

export const initialState: EnhancedConversationState = {
  messages: [],
  isStreaming: false,
  streamingMode: 'at-rest',
  activeStreamingUser: undefined,
  selectedAssistant: null,
  draftUserContent: '',
  editingAssistantId: undefined,
  currentTurn: undefined,
  streamingProgress: {
    currentPhase: 'complete',
    completedSteps: 0,
    totalSteps: 0
  },
  isStreamingToolCalls: false,
  isStreamingThinking: false,
};

export function reducer(state: ExtendedConversationState, action: ActionType): ExtendedConversationState {
  switch (action.type) {
    case 'SET_MESSAGES':
      return { ...state, messages: action.payload || [], _renderCounter: (state._renderCounter || 0) + 1 };

    case 'ADD_MESSAGE':
      return { ...state, messages: [...state.messages, action.payload], _renderCounter: (state._renderCounter || 0) + 1 };

    case 'UPDATE_MESSAGE':
      return {
        ...state,
        messages: state.messages.map(msg =>
          msg.id === action.payload.id ? { ...msg, ...action.payload.updates } : msg
        ),
        _renderCounter: (state._renderCounter || 0) + 1
      };

    case 'REMOVE_LAST_TURN': {
      const msgs = [...state.messages];
      while (msgs.length > 0) {
        const last = msgs[msgs.length - 1];
        msgs.pop();
        if (last.role.toLowerCase() === 'user') {
          break;
        }
      }
      return { ...state, messages: msgs, _renderCounter: (state._renderCounter || 0) + 1 };
    }

    case 'SET_STREAMING':
      return { ...state, isStreaming: action.payload };

    case 'SET_STREAMING_MODE': {
      const mode = action.payload.mode as StreamingMode;
      const newState = {
        ...state,
        streamingMode: mode,
        isStreaming: mode !== 'at-rest',
        activeStreamingUser: action.payload.activeUser
      };

      if (mode === 'observing' && !state.currentTurn) {
        return {
          ...newState,
          currentTurn: {
            id: generateTurnId(),
            assistantStepSection: undefined,
            assistantStepChunks: [],
            toolCalls: [],
            toolResults: [],
            finalResponse: undefined,
            startTime: new Date(),
            isComplete: false
          }
        };
      }

      return newState;
    }

    case 'SET_ASSISTANT':
      return { ...state, selectedAssistant: action.payload };

    case 'SET_DRAFT':
      return { ...state, draftUserContent: action.payload };

    case 'SET_EDITING':
      return { ...state, editingAssistantId: action.payload };

    case 'SET_EDIT_ERROR':
      return { ...state, editError: action.payload };

    case 'SET_EDIT_LOADING':
      return { ...state, isEditLoading: action.payload };

    case 'SET_ASSISTANTS':
      return { ...state, assistants: action.payload };

    case 'SET_CONVERSATION_STARTERS':
      return { ...state, conversationStarters: action.payload };

    case 'SET_INITIALIZED':
      return { ...state, _isInitialized: action.payload };

    case 'SET_JUST_COMPLETED_STREAMING':
      return {
        ...state,
        _justCompletedStreaming: action.payload,
        isStreaming: action.payload ? false : state.isStreaming
      };

    case 'SET_CANCELLING':
      return { ...state, _isCancelling: action.payload };

    case 'SET_USER_PROFILES':
      return { ...state, userProfiles: { ...(state.userProfiles || {}), ...action.payload } };

    case 'SET_NOTEBOOK_TEMPLATE':
      return { ...state, notebookTemplate: action.payload };

    case 'SET_STREAMING_ERROR':
      return { ...state, streamingError: action.payload };

    case 'APPEND_TOKEN': {
      let parsedPayload = action.payload;
      if (typeof action.payload === 'string') {
        try {
          parsedPayload = JSON.parse(action.payload);
        } catch (e) {
          parsedPayload = action.payload;
        }
      }

      const tokenPayload = typeof parsedPayload === 'object' && parsedPayload?.contentDelta
        ? parsedPayload.contentDelta
        : (typeof parsedPayload === 'string' ? parsedPayload : '');

      if (!tokenPayload) {
        console.warn('APPEND_TOKEN: No token content provided', action.payload);
        return state;
      }

      const shouldClearFirst = state.currentTurn?.pendingCellClear === true;

      const streamingMessageIndex = state.messages.findIndex(m =>
        m.role.toLowerCase() === 'assistant' && m.id.startsWith('streaming-')
      );

      if (streamingMessageIndex !== -1) {
        const currentMessage = state.messages[streamingMessageIndex];
        const newContent = shouldClearFirst
          ? tokenPayload
          : `${currentMessage.content ?? ''}${tokenPayload}`;

        return {
          ...state,
          _renderCounter: (state._renderCounter || 0) + 1,
          currentTurn: state.currentTurn ? {
            ...state.currentTurn,
            pendingCellClear: false
          } : undefined,
          isStreamingThinking: true,
          messages: state.messages.map((m, i) =>
            i === streamingMessageIndex
              ? { ...m, content: newContent }
              : m
          )
        };
      } else {
        const newMessage: MessageDto = {
          id: `streaming-${Date.now()}`,
          role: 'assistant',
          content: tokenPayload,
          created: new Date().toISOString(),
          isEdited: false,
          assistantName: state.selectedAssistant || (() => { throw new Error('No assistant selected for streaming message'); })()
        } as MessageDto;
        return {
          ...state,
          messages: [...state.messages, newMessage],
          isStreamingThinking: true,
          _renderCounter: (state._renderCounter || 0) + 1,
          currentTurn: state.currentTurn ? {
            ...state.currentTurn,
            pendingCellClear: false
          } : undefined
        };
      }
    }

    case 'FINALIZE_STREAMING_MESSAGE':
      console.log('FINALIZE_STREAMING_MESSAGE reducer called');
      return {
        ...state,
        messages: state.messages.map(m => {
          if (m.id.startsWith('streaming-') && m.role.toLowerCase() === 'assistant') {
            const incoming = (action.payload || {}) as Partial<MessageDto>;
            const finalizedMessage = {
              ...m,
              streaming: false,
              role: incoming.role ?? m.role,
              assistantName: incoming.assistantName ?? m.assistantName,
              toolCalls: incoming.toolCalls ?? m.toolCalls,
              content: (incoming.content !== undefined ? incoming.content : m.content)
            } as MessageDto;
            console.log('🔥 Finalizing streaming message with content:', finalizedMessage.content);
            return finalizedMessage;
          }
          return m;
        })
      };

    case 'CONVERT_STREAMING_IDS':
      return {
        ...state,
        messages: state.messages.map(m => {
          if (m.role.toLowerCase() === 'assistant' && m.id.startsWith('streaming-')) {
            return {
              ...m,
              id: `msg-${Date.now()}-assistant`,
              streaming: false as any
            } as MessageDto;
          }
          return m;
        })
      };

    case 'CLEAR_STREAMING_CELL':
      return {
        ...state,
        messages: state.messages.map(m => {
          if (m.role.toLowerCase() === 'assistant' && m.id.startsWith('streaming-')) {
            return { ...m, content: '' };
          }
          return m;
        }),
        _renderCounter: (state._renderCounter || 0) + 1
      };

    case 'SET_PENDING_CELL_CLEAR':
      if (!state.currentTurn) return state;
      return {
        ...state,
        currentTurn: {
          ...state.currentTurn,
          pendingCellClear: true
        }
      };

    case 'START_STREAMING_TURN': {
      const newTurnId = generateTurnId();
      console.log('🔥 START_STREAMING_TURN: Creating new turn with ID:', newTurnId);
      return {
        ...state,
        isStreaming: true,
        currentTurn: {
          id: newTurnId,
          toolCalls: [],
          toolResults: [],
          assistantStepChunks: [],
          startTime: new Date(),
          isComplete: false
        },
        streamingProgress: {
          currentPhase: 'thinking',
          completedSteps: 0,
          totalSteps: 1
        },
        _renderCounter: (state._renderCounter || 0) + 1
      };
    }

    case 'SET_TOOL_CALLS': {
      if (!state.currentTurn) return state;

      console.log('🔥 SET_TOOL_CALLS: Setting tool calls', {
        existingCount: state.currentTurn.toolCalls.length,
        newCount: action.payload.length,
        newToolIds: action.payload.map((tc: any) => tc.id)
      });

      const existingToolCalls = state.currentTurn.toolCalls;
      const newToolCalls = action.payload;

      const mergedToolCalls = [...existingToolCalls];
      newToolCalls.forEach((newTc: any) => {
        const exists = existingToolCalls.find(tc => tc.id === newTc.id);
        if (!exists) {
          console.log('🔥 SET_TOOL_CALLS: Adding new tool call:', newTc.id, newTc.name);
          mergedToolCalls.push(newTc);
        }
      });

      const updatedTurn = {
        ...state.currentTurn,
        toolCalls: mergedToolCalls,
        pendingCellClear: true
      };

      console.log('🔥 SET_TOOL_CALLS: Updated turn with merged tool calls');

      const streamingCellForSteps = state.messages.find(m =>
        m.role.toLowerCase() === 'assistant' && m.id.startsWith('streaming-')
      );
      const assistantContent = streamingCellForSteps?.content ?? '';
      const newToolTimestamp = newToolCalls.length > 0 ? newToolCalls[0].timestamp : new Date();
      const stepTimestamp = new Date(newToolTimestamp.getTime() - 1);
      const updatedAssistantStepChunks = appendAssistantStepChunk(
        state.currentTurn.assistantStepChunks ?? [],
        assistantContent,
        stepTimestamp
      );

      const updatedTurnWithSteps = {
        ...updatedTurn,
        assistantStepChunks: updatedAssistantStepChunks,
        assistantStepSection: updatedTurn.assistantStepSection ?? (updatedAssistantStepChunks.length > 0
          ? {
            content: updatedAssistantStepChunks[0].content,
            toolCalls: updatedTurn.toolCalls,
            isVisible: true
          }
          : undefined)
      };

      return {
        ...state,
        isStreamingThinking: false,
        isStreamingToolCalls: true,
        currentTurn: updatedTurnWithSteps,
        streamingProgress: calculateStreamingProgress(updatedTurnWithSteps),
        _renderCounter: (state._renderCounter || 0) + 1
      };
    }

    case 'ENSURE_TOOL_CALL': {
      console.log('🔥 ENSURE_TOOL_CALL reducer called with:', {
        hasCurrentTurn: !!state.currentTurn,
        currentTurnId: state.currentTurn?.id,
        toolCallId: action.payload.id,
        toolCallName: action.payload.name
      });

      if (!state.currentTurn) {
        console.log('🔥 ENSURE_TOOL_CALL: Creating new turn with tool call');
        const newTurn = {
          id: generateTurnId(),
          toolCalls: [action.payload],
          toolResults: [],
          startTime: new Date(),
          isComplete: false
        };
        const newTurnProgress = calculateStreamingProgress(newTurn);
        console.log('🔥 ENSURE_TOOL_CALL: New turn created with progress:', newTurnProgress);
        return {
          ...state,
          currentTurn: newTurn,
          streamingProgress: newTurnProgress,
          _renderCounter: (state._renderCounter || 0) + 1
        };
      }

      const existingCall = state.currentTurn.toolCalls.find(tc => tc.id === action.payload.id);
      if (existingCall) {
        console.log('🔥 ENSURE_TOOL_CALL: Tool call already exists, no change needed');
        return state;
      }

      console.log('🔥 ENSURE_TOOL_CALL: Adding new tool call to existing turn');
      const turnWithNewCall = {
        ...state.currentTurn,
        toolCalls: [...state.currentTurn.toolCalls, action.payload]
      };
      const updatedProgress = calculateStreamingProgress(turnWithNewCall);
      console.log('🔥 ENSURE_TOOL_CALL: Updated progress:', updatedProgress);
      return {
        ...state,
        currentTurn: turnWithNewCall,
        streamingProgress: updatedProgress,
        _renderCounter: (state._renderCounter || 0) + 1
      };
    }

    case 'ADD_TOOL_RESULT': {
      console.log('🔥 ADD_TOOL_RESULT reducer called with:', {
        hasCurrentTurn: !!state.currentTurn,
        currentTurnId: state.currentTurn?.id,
        toolCallsCount: state.currentTurn?.toolCalls?.length,
        toolResultsCount: state.currentTurn?.toolResults?.length,
        payload: action.payload
      });

      if (!state.currentTurn) {
        console.warn('🔥 ADD_TOOL_RESULT: No currentTurn exists, returning unchanged state');
        return state;
      }

      const toolResultPayload = action.payload as StreamingToolResult;
      console.log('🔥 ADD_TOOL_RESULT: Processing tool result for toolCallId:', toolResultPayload.toolCallId);

      const updatedToolCalls = state.currentTurn.toolCalls.map(tc =>
        tc.id === toolResultPayload.toolCallId
          ? { ...tc, status: toolResultPayload.isError ? 'error' as const : 'completed' as const, result: toolResultPayload }
          : tc
      );

      const turnWithToolResult = {
        ...state.currentTurn,
        toolCalls: updatedToolCalls,
        toolResults: [...state.currentTurn.toolResults, toolResultPayload]
      };

      const newProgress = calculateStreamingProgress(turnWithToolResult);
      console.log('🔥 ADD_TOOL_RESULT: New progress calculated:', newProgress);

      return {
        ...state,
        isStreamingToolCalls: true,
        currentTurn: turnWithToolResult,
        streamingProgress: newProgress,
        _renderCounter: (state._renderCounter || 0) + 1
      };
    }

    case 'ADD_FINAL_RESPONSE': {
      if (!state.currentTurn) return state;
      const turnWithFinalResponse = {
        ...state.currentTurn,
        finalResponse: action.payload as StreamingMessage
      };
      return {
        ...state,
        isStreamingThinking: false,
        currentTurn: turnWithFinalResponse,
        streamingProgress: calculateStreamingProgress(turnWithFinalResponse)
      };
    }

    case 'COMPLETE_STREAMING_TURN': {
      if (!state.currentTurn) return state;

      const streamingCell = state.messages.find(m =>
        m.role.toLowerCase() === 'assistant' && m.id.startsWith('streaming-')
      );
      const cellIsEmpty = !streamingCell?.content;

      let assistantStepChunksForWorkflow = state.currentTurn.assistantStepChunks || [];
      let finalCellContent = streamingCell?.content || '';

      if (cellIsEmpty && assistantStepChunksForWorkflow.length > 0) {
        const lastChunk = assistantStepChunksForWorkflow[assistantStepChunksForWorkflow.length - 1];
        finalCellContent = lastChunk.content;
        assistantStepChunksForWorkflow = assistantStepChunksForWorkflow.slice(0, -1);
      }

      const turnForConversion = {
        ...state.currentTurn,
        assistantStepChunks: assistantStepChunksForWorkflow
      };

      const newMessages = convertTurnToMessages(turnForConversion);

      const finalizedMessages = state.messages.map(m => {
        if (m.role.toLowerCase() === 'assistant' && m.id.startsWith('streaming-')) {
          return {
            ...m,
            id: `msg-${Date.now()}-assistant`,
            content: finalCellContent,
            streaming: false
          };
        }
        return m;
      });

      const toolMessages = newMessages.filter(m => m.role.toLowerCase() === 'tool');

      return {
        ...state,
        messages: [...finalizedMessages, ...toolMessages],
        currentTurn: undefined,
        streamingMode: 'at-rest',
        activeStreamingUser: undefined,
        isStreaming: false,
        isStreamingThinking: false,
        isStreamingToolCalls: false,
        streamingProgress: {
          currentPhase: 'complete',
          completedSteps: 0,
          totalSteps: 0
        },
        _justCompletedStreaming: true,
        _isCancelling: false
      };
    }

    case 'UPDATE_STREAMING_PROGRESS':
      return {
        ...state,
        streamingProgress: action.payload as StreamingProgress
      };

    case 'ADD_TOOL_ERROR': {
      if (!state.currentTurn) return state;
      const errorPayload = action.payload as { toolCallId: string; content: string; timestamp: Date };

      const errorUpdatedToolCalls = state.currentTurn.toolCalls.map(tc =>
        tc.id === errorPayload.toolCallId
          ? { ...tc, status: 'error' as const }
          : tc
      );

      const turnWithError = {
        ...state.currentTurn,
        toolCalls: errorUpdatedToolCalls,
        toolResults: [...state.currentTurn.toolResults, {
          toolCallId: errorPayload.toolCallId,
          content: errorPayload.content,
          isError: true,
          timestamp: errorPayload.timestamp
        }]
      };

      return {
        ...state,
        currentTurn: turnWithError,
        streamingProgress: calculateStreamingProgress(turnWithError)
      };
    }

    case 'ADD_ATTACHMENT':
      return { ...state, pendingAttachments: [...(state.pendingAttachments || []), action.payload] };

    case 'REMOVE_ATTACHMENT':
      return { ...state, pendingAttachments: (state.pendingAttachments || []).filter(a => a.notebookFileId !== action.payload) };

    case 'CLEAR_ATTACHMENTS':
      return { ...state, pendingAttachments: [] };

    default:
      return state;
  }
}
