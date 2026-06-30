import { FaCheck } from 'react-icons/fa';
import { diffStateChipClassName, diffStateLabel } from './mcpToolSource';
import type { McpDiscoveredToolRow } from './mcpToolSourceTypes';

export interface McpDiscoveryResultsProps {
  displayTools: McpDiscoveredToolRow[];
  showDiffReview: boolean;
  pendingDiscovery: McpDiscoveredToolRow[] | null;
  panelState: string;
  onApplyDiscovery: () => void;
  onToggleToolSelected: (backingToolId: string, selected: boolean) => void;
}

export function McpDiscoveryResults({
  displayTools,
  showDiffReview,
  pendingDiscovery,
  panelState,
  onApplyDiscovery,
  onToggleToolSelected,
}: McpDiscoveryResultsProps) {
  return (
    <>
      {showDiffReview && pendingDiscovery && (
        <div className="rounded-md border border-amber-300 bg-amber-50 p-3 space-y-2">
          <p className="text-sm font-medium text-amber-900">Review discovery changes before applying</p>
          <p className="text-xs text-amber-800">
            Added {pendingDiscovery.filter((t) => t.diffState === 'added').length}, changed{' '}
            {pendingDiscovery.filter((t) => t.diffState === 'changed').length}, removed{' '}
            {pendingDiscovery.filter((t) => t.diffState === 'removed').length}, disabled{' '}
            {pendingDiscovery.filter((t) => t.diffState === 'disabled').length}
          </p>
          <button
            type="button"
            onClick={onApplyDiscovery}
            className="inline-flex items-center gap-2 px-3 py-1.5 text-xs font-medium text-white bg-teal-600 rounded-md hover:bg-teal-700"
            data-testid="mcp-apply-discovery"
          >
            <FaCheck className="w-3 h-3" />
            Apply discovery to descriptor
          </button>
        </div>
      )}

      {displayTools.length > 0 && (
        <div className="space-y-2">
          <h4 className="text-sm font-medium text-gray-900">Discovered MCP tools</h4>
          {displayTools.map((row) => (
            <div
              key={row.backingToolId}
              className="flex items-start gap-3 p-3 bg-white border border-gray-200 rounded-md"
              data-testid={`mcp-tool-row-${row.backingToolId}`}
            >
              <input
                type="checkbox"
                checked={row.selected}
                disabled={row.diffState === 'removed'}
                onChange={(e) => onToggleToolSelected(row.backingToolId, e.target.checked)}
                className="mt-1"
                aria-label={`Enable ${row.name}`}
              />
              <div className="flex-1 min-w-0">
                <div className="flex items-center gap-2 flex-wrap">
                  <span className="text-sm font-medium text-gray-900">{row.name}</span>
                  {diffStateLabel(row.diffState) && (
                    <span
                      className={`inline-flex px-2 py-0.5 rounded text-xs font-medium ${diffStateChipClassName(row.diffState)}`}
                    >
                      {diffStateLabel(row.diffState)}
                    </span>
                  )}
                </div>
                <p className="text-xs text-gray-500 font-mono mt-0.5">id: {row.backingToolId}</p>
                {row.description && <p className="text-xs text-gray-600 mt-1">{row.description}</p>}
                <p className="text-xs text-gray-500 mt-1">
                  operationId: <code className="font-mono">{row.operationId}</code>
                </p>
              </div>
            </div>
          ))}
        </div>
      )}

      {panelState === 'discovering' && displayTools.length === 0 && (
        <p className="text-sm text-gray-600" aria-live="polite">
          Discovery in progress…
        </p>
      )}
    </>
  );
}
