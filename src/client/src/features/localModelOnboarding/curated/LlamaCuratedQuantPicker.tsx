import { FaRedo, FaSpinner, FaStar } from 'react-icons/fa';
import type { LlamaCatalogDefinitionDto, LlamaCatalogQuantsResponseDto } from '../../../types/settings';
import { formatBytes, summarizeFilenames } from './format';
import { isQuantRecommended } from './types';

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
  const recommendedLabels = definition.quantMetadata.recommendedLabels ?? [];

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

      {!quantsResponse || quantsResponse.quants.length === 0 ? (
        <p className="text-sm text-gray-500">No quant groups are available for this model.</p>
      ) : (
        <div className="space-y-2">
          {quantsResponse.quants.map((quant) => {
            const selected = quant.id === selectedQuantId;
            const recommended = isQuantRecommended(quant.label, recommendedLabels);
            const shardCount = quant.files[0]?.shardCount ?? (quant.files.length > 1 ? quant.files.length : 1);
            const filenames = quant.files.map((file) => file.path);
            const guidance = definition.quantMetadata.guidance?.[quant.id]?.summary ?? quant.guidance?.summary;

            return (
              <button
                key={quant.id}
                type="button"
                onClick={() => onSelect(quant.id)}
                className={`w-full rounded-lg border px-3 py-3 text-left transition ${
                  selected
                    ? 'border-blue-500 bg-blue-50 ring-1 ring-blue-500'
                    : 'border-gray-200 bg-white hover:border-gray-300 hover:bg-gray-50'
                }`}
              >
                <div className="flex items-center justify-between gap-2">
                  <div className="flex items-center gap-2">
                    <span className="text-sm font-semibold text-gray-900">{quant.label}</span>
                    {recommended ? (
                      <span className="inline-flex items-center gap-1 rounded-full bg-amber-100 px-2 py-0.5 text-[10px] font-medium uppercase tracking-wide text-amber-800">
                        <FaStar className="text-[9px]" /> Recommended
                      </span>
                    ) : null}
                  </div>
                  <span className="text-xs text-gray-500">{formatBytes(quant.totalBytes)}</span>
                </div>
                <div className="mt-1 text-xs text-gray-600">
                  {shardCount > 1 ? `${shardCount} shards` : 'Single file'} · {summarizeFilenames(filenames)}
                </div>
                {guidance ? <div className="mt-1 text-xs text-gray-500">{guidance}</div> : null}
              </button>
            );
          })}
        </div>
      )}

      {definition.hardwareNotes?.summary ? (
        <div className="rounded border border-gray-200 bg-gray-50 px-3 py-2 text-xs text-gray-700">
          <strong>Hardware note:</strong> {definition.hardwareNotes.summary}
        </div>
      ) : null}
    </div>
  );
}
