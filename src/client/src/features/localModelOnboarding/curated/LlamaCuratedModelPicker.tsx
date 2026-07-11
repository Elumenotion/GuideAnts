import { FaExternalLinkAlt, FaRedo, FaSpinner } from 'react-icons/fa';
import type { LlamaCatalogDefinitionDto } from '../../../types/settings';
import { filterCatalogModels } from './types';

interface LlamaCuratedModelPickerProps {
  models: LlamaCatalogDefinitionDto[];
  searchQuery: string;
  selectedDefinitionId: string | null;
  loading: boolean;
  error: string | null;
  onSearchChange: (query: string) => void;
  onSelect: (definitionId: string) => void;
  onRetry: () => void;
}

export function LlamaCuratedModelPicker({
  models,
  searchQuery,
  selectedDefinitionId,
  loading,
  error,
  onSearchChange,
  onSelect,
  onRetry,
}: LlamaCuratedModelPickerProps) {
  const filtered = filterCatalogModels(models, searchQuery);

  if (loading) {
    return (
      <div className="flex items-center gap-2 py-8 text-sm text-gray-600">
        <FaSpinner className="animate-spin text-blue-600" />
        Loading curated catalog…
      </div>
    );
  }

  if (error) {
    return (
      <div className="rounded border border-red-200 bg-red-50 px-3 py-3 text-sm text-red-800">
        <div>{error}</div>
        <button
          type="button"
          onClick={onRetry}
          className="mt-2 inline-flex items-center gap-1 rounded border border-red-300 bg-white px-2 py-1 text-xs font-medium text-red-700 hover:bg-red-100"
        >
          <FaRedo /> Retry
        </button>
      </div>
    );
  }

  return (
    <div className="space-y-3">
      <div>
        <label className="block text-xs font-medium uppercase tracking-wide text-gray-600">Search models</label>
        <input
          type="search"
          value={searchQuery}
          onChange={(event) => onSearchChange(event.target.value)}
          placeholder="Search by name, label, repository…"
          className="mt-1 w-full rounded border border-gray-300 px-3 py-2 text-sm text-gray-900 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
        />
      </div>

      {filtered.length === 0 ? (
        <p className="text-sm text-gray-500">No curated models match your search.</p>
      ) : (
        <div className="grid grid-cols-1 gap-3 md:grid-cols-3">
          {filtered.map((model) => {
            const selected = model.id === selectedDefinitionId;
            return (
              <button
                key={model.id}
                type="button"
                onClick={() => onSelect(model.id)}
                className={`rounded-lg border p-3 text-left transition ${
                  selected
                    ? 'border-blue-500 bg-blue-50 ring-1 ring-blue-500'
                    : 'border-gray-200 bg-white hover:border-gray-300 hover:bg-gray-50'
                }`}
              >
                <div className="text-sm font-semibold text-gray-900">{model.display.name}</div>
                <p className="mt-1 line-clamp-2 text-xs text-gray-600">{model.display.description}</p>
                <div className="mt-2 flex flex-wrap gap-1">
                  {model.display.labels.map((label) => (
                    <span
                      key={label}
                      className="rounded-full bg-slate-100 px-2 py-0.5 text-[10px] font-medium uppercase tracking-wide text-slate-700"
                    >
                      {label}
                    </span>
                  ))}
                </div>
                <div className="mt-2 space-y-0.5 text-[11px] text-gray-500">
                  <div>License: {model.display.license}</div>
                  <div className="font-mono">{model.source.repository}</div>
                  {model.hardwareNotes?.summary ? (
                    <div>{model.hardwareNotes.summary}</div>
                  ) : null}
                </div>
                {model.display.documentationUrl ? (
                  <a
                    href={model.display.documentationUrl}
                    target="_blank"
                    rel="noopener noreferrer"
                    onClick={(event) => event.stopPropagation()}
                    className="mt-2 inline-flex items-center gap-1 text-xs text-blue-700 hover:underline"
                  >
                    Documentation <FaExternalLinkAlt className="text-[10px]" />
                  </a>
                ) : null}
              </button>
            );
          })}
        </div>
      )}
    </div>
  );
}
