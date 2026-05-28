import { useCallback, useEffect, useMemo, useState } from 'react';
import { FaExternalLinkAlt, FaRedo, FaSave, FaSpinner, FaSyncAlt } from 'react-icons/fa';
import LoadingSpinner from '../../../components/LoadingSpinner';
import { useToast } from '../../../components/common/Toast';
import { api } from '../../../services/api';
import {
  ChatDefaultsDto,
  ServiceEditorStateDto,
  SettingsOverviewDto,
} from '../../../types/settings';
import type { ModelsRuntimeSubTab } from '../types';
import type { ServiceKey } from './ServicesTab';
import { mapChatProviderToSection } from '../utils';
import { getConnectionSectionDisplayName } from '../constants/displayLabels';
import {
  ChatModelConfigurator,
  ChatModelConfigValue,
} from '../../../components/chat-model/ChatModelConfigurator';
import {
  buildChatDefaultsUpdateRequest,
  chatDefaultsToConfig,
} from '../../../components/chat-model/chatDefaults';
import { TextActionButton } from './shared/ActionButtons';

/**
 * Settings landing surface: three-section summary (R-5.2 + R-9).
 * - Default Chat Model (global catalog default + optional hard override).
 * - Chat providers in use → Connections deep links.
 * - Five non-chat services → Services tab deep links.
 */
export interface OverviewTabProps {
  onOpenConnections: (focusedSection?: string) => void;
  onOpenServices: (serviceId: ServiceKey) => void;
  onOpenModelsRuntime: (subTab: ModelsRuntimeSubTab) => void;
  /**
   * Bumped by the parent page whenever the model catalog list changes (for
   * example after an Add Model wizard install completes). Passed straight into
   * `ChatModelConfigurator.refreshKey` so the Default Chat Model dropdown
   * immediately reflects the new row without a manual reload.
   */
  catalogVersion?: number;
}

/** Order matches `ServicesTab.tsx` `services` list. */
const NON_CHAT_SERVICES: Array<{ id: ServiceKey; label: string }> = [
  { id: 'SpeechTranscription', label: 'Speech Transcription' },
  { id: 'ImageGeneration', label: 'Image Generation' },
  { id: 'SpeechSynthesis', label: 'Speech Synthesis' },
  { id: 'DocumentIntelligence', label: 'Document Intelligence' },
  { id: 'Embeddings', label: 'Embeddings' },
];

function StatusPill({ status }: { status: 'ready' | 'not-ready' | 'not-configured' | 'loading' | 'unavailable' }) {
  if (status === 'loading') {
    return (
      <span
        className="inline-block h-5 w-14 animate-pulse rounded-md bg-gray-200"
        aria-hidden="true"
      />
    );
  }
  if (status === 'unavailable') {
    return (
      <span
        className="inline-flex items-center rounded-md bg-gray-100 px-2 py-0.5 text-xs font-medium text-gray-700 ring-1 ring-inset ring-gray-400/30"
        title="Could not load readiness"
      >
        Unavailable
      </span>
    );
  }
  if (status === 'not-configured') {
    return (
      <span
        className="inline-flex items-center rounded-md bg-slate-100 px-2 py-0.5 text-xs font-medium text-slate-700 ring-1 ring-inset ring-slate-500/20"
        title="This service has no saved active provider yet"
      >
        Not configured
      </span>
    );
  }
  const ready = status === 'ready';
  const tone = ready
    ? 'bg-emerald-50 text-emerald-700 ring-emerald-600/20'
    : 'bg-amber-50 text-amber-800 ring-amber-600/20';
  return (
    <span className={`inline-flex items-center rounded-md px-2 py-0.5 text-xs font-medium ring-1 ring-inset ${tone}`}>
      {ready ? 'Ready' : 'Not ready'}
    </span>
  );
}

interface ChatProviderRow {
  section: string;
  displayLabel: string;
  ready: boolean;
}

function buildChatProviderRows(overview: SettingsOverviewDto): ChatProviderRow[] {
  const issueSections = new Set(overview.providerIssues.map((i) => i.section));
  const sectionToLabel = new Map<string, string>();

  for (const target of overview.chatTargets.targets) {
    const section = mapChatProviderToSection(target.provider);
    if (!section) {
      continue;
    }
    if (!sectionToLabel.has(section)) {
      sectionToLabel.set(section, getConnectionSectionDisplayName(section));
    }
  }

  const rows: ChatProviderRow[] = [];
  for (const section of sectionToLabel.keys()) {
    rows.push({
      section,
      displayLabel: getConnectionSectionDisplayName(section),
      ready: !issueSections.has(section),
    });
  }
  rows.sort((a, b) => a.section.localeCompare(b.section));
  return rows;
}

export function OverviewTab({
  onOpenConnections,
  onOpenServices,
  onOpenModelsRuntime,
  catalogVersion,
}: OverviewTabProps) {
  const { showToast } = useToast();
  const [overview, setOverview] = useState<SettingsOverviewDto | null>(null);
  const [overviewLoading, setOverviewLoading] = useState(true);
  const [overviewError, setOverviewError] = useState<string | null>(null);

  type ServiceRowState = { status: 'loading' | 'ready' | 'not-ready' | 'not-configured' | 'unavailable'; error?: string };
  const [serviceStates, setServiceStates] = useState<Partial<Record<ServiceKey, ServiceRowState>>>({});

  const [chatDefaults, setChatDefaults] = useState<ChatDefaultsDto | null>(null);
  const [chatDefaultsLoading, setChatDefaultsLoading] = useState(true);
  const [chatDefaultsError, setChatDefaultsError] = useState<string | null>(null);
  const [chatDefaultsSaving, setChatDefaultsSaving] = useState(false);
  const [overrideAllChatModels, setOverrideAllChatModels] = useState(false);
  const [configValue, setConfigValue] = useState<ChatModelConfigValue>({
    modelId: '',
    temperature: null,
    topP: null,
    reasoningEffort: undefined,
    samplingOverrides: {},
  });
  const [showOverrideSavedBanner, setShowOverrideSavedBanner] = useState(false);

  const loadOverview = useCallback(async () => {
    setOverviewLoading(true);
    setOverviewError(null);
    try {
      const data = await api.settings.getOverview();
      setOverview(data);
    } catch (err) {
      setOverviewError(err instanceof Error ? err.message : 'Failed to load overview.');
      setOverview(null);
    } finally {
      setOverviewLoading(false);
    }
  }, []);

  const loadChatDefaults = useCallback(async () => {
    setChatDefaultsLoading(true);
    setChatDefaultsError(null);
    try {
      const data = await api.settings.chatDefaults.get();
      setChatDefaults(data);
      setOverrideAllChatModels(data.overrideAllChatModels);
      setConfigValue(chatDefaultsToConfig(data));
    } catch (err) {
      setChatDefaultsError(err instanceof Error ? err.message : 'Failed to load default chat settings.');
      setChatDefaults(null);
    } finally {
      setChatDefaultsLoading(false);
    }
  }, []);

  const loadServiceRows = useCallback(async () => {
    const initial: Record<ServiceKey, { status: 'loading' | 'ready' | 'not-ready' | 'not-configured' | 'unavailable'; error?: string }> =
      {} as Record<ServiceKey, { status: 'loading' | 'ready' | 'not-ready' | 'not-configured' | 'unavailable'; error?: string }>;
    for (const { id } of NON_CHAT_SERVICES) {
      initial[id] = { status: 'loading' };
    }
    setServiceStates(initial);

    await Promise.all(
      NON_CHAT_SERVICES.map(async ({ id }) => {
        try {
          const state: ServiceEditorStateDto = await api.settings.services.get(id);
          const ready = state.readiness.status === 'ready';
          setServiceStates((prev) => ({
            ...prev,
            [id]: { status: ready ? 'ready' : 'not-ready' },
          }));
        } catch (err) {
          const message = err instanceof Error ? err.message : 'Request failed.';
          setServiceStates((prev) => ({
            ...prev,
            [id]: { status: 'unavailable', error: message },
          }));
        }
      })
    );
  }, []);

  const refreshAll = useCallback(async () => {
    await Promise.all([loadOverview(), loadServiceRows(), loadChatDefaults()]);
  }, [loadOverview, loadServiceRows, loadChatDefaults]);

  useEffect(() => {
    void loadOverview();
    void loadServiceRows();
    void loadChatDefaults();
  }, [loadOverview, loadServiceRows, loadChatDefaults]);

  const chatRows = useMemo(() => (overview ? buildChatProviderRows(overview) : []), [overview]);

  const handleSaveChatDefaults = useCallback(async () => {
    if (!chatDefaults) {
      return;
    }
    setChatDefaultsSaving(true);
    setShowOverrideSavedBanner(false);
    try {
      const payload = buildChatDefaultsUpdateRequest(chatDefaults, configValue, overrideAllChatModels);
      const updated = await api.settings.chatDefaults.update(payload);
      setChatDefaults(updated);
      setOverrideAllChatModels(updated.overrideAllChatModels);
      setConfigValue(chatDefaultsToConfig(updated));
      showToast({ type: 'success', title: 'Default chat model saved' });
      if (updated.overrideAllChatModels) {
        setShowOverrideSavedBanner(true);
      }
    } catch (err: unknown) {
      const message = err instanceof Error ? err.message : 'Save failed.';
      showToast({ type: 'error', title: 'Could not save default chat model', message });
    } finally {
      setChatDefaultsSaving(false);
    }
  }, [chatDefaults, configValue, overrideAllChatModels, showToast]);

  const showInitialSpinner = overviewLoading && !overview && !overviewError;

  if (showInitialSpinner) {
    return (
      <section className="rounded-lg border border-gray-200 bg-white p-8">
        <LoadingSpinner message="Loading overview..." />
      </section>
    );
  }

  return (
    <div className="space-y-5">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h2 className="text-sm font-semibold uppercase tracking-wide text-gray-500">Overview</h2>
        <TextActionButton
          tone="neutral"
          icon={overviewLoading ? <FaSpinner className="animate-spin" /> : <FaSyncAlt />}
          disabled={overviewLoading}
          onClick={() => void refreshAll()}
          title="Refresh overview."
        >
          Refresh
        </TextActionButton>
      </div>

      {overviewError && (
        <div className="rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-800">
          <div className="font-medium">Chat provider summary unavailable</div>
          <div className="mt-1 text-red-700">{overviewError}</div>
          <div className="mt-2">
            <TextActionButton
              tone="neutral"
              icon={<FaRedo />}
              onClick={() => void loadOverview()}
              title="Retry loading overview."
            >
              Retry
            </TextActionButton>
          </div>
        </div>
      )}

      <section className="overflow-hidden rounded-lg border border-gray-200 bg-white">
        <div className="border-b border-gray-200 px-5 py-3">
          <h3 className="text-sm font-semibold text-gray-900">Default Chat Model</h3>
          <p className="mt-1 text-xs text-gray-500">
            Instance-wide default for assistants and guides when they use “Use Default Model”, and optional hard override
            for every chat turn.
          </p>
        </div>
        <div className="px-5 py-4 space-y-4">
          {chatDefaultsLoading && (
            <div className="text-sm text-gray-500">Loading default chat settings…</div>
          )}
          {chatDefaultsError && (
            <div className="rounded border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-800">
              {chatDefaultsError}
              <button
                type="button"
                className="ml-2 text-red-900 underline"
                onClick={() => void loadChatDefaults()}
              >
                Retry
              </button>
            </div>
          )}
          {!chatDefaultsLoading && !chatDefaultsError && chatDefaults && (
            <>
              {showOverrideSavedBanner && (
                <div
                  className="rounded-md border border-amber-200 bg-amber-50 px-3 py-2 text-sm text-amber-950"
                  role="status"
                >
                  Override is on: every assistant, guide, nested agent call, and tool-using chat turn uses this default
                  until you turn the override off.
                </div>
              )}
              <label className="flex cursor-pointer items-start gap-2">
                <input
                  type="checkbox"
                  className="mt-1 h-4 w-4 rounded border-gray-300"
                  checked={overrideAllChatModels}
                  onChange={(e) => setOverrideAllChatModels(e.target.checked)}
                />
                <span>
                  <span className="text-sm font-medium text-gray-900">Override all chat models</span>
                  <span className="block text-xs text-gray-500">
                    When enabled, per-entity model selection is ignored; every chat path uses the default catalog model
                    above.
                  </span>
                </span>
              </label>

              <ChatModelConfigurator
                mode="default"
                modelId={configValue.modelId}
                temperature={configValue.temperature}
                topP={configValue.topP}
                reasoningEffort={configValue.reasoningEffort}
                samplingOverrides={configValue.samplingOverrides}
                onChange={setConfigValue}
                refreshKey={catalogVersion}
              />

              <p className="text-xs text-gray-500">
                No models in the catalog?{' '}
                <button
                  type="button"
                  className="font-medium text-blue-700 underline"
                  onClick={() => onOpenModelsRuntime('catalog')}
                >
                  Open Models &amp; Runtime → Catalog
                </button>
                . Choosing a <code className="font-mono text-[11px]">llama-cpp</code> model uses the same load-on-demand
                behavior — see{' '}
                <button
                  type="button"
                  className="font-medium text-blue-700 underline"
                  onClick={() => onOpenModelsRuntime('local-llama')}
                >
                  Local Llama Runtime
                </button>
                .
              </p>

              <div className="flex justify-end">
                <TextActionButton
                  tone="primary"
                  icon={chatDefaultsSaving ? <FaSpinner className="animate-spin" /> : <FaSave />}
                  disabled={chatDefaultsSaving}
                  onClick={() => void handleSaveChatDefaults()}
                  title="Save chat defaults."
                >
                  Save
                </TextActionButton>
              </div>
            </>
          )}
        </div>
      </section>

      <section className="overflow-hidden rounded-lg border border-gray-200 bg-white">
        <div className="border-b border-gray-200 px-5 py-3">
          <h3 className="text-sm font-semibold text-gray-900">Chat providers</h3>
          <p className="mt-1 text-xs text-gray-500">
            Providers used by catalog models referenced by active assistants. Status reflects required connection fields.
          </p>
        </div>
        {!overview ? (
          <p className="px-5 py-4 text-sm text-gray-500">
            Load the overview to see which chat providers are in use.
          </p>
        ) : chatRows.length === 0 ? (
          <p className="px-5 py-4 text-sm text-gray-500">
            No chat providers are in use by any active assistant yet.
          </p>
        ) : (
          <ul className="divide-y divide-gray-100">
            {chatRows.map((row) => (
              <li key={row.section} className="flex flex-wrap items-center justify-between gap-3 px-5 py-3">
                <div className="min-w-0">
                  <div className="font-medium text-gray-900">{row.displayLabel}</div>
                </div>
                <div className="flex shrink-0 items-center gap-2">
                  <StatusPill status={row.ready ? 'ready' : 'not-ready'} />
                  <TextActionButton
                    tone="neutral"
                    icon={<FaExternalLinkAlt />}
                    onClick={() => onOpenConnections(row.section)}
                    title="Open in Connections."
                  >
                    Open
                  </TextActionButton>
                </div>
              </li>
            ))}
          </ul>
        )}
      </section>

      <section className="overflow-hidden rounded-lg border border-gray-200 bg-white">
        <div className="border-b border-gray-200 px-5 py-3">
          <h3 className="text-sm font-semibold text-gray-900">Services</h3>
          <p className="mt-1 text-xs text-gray-500">
            Non-chat routed services. Status shows whether each service has been configured yet, and whether its current setup is ready.
          </p>
        </div>
        <ul className="divide-y divide-gray-100">
          {NON_CHAT_SERVICES.map(({ id, label }) => {
            const rowState = serviceStates[id];
            const rollup = overview?.serviceModeReadiness.find((service) => service.service === id);
            const pillStatus = rollup && rollup.total === 0 ? 'not-configured' : (rowState?.status ?? 'loading');
            return (
              <li key={id} className="flex flex-wrap items-center justify-between gap-3 px-5 py-3">
                <div className="min-w-0">
                  <div className="font-medium text-gray-900">{label}</div>
                  <div className="font-mono text-xs text-gray-500">{id}</div>
                </div>
                <div className="flex shrink-0 items-center gap-2">
                  {pillStatus === 'unavailable' ? (
                    <span
                      className="inline-flex items-center rounded-md bg-gray-100 px-2 py-0.5 text-xs font-medium text-gray-700 ring-1 ring-inset ring-gray-400/30"
                      title={rowState?.error ?? 'Could not load readiness'}
                    >
                      Unavailable
                    </span>
                  ) : (
                    <StatusPill status={pillStatus} />
                  )}
                  <TextActionButton
                    tone="neutral"
                    icon={<FaExternalLinkAlt />}
                    onClick={() => onOpenServices(id)}
                    title="Open in Services."
                  >
                    Open
                  </TextActionButton>
                </div>
              </li>
            );
          })}
        </ul>
      </section>
    </div>
  );
}
