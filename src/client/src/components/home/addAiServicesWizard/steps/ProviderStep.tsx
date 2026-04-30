import { WIZARD_PROVIDER_OPTIONS } from '../constants';
import type { AddAiServicesWizardProvider } from '../types';

interface ProviderStepProps {
  value: AddAiServicesWizardProvider;
  onChange: (next: AddAiServicesWizardProvider) => void;
}

export function ProviderStep({ value, onChange }: ProviderStepProps) {
  return (
    <div className="space-y-4">
      <div>
        <h3 className="text-sm font-semibold text-gray-900">Choose a provider</h3>
        <p className="mt-1 text-sm text-gray-600">
          This wizard currently supports Microsoft Foundry, Google Gemini, and OpenAI setup paths.
        </p>
      </div>
      <label className="block text-xs font-semibold uppercase tracking-wide text-gray-600" htmlFor="add-ai-services-provider">
        Provider
      </label>
      <select
        id="add-ai-services-provider"
        value={value}
        onChange={(event) => onChange(event.target.value as AddAiServicesWizardProvider)}
        className="w-full rounded border border-gray-300 px-3 py-2 text-sm text-gray-900 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
      >
        {WIZARD_PROVIDER_OPTIONS.map((option) => (
          <option key={option.id} value={option.id}>
            {option.label}
          </option>
        ))}
      </select>
      <div className="space-y-2 rounded border border-gray-200 bg-gray-50 px-3 py-2">
        {WIZARD_PROVIDER_OPTIONS.map((option) => (
          <p key={`desc:${option.id}`} className={`text-xs ${option.id === value ? 'text-gray-900' : 'text-gray-600'}`}>
            <span className="font-semibold">{option.label}:</span>
            {' '}
            {option.description}
          </p>
        ))}
      </div>
    </div>
  );
}
