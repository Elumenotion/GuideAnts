import { useEffect, useRef, useState } from 'react';
import knownModels from '../../data/knownCloudModels.json';

export interface KnownCloudModel {
  modelId: string;
  displayName: string;
  providers: string[];
  description?: string;
  runtimeProfileId?: string;
}

const ALL_KNOWN_MODELS: KnownCloudModel[] = knownModels as KnownCloudModel[];

interface ModelIdTypeaheadProps {
  provider: string;
  value: string;
  onChange: (value: string) => void;
  onSelectSuggestion: (model: KnownCloudModel) => void;
  onBlur?: () => void;
  hasError?: boolean;
}

export function ModelIdTypeahead({
  provider,
  value,
  onChange,
  onSelectSuggestion,
  onBlur,
  hasError,
}: ModelIdTypeaheadProps) {
  const [open, setOpen] = useState(false);
  const [activeIndex, setActiveIndex] = useState(-1);
  const containerRef = useRef<HTMLDivElement>(null);
  const inputRef = useRef<HTMLInputElement>(null);

  const providerModels = ALL_KNOWN_MODELS.filter((m) => m.providers.includes(provider));

  const suggestions = providerModels.filter((m) => {
    if (!value.trim()) return true;
    const q = value.trim().toLowerCase();
    return m.modelId.toLowerCase().includes(q) || m.displayName.toLowerCase().includes(q);
  });

  const showDropdown = open && providerModels.length > 0;

  useEffect(() => {
    setActiveIndex(-1);
  }, [value]);

  useEffect(() => {
    function handleClickOutside(event: MouseEvent) {
      if (containerRef.current && !containerRef.current.contains(event.target as Node)) {
        setOpen(false);
      }
    }
    document.addEventListener('mousedown', handleClickOutside);
    return () => document.removeEventListener('mousedown', handleClickOutside);
  }, []);

  const select = (model: KnownCloudModel) => {
    onSelectSuggestion(model);
    setOpen(false);
    setActiveIndex(-1);
  };

  const handleKeyDown = (event: React.KeyboardEvent<HTMLInputElement>) => {
    if (!showDropdown) return;
    if (event.key === 'ArrowDown') {
      event.preventDefault();
      setActiveIndex((i) => Math.min(i + 1, suggestions.length - 1));
    } else if (event.key === 'ArrowUp') {
      event.preventDefault();
      setActiveIndex((i) => Math.max(i - 1, 0));
    } else if (event.key === 'Enter' && activeIndex >= 0) {
      event.preventDefault();
      const match = suggestions[activeIndex];
      if (match) select(match);
    } else if (event.key === 'Escape') {
      setOpen(false);
    }
  };

  return (
    <div ref={containerRef} className="relative">
      <input
        ref={inputRef}
        type="text"
        value={value}
        onChange={(e) => {
          onChange(e.target.value);
          setOpen(true);
        }}
        onFocus={() => setOpen(true)}
        onBlur={onBlur}
        onKeyDown={handleKeyDown}
        autoComplete="off"
        spellCheck={false}
        className={`w-full rounded border px-3 py-2 font-mono text-sm text-gray-900 focus:outline-none focus:ring-1 ${
          hasError
            ? 'border-red-400 focus:border-red-500 focus:ring-red-500'
            : 'border-gray-300 focus:border-blue-500 focus:ring-blue-500'
        }`}
      />
      {showDropdown && suggestions.length > 0 && (
        <ul className="absolute z-50 mt-1 max-h-64 w-full overflow-y-auto rounded border border-gray-200 bg-white shadow-lg">
          {suggestions.map((model, index) => (
            <li
              key={model.modelId}
              onMouseDown={(e) => {
                e.preventDefault();
                select(model);
              }}
              onMouseEnter={() => setActiveIndex(index)}
              className={`cursor-pointer px-3 py-2 text-sm ${
                index === activeIndex ? 'bg-blue-50 text-blue-900' : 'text-gray-900 hover:bg-gray-50'
              }`}
            >
              <div className="font-mono font-medium">{model.modelId}</div>
              <div className="text-xs text-gray-500">{model.displayName}</div>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
