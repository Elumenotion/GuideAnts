import { SECRET_MASK } from '../constants';

interface HuggingFaceConnectionStepProps {
  token: string;
  routerBaseUrl: string;
  tokenHasStoredValue: boolean;
  errors: Partial<Record<'token' | 'routerBaseUrl', string>>;
  onChange: (patch: Partial<{ token: string; routerBaseUrl: string }>) => void;
}

export function HuggingFaceConnectionStep({
  token,
  routerBaseUrl,
  tokenHasStoredValue,
  errors,
  onChange,
}: HuggingFaceConnectionStepProps) {
  return (
    <div className="space-y-4">
      <div>
        <h3 className="text-sm font-semibold text-gray-900">Hugging Face connection details</h3>
        <p className="mt-1 text-sm text-gray-600">
          Enter the Hugging Face token used by chat and HF service modes. Router Base URL applies to chat routes.
        </p>
      </div>

      <div className="space-y-1">
        <label className="block text-xs font-semibold uppercase tracking-wide text-gray-600" htmlFor="hf-token">
          Token
        </label>
        <input
          id="hf-token"
          type="password"
          value={token}
          onChange={(event) => onChange({ token: event.target.value })}
          autoComplete="off"
          className={`w-full rounded border px-3 py-2 text-sm text-gray-900 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500 ${
            errors.token ? 'border-red-500' : 'border-gray-300'
          }`}
        />
        {tokenHasStoredValue ? (
          <p className="text-xs text-gray-500">
            A token is already stored. Keep <span className="font-mono">{SECRET_MASK}</span> to preserve it.
          </p>
        ) : null}
        {errors.token ? <p className="text-xs text-red-700">{errors.token}</p> : null}
      </div>

      <div className="space-y-1">
        <label className="block text-xs font-semibold uppercase tracking-wide text-gray-600" htmlFor="hf-router-base-url">
          Router Base URL
        </label>
        <input
          id="hf-router-base-url"
          type="text"
          value={routerBaseUrl}
          onChange={(event) => onChange({ routerBaseUrl: event.target.value })}
          placeholder="https://router.huggingface.co/v1"
          className={`w-full rounded border px-3 py-2 text-sm text-gray-900 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500 ${
            errors.routerBaseUrl ? 'border-red-500' : 'border-gray-300'
          }`}
        />
        <p className="text-xs text-gray-500">
          Chat requests use this base URL. Non-chat services use the HF Inference API endpoint family.
        </p>
        {errors.routerBaseUrl ? <p className="text-xs text-red-700">{errors.routerBaseUrl}</p> : null}
      </div>
    </div>
  );
}
