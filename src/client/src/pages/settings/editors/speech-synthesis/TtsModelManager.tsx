import { useEffect, useRef, useState } from 'react';
import { FaDownload, FaPlay, FaSpinner, FaTimes, FaTrash } from 'react-icons/fa';
import { ConfirmationDialog } from '../../../../components/common/ConfirmationDialog';
import { api } from '../../../../services/api';
import { IconActionButton, TextActionButton } from '../../components/shared/ActionButtons';
import { LocalCapabilityFrame, type LocalCapabilityPhase } from '../../components/shared/LocalCapabilityFrame';
import { CatalogDownloadModelDialog } from '../common/CatalogDownloadModelDialog';
import type {
  LocalModelsUpstreamFailure,
} from '../../../../types/settings';
import { isSelectableLocalVoiceModelEntry } from '../common/localModelSelection';
import {
  isOperationFailedStatus,
  isOperationInFlight,
  normalizeOperationStatus,
  isOperationTerminalStatus,
  LOCAL_OPERATION_UNREACHABLE_MESSAGE,
  type LocalDownloadOperationState,
  type LocalRuntimeReadinessState,
  startLocalOperationPoll,
} from '../common/localOperationPolling';

type TtsModelEntry = {
  modelRef: string;
  path?: string;
  isDirectory?: boolean;
  activeModel?: boolean;
  activeTokenizer?: boolean;
};

type TtsListPayload = {
  modelDir?: string;
  items?: TtsModelEntry[];
};

type TtsReadiness = {
  ready?: boolean;
  loaded?: boolean;
  modelRef?: string | null;
  tokenizerRef?: string | null;
  loadedAtUtc?: string | null;
  voice?: string | null;
  langCode?: string | null;
  speed?: number | null;
  warmupEnabled?: boolean;
  warmupRan?: boolean;
  warmupSucceeded?: boolean;
  warmupError?: string | null;
};

type DownloadOp = {
  operationId: string;
  modelId?: string;
  modelRef?: string;
  status: string;
  error?: string | null;
};

const SERVICE_ID = 'SpeechSynthesis';

interface TtsModelManagerProps {
  enabled: boolean;
  onDownloadOperationChange?: (state: LocalDownloadOperationState | null) => void;
  onRuntimeReadinessChange?: (state: LocalRuntimeReadinessState | null) => void;
}

export function TtsModelManager({ enabled, onDownloadOperationChange, onRuntimeReadinessChange }: TtsModelManagerProps) {
  const [phase, setPhase] = useState<LocalCapabilityPhase>('loading');
  const [list, setList] = useState<TtsListPayload | undefined>(undefined);
  const [readiness, setReadiness] = useState<TtsReadiness | undefined>(undefined);
  const [errorMessage, setErrorMessage] = useState<string | undefined>(undefined);
  const [errorUpstream, setErrorUpstream] = useState<LocalModelsUpstreamFailure | undefined>(undefined);
  const [actionError, setActionError] = useState<string | null>(null);
  const [downloadOpen, setDownloadOpen] = useState(false);
  const [pendingRemove, setPendingRemove] = useState<string | null>(null);
  const [removing, setRemoving] = useState(false);
  const [activeDownload, setActiveDownload] = useState<DownloadOp | null>(null);
  const [engineBusy, setEngineBusy] = useState<null | { op: 'load'; modelRef?: string }>(null);

  const pollRef = useRef<number | null>(null);
  const hasInFlightDownload = activeDownload !== null && isOperationInFlight(activeDownload.status);

  const stopPolling = () => {
    if (pollRef.current != null) {
      window.clearInterval(pollRef.current);
      pollRef.current = null;
    }
  };

  const refresh = async (): Promise<void> => {
    setActionError(null);
    setPhase((p) => (p === 'available' ? 'available' : 'loading'));
    const [listOutcome, readyOutcome] = await Promise.all([
      api.settings.localModels.listOutcome(SERVICE_ID),
      api.settings.localModels.runtimeReadinessOutcome(SERVICE_ID),
    ]);
    if (listOutcome.kind === 'error') {
      setPhase('error');
      setList(undefined);
      setReadiness(undefined);
      setErrorMessage(listOutcome.message);
      setErrorUpstream(listOutcome.upstream);
      return;
    }
    setList(listOutcome.payload as TtsListPayload);
    if (readyOutcome.kind === 'available') {
      setReadiness(readyOutcome.payload as TtsReadiness);
    } else {
      setReadiness(undefined);
    }
    setPhase('available');
    setErrorMessage(undefined);
    setErrorUpstream(undefined);
  };

  const tryAutoLoadDownloadedModel = async (operation: DownloadOp): Promise<void> => {
    if (normalizeOperationStatus(operation.status) !== 'completed') {
      return;
    }
    const modelRef = operation.modelRef?.trim();
    if (!modelRef) {
      return;
    }

    setEngineBusy({ op: 'load', modelRef });
    try {
      await api.settings.localModels.load(SERVICE_ID, { model_path: modelRef });
    } finally {
      setEngineBusy(null);
    }
  };

  useEffect(() => {
    if (!enabled) {
      setPhase('hidden');
      return;
    }
    void refresh();
  }, [enabled]);

  useEffect(() => {
    return () => {
      stopPolling();
      onDownloadOperationChange?.(null);
      onRuntimeReadinessChange?.(null);
    };
  }, [onDownloadOperationChange, onRuntimeReadinessChange]);

  useEffect(() => {
    if (!onDownloadOperationChange) {
      return;
    }
    if (!activeDownload) {
      onDownloadOperationChange(null);
      return;
    }
    onDownloadOperationChange({
      serviceId: SERVICE_ID,
      operationId: activeDownload.operationId,
      status: activeDownload.status,
      inFlight: isOperationInFlight(activeDownload.status),
      error: activeDownload.error ?? null,
    });
  }, [activeDownload, onDownloadOperationChange]);

  useEffect(() => {
    if (!onRuntimeReadinessChange) {
      return;
    }
    if (!enabled || phase === 'hidden') {
      onRuntimeReadinessChange(null);
      return;
    }
    if (phase === 'loading') {
      onRuntimeReadinessChange(null);
      return;
    }
    if (phase === 'error') {
      onRuntimeReadinessChange({
        serviceId: SERVICE_ID,
        ready: false,
        status: 'Local TTS service unavailable',
        detail: errorMessage ?? null,
      });
      return;
    }
    if (!readiness) {
      onRuntimeReadinessChange({
        serviceId: SERVICE_ID,
        ready: false,
        status: 'Runtime readiness probe unavailable',
      });
      return;
    }
    if (readiness.ready) {
      onRuntimeReadinessChange({
        serviceId: SERVICE_ID,
        ready: true,
        status: 'Ready',
      });
      return;
    }
    onRuntimeReadinessChange({
      serviceId: SERVICE_ID,
      ready: false,
      status: readiness.loaded ? 'Loaded, warmup pending' : 'No model loaded',
    });
  }, [enabled, errorMessage, onRuntimeReadinessChange, phase, readiness]);

  const startDownload = async (values: { modelId: string; revision: string }) => {
    setActionError(null);
    const body: Record<string, unknown> = { model_id: values.modelId.trim() };
    if (values.revision.trim()) {
      body.revision = values.revision.trim();
    }
    try {
      const op = (await api.settings.localModels.startDownload(SERVICE_ID, body)) as DownloadOp;
      setDownloadOpen(false);
      setActiveDownload(op);
      stopPolling();
      pollRef.current = startLocalOperationPoll<DownloadOp>({
        poll: () => api.settings.localModels.getOperation(SERVICE_ID, op.operationId) as Promise<DownloadOp>,
        onUpdate: (latest) => setActiveDownload(latest),
        onTerminal: (latest) => {
          stopPolling();
          void (async () => {
            try {
              await tryAutoLoadDownloadedModel(latest);
            } catch (e) {
              setActionError(e instanceof Error ? e.message : 'Auto-load failed.');
            } finally {
              await refresh();
            }
          })();
        },
        onPollFailureThreshold: () => {
          stopPolling();
          setActionError(LOCAL_OPERATION_UNREACHABLE_MESSAGE);
          setActiveDownload((previous) =>
            previous
              ? { ...previous, status: 'error', error: LOCAL_OPERATION_UNREACHABLE_MESSAGE }
              : previous
          );
        },
      });
    } catch (e) {
      setActionError(e instanceof Error ? e.message : 'Download failed to start.');
    }
  };

  const handleCancelDownload = async () => {
    if (!activeDownload || !isOperationInFlight(activeDownload.status)) {
      return;
    }
    setActionError(null);
    try {
      const latest = (await api.settings.localModels.cancelOperation(SERVICE_ID, activeDownload.operationId)) as DownloadOp;
      setActiveDownload(latest);
      if (isOperationTerminalStatus(latest.status)) {
        stopPolling();
        void refresh();
      }
    } catch (e) {
      setActionError(e instanceof Error ? e.message : 'Cancel failed.');
    }
  };

  const handleLoadRow = async (modelRef: string) => {
    if (engineBusy !== null || hasInFlightDownload) return;
    setActionError(null);
    setEngineBusy({ op: 'load', modelRef });
    try {
      await api.settings.localModels.load(SERVICE_ID, { model_path: modelRef });
      await refresh();
    } catch (e) {
      setActionError(e instanceof Error ? e.message : 'Load failed.');
    } finally {
      setEngineBusy(null);
    }
  };

  const removeConfirmed = async () => {
    if (!pendingRemove) return;
    setRemoving(true);
    setActionError(null);
    try {
      await api.settings.localModels.remove(SERVICE_ID, pendingRemove);
      setPendingRemove(null);
      await refresh();
    } catch (e) {
      setActionError(e instanceof Error ? e.message : 'Remove failed.');
    } finally {
      setRemoving(false);
    }
  };

  const items = (list?.items ?? []).filter((m) => isSelectableLocalVoiceModelEntry(m));

  return (
    <>
      <LocalCapabilityFrame
        title="Local TTS model"
        phase={phase}
        errorMessage={errorMessage}
        upstream={errorUpstream}
        onRefresh={phase === 'available' || phase === 'error' ? () => void refresh() : undefined}
      >
        <EngineStatusPanel readiness={readiness} />

        {activeDownload ? (
          <DownloadOperationStatus
            operation={activeDownload}
            onCancel={isOperationInFlight(activeDownload.status) ? () => void handleCancelDownload() : undefined}
          />
        ) : null}

        {list?.modelDir ? (
          <p className="text-xs text-gray-500">
            Model directory on the TTS container: <span className="font-mono">{list.modelDir}</span>
          </p>
        ) : null}

        <div className="overflow-hidden rounded border border-gray-200">
          <table className="w-full table-fixed text-sm">
            <colgroup>
              <col />
              <col className="w-[14%]" />
              <col className="w-[22%]" />
              <col className="w-[22%]" />
            </colgroup>
            <thead className="bg-gray-50 text-xs uppercase tracking-wide text-gray-600">
              <tr>
                <th className="px-3 py-2 text-left">Entry</th>
                <th className="px-3 py-2 text-left">Kind</th>
                <th className="px-3 py-2 text-left">Status</th>
                <th className="px-3 py-2 text-right">Actions</th>
              </tr>
            </thead>
            <tbody>
              {items.length === 0 ? (
                <tr>
                  <td colSpan={4} className="px-3 py-4 text-center text-sm text-gray-500">
                    No TTS models installed. Click <span className="font-medium">Add model</span> to download one from the catalog.
                  </td>
                </tr>
              ) : null}
              {items.map((m) => {
                const isLoaded = !!m.activeModel;
                const isBusyRow =
                  engineBusy !== null && engineBusy.op === 'load' && engineBusy.modelRef === m.modelRef;
                const disableLoad = engineBusy !== null || hasInFlightDownload || isLoaded;
                const disableRemove = engineBusy !== null || hasInFlightDownload || isLoaded || !!m.activeTokenizer;
                return (
                  <tr key={m.modelRef} className="border-t border-gray-100">
                    <td className="px-3 py-2 font-mono text-xs text-gray-900">{m.modelRef}</td>
                    <td className="px-3 py-2 text-xs text-gray-700">{m.isDirectory ? 'directory' : 'file'}</td>
                    <td className="px-3 py-2 text-xs">
                      <div className="flex flex-wrap gap-1">
                        {m.activeModel ? (
                          <span className="inline-flex items-center rounded bg-blue-100 px-2 py-0.5 font-semibold text-blue-800">
                            Loaded (model)
                          </span>
                        ) : null}
                        {m.activeTokenizer ? (
                          <span className="inline-flex items-center rounded bg-blue-100 px-2 py-0.5 font-semibold text-blue-800">
                            Loaded (tokenizer)
                          </span>
                        ) : null}
                        {!m.activeModel && !m.activeTokenizer ? <span className="text-gray-500">—</span> : null}
                      </div>
                    </td>
                    <td className="px-3 py-2 text-right">
                      <div
                        className="flex items-center justify-end gap-1.5"
                        role="group"
                        aria-label={`Actions for TTS model ${m.modelRef}`}
                      >
                        <IconActionButton
                          label="Load"
                          tone="success"
                          icon={isBusyRow ? <FaSpinner className="animate-spin" /> : <FaPlay />}
                          disabled={disableLoad}
                          onClick={() => void handleLoadRow(m.modelRef)}
                          title={
                            isLoaded
                              ? 'This model is already loaded.'
                              : 'Load this model into the TTS engine.'
                          }
                        />
                        <IconActionButton
                          label="Remove"
                          tone="danger"
                          icon={<FaTrash />}
                          disabled={disableRemove}
                          onClick={() => setPendingRemove(m.modelRef)}
                          title={
                            isLoaded || m.activeTokenizer
                              ? 'Cannot remove a loaded model or tokenizer — unload first.'
                              : 'Delete this entry from disk.'
                          }
                        />
                      </div>
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>

        <div className="flex justify-end">
          <TextActionButton
            tone="primary"
            icon={<FaDownload />}
            disabled={engineBusy !== null || hasInFlightDownload}
            onClick={() => setDownloadOpen(true)}
            title={
              engineBusy !== null || hasInFlightDownload
                ? 'Wait for the current operation to finish or cancel it first.'
                : 'Add a curated catalog TTS model.'
            }
          >
            Add model
          </TextActionButton>
        </div>

        {actionError ? <div className="text-xs text-red-700">{actionError}</div> : null}
      </LocalCapabilityFrame>

      <CatalogDownloadModelDialog
        serviceId="SpeechSynthesis"
        isOpen={downloadOpen}
        onClose={() => setDownloadOpen(false)}
        onSubmit={startDownload}
        title="Download curated TTS model"
        description="Local TTS is constrained to the curated catalog. Voice selection in provider settings follows the loaded model's voiceInput (reference pack, built-in speaker, optional reference, or voice-design text)."
        submitLabel="Download snapshot"
        submitTitle="Download the selected catalog snapshot from its allowlisted Hugging Face source."
      />

      <ConfirmationDialog
        isOpen={pendingRemove !== null}
        title="Remove TTS model"
        message={
          pendingRemove
            ? `Remove "${pendingRemove}" from the TTS container's model directory? This cannot be undone.`
            : ''
        }
        confirmText="Remove"
        onClose={() => (removing ? undefined : setPendingRemove(null))}
        onConfirm={() => void removeConfirmed()}
        isLoading={removing}
      />
    </>
  );
}

function EngineStatusPanel({
  readiness,
}: {
  readiness: TtsReadiness | undefined;
}) {
  if (!readiness) {
    return (
      <div className="rounded border border-gray-200 bg-gray-50 p-3 text-xs text-gray-500">
        Runtime readiness probe not available. The model list below reflects disk state only.
      </div>
    );
  }
  const readyLabel = readiness.ready ? 'Ready' : readiness.loaded ? 'Loaded, warmup pending' : 'Not loaded';
  const color = readiness.ready
    ? 'bg-green-100 text-green-800'
    : readiness.loaded
    ? 'bg-amber-100 text-amber-800'
    : 'bg-gray-100 text-gray-800';
  return (
    <div
      className={`rounded border p-3 text-xs ${
        readiness.loaded ? 'border-blue-200 bg-blue-50 text-blue-900' : 'border-gray-200 bg-gray-50 text-gray-800'
      }`}
    >
      <div className="flex flex-wrap items-center justify-between gap-2">
        <div className="flex flex-wrap items-center gap-2">
          <span className="font-semibold">TTS engine:</span>
          <span className={`inline-flex items-center rounded px-2 py-0.5 font-semibold ${color}`}>{readyLabel}</span>
          {readiness.loaded ? (
            <>
              <span className="text-gray-700">
                model <span className="font-mono">{readiness.modelRef ?? '—'}</span>
              </span>
              {readiness.voice ? (
                <span className="text-gray-700">
                  voice <span className="font-mono">{readiness.voice}</span>
                </span>
              ) : null}
              {readiness.langCode ? (
                <span className="text-gray-700">
                  language <span className="font-mono">{readiness.langCode}</span>
                </span>
              ) : null}
            </>
          ) : (
            <span className="text-gray-500">No model loaded.</span>
          )}
        </div>
      </div>
      {readiness.warmupEnabled ? (
        <p className="mt-1 text-[11px] text-gray-600">
          Warmup: {readiness.warmupSucceeded ? 'succeeded' : readiness.warmupRan ? 'failed' : 'pending'}
          {readiness.warmupError ? <span className="ml-1 font-mono text-red-700">— {readiness.warmupError}</span> : null}
        </p>
      ) : null}
    </div>
  );
}

function DownloadOperationStatus({ operation, onCancel }: { operation: DownloadOp; onCancel?: () => void }) {
  const failed = isOperationFailedStatus(operation.status);
  const completed = !failed && !isOperationInFlight(operation.status);
  return (
    <div
      className={`rounded border p-3 text-xs ${
        failed
          ? 'border-red-300 bg-red-50 text-red-800'
          : completed
          ? 'border-green-300 bg-green-50 text-green-800'
          : 'border-blue-300 bg-blue-50 text-blue-800'
      }`}
    >
      <div className="flex items-center justify-between gap-2">
        <div className="font-semibold">
          Download {operation.modelId ? `"${operation.modelId}"` : ''}: {operation.status}
        </div>
        {onCancel ? (
          <TextActionButton tone="danger" icon={<FaTimes />} onClick={onCancel} title="Cancel this download operation.">
            Cancel
          </TextActionButton>
        ) : null}
      </div>
      {operation.error ? <div className="mt-1 font-mono">{operation.error}</div> : null}
    </div>
  );
}
