interface ProviderStepProps {
  value: string;
  onChange: (next: string) => void;
}

export function ProviderStep({ value, onChange }: ProviderStepProps) {
  return (
    <div className="space-y-4">
      <div>
        <h3 className="text-sm font-semibold text-gray-900">Choose a provider</h3>
        <p className="mt-1 text-sm text-gray-600">
          This first release supports Microsot Foundry. Additional providers will appear here in future iterations.
        </p>
      </div>
      <label className="block text-xs font-semibold uppercase tracking-wide text-gray-600" htmlFor="add-ai-services-provider">
        Provider
      </label>
      <select
        id="add-ai-services-provider"
        value={value}
        onChange={(event) => onChange(event.target.value)}
        className="w-full rounded border border-gray-300 px-3 py-2 text-sm text-gray-900 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
      >
        <option value="microsot-foundry">Microsot Foundry</option>
      </select>
    </div>
  );
}

