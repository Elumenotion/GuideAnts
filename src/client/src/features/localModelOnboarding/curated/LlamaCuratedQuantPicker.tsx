import { FaRedo, FaSpinner } from 'react-icons/fa';
import type { LlamaCatalogDefinitionDto, LlamaCatalogQuantsResponseDto } from '../../../types/settings';
import { QuantSelect } from './QuantSelect';

interface LlamaCuratedQuantPickerProps {
  definition: LlamaCatalogDefinitionDto;
  quantsResponse: LlamaCatalogQuantsResponseDto | null;
  selectedQuantId: string | null;
  loading: boolean;
  error: string | null;
  selectionInvalidated: boolean;
  onSelect: (quantId: string) => void;
  onRefresh: () => void;
}

export function LlamaCuratedQuantPicker({
  definition,
  quantsResponse,
  selectedQuantId,
  loading,
  error,
  selectionInvalidated,
  onSelect,
  onRefresh,
}: LlamaCuratedQuantPickerProps) {
  if (loading && !quantsResponse) {
    return (
      <div className="flex items-center gap-2 py-6 text-sm text-gray-600">
        <FaSpinner className="animate-spin text-blue-600" />
        Resolving repository quants…
      </div>
    );
  }

  return (
    <div className="space-y-3">
      <div className="flex items-start justify-between gap-3">
        <div>
          <div className="text-sm font-semibold text-gray-900">{definition.display.name}</div>
          <div className="mt-0.5 font-mono text-xs text-gray-500">{quantsResponse?.repository ?? definition.source.repository}</div>
          {quantsResponse?.resolvedRevision ? (
            <div className="mt-1 text-xs text-gray-500">
              Commit: <span className="font-mono">{quantsResponse.resolvedRevision.slice(0, 12)}…</span>
            </div>
          ) : null}
        </div>
        <button
          type="button"
          onClick={onRefresh}
          disabled={loading}
          className="inline-flex items-center gap-1 rounded border border-gray-300 bg-white px-2 py-1 text-xs text-gray-700 hover:bg-gray-50 disabled:opacity-50"
        >
          {loading ? <FaSpinner className="animate-spin" /> : <FaRedo />}
          Refresh
        </button>
      </div>

      {selectionInvalidated ? (
        <div className="rounded border border-amber-200 bg-amber-50 px-3 py-2 text-xs text-amber-900">
          Repository revision changed. Refresh completed — choose a quant again before continuing.
        </div>
      ) : null}

      {error ? (
        <div className="rounded border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-800">{error}</div>
      ) : null}

      <QuantSelect
        selectId="curated-quant-select"
        quants={quantsResponse?.quants ?? []}
        selectedQuantId={selectedQuantId}
        onSelect={onSelect}
        disabled={loading}
        recommendedLabels={definition.quantMetadata.recommendedLabels}
        definitionGuidance={definition.quantMetadata.guidance}
      />

      {definition.hardwareNotes?.summary ? (
        <div className="rounded border border-gray-200 bg-gray-50 px-3 py-2 text-xs text-gray-700">
          <strong>Hardware note:</strong> {definition.hardwareNotes.summary}
        </div>
      ) : null}
    </div>
  );
}
