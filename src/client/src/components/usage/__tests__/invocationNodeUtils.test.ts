import { describe, expect, it } from 'vitest';
import type { InvocationNodeDto } from '../../../types/usage';
import {
  computeTurnTimelineSegments,
  flattenInvocationNodes,
  formatDurationMs,
  getInvocationNodeKind,
  getNodeDurationMs,
  isInvocationNodeClickable,
} from '../invocationNodeUtils';

function makeNode(overrides: Partial<InvocationNodeDto> & Pick<InvocationNodeDto, 'id' | 'assistantName'>): InvocationNodeDto {
  return {
    parentInvocationId: null,
    triggeringToolCallId: null,
    assistantId: 'agent-1',
    modelDeploymentId: null,
    depth: 0,
    status: 'completed',
    promptTokens: 0,
    completionTokens: 0,
    toolCallCount: 0,
    llmRoundTrips: 0,
    chargeUsd: 0,
    created: '2026-07-10T20:00:00.000Z',
    completed: '2026-07-10T20:00:10.000Z',
    durationMs: 10_000,
    isOutlier: false,
    children: [],
    ...overrides,
  };
}

describe('invocationNodeUtils', () => {
  it('classifies agent, tool, and service nodes', () => {
    expect(getInvocationNodeKind(makeNode({ id: '1', assistantName: 'Guide', assistantId: 'a' }))).toBe('agent');
    expect(getInvocationNodeKind(makeNode({ id: '2', assistantName: 'Tool: run_bash', assistantId: null }))).toBe('tool');
    expect(getInvocationNodeKind(makeNode({ id: '3', assistantName: 'TTS', assistantId: null }))).toBe('service');
  });

  it('only treats agent nodes with assistantId as clickable', () => {
    expect(isInvocationNodeClickable(makeNode({ id: '1', assistantName: 'Guide', assistantId: 'a' }))).toBe(true);
    expect(isInvocationNodeClickable(makeNode({ id: '2', assistantName: 'Tool: run_bash', assistantId: null }))).toBe(false);
  });

  it('flattens nested nodes in depth-first order', () => {
    const child = makeNode({ id: 'child', assistantName: 'Tool: run_python', assistantId: null });
    const root = makeNode({ id: 'root', assistantName: 'Code Executor', children: [child] });
    expect(flattenInvocationNodes([root]).map((n) => n.id)).toEqual(['root', 'child']);
  });

  it('computes node duration from completed timestamp', () => {
    const node = makeNode({
      id: '1',
      assistantName: 'Guide',
      created: '2026-07-10T20:00:00.000Z',
      completed: '2026-07-10T20:00:05.000Z',
      durationMs: 0,
    });
    expect(getNodeDurationMs(node)).toBe(5_000);
  });

  it('formats durations for display', () => {
    expect(formatDurationMs(250)).toBe('250ms');
    expect(formatDurationMs(2500)).toBe('2.5s');
    expect(formatDurationMs(65_000)).toBe('1m 5s');
  });

  it('positions timeline segments by timestamp within turn bounds', () => {
    const agent = makeNode({
      id: 'agent',
      assistantName: 'Code Executor',
      created: '2026-07-10T20:00:00.000Z',
      completed: '2026-07-10T20:00:20.000Z',
      durationMs: 20_000,
    });
    const tool = makeNode({
      id: 'tool',
      assistantName: 'Tool: run_bash',
      assistantId: null,
      created: '2026-07-10T20:00:05.000Z',
      completed: '2026-07-10T20:00:05.000Z',
      durationMs: 0,
      children: [],
    });

    const { segments, elapsedMs } = computeTurnTimelineSegments(
      [agent],
      '2026-07-10T20:00:00.000Z',
    );

    expect(elapsedMs).toBe(20_000);
    expect(segments).toHaveLength(1);
    expect(segments[0]?.leftPercent).toBe(0);
    expect(segments[0]?.widthPercent).toBeGreaterThan(0);

    const nested = computeTurnTimelineSegments([{ ...agent, children: [tool] }], agent.created);
    expect(nested.segments).toHaveLength(2);
    expect(nested.segments[1]?.leftPercent).toBeGreaterThan(0);
  });
});
