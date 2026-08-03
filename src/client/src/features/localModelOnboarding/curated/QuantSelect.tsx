import type { LlamaCatalogQuantGuidanceDto, LlamaQuantGroupDto } from '../../../types/settings';
import { formatBytes, summarizeFilenames } from './format';
import { isQuantRecommended } from './types';

export interface QuantSelectProps {
  quants: LlamaQuantGroupDto[];
  selectedQuantId: string | null;
  onSelect: (quantId: string) => void;
  label?: string;
  placeholder?: string;
  emptyMessage?: string;
  selectId?: string;
  disabled?: boolean;
  /** Quant labels the catalog definition marks as recommended. */
  recommendedLabels?: string[] | null;
  /** Catalog-level guidance keyed by quant id. Takes precedence over the repository's own guidance. */
  definitionGuidance?: Record<string, LlamaCatalogQuantGuidanceDto> | null;
  /** Quant already installed for this model. Shown as the current value and not selectable. */
  installedQuantId?: string | null;
  /**
   * Label recorded for the installed quant. Used when the installed quant is no
   * longer present in the resolved list, so the current value is still named.
   */
  installedQuantLabel?: string | null;
}

function shardCountOf(quant: LlamaQuantGroupDto): number {
  return quant.files[0]?.shardCount ?? (quant.files.length > 1 ? quant.files.length : 1);
}

function describeShards(quant: LlamaQuantGroupDto): string {
  const shards = shardCountOf(quant);
  return shards > 1 ? `${shards} shards` : 'single file';
}

/** Size and layout of a quant group, e.g. `33 GiB · single file`. */
export function formatQuantSummary(quant: LlamaQuantGroupDto): string {
  return `${formatBytes(quant.totalBytes)} · ${describeShards(quant)}`;
}

function describeOption(quant: LlamaQuantGroupDto, recommended: boolean, installed: boolean): string {
  const notes = [formatBytes(quant.totalBytes), describeShards(quant)];
  if (recommended) {
    notes.push('recommended');
  }
  if (installed) {
    notes.push('installed');
  }
  return `${quant.label} — ${notes.join(' · ')}`;
}

/**
 * Single quant-group selector shared by curated onboarding and the installed
 * model's change-quant dialog, so both surfaces present the same options,
 * annotations, and detail for a chosen quant.
 */
export function QuantSelect({
  quants,
  selectedQuantId,
  onSelect,
  label = 'Quant group',
  placeholder = 'Select a quant…',
  emptyMessage = 'No quant groups are available for this model.',
  selectId = 'quant-select',
  disabled = false,
  recommendedLabels,
  definitionGuidance,
  installedQuantId,
  installedQuantLabel,
}: QuantSelectProps) {
  if (quants.length === 0) {
    return <p className="text-sm text-gray-500">{emptyMessage}</p>;
  }

  const selected = quants.find((quant) => quant.id === selectedQuantId) ?? null;
  const guidance = selected
    ? definitionGuidance?.[selected.id]?.summary ?? selected.guidance?.summary
    : undefined;

  const installed = installedQuantId ? quants.find((quant) => quant.id === installedQuantId) ?? null : null;
  const installedName = installed?.label ?? installedQuantLabel ?? installedQuantId;
  const installedDetail = installed ? formatQuantSummary(installed) : null;

  return (
    <div className="space-y-2">
      <label htmlFor={selectId} className="block text-xs font-medium uppercase tracking-wide text-gray-600">
        {label}
      </label>
      <select
        id={selectId}
        value={selectedQuantId ?? ''}
        onChange={(event) => onSelect(event.target.value)}
        disabled={disabled}
        className="w-full rounded border border-gray-300 bg-white px-3 py-2 text-sm text-gray-900 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500 disabled:bg-gray-100 disabled:text-gray-500"
      >
        <option value="" disabled>
          {placeholder}
        </option>
        {quants.map((quant) => {
          const installed = !!installedQuantId && quant.id === installedQuantId;
          return (
            <option key={quant.id} value={quant.id} disabled={installed}>
              {describeOption(quant, isQuantRecommended(quant.label, recommendedLabels), installed)}
            </option>
          );
        })}
      </select>

      {selected ? (
        <div className="rounded border border-gray-200 bg-gray-50 px-3 py-2 text-xs text-gray-700">
          <div className="font-medium text-gray-900">{selected.label}</div>
          <div className="mt-0.5">
            {formatBytes(selected.totalBytes)} · {describeShards(selected)}
          </div>
          <div className="mt-0.5 font-mono break-all text-gray-600">
            {summarizeFilenames(selected.files.map((file) => file.path))}
          </div>
          {installedName && selected.id !== installedQuantId ? (
            <div className="mt-1 text-gray-600">
              Replaces <span className="font-mono">{installedName}</span>
              {installedDetail ? ` (${installedDetail})` : ''}
            </div>
          ) : null}
          {guidance ? <div className="mt-1 text-gray-600">{guidance}</div> : null}
        </div>
      ) : null}
    </div>
  );
}
