export interface ProviderSelectorOption {
  providerId: string;
  displayName: string;
  kind: string;
}

interface ProviderSelectorProps {
  value: string;
  options: ProviderSelectorOption[];
  onChange: (providerId: string) => void;
}

export function ProviderSelector({ value, options, onChange }: ProviderSelectorProps) {
  return (
    <div className="flex flex-wrap gap-2">
      {options.map((option) => {
        const selected = option.providerId === value;
        return (
          <button
            key={option.providerId}
            type="button"
            onClick={() => onChange(option.providerId)}
            className={[
              'rounded border px-3 py-2 text-left text-sm',
              selected
                ? 'border-indigo-500 bg-indigo-50 text-indigo-800'
                : 'border-gray-300 bg-white text-gray-700 hover:bg-gray-50',
            ].join(' ')}
          >
            <div className="font-medium">{option.displayName}</div>
            <div className="text-xs opacity-70">{option.kind}</div>
          </button>
        );
      })}
    </div>
  );
}
