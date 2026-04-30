import { SECRET_MASK } from '../constants';

interface OpenAiConnectionStepProps {
  apiKey: string;
  endpoint: string;
  apiKeyHasStoredValue: boolean;
  errors: Partial<Record<'apiKey' | 'endpoint', string>>;
  onChange: (patch: Partial<{ apiKey: string; endpoint: string }>) => void;
}

export function OpenAiConnectionStep({
  apiKey,
  endpoint,
  apiKeyHasStoredValue,
  errors,
  onChange,
}: OpenAiConnectionStepProps) {
  return (
    <div className="space-y-4">
      <div>
        <h3 className="text-sm font-semibold text-gray-900">OpenAI connection details</h3>
        <p className="mt-1 text-sm text-gray-600">
          Enter the OpenAI API key used for chat and OpenAI service modes.
        </p>
      </div>

      <div className="space-y-1">
        <label className="block text-xs font-semibold uppercase tracking-wide text-gray-600" htmlFor="openai-api-key">
          API key
        </label>
        <input
          id="openai-api-key"
          type="password"
          value={apiKey}
          onChange={(event) => onChange({ apiKey: event.target.value })}
          autoComplete="off"
          className={`w-full rounded border px-3 py-2 text-sm text-gray-900 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500 ${
            errors.apiKey ? 'border-red-500' : 'border-gray-300'
          }`}
        />
        {apiKeyHasStoredValue ? (
          <p className="text-xs text-gray-500">
            A key is already stored. Keep <span className="font-mono">{SECRET_MASK}</span> to preserve it.
          </p>
        ) : null}
        {errors.apiKey ? <p className="text-xs text-red-700">{errors.apiKey}</p> : null}
      </div>

      <div className="space-y-1">
        <label className="block text-xs font-semibold uppercase tracking-wide text-gray-600" htmlFor="openai-endpoint">
          Endpoint (optional)
        </label>
        <input
          id="openai-endpoint"
          type="text"
          value={endpoint}
          onChange={(event) => onChange({ endpoint: event.target.value })}
          placeholder="https://api.openai.com/v1"
          className={`w-full rounded border px-3 py-2 text-sm text-gray-900 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500 ${
            errors.endpoint ? 'border-red-500' : 'border-gray-300'
          }`}
        />
        <p className="text-xs text-gray-500">
          Leave blank to use the default OpenAI endpoint. Override for proxy servers or compatible APIs.
        </p>
        {errors.endpoint ? <p className="text-xs text-red-700">{errors.endpoint}</p> : null}
      </div>
    </div>
  );
}
