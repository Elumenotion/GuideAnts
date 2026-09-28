import type { ConversationContextStatus } from '../../../types/notebook';

interface ContextMeterProps {
  contextStatus: ConversationContextStatus | null;
}

const formatNumber = (value: number) => new Intl.NumberFormat('en-US').format(value);

export default function ContextMeter({ contextStatus }: ContextMeterProps) {
  if (
    contextStatus == null ||
    contextStatus.contextWindowTokens == null ||
    contextStatus.estimatedPromptTokens == null
  ) {
    return (
      <span className="text-xs text-gray-500" data-testid="context-meter">
        Context: unknown
      </span>
    );
  }

  const { estimatedPromptTokens, contextWindowTokens, boundaryTurnIndex } = contextStatus;
  const percent = Math.min(100, Math.round((estimatedPromptTokens / contextWindowTokens) * 100));
  const utilizationColorClass = percent >= 90 ? 'text-red-600' : percent >= 75 ? 'text-amber-600' : 'text-gray-500';

  return (
    <span className={`text-xs ${utilizationColorClass}`} data-testid="context-meter">
      {boundaryTurnIndex != null && (
        <span className="mr-1 text-gray-400" title="This conversation has been compacted">
          Compacted ·
        </span>
      )}
      {formatNumber(estimatedPromptTokens)} / {formatNumber(contextWindowTokens)} tokens (<span>{percent}%</span>)
    </span>
  );
}
