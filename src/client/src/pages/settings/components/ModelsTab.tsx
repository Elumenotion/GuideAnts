import { useEffect, useMemo, useRef, useState } from 'react';
import { FaEdit, FaPlus, FaRedo, FaSpinner, FaTrash } from 'react-icons/fa';
import LoadingSpinner from '../../../components/LoadingSpinner';
import { api } from '../../../services/api';
import {
  ChatTargetReadinessDto,
  LlamaRuntimeInventoryItemDto,
  SettingsModelDto,
  SettingsRuntimeProfileDto,
} from '../../../types/settings';
import { ActiveAddOperationState } from '../types';
import { formatDateTime, parseCanonicalLocalRuntimeJson } from '../utils';
import { IconActionButton, TextActionButton } from './shared/ActionButtons';
import { CatalogRowEditModal } from './catalog/CatalogRowEditModal';

interface ModelsTabProps {
  modelsLoading: boolean;
  modelsError: string | null;
  orderedModels: SettingsModelDto[];
  deletingModelId: string | null;
  onRetryLoadModels: () => void;
  onRequestDeleteModel: (modelId: string) => void;
  llamaInventory?: LlamaRuntimeInventoryItemDto[];
  llamaInventoryLoading?: boolean;
  profiles: SettingsRuntimeProfileDto[];
  profilesLoading: boolean;
  onCatalogEdited: () => Promise<void>;
  onOpenAddModel: (providerPreselect?: string) => void;
  activeAddOperation: ActiveAddOperationState | null;
  /** Phase F (R-6.*): deep-link target; the row with this model id scrolls
   * into view and highlights for ~2s. Mirrors the Phase E
   * `InfrastructureTab.focusedRuntimeKey` pattern. */
  focusedModelId?: string | null;
}

/**
 * Phase F (R-6.*): narrow classification of a catalog row's local-runtime
 * state for llama-cpp rows. Returns a distinct `invalid-json` case so the
 * UI can paint a real blocker badge instead of hiding behind a silent
 * default (per the "fallback is a dirty word" rule).
 */
type LocalRuntimeClassification =
  | { state: 'n/a' }
  | { state: 'missing-json' }
  | { state: 'invalid-json'; detail: string }
  | { state: 'ok'; routerModelId: string; runtimeProfileId: string };

function classifyLocalRuntime(model: SettingsModelDto): LocalRuntimeClassification {
  if (model.provider !== 'llama-cpp') {
    return { state: 'n/a' };
  }
  if (!model.localRuntimeJson || model.localRuntimeJson.trim().length === 0) {
    return { state: 'missing-json' };
  }
  try {
    const raw = JSON.parse(model.localRuntimeJson) as Record<string, unknown>;
    const routerModelId = typeof raw.routerModelId === 'string' ? raw.routerModelId : null;
    const runtimeProfileId = typeof raw.runtimeProfileId === 'string' ? raw.runtimeProfileId : null;
    if (!routerModelId || !runtimeProfileId) {
      return {
        state: 'invalid-json',
        detail: 'Missing required fields (routerModelId, runtimeProfileId).',
      };
    }
    return { state: 'ok', routerModelId, runtimeProfileId };
  } catch (err) {
    return { state: 'invalid-json', detail: err instanceof Error ? err.message : 'JSON parse failed.' };
  }
}

/**
 * An llama-cpp alias that is merely `unloaded` is not a configuration problem —
 * chat dispatch and the notebook preload path auto-load aliases on demand.
 * Labelling it "Blocked" confuses operators with the real blocker cases
 * (missing credentials, missing runtime profile, missing artifacts). Detect
 * the "only blocker is RUNTIME_STATE unloaded" shape and render it as a
 * neutral "Not loaded" pill instead.
 */
function isOnlyUnloadedBlocker(readiness: ChatTargetReadinessDto): boolean {
  if (readiness.blockers.length !== 1) {
    return false;
  }
  const blocker = readiness.blockers[0] ?? '';
  return blocker.startsWith('RUNTIME_STATE') && blocker.includes("'unloaded'");
}

function ReadinessBadge({
  readiness,
  loading,
}: {
  readiness: ChatTargetReadinessDto | undefined;
  loading: boolean;
}) {
  if (loading && !readiness) {
    return (
      <span className="inline-flex items-center rounded-md bg-gray-100 px-2 py-0.5 text-xs font-medium text-gray-600 ring-1 ring-inset ring-gray-500/20">
        …
      </span>
    );
  }
  if (!readiness) {
    return (
      <span className="inline-flex items-center rounded-md bg-gray-100 px-2 py-0.5 text-xs font-medium text-gray-600 ring-1 ring-inset ring-gray-500/20">
        N/A
      </span>
    );
  }
  if (readiness.status === 'ready') {
    return (
      <span
        title="Ready"
        className="inline-flex items-center rounded-md bg-emerald-50 px-2 py-0.5 text-xs font-medium text-emerald-700 ring-1 ring-inset ring-emerald-600/20"
      >
        Ready
      </span>
    );
  }
  if (isOnlyUnloadedBlocker(readiness)) {
    return (
      <span
        title="Local runtime not loaded. Chat dispatch will load on demand, or use the Load action on Local Llama Runtime."
        className="inline-flex items-center rounded-md bg-slate-100 px-2 py-0.5 text-xs font-medium text-slate-800 ring-1 ring-inset ring-slate-500/20"
      >
        Not loaded
      </span>
    );
  }
  return (
    <span
      title={readiness.blockers.join('\n')}
      className="inline-flex items-center rounded-md bg-amber-50 px-2 py-0.5 text-xs font-medium text-amber-800 ring-1 ring-inset ring-amber-600/20"
    >
      Blocked ({readiness.blockers.length})
    </span>
  );
}

export function ModelsTab({
  modelsLoading,
  modelsError,
  orderedModels,
  deletingModelId,
  onRetryLoadModels,
  onRequestDeleteModel,
  llamaInventory,
  llamaInventoryLoading,
  profiles,
  profilesLoading,
  onCatalogEdited,
  onOpenAddModel,
  activeAddOperation,
  focusedModelId,
}: ModelsTabProps) {
  const [readinessByModel, setReadinessByModel] = useState<Record<string, ChatTargetReadinessDto>>({});
  const [readinessLoading, setReadinessLoading] = useState(false);
  const [highlightedModelId, setHighlightedModelId] = useState<string | null>(null);
  const [editingModel, setEditingModel] = useState<SettingsModelDto | null>(null);
  const rowRefsRef = useRef<Map<string, HTMLTableRowElement>>(new Map());

  useEffect(() => {
    if (!focusedModelId || orderedModels.length === 0) {
      return;
    }
    const node = rowRefsRef.current.get(focusedModelId);
    if (!node) {
      return;
    }
    node.scrollIntoView({ block: 'center', behavior: 'smooth' });
    setHighlightedModelId(focusedModelId);
    const timeoutId = window.setTimeout(() => {
      setHighlightedModelId(null);
    }, 2000);
    return () => window.clearTimeout(timeoutId);
  }, [focusedModelId, orderedModels]);

  // Phase F (R-6.*): per-row readiness probe. Keyed by modelId so re-renders
  // don't re-probe unchanged rows. We silently skip failures here because the
  // per-model endpoint returns 404 for missing catalog rows — that case is
  // already covered visually by the model just not being in `orderedModels`.
  useEffect(() => {
    let cancelled = false;
    if (orderedModels.length === 0) {
      setReadinessByModel({});
      return;
    }
    setReadinessLoading(true);
    (async () => {
      const next: Record<string, ChatTargetReadinessDto> = {};
      for (const model of orderedModels) {
        if (cancelled) return;
        try {
          const r = await api.settings.routing.getChatTargetReadiness(model.modelId);
          next[model.modelId] = r;
        } catch {
          // endpoint-level failure surfaces as a missing readiness badge in the UI
        }
      }
      if (!cancelled) {
        setReadinessByModel(next);
        setReadinessLoading(false);
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [orderedModels]);

  // Phase F (R-6.*): precompute per-profile usage ("Used by: m1, m2, …")
  // from the catalog's declared runtime profile ids. Only llama-cpp rows
  // contribute. If a model references an unknown profile, the counter still
  // shows the id — we don't want to silently drop misconfigurations.
  const profileUsage = useMemo(() => {
    const map = new Map<string, string[]>();
    for (const model of orderedModels) {
      const cls = classifyLocalRuntime(model);
      if (cls.state !== 'ok') continue;
      const list = map.get(cls.runtimeProfileId) ?? [];
      list.push(model.modelId);
      map.set(cls.runtimeProfileId, list);
    }
    return map;
  }, [orderedModels]);

  function localLlamaBadge(model: SettingsModelDto): { label: string; tone: 'ok' | 'warn' | 'err' | 'pending' | 'neutral' } | null {
    if (activeAddOperation?.catalogModelId === model.modelId) {
      return { label: 'Installing…', tone: 'pending' };
    }
    if (model.provider !== 'llama-cpp') {
      return null;
    }
    const parsed = parseCanonicalLocalRuntimeJson(model.localRuntimeJson);
    if (!parsed?.routerModelId) {
      return { label: 'Invalid local JSON', tone: 'err' };
    }
    if (llamaInventoryLoading || !llamaInventory?.length) {
      return { label: '…', tone: 'pending' };
    }
    const row = llamaInventory.find((r) => r.routerModelId === parsed.routerModelId);
    if (!row) {
      return { label: 'Unregistered', tone: 'warn' };
    }
    if (!row.modelPath && !row.mmprojPath) {
      return { label: 'Unregistered', tone: 'warn' };
    }
    if (!row.hasModelFile || !row.hasMmprojFile) {
      return { label: 'Missing artifact', tone: 'err' };
    }
    if (row.runtimeState === 'loaded') {
      return { label: 'Loaded', tone: 'ok' };
    }
    if (row.runtimeState === 'loading') {
      return { label: 'Loading', tone: 'pending' };
    }
    return { label: 'Unloaded', tone: 'neutral' };
  }

  const badgeToneClasses: Record<'ok' | 'warn' | 'err' | 'pending' | 'neutral', string> = {
    ok: 'bg-emerald-50 text-emerald-700 ring-emerald-600/20',
    warn: 'bg-amber-50 text-amber-800 ring-amber-600/20',
    err: 'bg-red-50 text-red-700 ring-red-600/20',
    pending: 'bg-gray-100 text-gray-600 ring-gray-500/20',
    neutral: 'bg-slate-100 text-slate-800 ring-slate-500/20',
  };

  return (
    <>
      <section className="rounded-lg border border-gray-200 bg-white">
        <div className="flex flex-wrap items-center justify-between gap-3 border-b border-gray-200 px-6 py-4">
          <div>
            <h2 className="text-base font-semibold text-gray-900">Catalog</h2>
            <p className="mt-1 text-sm text-gray-600">Add models through the wizard. Edit and delete remain row-level actions.</p>
          </div>
          <TextActionButton
            tone="primary"
            icon={<FaPlus />}
            onClick={() => onOpenAddModel()}
            title="Open Add Model wizard"
          >
            Add Model
          </TextActionButton>
        </div>
        {activeAddOperation ? (
          <div className="mx-6 mt-4 rounded border border-blue-200 bg-blue-50 px-3 py-2 text-sm text-blue-800">
            Add operation in progress for <span className="font-mono">{activeAddOperation.catalogModelId}</span>.
          </div>
        ) : null}

        {modelsLoading ? (
          <div className="px-6 py-8">
            <LoadingSpinner message="Loading models..." />
          </div>
        ) : modelsError ? (
          <div className="px-6 py-4">
            <div className="rounded border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">{modelsError}</div>
            <div className="mt-3">
              <TextActionButton tone="neutral" icon={<FaRedo />} onClick={onRetryLoadModels} title="Retry loading models.">
                Retry
              </TextActionButton>
            </div>
          </div>
        ) : (
          <div className="overflow-x-auto">
            <table className="min-w-full divide-y divide-gray-200">
              <thead className="bg-gray-50">
                <tr>
                  <th className="px-4 py-3 text-left text-xs font-medium uppercase tracking-wide text-gray-500">Model ID</th>
                  <th className="px-4 py-3 text-left text-xs font-medium uppercase tracking-wide text-gray-500">Display</th>
                  <th className="px-4 py-3 text-left text-xs font-medium uppercase tracking-wide text-gray-500">Provider</th>
                  <th className="px-4 py-3 text-left text-xs font-medium uppercase tracking-wide text-gray-500">Order</th>
                  <th className="px-4 py-3 text-left text-xs font-medium uppercase tracking-wide text-gray-500">Active</th>
                  <th className="px-4 py-3 text-left text-xs font-medium uppercase tracking-wide text-gray-500">Updated</th>
                  <th className="px-4 py-3 text-left text-xs font-medium uppercase tracking-wide text-gray-500">Readiness</th>
                  <th className="px-4 py-3 text-left text-xs font-medium uppercase tracking-wide text-gray-500">Local runtime</th>
                  <th className="px-4 py-3 text-left text-xs font-medium uppercase tracking-wide text-gray-500">Profile</th>
                  <th className="px-4 py-3 text-right text-xs font-medium uppercase tracking-wide text-gray-500">Actions</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-gray-200 bg-white">
                {orderedModels.map((model) => {
                  const llamaBadge = localLlamaBadge(model);
                  const runtimeClassification = classifyLocalRuntime(model);
                  const profileId =
                    runtimeClassification.state === 'ok' ? runtimeClassification.runtimeProfileId : null;
                  const usedBy = profileId ? profileUsage.get(profileId) ?? [] : [];
                  const othersUsing = usedBy.filter((id) => id !== model.modelId);
                  const highlighted = highlightedModelId === model.modelId;
                  return (
                  <tr
                    key={model.modelId}
                    data-model-id={model.modelId}
                    ref={(node) => {
                      if (node) {
                        rowRefsRef.current.set(model.modelId, node);
                      } else {
                        rowRefsRef.current.delete(model.modelId);
                      }
                    }}
                    className={highlighted ? 'bg-amber-50 transition-colors duration-500' : 'transition-colors duration-500'}
                  >
                    <td className="whitespace-nowrap px-4 py-3 text-sm font-mono text-gray-900">{model.modelId}</td>
                    <td className="px-4 py-3 text-sm text-gray-900">{model.displayName}</td>
                    <td className="px-4 py-3 text-sm text-gray-700">{model.provider}</td>
                    <td className="px-4 py-3 text-sm text-gray-700">{model.displayOrder ?? '-'}</td>
                    <td className="px-4 py-3 text-sm text-gray-700">{model.isActive ? 'Yes' : 'No'}</td>
                    <td className="px-4 py-3 text-sm text-gray-700">{formatDateTime(model.updated ?? model.created)}</td>
                    <td className="px-4 py-3 text-sm text-gray-700">
                      <ReadinessBadge readiness={readinessByModel[model.modelId]} loading={readinessLoading} />
                    </td>
                    <td className="px-4 py-3 text-sm text-gray-700">
                      {llamaBadge ? (
                        <span
                          className={`inline-flex rounded-md px-2 py-0.5 text-xs font-medium ring-1 ring-inset ${badgeToneClasses[llamaBadge.tone]}`}
                        >
                          {llamaBadge.label}
                        </span>
                      ) : (
                        '—'
                      )}
                      {runtimeClassification.state === 'invalid-json' && (
                        <div className="mt-1 text-xs text-red-700" title={runtimeClassification.detail}>
                          {runtimeClassification.detail}
                        </div>
                      )}
                    </td>
                    <td className="px-4 py-3 text-sm text-gray-700">
                      {profileId ? (
                        <div className="flex flex-col gap-0.5">
                          <span className="font-mono text-xs text-gray-900">{profileId}</span>
                          {othersUsing.length > 0 && (
                            <span className="text-xs text-gray-500" title={othersUsing.join(', ')}>
                              Also used by {othersUsing.length} model(s)
                            </span>
                          )}
                        </div>
                      ) : (
                        '—'
                      )}
                    </td>
                    <td className="px-4 py-3 text-right text-sm">
                      <div
                        className="flex items-center justify-end gap-1.5"
                        role="group"
                        aria-label={`Actions for model ${model.modelId}`}
                      >
                        <IconActionButton
                          label="Edit"
                          tone="info"
                          icon={<FaEdit />}
                          onClick={() => setEditingModel(model)}
                          title={`Edit model ${model.modelId}`}
                        />
                        <IconActionButton
                          label="Delete"
                          tone="danger"
                          icon={deletingModelId === model.modelId ? <FaSpinner className="animate-spin" /> : <FaTrash />}
                          disabled={deletingModelId === model.modelId}
                          onClick={() => onRequestDeleteModel(model.modelId)}
                          title={`Delete model ${model.modelId}`}
                        />
                      </div>
                    </td>
                  </tr>
                  );
                })}
                {orderedModels.length === 0 && (
                  <tr>
                    <td colSpan={10} className="px-4 py-8 text-center text-sm text-gray-600">
                      No models configured yet.
                    </td>
                  </tr>
                )}
              </tbody>
            </table>
          </div>
        )}
      </section>
      <CatalogRowEditModal
        model={editingModel}
        profiles={profiles}
        profilesLoading={profilesLoading}
        inventory={llamaInventory}
        isOpen={editingModel !== null}
        onClose={() => setEditingModel(null)}
        onSaved={onCatalogEdited}
      />
    </>
  );
}
