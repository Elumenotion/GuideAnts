import type { NonLocalParameterSurface } from '../../parameterSurface';
import {
  parseReasoningChoicesJson,
  validateReasoningChoicesJson,
  validateSamplingParametersJson,
} from '../../parameterSurface';

interface NonLocalModelParameterSurfaceEditorProps {
  value: NonLocalParameterSurface;
  onChange: (updates: Partial<NonLocalParameterSurface>) => void;
}

export function NonLocalModelParameterSurfaceEditor({
  value,
  onChange,
}: NonLocalModelParameterSurfaceEditorProps) {
  const samplingError = validateSamplingParametersJson(value.samplingParametersJson);
  const reasoningError = validateReasoningChoicesJson(value.reasoningChoicesJson);
  const reasoningChoicesText = parseReasoningChoicesJson(value.reasoningChoicesJson).join(', ');

  return (
    <div className="space-y-4">
      <div className="space-y-2">
        <label className="block text-xs font-medium uppercase tracking-wide text-gray-600">
          Sampling Parameters JSON
        </label>
        <textarea
          value={value.samplingParametersJson}
          onChange={(event) => onChange({ samplingParametersJson: event.target.value })}
          rows={6}
          spellCheck={false}
          className={`w-full rounded border px-3 py-2 font-mono text-xs text-gray-900 focus:outline-none focus:ring-1 ${
            samplingError
              ? 'border-red-400 focus:border-red-500 focus:ring-red-500'
              : 'border-gray-300 focus:border-blue-500 focus:ring-blue-500'
          }`}
        />
        {samplingError ? <p className="text-xs text-red-700">{samplingError}</p> : null}
      </div>

      <div className="space-y-2">
        <label className="block text-xs font-medium uppercase tracking-wide text-gray-600">Reasoning Choices</label>
        <input
          type="text"
          value={reasoningChoicesText}
          onChange={(event) => {
            const choices = event.target.value
              .split(',')
              .map((choice) => choice.trim())
              .filter((choice) => choice.length > 0);
            onChange({
              reasoningChoicesJson: choices.length === 0 ? '' : JSON.stringify(choices),
            });
          }}
          placeholder="none, low, medium, high"
          className={`w-full rounded border px-3 py-2 font-mono text-sm text-gray-900 focus:outline-none focus:ring-1 ${
            reasoningError
              ? 'border-red-400 focus:border-red-500 focus:ring-red-500'
              : 'border-gray-300 focus:border-blue-500 focus:ring-blue-500'
          }`}
        />
        <p className="text-[11px] text-gray-500">
          Comma-separated values saved to the model row as ReasoningChoicesJson.
        </p>
        {reasoningError ? <p className="text-xs text-red-700">{reasoningError}</p> : null}
      </div>
    </div>
  );
}
