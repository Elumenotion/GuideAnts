import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { FaPlay, FaPlus, FaSpinner, FaStop, FaSyncAlt, FaTrash } from 'react-icons/fa';
import LoadingSpinner from '../../../components/LoadingSpinner';
import { api } from '../../../services/api';
import {
  LlamaRuntimeAliasStatusDto,
  LlamaRuntimeInventoryItemDto
} from '../../../types/settings';
import { getErrorMessage } from '../utils';
import { IconActionButton, TextActionButton } from './shared/ActionButtons';

interface LocalLlamaRuntimeTabProps {
  inventory: LlamaRuntimeInventoryItemDto[];
  inventoryLoading: boolean;
  inventoryRefreshing: boolean;
  inventoryError: string | null;
  onRefresh: () => void;
  onLoad: (routerModelId: string) => Promise<void>;
  onRequestUnload: (routerModelId: string, notebookReferenceCount: number) => void;
  onRequestDelete: (
    routerModelId: string,
    catalogModelIds: string[],
    notebookReferenceCount: number
  ) => void;
  onOpenAddModelWizard: (providerPreselect: string) => void;
  /** Phase F (R-6.*): deep-link target for the runtime inventory table.
   * Setting this triggers scroll + brief highlight; pattern mirrors
   * `InfrastructureTab.focusedRuntimeKey` from Phase E. */
  focusedAlias?: string | null;
}

export function LocalLlamaRuntimeTab({
  inventory,
  inventoryLoading,
  inventoryRefreshing,
  inventoryError,
  onRefresh,
  onLoad,
  onRequestUnload,
  onRequestDelete,
  onOpenAddModelWizard,
  focusedAlias,
}: LocalLlamaRuntimeTabProps) {
  const [loadActionId, setLoadActionId] = useState<string | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);

  // Phase F (R-6.10 / R-7.1): alias-level runtime status (load state, in-flight,
  // coordinator lock). Purely observational — never mutates any lock.
  const [aliasStatus, setAliasStatus] = useState<LlamaRuntimeAliasStatusDto[]>([]);
  const [aliasStatusError, setAliasStatusError] = useState<string | null>(null);
  const [aliasLoadStartedAt, setAliasLoadStartedAt] = useState<Record<string, number>>({});
  const [aliasLastLoadMs, setAliasLastLoadMs] = useState<Record<string, number>>({});

  const [highlightedAlias, setHighlightedAlias] = useState<string | null>(null);
  const aliasRowRefsRef = useRef<Map<string, HTMLTableRowElement>>(new Map());

  const hasLoadingRuntime = useMemo(
    () => inventory.some((row) => row.runtimeState === 'loading'),
    [inventory]
  );

  const hasInFlightAlias = useMemo(
    () => aliasStatus.some((s) => s.inProgress),
    [aliasStatus]
  );

  const refreshAliasStatus = useCallback(async () => {
    try {
      const list = await api.settings.getLlamaRuntimeStatus();
      setAliasStatus(list);
      setAliasStatusError(null);
    } catch (error) {
      setAliasStatusError(getErrorMessage(error, 'Failed to load runtime status.'));
    }
  }, []);

  useEffect(() => {
    void refreshAliasStatus();
  }, [refreshAliasStatus]);

  useEffect(() => {
    if (!hasLoadingRuntime && !hasInFlightAlias) {
      return;
    }
    const id = window.setInterval(() => {
      onRefresh();
      void refreshAliasStatus();
    }, 2000);
    return () => window.clearInterval(id);
  }, [hasLoadingRuntime, hasInFlightAlias, onRefresh, refreshAliasStatus]);

  useEffect(() => {
    if (!focusedAlias || inventory.length === 0) {
      return;
    }
    const node = aliasRowRefsRef.current.get(focusedAlias);
    if (!node) {
      return;
    }
    node.scrollIntoView({ block: 'center', behavior: 'smooth' });
    setHighlightedAlias(focusedAlias);
    const timeoutId = window.setTimeout(() => {
      setHighlightedAlias(null);
    }, 2000);
    return () => window.clearTimeout(timeoutId);
  }, [focusedAlias, inventory]);

  return (
    <div className="space-y-8">
      <section className="rounded-lg border border-gray-200 bg-white">
        <div className="flex flex-wrap items-center justify-between gap-3 border-b border-gray-200 px-6 py-4">
          <div>
            <h2 className="text-base font-semibold text-gray-900">Local Llama Runtime</h2>
            <p className="mt-1 text-sm text-gray-600">
              Runtime operations only. Use Catalog Add Model for installs and alias adoption.
            </p>
          </div>
          <TextActionButton
            tone="primary"
            icon={<FaPlus />}
            onClick={() => onOpenAddModelWizard('llama-cpp')}
            title="Open Add Model wizard preselected to llama-cpp"
          >
            Add a model
          </TextActionButton>
        </div>
        <div className="px-6 py-4 text-sm text-gray-600">
          Download &amp; Register moved to <span className="font-medium">Catalog → Add Model</span> so model onboarding is provider-driven and consistent.
        </div>
      </section>

      <section className="rounded-lg border border-gray-200 bg-white">
        <div className="border-b border-gray-200 px-6 py-4">
          <h2 className="text-base font-semibold text-gray-900">Runtime Inventory</h2>
          <p className="mt-1 text-sm text-gray-600">
            Router aliases, disk artifacts, and llama server load state.
          </p>
        </div>
        <div className="flex items-center justify-between gap-3 px-6 py-4">
          <div className="min-h-5 text-sm text-gray-500" aria-live="polite">
            {inventoryRefreshing && inventory.length > 0 ? 'Refreshing inventory...' : null}
          </div>
          <TextActionButton
            tone="neutral"
            icon={inventoryRefreshing ? <FaSpinner className="animate-spin" /> : <FaSyncAlt />}
            onClick={() => onRefresh()}
            title="Refresh runtime inventory."
          >
            Refresh
          </TextActionButton>
        </div>
        {loadError && (
          <div className="mx-6 mt-4 rounded border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">{loadError}</div>
        )}
        {inventoryLoading && inventory.length === 0 ? (
          <div className="px-6 py-8">
            <LoadingSpinner message="Loading inventory..." />
          </div>
        ) : inventoryError && inventory.length === 0 ? (
          <div className="px-6 py-4 text-sm text-red-700">{inventoryError}</div>
        ) : (
          <>
            {inventoryError && (
              <div className="mx-6 mb-4 rounded border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">
                {inventoryError}
              </div>
            )}
          <div className="overflow-x-auto">
            <table className="min-w-full divide-y divide-gray-200">
              <thead className="bg-gray-50">
                <tr>
                  <th className="px-4 py-3 text-left text-xs font-medium uppercase tracking-wide text-gray-500">Router ID</th>
                  <th className="px-4 py-3 text-left text-xs font-medium uppercase tracking-wide text-gray-500">Runtime</th>
                  <th className="px-4 py-3 text-left text-xs font-medium uppercase tracking-wide text-gray-500">GGUF</th>
                  <th className="px-4 py-3 text-left text-xs font-medium uppercase tracking-wide text-gray-500">mmproj</th>
                  <th className="px-4 py-3 text-left text-xs font-medium uppercase tracking-wide text-gray-500">Catalog models</th>
                  <th className="px-4 py-3 text-right text-xs font-medium uppercase tracking-wide text-gray-500">Actions</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-gray-200 bg-white">
                {inventory.map((row) => {
                  const status = aliasStatus.find((s) => s.alias === row.routerModelId);
                  const inProgress = status?.inProgress === true || row.runtimeState === 'loading';
                  const lastLoadMs = aliasLastLoadMs[row.routerModelId];
                  const isHighlighted = highlightedAlias === row.routerModelId;
                  return (
                    <tr
                      key={row.routerModelId}
                      data-alias={row.routerModelId}
                      ref={(node) => {
                        if (node) {
                          aliasRowRefsRef.current.set(row.routerModelId, node);
                        } else {
                          aliasRowRefsRef.current.delete(row.routerModelId);
                        }
                      }}
                      className={isHighlighted ? 'bg-amber-50 transition-colors duration-500' : 'transition-colors duration-500'}
                    >
                      <td className="whitespace-nowrap px-4 py-3 font-mono text-sm text-gray-900">{row.routerModelId}</td>
                      <td className="px-4 py-3 text-sm">
                        <div className="flex flex-col gap-0.5">
                          <span
                            className={`inline-flex w-fit rounded-full px-2 py-0.5 text-xs font-medium ring-1 ring-inset ${
                              row.runtimeState === 'loaded'
                                ? 'bg-emerald-50 text-emerald-700 ring-emerald-600/20'
                                : inProgress
                                ? 'bg-blue-50 text-blue-700 ring-blue-600/20'
                                : 'bg-gray-100 text-gray-700 ring-gray-500/20'
                            }`}
                          >
                            {inProgress && row.runtimeState !== 'loaded' ? 'in progress' : row.runtimeState}
                          </span>
                          {lastLoadMs !== undefined && (
                            <span className="text-xs text-gray-500">last load {lastLoadMs.toFixed(0)} ms</span>
                          )}
                        </div>
                      </td>
                      <td className="px-4 py-3 text-sm text-gray-700">{row.hasModelFile ? 'Yes' : 'No'}</td>
                      <td className="px-4 py-3 text-sm text-gray-700">{row.hasMmprojFile ? 'Yes' : 'No'}</td>
                      <td className="max-w-xs px-4 py-3 text-xs text-gray-600">
                        {row.catalogModelIds.length > 0 ? row.catalogModelIds.join(', ') : '—'}
                      </td>
                      <td className="whitespace-nowrap px-4 py-3 text-right text-sm">
                        <div
                          className="flex items-center justify-end gap-1.5"
                          role="group"
                          aria-label={`Actions for ${row.routerModelId}`}
                        >
                          <IconActionButton
                            label="Load"
                            tone="success"
                            icon={loadActionId === row.routerModelId ? <FaSpinner className="animate-spin" /> : <FaPlay />}
                            disabled={
                              loadActionId === row.routerModelId ||
                              row.runtimeState === 'loaded' ||
                              inProgress ||
                              !row.hasModelFile
                            }
                            onClick={() => {
                              void (async () => {
                                setLoadActionId(row.routerModelId);
                                setLoadError(null);
                                const startedAt = performance.now();
                                setAliasLoadStartedAt((prev) => ({ ...prev, [row.routerModelId]: startedAt }));
                                try {
                                  await onLoad(row.routerModelId);
                                  setAliasLastLoadMs((prev) => ({
                                    ...prev,
                                    [row.routerModelId]: performance.now() - startedAt,
                                  }));
                                } catch (e) {
                                  setLoadError(getErrorMessage(e, `Failed to load ${row.routerModelId}.`));
                                } finally {
                                  setLoadActionId(null);
                                  void refreshAliasStatus();
                                }
                              })();
                            }}
                            title={
                              !row.hasModelFile
                                ? 'Model file not present on disk.'
                                : row.runtimeState === 'loaded'
                                ? 'Already loaded.'
                                : inProgress
                                ? 'Load in progress.'
                                : `Load ${row.routerModelId}`
                            }
                          />
                          <IconActionButton
                            label="Unload"
                            tone="neutral"
                            icon={<FaStop />}
                            disabled={row.runtimeState !== 'loaded' && row.runtimeState !== 'loading'}
                            onClick={() => onRequestUnload(row.routerModelId, row.notebookReferenceCount)}
                            title={`Unload ${row.routerModelId}`}
                          />
                          <IconActionButton
                            label="Delete alias + files"
                            tone="danger"
                            icon={<FaTrash />}
                            disabled={
                              inProgress || loadActionId === row.routerModelId
                            }
                            onClick={() =>
                              onRequestDelete(
                                row.routerModelId,
                                row.catalogModelIds,
                                row.notebookReferenceCount
                              )
                            }
                            title={
                              inProgress
                                ? 'Wait for the in-flight operation on this alias.'
                                : `Delete alias ${row.routerModelId}, remove GGUF/mmproj files, and cascade remove linked catalog rows.`
                            }
                          />
                        </div>
                      </td>
                    </tr>
                  );
                })}
                {inventory.length === 0 && (
                  <tr>
                    <td colSpan={6} className="px-4 py-8 text-center text-sm text-gray-600">
                      No router entries yet.
                    </td>
                  </tr>
                )}
              </tbody>
            </table>
          </div>
          </>
        )}
      </section>

      <section className="rounded-lg border border-gray-200 bg-white">
        <div className="border-b border-gray-200 px-6 py-4">
          <h2 className="text-base font-semibold text-gray-900">Router mapping preview</h2>
          <p className="mt-1 text-sm text-gray-600">Effective alias → file paths from inventory.</p>
        </div>
        <div className="overflow-x-auto px-6 py-4">
          <table className="min-w-full text-sm">
            <thead>
              <tr className="border-b border-gray-200 text-left text-xs uppercase text-gray-500">
                <th className="py-2 pr-4">Alias</th>
                <th className="py-2 pr-4">model</th>
                <th className="py-2">mmproj</th>
              </tr>
            </thead>
            <tbody>
              {inventory
                .filter((r) => r.modelPath || r.mmprojPath)
                .map((row) => (
                  <tr key={`map-${row.routerModelId}`} className="border-b border-gray-100">
                    <td className="py-2 pr-4 font-mono text-xs">{row.routerModelId}</td>
                    <td className="py-2 pr-4 font-mono text-xs text-gray-700">{row.modelPath ?? '—'}</td>
                    <td className="py-2 font-mono text-xs text-gray-700">{row.mmprojPath ?? '—'}</td>
                  </tr>
                ))}
            </tbody>
          </table>
          {inventory.every((r) => !r.modelPath && !r.mmprojPath) && (
            <p className="text-sm text-gray-600">No registered router paths yet.</p>
          )}
        </div>
      </section>

      <section className="rounded-lg border border-gray-200 bg-white">
        <div className="flex flex-wrap items-center justify-between gap-3 border-b border-gray-200 px-6 py-4">
          <div>
            <h2 className="text-base font-semibold text-gray-900">Runtime diagnostics</h2>
            <p className="mt-1 text-sm text-gray-600">
              Per-alias load state from the llama server plus coordinator lock state. Read-only — polling does not
              acquire any locks.
            </p>
          </div>
          <TextActionButton
            tone="neutral"
            icon={<FaSyncAlt />}
            onClick={() => void refreshAliasStatus()}
            title="Refresh alias status."
          >
            Refresh
          </TextActionButton>
        </div>
        {aliasStatusError && (
          <div className="mx-6 mt-4 rounded border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">{aliasStatusError}</div>
        )}
        <div className="overflow-x-auto">
          <table className="min-w-full divide-y divide-gray-200">
            <thead className="bg-gray-50">
              <tr>
                <th className="px-4 py-3 text-left text-xs font-medium uppercase tracking-wide text-gray-500">Alias</th>
                <th className="px-4 py-3 text-left text-xs font-medium uppercase tracking-wide text-gray-500">State</th>
                <th className="px-4 py-3 text-left text-xs font-medium uppercase tracking-wide text-gray-500">In progress</th>
                <th className="px-4 py-3 text-left text-xs font-medium uppercase tracking-wide text-gray-500">Router model</th>
                <th className="px-4 py-3 text-left text-xs font-medium uppercase tracking-wide text-gray-500">Last load</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-gray-200 bg-white">
              {aliasStatus.length === 0 && (
                <tr>
                  <td colSpan={5} className="px-4 py-6 text-center text-sm text-gray-600">
                    No alias status records yet.
                  </td>
                </tr>
              )}
              {aliasStatus.map((s) => {
                const clientStartedAt = aliasLoadStartedAt[s.alias];
                const clientLastMs = aliasLastLoadMs[s.alias];
                const lastLoadMs = s.lastLoadDurationMs ?? clientLastMs;
                return (
                  <tr key={s.alias}>
                    <td className="whitespace-nowrap px-4 py-3 font-mono text-sm text-gray-900">{s.alias}</td>
                    <td className="px-4 py-3 text-sm">
                      <span
                        className={`inline-flex rounded-full px-2 py-0.5 text-xs font-medium ring-1 ring-inset ${
                          s.loaded
                            ? 'bg-emerald-50 text-emerald-700 ring-emerald-600/20'
                            : s.inProgress
                            ? 'bg-blue-50 text-blue-700 ring-blue-600/20'
                            : 'bg-gray-100 text-gray-700 ring-gray-500/20'
                        }`}
                      >
                        {s.runtimeState}
                      </span>
                    </td>
                    <td className="px-4 py-3 text-sm text-gray-700">
                      {s.inProgress ? (
                        <span className="inline-flex rounded-full bg-blue-50 px-2 py-0.5 text-xs font-medium text-blue-700 ring-1 ring-inset ring-blue-600/20">
                          yes
                        </span>
                      ) : (
                        <span className="text-xs text-gray-500">no</span>
                      )}
                    </td>
                    <td className="px-4 py-3 font-mono text-xs text-gray-700">{s.routerModelId ?? '—'}</td>
                    <td className="px-4 py-3 text-xs text-gray-700">
                      {lastLoadMs !== undefined && lastLoadMs !== null ? (
                        <span>{lastLoadMs.toFixed(0)} ms</span>
                      ) : s.inProgress && clientStartedAt !== undefined ? (
                        <span className="text-blue-700">in progress…</span>
                      ) : (
                        <span className="text-gray-500">—</span>
                      )}
                      {s.lastError && (
                        <div className="mt-1 text-xs text-red-700" title={s.lastError}>
                          {s.lastError}
                        </div>
                      )}
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      </section>
    </div>
  );
}
