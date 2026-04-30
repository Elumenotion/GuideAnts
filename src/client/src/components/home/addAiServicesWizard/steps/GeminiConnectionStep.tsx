import { SECRET_MASK } from '../constants';

interface GeminiConnectionStepProps {
  apiKey: string;
  apiKeyHasStoredValue: boolean;
  errors: Partial<Record<'apiKey', string>>;
  onChange: (patch: Partial<{ apiKey: string }>) => void;
}

export function GeminiConnectionStep({
  apiKey,
  apiKeyHasStoredValue,
  errors,
  onChange,
}: GeminiConnectionStepProps) {
  return (
    <div className="space-y-4">
      <div>
        <h3 className="text-sm font-semibold text-gray-900">Google Gemini connection details</h3>
        <p className="mt-1 text-sm text-gray-600">
          Enter the Gemini API key used for chat and Gemini service modes.
        </p>
      </div>

      <div className="space-y-1">
        <label className="block text-xs font-semibold uppercase tracking-wide text-gray-600" htmlFor="gemini-api-key">
          API key
        </label>
        <input
          id="gemini-api-key"
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
    </div>
  );
}

