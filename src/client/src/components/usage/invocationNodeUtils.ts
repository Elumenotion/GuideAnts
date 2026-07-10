import type { InvocationNodeDto } from '../../types/usage';

export type InvocationNodeKind = 'agent' | 'tool' | 'service';

export function getInvocationNodeKind(node: InvocationNodeDto): InvocationNodeKind {
  if (node.assistantName.startsWith('Tool: ')) {
    return 'tool';
  }
  if (!node.assistantId) {
    return 'service';
  }
  return 'agent';
}

export function isInvocationNodeClickable(node: InvocationNodeDto): boolean {
  return getInvocationNodeKind(node) === 'agent' && !!node.assistantId;
}

export function flattenInvocationNodes(nodes: InvocationNodeDto[]): InvocationNodeDto[] {
  const result: InvocationNodeDto[] = [];
  for (const node of nodes) {
    result.push(node);
    if (node.children?.length) {
      result.push(...flattenInvocationNodes(node.children));
    }
  }
  return result;
}

export function getNodeStartMs(node: InvocationNodeDto): number {
  return new Date(node.created).getTime();
}

export function getNodeEndMs(node: InvocationNodeDto): number {
  const start = getNodeStartMs(node);
  if (node.completed) {
    const end = new Date(node.completed).getTime();
    if (end > start) {
      return end;
    }
  }
  if (node.durationMs > 0) {
    return start + node.durationMs;
  }
  return node.completed ? new Date(node.completed).getTime() : start;
}

export function getNodeDurationMs(node: InvocationNodeDto): number {
  if (node.durationMs > 0) {
    return node.durationMs;
  }
  return Math.max(0, getNodeEndMs(node) - getNodeStartMs(node));
}

export function formatDurationMs(ms: number): string {
  if (ms < 1000) {
    return `${Math.round(ms)}ms`;
  }
  if (ms < 60_000) {
    return `${(ms / 1000).toFixed(1)}s`;
  }
  const minutes = Math.floor(ms / 60_000);
  const seconds = Math.round((ms % 60_000) / 1000);
  return `${minutes}m ${seconds}s`;
}

export interface TimelineSegment {
  node: InvocationNodeDto;
  kind: InvocationNodeKind;
  leftPercent: number;
  widthPercent: number;
  durationMs: number;
}

const MIN_WIDTH_PERCENT = 0.5;

export function computeTurnTimelineSegments(
  nodes: InvocationNodeDto[],
  turnStarted: string,
): { segments: TimelineSegment[]; elapsedMs: number } {
  const flat = flattenInvocationNodes(nodes);
  if (flat.length === 0) {
    return { segments: [], elapsedMs: 0 };
  }

  const starts = flat.map(getNodeStartMs);
  const ends = flat.map(getNodeEndMs);
  const boundsStart = Math.min(new Date(turnStarted).getTime(), ...starts);
  const boundsEnd = Math.max(...ends);
  const elapsedMs = Math.max(boundsEnd - boundsStart, 1);

  const segments = flat.map((node) => {
    const start = getNodeStartMs(node);
    const end = getNodeEndMs(node);
    const durationMs = Math.max(end - start, 0);
    const displayDuration = Math.max(durationMs, 1);
    const leftPercent = ((start - boundsStart) / elapsedMs) * 100;
    let widthPercent = (displayDuration / elapsedMs) * 100;
    widthPercent = Math.max(widthPercent, MIN_WIDTH_PERCENT);
    const clampedWidth = Math.min(widthPercent, Math.max(100 - leftPercent, MIN_WIDTH_PERCENT));

    return {
      node,
      kind: getInvocationNodeKind(node),
      leftPercent,
      widthPercent: clampedWidth,
      durationMs,
    };
  });

  return { segments, elapsedMs };
}

export const TIMELINE_SEGMENT_COLORS: Record<InvocationNodeKind, string> = {
  agent: 'bg-blue-500',
  tool: 'bg-purple-500',
  service: 'bg-emerald-500',
};

export const TIMELINE_SEGMENT_HOVER_COLORS: Record<InvocationNodeKind, string> = {
  agent: 'hover:bg-blue-600',
  tool: 'hover:bg-purple-600',
  service: 'hover:bg-emerald-600',
};
