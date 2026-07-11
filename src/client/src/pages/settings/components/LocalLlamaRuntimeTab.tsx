import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { FaLink, FaPlay, FaPlus, FaSpinner, FaStop, FaSyncAlt, FaTrash } from 'react-icons/fa';
import LoadingSpinner from '../../../components/LoadingSpinner';
import { api } from '../../../services/api';
import {
  LlamaRuntimeAliasStatusDto,
  LlamaRuntimeInventoryItemDto
} from '../../../types/settings';
import { getErrorMessage } from '../utils';
import { IconActionButton, TextActionButton } from './shared/ActionButtons';
import type { OpenAddModelWizardHandler } from '../types';

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
  onOpenAddModelWizard: OpenAddModelWizardHandler;
  focusedAlias?: string | null;
}

function isLlamaRuntimeUnavailable(error: string | null): boolean {
  if (!error) {
    return false;
  }

  const normalized = error.toLowerCase();
  return normalized.includes('no local llama server')
    || error.includes('127.0.0.1:9')
    || normalized.includes('connection refused');
}

function getLlamaRuntimeUnavailableMessage(error: string | null): string {
  if (error && error.trim()) {
    return error;
  }

  return 'No local llama server is configured for this container yet.';
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
  const [aliasStatus, setAliasStatus] = useState<LlamaRuntimeAliasStatusDto[]>([]);
  const [highlightedAlias, setHighlightedAlias] = useState<string | null>(null);
  const aliasRowRefsRef = useRef<Map<string, HTMLTableRowElement>>(new Map());
  const runtimeUnavailable = isLlamaRuntimeUnavailable(inventoryError);

  const hasLoadingRuntime = useMemo(
    () => inventory.some((row) => row.runtimeState === 'loading'),
    [inventory]
  );

  const hasInFlightAlias = useMemo(
    () => aliasStatus.some((s) => s.inProgress),
    [aliasStatus]
  );

  const refreshAliasStatus = useCallback(async () => {
    if (runtimeUnavailable) {
      setAliasStatus([]);
      return;
    }

    try {
      const list = await api.settings.getLlamaRuntimeStatus();
      setAliasStatus(list);
    } catch {
      // Status poll is optional — inventory row state is authoritative for operator actions.
    }
  }, [runtimeUnavailable]);

  useEffect(() => {
    if (runtimeUnavailable) {
      setAliasStatus([]);
      return;
    }

    void refreshAliasStatus();
  }, [refreshAliasStatus, runtimeUnavailable]);

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
    <section className="rounded-lg border border-gray-200 bg-white">
      <div className="flex flex-wrap items-center justify-between gap-3 border-b border-gray-200 px-6 py-4">
        <div>
          <h2 className="text-base font-semibold text-gray-900">Loaded models</h2>
          <p className="mt-1 text-sm text-gray-600">
            Load and unload router aliases. Models not in the catalog can be attached with the link action.
          </p>
        </div>
        <TextActionButton
          tone="primary"
          icon={<FaPlus />}
          onClick={() => onOpenAddModelWizard('llama-cpp')}
          title="Add a local model"
        >
          Add model
        </TextActionButton>
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
      ) : runtimeUnavailable && inventory.length === 0 ? (
        <div className="px-6 py-4">
          <div className="rounded border border-amber-200 bg-amber-50 px-3 py-2 text-sm text-amber-950">
            {getLlamaRuntimeUnavailableMessage(inventoryError)}
          </div>
        </div>
      ) : inventoryError && inventory.length === 0 ? (
        <div className="px-6 py-4 text-sm text-red-700">{inventoryError}</div>
      ) : (
        <>
          {inventoryError && !runtimeUnavailable && (
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
                  const isHighlighted = highlightedAlias === row.routerModelId;
                  const isUnbound = row.catalogModelIds.length === 0 && row.hasModelFile;
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
                      </td>
                      <td className="px-4 py-3 text-sm text-gray-700">{row.hasModelFile ? 'Yes' : 'No'}</td>
                      <td className="px-4 py-3 text-sm text-gray-700">{row.hasMmprojFile ? 'Yes' : 'No'}</td>
                      <td className="max-w-xs px-4 py-3 text-xs text-gray-600">
                        {row.catalogModelIds.length > 0 ? (
                          row.catalogModelIds.join(', ')
                        ) : isUnbound ? (
                          <span className="text-amber-800">Not in catalog</span>
                        ) : (
                          '—'
                        )}
                      </td>
                      <td className="whitespace-nowrap px-4 py-3 text-right text-sm">
                        <div
                          className="flex items-center justify-end gap-1.5"
                          role="group"
                          aria-label={`Actions for ${row.routerModelId}`}
                        >
                          {isUnbound ? (
                            <IconActionButton
                              label="Attach to catalog"
                              tone="primary"
                              icon={<FaLink />}
                              onClick={() => onOpenAddModelWizard('llama-cpp', row.routerModelId)}
                              title={`Create a catalog model bound to alias ${row.routerModelId} without changing its preset.`}
                            />
                          ) : null}
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
                                try {
                                  await onLoad(row.routerModelId);
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
  );
}
