import { useState } from 'react';
import { LimitNumberField } from './LimitNumberField';

interface ToolExecutionLimitsSectionProps {
  maxToolCallsPerTurn?: number;
  maxToolRoundsPerTurn?: number;
  onMaxToolCallsPerTurnChange: (value: number | undefined) => void;
  onMaxToolRoundsPerTurnChange: (value: number | undefined) => void;
  onValidationChange?: (hasErrors: boolean) => void;
  onDirtyChange?: () => void;
}

export function ToolExecutionLimitsSection({
  maxToolCallsPerTurn,
  maxToolRoundsPerTurn,
  onMaxToolCallsPerTurnChange,
  onMaxToolRoundsPerTurnChange,
  onValidationChange,
  onDirtyChange,
}: ToolExecutionLimitsSectionProps) {
  const [showAdvanced, setShowAdvanced] = useState(false);
  const [callsHasError, setCallsHasError] = useState(false);
  const [roundsHasError, setRoundsHasError] = useState(false);

  const updateValidation = (callsError: boolean, roundsError: boolean) => {
    onValidationChange?.(callsError || roundsError);
  };

  return (
    <section
      className="mt-6 border-t border-gray-200 pt-6 space-y-5"
      data-tour-id="guide.tools.execution-limits"
      aria-labelledby="tool-execution-limits-heading"
    >
      <div>
        <h3 id="tool-execution-limits-heading" className="text-sm font-medium text-gray-900">
          Execution limits
        </h3>
        <p className="text-sm text-gray-600 mt-1">
          When reached, the assistant must stop calling tools and finish its response with gathered
          results. Applies within one assistant response (private notebook and published conversations).
          Separate from{' '}
          <span className="font-medium text-gray-700">Max Conversation Turns</span> in the publish
          dialog, which caps how many user messages a published conversation allows.
        </p>
      </div>

      <LimitNumberField
        id="maxToolCallsPerTurn"
        label="Max tool calls per turn"
        value={maxToolCallsPerTurn}
        onErrorChange={(hasError) => {
          setCallsHasError(hasError);
          updateValidation(hasError, roundsHasError);
        }}
        onChange={(value) => {
          onMaxToolCallsPerTurnChange(value);
          onDirtyChange?.();
        }}
      />

      <div>
        <button
          type="button"
          className="text-sm text-blue-600 hover:text-blue-800 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
          aria-expanded={showAdvanced}
          onClick={() => setShowAdvanced((open) => !open)}
        >
          {showAdvanced ? 'Hide advanced' : 'Show advanced'}
        </button>
      </div>

      {showAdvanced && (
        <LimitNumberField
          id="maxToolRoundsPerTurn"
          label="Max tool rounds per turn"
          value={maxToolRoundsPerTurn}
          onErrorChange={(hasError) => {
            setRoundsHasError(hasError);
            updateValidation(callsHasError, hasError);
          }}
          onChange={(value) => {
            onMaxToolRoundsPerTurnChange(value);
            onDirtyChange?.();
          }}
        />
      )}
    </section>
  );
}
