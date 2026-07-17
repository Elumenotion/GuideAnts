import { LimitNumberField } from './LimitNumberField';

interface ToolExecutionLimitsSectionProps {
  maxToolCallsPerTurn?: number;
  onMaxToolCallsPerTurnChange: (value: number | undefined) => void;
  onValidationChange?: (hasErrors: boolean) => void;
  onDirtyChange?: () => void;
}

export function ToolExecutionLimitsSection({
  maxToolCallsPerTurn,
  onMaxToolCallsPerTurnChange,
  onValidationChange,
  onDirtyChange,
}: ToolExecutionLimitsSectionProps) {
  return (
    <section
      className="mt-6 border-t border-gray-200 pt-6 space-y-5"
      data-tour-id="guide.tools.execution-limits"
      aria-labelledby="tool-execution-limits-heading"
    >
      <div className="space-y-3">
        <h3 id="tool-execution-limits-heading" className="text-sm font-medium text-gray-900">
          Execution limits
        </h3>
        <p className="text-sm text-gray-600">
          Cap tool use while the assistant is answering one user message. When the limit is hit, it
          stops calling tools and finishes with whatever it has gathered so far.
        </p>
        <p className="text-xs text-gray-500">
          This is separate from{' '}
          <span className="font-medium text-gray-700">Max Conversation Turns</span> in the publish
          dialog, which caps how many user messages a published conversation accepts in total.
        </p>
      </div>

      <LimitNumberField
        id="maxToolCallsPerTurn"
        label="Max tools per response"
        description="Maximum number of tool executions allowed in one assistant response."
        value={maxToolCallsPerTurn}
        onErrorChange={(hasError) => {
          onValidationChange?.(hasError);
        }}
        onChange={(value) => {
          onMaxToolCallsPerTurnChange(value);
          onDirtyChange?.();
        }}
      />
    </section>
  );
}
