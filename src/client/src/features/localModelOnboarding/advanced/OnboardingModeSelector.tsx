import type { LocalModelOnboardingMode } from '../curated/types';

const MODE_OPTIONS: Array<{ id: LocalModelOnboardingMode; title: string; description: string }> = [
  {
    id: 'curated',
    title: 'Curated catalog',
    description: 'Pick a shipped model and explicit quant. Recommended default.',
  },
  {
    id: 'custom',
    title: 'Custom Hugging Face',
    description: 'Advanced install with explicit revision, artifact group, alias preset, and profile.',
  },
  {
    id: 'existingAlias',
    title: 'Attach existing alias',
    description: 'Bind a catalog row to an unbound router alias without rewriting its preset.',
  },
];

export interface OnboardingModeSelectorProps {
  mode: LocalModelOnboardingMode;
  onChange: (mode: LocalModelOnboardingMode) => void;
}

export function OnboardingModeSelector({ mode, onChange }: OnboardingModeSelectorProps) {
  return (
    <div className="grid gap-2 md:grid-cols-3">
      {MODE_OPTIONS.map((option) => {
        const selected = mode === option.id;
        return (
          <button
            key={option.id}
            type="button"
            onClick={() => onChange(option.id)}
            className={`rounded border px-3 py-3 text-left transition-colors ${
              selected
                ? 'border-blue-500 bg-blue-50 ring-1 ring-blue-500'
                : 'border-gray-200 bg-white hover:border-gray-300'
            }`}
          >
            <div className="text-sm font-medium text-gray-900">{option.title}</div>
            <p className="mt-1 text-xs text-gray-600">{option.description}</p>
          </button>
        );
      })}
    </div>
  );
}
