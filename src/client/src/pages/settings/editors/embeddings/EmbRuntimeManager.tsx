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

type EmbModelEntry = {
  modelRef: string;
  path?: string;
  isDirectory?: boolean;
  sizeBytes?: number;
  active?: boolean;
};

type EmbListPayload = {
  modelDir?: string;
  items?: EmbModelEntry[];
};

type EmbReadiness = {
  ready?: boolean;
  loaded?: boolean;
  loading?: boolean;
  modelRef?: string | null;
  device?: string | null;
  dimensions?: number | null;
  loadedAtUtc?: string | null;
  loadError?: string | null;
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

const SERVICE_ID = 'Embeddings';

interface EmbRuntimeManagerProps {
  enabled: boolean;
  onDownloadOperationChange?: (state: LocalDownloadOperationState | null) => void;
  onRuntimeReadinessChange?: (state: LocalRuntimeReadinessState | null) => void;
  onModelAutoLoaded?: (modelRef: string) => void;
}

export function EmbRuntimeManager({
  enabled,
  onDownloadOperationChange,
  onRuntimeReadinessChange,
  onModelAutoLoaded,
}: EmbRuntimeManagerProps) {
  const [phase, setPhase] = useState<LocalCapabilityPhase>('loading');
  const [list, setList] = useState<EmbListPayload | undefined>(undefined);
  const [readiness, setReadiness] = useState<EmbReadiness | undefined>(undefined);
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
      onModelAutoLoaded?.(modelRef);
    } finally {
      setEngineBusy(null);
    }
  };

  const refresh = async (): Promise<void> => {
    setActionError(null);
    setPhase((p) => (p === 'available' ? 'available' : 'loading'));
    const [listOutcome, readyOutcome] = await Promise.all([
      api.settings.localModels.listOutcome(SERVICE_ID),
      api.settings.localModels.runtimeReadinessOutcome(SERVICE_ID),
    ]);
    if (!listOutcome || (listOutcome.kind !== 'available' && listOutcome.kind !== 'error')) {
      setPhase('error');
      setList(undefined);
      setReadiness(undefined);
      setErrorMessage('Model list response was unavailable.');
      setErrorUpstream(undefined);
      return;
    }
    if (listOutcome.kind === 'error') {
      setPhase('error');
      setList(undefined);
      setReadiness(undefined);
      setErrorMessage(listOutcome.message);
      setErrorUpstream(listOutcome.upstream);
      return;
    }
    setList(listOutcome.payload as EmbListPayload);
    if (readyOutcome.kind === 'available') {
      setReadiness(readyOutcome.payload as EmbReadiness);
    } else {
      setReadiness(undefined);
    }
    setPhase('available');
    setErrorMessage(undefined);
    setErrorUpstream(undefined);
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
        status: 'Local embeddings service unavailable',
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
      status: readiness.loading ? 'Loading model' : readiness.loaded ? 'Loaded, warmup pending' : 'No model loaded',
      detail: readiness.loadError ?? readiness.warmupError ?? null,
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
        title="Local embedding model"
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
            Model directory on the embeddings container: <span className="font-mono">{list.modelDir}</span>
          </p>
        ) : null}

        <div className="overflow-hidden rounded border border-gray-200">
          <table className="w-full table-fixed text-sm">
            <colgroup>
              <col />
              <col className="w-[22%]" />
              <col className="w-[14%]" />
              <col className="w-[22%]" />
            </colgroup>
            <thead className="bg-gray-50 text-xs uppercase tracking-wide text-gray-600">
              <tr>
                <th className="px-3 py-2 text-left">Model</th>
                <th className="px-3 py-2 text-left">Kind</th>
                <th className="px-3 py-2 text-left">Status</th>
                <th className="px-3 py-2 text-right">Actions</th>
              </tr>
            </thead>
            <tbody>
              {items.length === 0 ? (
                <tr>
                  <td colSpan={4} className="px-3 py-4 text-center text-sm text-gray-500">
                    No embedding models installed. Click <span className="font-medium">Add model</span> to download one from the catalog.
                  </td>
                </tr>
              ) : null}
              {items.map((m) => {
                const isBusyRow =
                  engineBusy !== null && engineBusy.op === 'load' && engineBusy.modelRef === m.modelRef;
                const disableLoad = engineBusy !== null || hasInFlightDownload || !!m.active;
                const disableRemove = engineBusy !== null || hasInFlightDownload || !!m.active;
                return (
                  <tr key={m.modelRef} className="border-t border-gray-100">
                    <td className="px-3 py-2 font-mono text-xs text-gray-900">{m.modelRef}</td>
                    <td className="px-3 py-2 text-xs text-gray-700">
                      {m.isDirectory ? 'directory' : `${formatBytes(m.sizeBytes ?? 0)} file`}
                    </td>
                    <td className="px-3 py-2 text-xs">
                      {m.active ? (
                        <span className="inline-flex items-center rounded bg-blue-100 px-2 py-0.5 font-semibold text-blue-800">
                          Selected
                        </span>
                      ) : (
                        <span className="text-gray-500">—</span>
                      )}
                    </td>
                    <td className="px-3 py-2 text-right">
                      <div
                        className="flex items-center justify-end gap-1.5"
                        role="group"
                        aria-label={`Actions for embedding model ${m.modelRef}`}
                      >
                        <IconActionButton
                          label="Load"
                          tone="success"
                          icon={isBusyRow ? <FaSpinner className="animate-spin" /> : <FaPlay />}
                          disabled={disableLoad}
                          onClick={() => void handleLoadRow(m.modelRef)}
                          title={
                            m.active
                              ? 'This model is selected on disk. Load remains available if runtime is not currently ready.'
                              : 'Load this model into the embedding engine.'
                          }
                        />
                        <IconActionButton
                          label="Remove"
                          tone="danger"
                          icon={<FaTrash />}
                          disabled={disableRemove}
                          onClick={() => setPendingRemove(m.modelRef)}
                          title={
                            m.active
                              ? 'Cannot remove the loaded model — unload it first.'
                              : 'Delete this model from disk.'
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
                : 'Add a curated catalog embedding model.'
            }
          >
            Add model
          </TextActionButton>
        </div>

        {actionError ? <div className="text-xs text-red-700">{actionError}</div> : null}
      </LocalCapabilityFrame>

      <CatalogDownloadModelDialog
        serviceId="Embeddings"
        isOpen={downloadOpen}
        onClose={() => setDownloadOpen(false)}
        onSubmit={startDownload}
        title="Download curated embedding model"
        description="Embeddings models are constrained to the curated catalog. Only verified GGUF embedders with produced dimension ≤ 1536 are offered. Free-form Hugging Face browse is not supported."
        submitLabel="Download GGUF"
        submitTitle="Download the selected GGUF from its allowlisted Hugging Face source."
      />

      <ConfirmationDialog
        isOpen={pendingRemove !== null}
        title="Remove embedding model"
        message={
          pendingRemove
            ? `Remove local embedding model "${pendingRemove}" from the container's model directory? This cannot be undone.`
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
  readiness: EmbReadiness | undefined;
}) {
  if (!readiness) {
    return (
      <div className="rounded border border-gray-200 bg-gray-50 p-3 text-xs text-gray-500">
        Runtime readiness probe not available. The model list below reflects disk state only.
      </div>
    );
  }
  const readyLabel = readiness.ready
    ? 'Ready'
    : readiness.loading
    ? 'Loading…'
    : readiness.loaded
    ? 'Loaded, warmup pending'
    : 'Not loaded';
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
          <span className="font-semibold">Embedding engine:</span>
          <span className={`inline-flex items-center rounded px-2 py-0.5 font-semibold ${color}`}>{readyLabel}</span>
          {readiness.loaded && readiness.modelRef ? (
            <span className="text-gray-700">
              model <span className="font-mono">{readiness.modelRef}</span>
            </span>
          ) : (
            <span className="text-gray-500">No model loaded.</span>
          )}
          {readiness.device ? <span className="text-gray-500">device {readiness.device}</span> : null}
          {readiness.dimensions ? <span className="text-gray-500">{readiness.dimensions}-d</span> : null}
        </div>
      </div>
      {readiness.warmupEnabled ? (
        <p className="mt-1 text-[11px] text-gray-600">
          Warmup: {readiness.warmupSucceeded ? 'succeeded' : readiness.warmupRan ? 'failed' : 'pending'}
          {readiness.warmupError ? <span className="ml-1 font-mono text-red-700">— {readiness.warmupError}</span> : null}
        </p>
      ) : null}
      {readiness.loadError ? (
        <p className="mt-1 font-mono text-[11px] text-red-700">Load error: {readiness.loadError}</p>
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

function formatBytes(n: number): string {
  if (!n || n <= 0) {
    return '0 B';
  }
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let value = n;
  let i = 0;
  while (value >= 1024 && i < units.length - 1) {
    value /= 1024;
    i += 1;
  }
  return `${value.toFixed(value >= 100 ? 0 : 1)} ${units[i]}`;
}
