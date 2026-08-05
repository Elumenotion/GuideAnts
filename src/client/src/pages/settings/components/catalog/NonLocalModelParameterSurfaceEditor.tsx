import type { NonLocalParameterSurface } from '../../parameterSurface';
import {
  parseReasoningChoicesJson,
  providerSupportsRowOwnedRequestShaping,
  validateOptionalJsonObject,
  validateReasoningChoicesJson,
  validateSamplingParametersJson,
} from '../../parameterSurface';

export interface NonLocalModelParameterSurfaceValue extends NonLocalParameterSurface {
  thinkingControlJson: string;
  requestFieldsWhenToolsPresentJson: string;
}

interface NonLocalModelParameterSurfaceEditorProps {
  provider: string;
  value: NonLocalModelParameterSurfaceValue;
  onChange: (updates: Partial<NonLocalModelParameterSurfaceValue>) => void;
}

export function NonLocalModelParameterSurfaceEditor({
  provider,
  value,
  onChange,
}: NonLocalModelParameterSurfaceEditorProps) {
  const samplingError = validateSamplingParametersJson(value.samplingParametersJson);
  const reasoningError = validateReasoningChoicesJson(value.reasoningChoicesJson);
  const reasoningChoicesText = parseReasoningChoicesJson(value.reasoningChoicesJson).join(', ');
  const supportsRequestShaping = providerSupportsRowOwnedRequestShaping(provider);
  const thinkingError = supportsRequestShaping
    ? validateOptionalJsonObject(value.thinkingControlJson, 'Thinking control JSON')
    : null;
  const requestFieldsError = supportsRequestShaping
    ? validateOptionalJsonObject(value.requestFieldsWhenToolsPresentJson, 'Extra request fields JSON')
    : null;

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

      {supportsRequestShaping ? (
        <>
          <div className="space-y-2">
            <label className="block text-xs font-medium uppercase tracking-wide text-gray-600">
              Thinking Control JSON
            </label>
            <p className="text-xs text-gray-600">
              Optional. Maps each reasoning choice to request fields for models this provider does not
              normalize — for example{' '}
              <code className="font-mono">chat_template_kwargs.enable_thinking</code> via a{' '}
              <code className="font-mono">NestedRequestField</code> action. Leave <code className="font-mono">{'{}'}</code>{' '}
              to use the provider default reasoning mapping.
            </p>
            <textarea
              value={value.thinkingControlJson}
              onChange={(event) => onChange({ thinkingControlJson: event.target.value })}
              rows={6}
              spellCheck={false}
              className={`w-full rounded border px-3 py-2 font-mono text-xs text-gray-900 focus:outline-none focus:ring-1 ${
                thinkingError
                  ? 'border-red-400 focus:border-red-500 focus:ring-red-500'
                  : 'border-gray-300 focus:border-blue-500 focus:ring-blue-500'
              }`}
            />
            {thinkingError ? <p className="text-xs text-red-700">{thinkingError}</p> : null}
          </div>

          <div className="space-y-2">
            <label className="block text-xs font-medium uppercase tracking-wide text-gray-600">
              Extra Request Fields JSON
            </label>
            <p className="text-xs text-gray-600">
              Optional. Additional request body fields merged into every completion for this model (for
              example <code className="font-mono">{'{"parallel_tool_calls": false}'}</code>).{' '}
              <strong>Primitive values only</strong> — booleans, numbers, strings, null. For a field
              whose value is an object (such as OpenRouter{' '}
              <code className="font-mono">reasoning</code>), use a{' '}
              <code className="font-mono">RequestField</code> action in Thinking Control JSON instead.
            </p>
            <textarea
              value={value.requestFieldsWhenToolsPresentJson}
              onChange={(event) => onChange({ requestFieldsWhenToolsPresentJson: event.target.value })}
              rows={3}
              spellCheck={false}
              className={`w-full rounded border px-3 py-2 font-mono text-xs text-gray-900 focus:outline-none focus:ring-1 ${
                requestFieldsError
                  ? 'border-red-400 focus:border-red-500 focus:ring-red-500'
                  : 'border-gray-300 focus:border-blue-500 focus:ring-blue-500'
              }`}
            />
            {requestFieldsError ? <p className="text-xs text-red-700">{requestFieldsError}</p> : null}
          </div>
        </>
      ) : null}
    </div>
  );
}
