import { describe, it, expect, beforeEach, vi } from 'vitest';

// Unmock ConversationContext to test the real implementation
vi.unmock('../ConversationContext');

// Mock NotebookContext for reducer tests (not used but avoids import issues)
vi.mock('../NotebookContext', () => ({
  useNotebook: () => ({
    loadNotebookFiles: vi.fn().mockResolvedValue(undefined),
  }),
}));

import { reducer } from '../ConversationContext';
import { createInitialState, createMockMessage, createMockAssistantMessage, createMockToolCall, createMockToolResult, createMockStreamingTurn } from '../../test/conversationContextTestUtils';

describe('ConversationContext reducer', () => {
  let initialState: any;

  beforeEach(() => {
    initialState = createInitialState();
  });

  describe('Basic message operations', () => {
    it('SET_MESSAGES should set messages and increment render counter', () => {
      const messages = [createMockMessage(), createMockAssistantMessage()];
      const result = reducer(initialState, { type: 'SET_MESSAGES', payload: messages });

      expect(result.messages).toEqual(messages);
      expect(result._renderCounter).toBe(1);
    });

    it('ADD_MESSAGE should append message and increment render counter', () => {
      const existingMessage = createMockMessage();
      const newMessage = createMockAssistantMessage();
      const state = { ...initialState, messages: [existingMessage] };

      const result = reducer(state, { type: 'ADD_MESSAGE', payload: newMessage });

      expect(result.messages).toEqual([existingMessage, newMessage]);
      expect(result._renderCounter).toBe(1);
    });

    it('UPDATE_MESSAGE should update specific message by id', () => {
      const message1 = createMockMessage({ id: 'msg-1', content: 'Original' });
      const message2 = createMockMessage({ id: 'msg-2', content: 'Other' });
      const state = { ...initialState, messages: [message1, message2] };

      const result = reducer(state, {
        type: 'UPDATE_MESSAGE',
        payload: { id: 'msg-1', updates: { content: 'Updated' } }
      });

      expect(result.messages[0].content).toBe('Updated');
      expect(result.messages[1].content).toBe('Other');
      expect(result._renderCounter).toBe(1);
    });

    it('REMOVE_LAST_TURN should remove complete conversation turn', () => {
      const userMsg = createMockMessage({ role: 'user', content: 'Hello' });
      const assistantMsg = createMockAssistantMessage({ content: 'Hi there' });
      const toolMsg = createMockMessage({ role: 'tool', content: 'Tool result' });
      const state = { ...initialState, messages: [userMsg, assistantMsg, toolMsg] };

      const result = reducer(state, { type: 'REMOVE_LAST_TURN' });

      // Should remove messages from the end until it finds and removes a user message
      expect(result.messages).toEqual([]);
      expect(result._renderCounter).toBe(1);
    });
  });

  describe('Streaming operations', () => {
    it('SET_STREAMING should update streaming state', () => {
      const result = reducer(initialState, { type: 'SET_STREAMING', payload: true });
      expect(result.isStreaming).toBe(true);
    });

    it('START_STREAMING_TURN should initialize streaming turn', () => {
      const result = reducer(initialState, { type: 'START_STREAMING_TURN' });

      expect(result.isStreaming).toBe(true);
      expect(result.currentTurn).toBeDefined();
      expect(result.currentTurn?.id).toMatch(/^turn-\d+-/);
      expect(result.currentTurn?.toolCalls).toEqual([]);
      expect(result.currentTurn?.toolResults).toEqual([]);
      expect(result.streamingProgress.currentPhase).toBe('thinking');
      expect(result.streamingProgress.totalSteps).toBe(1);
      expect(result._renderCounter).toBe(1);
    });

    it('APPEND_TOKEN should create streaming message if none exists', () => {
      const result = reducer(initialState, { type: 'APPEND_TOKEN', payload: 'Hello' });

      expect(result.messages).toHaveLength(1);
      expect(result.messages[0].content).toBe('Hello');
      expect(result.messages[0].id).toMatch(/^streaming-/);
      expect(result.messages[0].role).toBe('assistant');
      expect(result._renderCounter).toBe(1);
    });

    it('APPEND_TOKEN should append to existing streaming message', () => {
      const streamingMsg = createMockAssistantMessage({ 
        id: 'streaming-123', 
        content: 'Hello ' 
      });
      const state = { ...initialState, messages: [streamingMsg] };

      const result = reducer(state, { type: 'APPEND_TOKEN', payload: 'world!' });

      expect(result.messages).toHaveLength(1);
      expect(result.messages[0].content).toBe('Hello world!');
      expect(result._renderCounter).toBe(1);
    });

    it('APPEND_TOKEN should handle JSON payload format', () => {
      const payload = JSON.stringify({ contentDelta: 'test content' });
      const result = reducer(initialState, { type: 'APPEND_TOKEN', payload });

      expect(result.messages[0].content).toBe('test content');
    });

    it('FINALIZE_STREAMING_MESSAGE should convert streaming message to final', () => {
      const streamingMsg = {
        ...createMockAssistantMessage({ 
          id: 'streaming-123', 
          content: 'Final content'
        }),
        streaming: true
      };
      const state = { ...initialState, messages: [streamingMsg] };

      const result = reducer(state, { type: 'FINALIZE_STREAMING_MESSAGE' });

      expect(result.messages[0].id).toMatch(/^msg-/);
      expect(result.messages[0].content).toBe('Final content');
      expect((result.messages[0] as any).streaming).toBe(false);
    });

    it('FINALIZE_STREAMING_MESSAGE should preserve content when cancelled', () => {
      const streamingMsg = {
        ...createMockAssistantMessage({ 
          id: 'streaming-123', 
          content: ''
        }),
        streaming: true
      };
      const state = { ...initialState, messages: [streamingMsg] };

      const result = reducer(state, { type: 'FINALIZE_STREAMING_MESSAGE' });

      expect(result.messages[0].content).toBe('[Stream was cancelled]');
    });
  });

  describe('Tool call operations', () => {
    it('SET_TOOL_CALLS should merge new tool calls without duplicates', () => {
      const existingTurn = createMockStreamingTurn({
        toolCalls: [createMockToolCall({ id: 'tool-1', name: 'existing' })]
      });
      const state = { ...initialState, currentTurn: existingTurn };

      const newToolCalls = [
        createMockToolCall({ id: 'tool-1', name: 'existing' }), // duplicate
        createMockToolCall({ id: 'tool-2', name: 'new' })       // new
      ];

      const result = reducer(state, { type: 'SET_TOOL_CALLS', payload: newToolCalls });

      expect(result.currentTurn?.toolCalls).toHaveLength(2);
      expect(result.currentTurn?.toolCalls.map(tc => tc.id)).toEqual(['tool-1', 'tool-2']);
      expect(result.isStreamingToolCalls).toBe(true);
    });

    it('ENSURE_TOOL_CALL should create new turn if none exists', () => {
      const toolCall = createMockToolCall({ id: 'tool-1', name: 'test' });
      const result = reducer(initialState, { type: 'ENSURE_TOOL_CALL', payload: toolCall });

      expect(result.currentTurn).toBeDefined();
      expect(result.currentTurn?.toolCalls).toEqual([toolCall]);
    });

    it('ENSURE_TOOL_CALL should add tool call to existing turn', () => {
      const existingTurn = createMockStreamingTurn({
        toolCalls: [createMockToolCall({ id: 'tool-1' })]
      });
      const state = { ...initialState, currentTurn: existingTurn };
      const newToolCall = createMockToolCall({ id: 'tool-2' });

      const result = reducer(state, { type: 'ENSURE_TOOL_CALL', payload: newToolCall });

      expect(result.currentTurn?.toolCalls).toHaveLength(2);
      expect(result.currentTurn?.toolCalls[1]).toEqual(newToolCall);
    });

    it('ENSURE_TOOL_CALL should not duplicate existing tool call', () => {
      const toolCall = createMockToolCall({ id: 'tool-1' });
      const existingTurn = createMockStreamingTurn({ toolCalls: [toolCall] });
      const state = { ...initialState, currentTurn: existingTurn };

      const result = reducer(state, { type: 'ENSURE_TOOL_CALL', payload: toolCall });

      expect(result.currentTurn?.toolCalls).toHaveLength(1);
      expect(result).toBe(state); // Should return same state if no change
    });

    it('ADD_TOOL_RESULT should update tool call status and add result', () => {
      const toolCall = createMockToolCall({ id: 'tool-1', status: 'executing' });
      const existingTurn = createMockStreamingTurn({ toolCalls: [toolCall] });
      const state = { ...initialState, currentTurn: existingTurn };

      const toolResult = createMockToolResult('tool-1', { content: 'Success!' });

      const result = reducer(state, { type: 'ADD_TOOL_RESULT', payload: toolResult });

      expect(result.currentTurn?.toolCalls[0].status).toBe('completed');
      expect(result.currentTurn?.toolResults).toHaveLength(1);
      expect(result.currentTurn?.toolResults[0]).toEqual(toolResult);
      expect(result.isStreamingToolCalls).toBe(true);
    });

    it('ADD_TOOL_ERROR should mark tool call as error', () => {
      const toolCall = createMockToolCall({ id: 'tool-1', status: 'executing' });
      const existingTurn = createMockStreamingTurn({ toolCalls: [toolCall] });
      const state = { ...initialState, currentTurn: existingTurn };

      const errorPayload = {
        toolCallId: 'tool-1',
        content: 'Tool failed',
        timestamp: new Date()
      };

      const result = reducer(state, { type: 'ADD_TOOL_ERROR', payload: errorPayload });

      expect(result.currentTurn?.toolCalls[0].status).toBe('error');
      expect(result.currentTurn?.toolResults).toHaveLength(1);
      expect(result.currentTurn?.toolResults[0].isError).toBe(true);
      expect(result.currentTurn?.toolResults[0].content).toBe('Tool failed');
    });
  });

  describe('Turn completion', () => {
    it('ADD_FINAL_RESPONSE should add final response to turn', () => {
      const existingTurn = createMockStreamingTurn();
      const state = { ...initialState, currentTurn: existingTurn };

      const finalResponse = {
        role: 'assistant',
        content: 'Here is my final answer',
        timestamp: new Date()
      };

      const result = reducer(state, { type: 'ADD_FINAL_RESPONSE', payload: finalResponse });

      expect(result.currentTurn?.finalResponse).toEqual(finalResponse);
      expect(result.isStreamingThinking).toBe(false);
    });

    it('COMPLETE_STREAMING_TURN should convert turn to messages', () => {
      const userMessage = { role: 'user', content: 'Hello', timestamp: new Date() };
      const toolCall = createMockToolCall({ id: 'tool-1', status: 'completed' });
      const toolResult = createMockToolResult('tool-1');
      const finalResponse = { role: 'assistant', content: 'Done!', timestamp: new Date() };

      const completeTurn = createMockStreamingTurn({
        userMessage,
        toolCalls: [toolCall],
        toolResults: [toolResult],
        finalResponse
      });

      const state = { 
        ...initialState, 
        currentTurn: completeTurn,
        isStreaming: true,
        isStreamingThinking: true,
        isStreamingToolCalls: true
      };

      const result = reducer(state, { type: 'COMPLETE_STREAMING_TURN' });

      expect(result.messages.length).toBeGreaterThan(0);
      expect(result.currentTurn).toBeUndefined();
      expect(result.isStreaming).toBe(false);
      expect(result.isStreamingThinking).toBe(false);
      expect(result.isStreamingToolCalls).toBe(false);
      expect(result.streamingProgress.currentPhase).toBe('complete');
      expect(result._justCompletedStreaming).toBe(true);
    });

    it('COMPLETE_STREAMING_TURN should reset cancelling state (bug fix)', () => {
      const completeTurn = createMockStreamingTurn({
        finalResponse: { role: 'assistant', content: 'Partial response', timestamp: new Date() }
      });

      const stateWithCancelling = {
        ...initialState,
        _isCancelling: true,
        isStreaming: true,
        currentTurn: completeTurn,
        messages: [
          {
            id: 'streaming-123',
            role: 'assistant',
            content: 'Partial response',
            created: new Date().toISOString(),
            isEdited: false
          }
        ]
      };

      const result = reducer(stateWithCancelling, { type: 'COMPLETE_STREAMING_TURN' });

      // Critical: cancelling state must be reset to prevent locked conversation
      expect(result._isCancelling).toBe(false);
      expect(result.isStreaming).toBe(false);
      expect(result.streamingMode).toBe('at-rest');
      expect(result.currentTurn).toBeUndefined();
    });
  });

  describe('Streaming assistant steps', () => {
    it('SET_TOOL_CALLS should capture the current assistant step', () => {
      const streamingMessage = createMockAssistantMessage({
        id: 'streaming-1',
        content: 'Step one content',
      });
      const existingTurn = createMockStreamingTurn();
      const state = { ...initialState, currentTurn: existingTurn, messages: [streamingMessage] };

      const toolCalls = [createMockToolCall({ id: 'tool-1', status: 'executing' })];
      const result = reducer(state, { type: 'SET_TOOL_CALLS', payload: toolCalls });

      expect(result.currentTurn?.assistantStepChunks).toHaveLength(1);
      expect(result.currentTurn?.assistantStepChunks?.[0].content).toBe('Step one content');
      expect(result.currentTurn?.assistantStepSection?.content).toBe('Step one content');
    });
  });

  describe('Assistant and UI state', () => {
    it('SET_ASSISTANT should update selected assistant', () => {
      const result = reducer(initialState, { type: 'SET_ASSISTANT', payload: 'GPT-4' });
      expect(result.selectedAssistant).toBe('GPT-4');
    });

    it('SET_DRAFT should update draft content', () => {
      const result = reducer(initialState, { type: 'SET_DRAFT', payload: 'Draft message' });
      expect(result.draftUserContent).toBe('Draft message');
    });

    it('SET_EDITING should set editing message id', () => {
      const result = reducer(initialState, { type: 'SET_EDITING', payload: 'msg-123' });
      expect(result.editingAssistantId).toBe('msg-123');
    });

    it('SET_EDIT_ERROR should set edit error', () => {
      const result = reducer(initialState, { type: 'SET_EDIT_ERROR', payload: 'Edit failed' });
      expect(result.editError).toBe('Edit failed');
    });

    it('SET_EDIT_LOADING should set edit loading state', () => {
      const result = reducer(initialState, { type: 'SET_EDIT_LOADING', payload: true });
      expect(result.isEditLoading).toBe(true);
    });

    it('SET_ASSISTANTS should set assistants list', () => {
      const assistants = [{ name: 'GPT-4', model: 'gpt-4' }];
      const result = reducer(initialState, { type: 'SET_ASSISTANTS', payload: assistants });
      expect(result.assistants).toEqual(assistants);
    });

    it('SET_CONVERSATION_STARTERS should set conversation starters', () => {
      const starters = ['Hello', 'How can I help?'];
      const result = reducer(initialState, { type: 'SET_CONVERSATION_STARTERS', payload: starters });
      expect(result.conversationStarters).toEqual(starters);
    });

    it('SET_INITIALIZED should set initialization state', () => {
      const result = reducer(initialState, { type: 'SET_INITIALIZED', payload: true });
      expect(result._isInitialized).toBe(true);
    });

    it('SET_CANCELLING should set cancelling state', () => {
      const result = reducer(initialState, { type: 'SET_CANCELLING', payload: true });
      expect(result._isCancelling).toBe(true);
    });

    it('SET_STREAMING_ERROR should set streaming error', () => {
      const result = reducer(initialState, { type: 'SET_STREAMING_ERROR', payload: 'Stream error' });
      expect(result.streamingError).toBe('Stream error');
    });
  });

  describe('Attachment operations', () => {
    it('ADD_ATTACHMENT should add pending attachment', () => {
      const attachment = { notebookFileId: 'file-1', fileName: 'test.txt', uploadType: 'text' };
      const result = reducer(initialState, { type: 'ADD_ATTACHMENT', payload: attachment });

      expect(result.pendingAttachments).toHaveLength(1);
      expect(result.pendingAttachments?.[0]).toEqual(attachment);
    });

    it('REMOVE_ATTACHMENT should remove attachment by fileId', () => {
      const attachment1 = { notebookFileId: 'file-1', fileName: 'test1.txt', uploadType: 'text' };
      const attachment2 = { notebookFileId: 'file-2', fileName: 'test2.txt', uploadType: 'text' };
      const state = { ...initialState, pendingAttachments: [attachment1, attachment2] };

      const result = reducer(state, { type: 'REMOVE_ATTACHMENT', payload: 'file-1' });

      expect(result.pendingAttachments).toHaveLength(1);
      expect(result.pendingAttachments?.[0].notebookFileId).toBe('file-2');
    });

    it('CLEAR_ATTACHMENTS should clear all attachments', () => {
      const attachments = [
        { notebookFileId: 'file-1', fileName: 'test1.txt', uploadType: 'text' },
        { notebookFileId: 'file-2', fileName: 'test2.txt', uploadType: 'text' }
      ];
      const state = { ...initialState, pendingAttachments: attachments };

      const result = reducer(state, { type: 'CLEAR_ATTACHMENTS' });

      expect(result.pendingAttachments).toEqual([]);
    });
  });

  describe('User profiles', () => {
    it('SET_USER_PROFILES should merge user profiles', () => {
      const existingProfiles = { 'user-1': { id: 'user-1', name: 'John' } };
      const state = { ...initialState, userProfiles: existingProfiles };

      const newProfiles = { 'user-2': { id: 'user-2', name: 'Jane' } };
      const result = reducer(state, { type: 'SET_USER_PROFILES', payload: newProfiles });

      expect(result.userProfiles).toEqual({
        'user-1': { id: 'user-1', name: 'John' },
        'user-2': { id: 'user-2', name: 'Jane' }
      });
    });
  });

  describe('Edge cases and error handling', () => {
    it('should handle actions with no current turn gracefully', () => {
      const actions = [
        { type: 'ADD_TOOL_RESULT', payload: createMockToolResult('tool-1') },
        { type: 'ADD_FINAL_RESPONSE', payload: { role: 'assistant', content: 'test' } },
        { type: 'COMPLETE_STREAMING_TURN' }
      ];

      actions.forEach(action => {
        const result = reducer(initialState, action as any);
        expect(result).toBe(initialState); // Should return unchanged state
      });
    });

    it('should handle unknown action types gracefully', () => {
      const result = reducer(initialState, { type: 'UNKNOWN_ACTION' as any });
      expect(result).toBe(initialState);
    });

    it('should handle SET_MESSAGES with null/undefined payload', () => {
      const result = reducer(initialState, { type: 'SET_MESSAGES', payload: null });
      expect(result.messages).toEqual([]);
    });

    it('should handle APPEND_TOKEN with empty payload', () => {
      const result = reducer(initialState, { type: 'APPEND_TOKEN', payload: '' });
      expect(result).toBe(initialState); // Should not create empty message
    });
  });
}); 