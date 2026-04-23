import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import {
  generateTurnId,
  normalizeStreamingMatch,
  appendAssistantStepChunk,
  convertTurnToMessages,
  groupToolCallsByExecution,
  calculateStreamingProgress,
} from '../conversation/streamingHelpers';
import type { StreamingTurn, StreamingToolCall, StreamingToolResult } from '../../types/conversation';

function makeTurn(overrides: Partial<StreamingTurn> = {}): StreamingTurn {
  return {
    id: 'turn-1',
    toolCalls: [],
    toolResults: [],
    startTime: new Date('2025-01-01T00:00:00Z'),
    isComplete: false,
    ...overrides,
  };
}

function makeToolCall(overrides: Partial<StreamingToolCall> = {}): StreamingToolCall {
  return {
    id: 'tc-1',
    name: 'test_tool',
    arguments: '{}',
    status: 'pending',
    timestamp: new Date('2025-01-01T00:00:00Z'),
    ...overrides,
  };
}

describe('streamingHelpers', () => {
  describe('generateTurnId', () => {
    it('returns a string prefixed with "turn-"', () => {
      const id = generateTurnId();
      expect(id).toMatch(/^turn-\d+-[a-z0-9]+$/);
    });

    it('returns unique IDs on successive calls', () => {
      const ids = new Set(Array.from({ length: 20 }, () => generateTurnId()));
      expect(ids.size).toBe(20);
    });
  });

  describe('normalizeStreamingMatch', () => {
    it('collapses whitespace and trims', () => {
      expect(normalizeStreamingMatch('  hello   world  ')).toBe('hello world');
    });

    it('handles newlines and tabs', () => {
      expect(normalizeStreamingMatch('a\n\tb')).toBe('a b');
    });

    it('returns empty string for whitespace-only input', () => {
      expect(normalizeStreamingMatch('   ')).toBe('');
    });
  });

  describe('appendAssistantStepChunk', () => {
    it('appends a new chunk to an empty array', () => {
      const ts = new Date();
      const result = appendAssistantStepChunk([], 'hello', ts);
      expect(result).toEqual([{ content: 'hello', timestamp: ts }]);
    });

    it('ignores whitespace-only content', () => {
      expect(appendAssistantStepChunk([], '   ', new Date())).toEqual([]);
    });

    it('ignores duplicate content (after normalization)', () => {
      const ts = new Date();
      const existing = [{ content: 'hello world', timestamp: ts }];
      const result = appendAssistantStepChunk(existing, '  hello   world  ', new Date());
      expect(result).toBe(existing);
    });

    it('appends when content differs', () => {
      const ts = new Date();
      const existing = [{ content: 'hello', timestamp: ts }];
      const result = appendAssistantStepChunk(existing, 'world', new Date());
      expect(result).toHaveLength(2);
      expect(result[1].content).toBe('world');
    });
  });

  describe('convertTurnToMessages', () => {
    it('returns empty array for a turn with no content', () => {
      expect(convertTurnToMessages(makeTurn())).toEqual([]);
    });

    it('converts a turn with only assistantStepSection (no tool calls) into a single assistant message', () => {
      const turn = makeTurn({
        assistantStepSection: { content: 'thinking...', toolCalls: [], isVisible: true },
      });
      const msgs = convertTurnToMessages(turn);
      expect(msgs).toHaveLength(1);
      expect(msgs[0].role).toBe('assistant');
      expect(msgs[0].content).toBe('thinking...');
    });

    it('converts a turn with tool calls into assistant-tools + tool result messages', () => {
      const tc = makeToolCall({ id: 'tc-1', name: 'search' });
      const tr: StreamingToolResult = {
        toolCallId: 'tc-1',
        content: 'found it',
        isError: false,
        timestamp: new Date('2025-01-01T00:01:00Z'),
      };
      const turn = makeTurn({
        toolCalls: [tc],
        toolResults: [tr],
        assistantStepSection: { content: 'let me search', toolCalls: [tc], isVisible: true },
      });

      const msgs = convertTurnToMessages(turn);
      expect(msgs.length).toBeGreaterThanOrEqual(2);
      expect(msgs[0].role).toBe('assistant');
      expect(msgs[0].toolCalls).toHaveLength(1);
      expect(msgs[1].role).toBe('tool');
      expect(msgs[1].content).toBe('found it');
    });

    it('includes the final response when present', () => {
      const turn = makeTurn({
        toolCalls: [makeToolCall()],
        toolResults: [],
        finalResponse: {
          role: 'assistant',
          content: 'Here is my answer',
          timestamp: new Date('2025-01-01T00:02:00Z'),
        },
      });

      const msgs = convertTurnToMessages(turn);
      const final = msgs[msgs.length - 1];
      expect(final.role).toBe('assistant');
      expect(final.content).toBe('Here is my answer');
    });

    it('does not emit a final-response message when finalResponse.content is empty', () => {
      const turn = makeTurn({
        finalResponse: {
          role: 'assistant',
          content: '',
          timestamp: new Date(),
        },
      });
      expect(convertTurnToMessages(turn)).toEqual([]);
    });
  });

  describe('groupToolCallsByExecution', () => {
    it('returns empty array for no tool calls', () => {
      expect(groupToolCallsByExecution([])).toEqual([]);
    });

    it('groups tool calls within 1-second window', () => {
      const base = new Date('2025-01-01T00:00:00Z');
      const calls = [
        { id: 'a', timestamp: base },
        { id: 'b', timestamp: new Date(base.getTime() + 500) },
        { id: 'c', timestamp: new Date(base.getTime() + 800) },
      ];
      const groups = groupToolCallsByExecution(calls);
      expect(groups).toHaveLength(1);
      expect(groups[0]).toHaveLength(3);
    });

    it('splits groups when gap exceeds 1 second', () => {
      const base = new Date('2025-01-01T00:00:00Z');
      const calls = [
        { id: 'a', timestamp: base },
        { id: 'b', timestamp: new Date(base.getTime() + 500) },
        { id: 'c', timestamp: new Date(base.getTime() + 2000) },
      ];
      const groups = groupToolCallsByExecution(calls);
      expect(groups).toHaveLength(2);
      expect(groups[0]).toHaveLength(2);
      expect(groups[1]).toHaveLength(1);
    });

    it('handles a single tool call', () => {
      const groups = groupToolCallsByExecution([{ id: 'a', timestamp: new Date() }]);
      expect(groups).toHaveLength(1);
      expect(groups[0]).toHaveLength(1);
    });
  });

  describe('calculateStreamingProgress', () => {
    it('returns thinking phase with totalSteps ≥ 1 for an empty turn', () => {
      const progress = calculateStreamingProgress(makeTurn());
      expect(progress.currentPhase).toBe('thinking');
      expect(progress.totalSteps).toBeGreaterThanOrEqual(1);
      expect(progress.completedSteps).toBe(0);
    });

    it('reports tool_execution when tools are pending', () => {
      const turn = makeTurn({
        toolCalls: [makeToolCall({ status: 'executing' })],
      });
      const progress = calculateStreamingProgress(turn);
      expect(progress.currentPhase).toBe('tool_execution');
    });

    it('reports final_response when all tools are complete but no finalResponse', () => {
      const turn = makeTurn({
        toolCalls: [makeToolCall({ status: 'completed' })],
      });
      const progress = calculateStreamingProgress(turn);
      expect(progress.currentPhase).toBe('final_response');
    });

    it('reports final_response when finalResponse is present', () => {
      const turn = makeTurn({
        finalResponse: { role: 'assistant', content: 'done', timestamp: new Date() },
      });
      const progress = calculateStreamingProgress(turn);
      expect(progress.currentPhase).toBe('final_response');
    });

    it('counts completed tool groups correctly', () => {
      const base = new Date('2025-01-01T00:00:00Z');
      const turn = makeTurn({
        toolCalls: [
          makeToolCall({ id: 'a', status: 'completed', timestamp: base }),
          makeToolCall({ id: 'b', status: 'completed', timestamp: new Date(base.getTime() + 100) }),
          makeToolCall({ id: 'c', status: 'pending', timestamp: new Date(base.getTime() + 5000) }),
        ],
      });
      const progress = calculateStreamingProgress(turn);
      expect(progress.completedSteps).toBeGreaterThanOrEqual(1);
      expect(progress.totalSteps).toBeGreaterThanOrEqual(2);
    });

    it('counts assistantStepChunks when present', () => {
      const turn = makeTurn({
        assistantStepChunks: [
          { content: 'step 1', timestamp: new Date() },
          { content: 'step 2', timestamp: new Date() },
        ],
      });
      const progress = calculateStreamingProgress(turn);
      expect(progress.completedSteps).toBe(2);
      expect(progress.totalSteps).toBeGreaterThanOrEqual(2);
    });
  });
});
