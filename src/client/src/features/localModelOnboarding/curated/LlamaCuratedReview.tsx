import { useState } from 'react';
import type { LlamaCatalogDefinitionDto, LlamaCatalogQuantsResponseDto, LlamaQuantGroupDto } from '../../../types/settings';
import { formatBytes } from './format';

interface LlamaCuratedReviewProps {
  definition: LlamaCatalogDefinitionDto;
  quantsResponse: LlamaCatalogQuantsResponseDto;
  selectedQuant: LlamaQuantGroupDto;
  warnings?: string[];
}

export function LlamaCuratedReview({
  definition,
  quantsResponse,
  selectedQuant,
  warnings = [],
}: LlamaCuratedReviewProps) {
  const [showTechnical, setShowTechnical] = useState(false);
  const sortedFiles = [...selectedQuant.files].sort((a, b) => {
    const aIndex = a.shardIndex ?? 0;
    const bIndex = b.shardIndex ?? 0;
    return aIndex - bIndex;
  });

  return (
    <div className="space-y-3 text-sm">
      <div className="rounded border border-gray-200 bg-gray-50 px-3 py-3">
        <div><strong>Display name:</strong> {definition.display.name}</div>
        <div><strong>Quant:</strong> {selectedQuant.label} ({formatBytes(selectedQuant.totalBytes)})</div>
        <div><strong>Repository:</strong> <span className="font-mono">{quantsResponse.repository}</span></div>
        <div><strong>Commit:</strong> <span className="font-mono">{quantsResponse.resolvedRevision}</span></div>
        <div><strong>Destination:</strong> <span className="font-mono">{definition.defaults.targetDirectory}</span></div>
        <div><strong>Context (preset):</strong> <span className="font-mono">{definition.defaults.routerPreset['ctx-size'] ?? '—'}</span></div>
      </div>

      <div className="rounded border border-gray-200 bg-white px-3 py-3">
        <div className="text-xs font-semibold uppercase tracking-wide text-gray-600">Files</div>
        <ol className="mt-2 list-decimal space-y-1 pl-5 font-mono text-xs text-gray-800">
          {sortedFiles.map((file) => (
            <li key={file.path}>
              {file.path}
              {file.size != null ? <span className="ml-2 text-gray-500">({formatBytes(file.size)})</span> : null}
            </li>
          ))}
        </ol>
      </div>

      {quantsResponse.projector ? (
        <div className="rounded border border-gray-200 bg-white px-3 py-3">
          <div className="text-xs font-semibold uppercase tracking-wide text-gray-600">Projector</div>
          <div className="mt-1 font-mono text-xs text-gray-800">{quantsResponse.projector.path}</div>
        </div>
      ) : definition.defaults.mmproj ? (
        <div className="rounded border border-amber-200 bg-amber-50 px-3 py-2 text-xs text-amber-900">
          Catalog expects projector <span className="font-mono">{definition.defaults.mmproj.path}</span> but none was resolved at this commit.
        </div>
      ) : null}

      {warnings.map((warning) => (
        <div key={warning} className="rounded border border-amber-200 bg-amber-50 px-3 py-2 text-xs text-amber-900">
          {warning}
        </div>
      ))}

      <button
        type="button"
        onClick={() => setShowTechnical((previous) => !previous)}
        className="text-xs font-medium text-blue-700 hover:underline"
      >
        {showTechnical ? 'Hide technical details' : 'Show technical details'}
      </button>

      {showTechnical ? (
        <div className="space-y-2 rounded border border-gray-200 bg-gray-50 px-3 py-3 text-xs text-gray-700">
          <div>
            <div className="font-semibold uppercase tracking-wide text-gray-600">Runtime profile</div>
            <div className="font-mono">{definition.defaults.runtimeProfileId}</div>
          </div>
          <div>
            <div className="font-semibold uppercase tracking-wide text-gray-600">Router preset</div>
            <pre className="mt-1 overflow-x-auto rounded bg-white p-2 font-mono text-[11px]">
              {JSON.stringify(definition.defaults.routerPreset, null, 2)}
            </pre>
          </div>
          <div>
            <div className="font-semibold uppercase tracking-wide text-gray-600">Catalog defaults</div>
            <pre className="mt-1 overflow-x-auto rounded bg-white p-2 font-mono text-[11px]">
              {JSON.stringify(definition.defaults, null, 2)}
            </pre>
          </div>
        </div>
      ) : null}
    </div>
  );
}
