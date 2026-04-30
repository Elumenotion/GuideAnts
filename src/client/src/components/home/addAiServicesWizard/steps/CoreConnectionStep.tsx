import { SECRET_MASK } from '../constants';

interface CoreConnectionStepProps {
  resource: string;
  apiKey: string;
  apiVersion: string;
  apiKeyHasStoredValue: boolean;
  errors: Partial<Record<'resource' | 'apiKey' | 'apiVersion', string>>;
  onChange: (patch: Partial<{ resource: string; apiKey: string; apiVersion: string }>) => void;
}

export function CoreConnectionStep({
  resource,
  apiKey,
  apiVersion,
  apiKeyHasStoredValue,
  errors,
  onChange,
}: CoreConnectionStepProps) {
  return (
    <div className="space-y-4">
      <div>
        <h3 className="text-sm font-semibold text-gray-900">Microsoft Foundry connection details</h3>
        <p className="mt-1 text-sm text-gray-600">
          Enter the core resource details used for chat model routing.
        </p>
      </div>

      <div className="space-y-1">
        <label className="block text-xs font-semibold uppercase tracking-wide text-gray-600" htmlFor="core-resource">
          Resource
        </label>
        <input
          id="core-resource"
          value={resource}
          onChange={(event) => onChange({ resource: event.target.value })}
          placeholder="my-foundry-resource"
          autoComplete="off"
          className={`w-full rounded border px-3 py-2 text-sm text-gray-900 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500 ${
            errors.resource ? 'border-red-500' : 'border-gray-300'
          }`}
        />
        {errors.resource ? <p className="text-xs text-red-700">{errors.resource}</p> : null}
      </div>

      <div className="space-y-1">
        <label className="block text-xs font-semibold uppercase tracking-wide text-gray-600" htmlFor="core-api-key">
          API key
        </label>
        <input
          id="core-api-key"
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
        <label className="block text-xs font-semibold uppercase tracking-wide text-gray-600" htmlFor="core-api-version">
          API version
        </label>
        <input
          id="core-api-version"
          value={apiVersion}
          onChange={(event) => onChange({ apiVersion: event.target.value })}
          autoComplete="off"
          className={`w-full rounded border px-3 py-2 text-sm text-gray-900 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500 ${
            errors.apiVersion ? 'border-red-500' : 'border-gray-300'
          }`}
        />
        {errors.apiVersion ? <p className="text-xs text-red-700">{errors.apiVersion}</p> : null}
      </div>

      <div className="rounded border border-blue-200 bg-blue-50 px-3 py-2 text-xs text-blue-900">
        Use deployment names exactly as configured in Microsoft Foundry when you add models in the next step.
      </div>
    </div>
  );
}
