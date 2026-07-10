import type { InvocationNodeDto } from '../../types/usage';
import {
  TIMELINE_SEGMENT_COLORS,
  TIMELINE_SEGMENT_HOVER_COLORS,
  computeTurnTimelineSegments,
  formatDurationMs,
  isInvocationNodeClickable,
} from './invocationNodeUtils';

interface TurnInvocationTimelineProps {
  nodes: InvocationNodeDto[];
  turnStarted: string;
  onNodeClick?: (node: InvocationNodeDto) => void;
}

export function TurnInvocationTimeline({
  nodes,
  turnStarted,
  onNodeClick,
}: TurnInvocationTimelineProps) {
  const { segments, elapsedMs } = computeTurnTimelineSegments(nodes, turnStarted);

  if (segments.length === 0) {
    return null;
  }

  return (
    <div className="mb-3">
      <div className="flex items-center justify-between text-xs text-gray-500 mb-1.5">
        <span className="font-medium text-gray-600">Timeline</span>
        <span>{formatDurationMs(elapsedMs)}</span>
      </div>
      <div
        className="relative h-3 w-full rounded-full bg-gray-200 overflow-hidden"
        role="img"
        aria-label={`Turn timeline, total duration ${formatDurationMs(elapsedMs)}`}
      >
        {segments.map((segment, index) => {
          const clickable = isInvocationNodeClickable(segment.node) && !!onNodeClick;
          const label = segment.node.assistantName;
          const title = durationMsLabel(segment.durationMs, label);

          const commonClass = [
            'absolute top-0 h-full',
            TIMELINE_SEGMENT_COLORS[segment.kind],
            clickable ? `cursor-pointer ${TIMELINE_SEGMENT_HOVER_COLORS[segment.kind]}` : '',
          ]
            .filter(Boolean)
            .join(' ');

          const style = {
            left: `${segment.leftPercent}%`,
            width: `${segment.widthPercent}%`,
            zIndex: index + 1,
          };

          if (clickable) {
            return (
              <button
                key={segment.node.id}
                type="button"
                className={commonClass}
                style={style}
                title={title}
                aria-label={title}
                onClick={() => onNodeClick?.(segment.node)}
              />
            );
          }

          return (
            <div
              key={segment.node.id}
              className={commonClass}
              style={style}
              title={title}
              aria-hidden
            />
          );
        })}
      </div>
      <div className="mt-1.5 flex flex-wrap gap-x-3 gap-y-1 text-[10px] text-gray-500">
        <LegendSwatch kind="agent" label="Agent" />
        <LegendSwatch kind="tool" label="Tool" />
        <LegendSwatch kind="service" label="AI Service" />
      </div>
    </div>
  );
}

function durationMsLabel(durationMs: number, label: string): string {
  if (durationMs <= 0) {
    return label;
  }
  return `${label} — ${formatDurationMs(durationMs)}`;
}

function LegendSwatch({ kind, label }: { kind: 'agent' | 'tool' | 'service'; label: string }) {
  return (
    <span className="inline-flex items-center gap-1">
      <span className={`inline-block w-2 h-2 rounded-sm ${TIMELINE_SEGMENT_COLORS[kind]}`} />
      {label}
    </span>
  );
}
