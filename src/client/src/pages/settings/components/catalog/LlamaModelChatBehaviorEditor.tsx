import {
  parseReasoningChoicesJson,
  validateReasoningChoicesJson,
  validateSamplingParametersJson,
} from '../../parameterSurface';

export interface LlamaModelChatBehaviorValue {
  samplingParametersJson: string;
  reasoningChoicesJson: string;
  thinkingControlJson: string;
  requestFieldsWhenToolsPresentJson: string;
  combineSystemAndDeveloperMessages: boolean;
  thoughtBlockPattern: string;
}

interface LlamaModelChatBehaviorEditorProps {
  value: LlamaModelChatBehaviorValue;
  onChange: (updates: Partial<LlamaModelChatBehaviorValue>) => void;
}

function validateJsonObject(json: string, label: string): string | null {
  const trimmed = json.trim();
  if (trimmed.length === 0) {
    return `${label} is required.`;
  }
  try {
    const parsed = JSON.parse(trimmed) as unknown;
    if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) {
      return `${label} must be a JSON object.`;
    }
    return null;
  } catch {
    return `${label} must be valid JSON.`;
  }
}

export function LlamaModelChatBehaviorEditor({ value, onChange }: LlamaModelChatBehaviorEditorProps) {
  const samplingError = validateSamplingParametersJson(value.samplingParametersJson);
  const reasoningError = validateReasoningChoicesJson(value.reasoningChoicesJson);
  const thinkingError = validateJsonObject(value.thinkingControlJson, 'Thinking control JSON');
  const requestFieldsError = validateJsonObject(
    value.requestFieldsWhenToolsPresentJson,
    'Extra request fields JSON'
  );
  const reasoningChoicesText = parseReasoningChoicesJson(value.reasoningChoicesJson).join(', ');

  return (
    <div className="space-y-4 rounded border border-gray-200 bg-gray-50 px-3 py-3">
      <div>
        <h3 className="text-sm font-semibold text-gray-900">Model chat behavior</h3>
        <p className="mt-1 text-xs text-gray-600">
          Saved on the catalog model row. These fields are the authority for sampling and reasoning controls.
        </p>
      </div>

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
          Comma-separated values saved to the model row as ReasoningChoicesJson. Leave empty to derive from thinking
          control keys at create time.
        </p>
        {reasoningError ? <p className="text-xs text-red-700">{reasoningError}</p> : null}
      </div>

      <div className="space-y-2">
        <label className="block text-xs font-medium uppercase tracking-wide text-gray-600">
          Thinking Control JSON
        </label>
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
          Additional request body fields merged into every completion for this model (for example{' '}
          <code className="font-mono">parallel_tool_calls</code>).
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

      <label className="inline-flex items-center gap-2 text-sm text-gray-700">
        <input
          type="checkbox"
          checked={value.combineSystemAndDeveloperMessages}
          onChange={(event) => onChange({ combineSystemAndDeveloperMessages: event.target.checked })}
          className="h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500"
        />
        Combine system and developer messages
      </label>

      <div className="space-y-2">
        <label className="block text-xs font-medium uppercase tracking-wide text-gray-600">
          Thought Block Pattern
        </label>
        <input
          type="text"
          value={value.thoughtBlockPattern}
          onChange={(event) => onChange({ thoughtBlockPattern: event.target.value })}
          className="w-full rounded border border-gray-300 px-3 py-2 font-mono text-sm text-gray-900 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
          spellCheck={false}
        />
      </div>
    </div>
  );
}
