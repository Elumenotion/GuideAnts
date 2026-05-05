import { useEffect, useRef, useState } from 'react';
import { FaDownload, FaPlay, FaSpinner, FaStop, FaTimes, FaTrash } from 'react-icons/fa';
import { ConfirmationDialog } from '../../../../components/common/ConfirmationDialog';
import { api } from '../../../../services/api';
import { IconActionButton, TextActionButton } from '../../components/shared/ActionButtons';
import { LocalCapabilityFrame, type LocalCapabilityPhase } from '../../components/shared/LocalCapabilityFrame';
import { SettingsModal } from '../../components/shared/SettingsModal';
import type {
  HuggingFaceRepositoryListingDto,
  LocalModelsUpstreamFailure,
} from '../../../../types/settings';
import { RepositoryFilePicker, snapshotPreviewClassifier } from '../common';
import { isSelectableLocalVoiceModelEntry } from '../common/localModelSelection';
import {
  isOperationFailedStatus,
  isOperationInFlight,
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
  status: string;
  error?: string | null;
};

const SERVICE_ID = 'Embeddings';

interface EmbRuntimeManagerProps {
  enabled: boolean;
  onDownloadOperationChange?: (state: LocalDownloadOperationState | null) => void;
  onRuntimeReadinessChange?: (state: LocalRuntimeReadinessState | null) => void;
}

export function EmbRuntimeManager({
  enabled,
  onDownloadOperationChange,
  onRuntimeReadinessChange,
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
  const [engineBusy, setEngineBusy] = useState<null | { op: 'load' | 'unload'; modelRef?: string }>(null);

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
        onTerminal: () => {
          stopPolling();
          void refresh();
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

  const handleUnload = async () => {
    if (engineBusy !== null || hasInFlightDownload) return;
    setActionError(null);
    setEngineBusy({ op: 'unload' });
    try {
      await api.settings.localModels.unload(SERVICE_ID);
      await refresh();
    } catch (e) {
      const err = e as { status?: number };
      if (err?.status === 409) {
        setActionError('Another load or unload is already in progress. Try again.');
      } else {
        setActionError(e instanceof Error ? e.message : 'Unload failed.');
      }
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
        <EngineStatusPanel
          readiness={readiness}
          engineBusy={engineBusy}
          onUnload={() => void handleUnload()}
        />

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
                    No embedding models installed. Click <span className="font-medium">Add model</span> to fetch one from Hugging Face.
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
                          Loaded
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
                              ? 'This model is already loaded.'
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
                : 'Add a new embedding model from Hugging Face.'
            }
          >
            Add model
          </TextActionButton>
        </div>

        {actionError ? <div className="text-xs text-red-700">{actionError}</div> : null}
      </LocalCapabilityFrame>

      <DownloadModelDialog
        isOpen={downloadOpen}
        onClose={() => setDownloadOpen(false)}
        onSubmit={startDownload}
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
  engineBusy,
  onUnload,
}: {
  readiness: EmbReadiness | undefined;
  engineBusy: null | { op: 'load' | 'unload'; modelRef?: string };
  onUnload: () => void;
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
        <div className="flex gap-1">
          <TextActionButton
            tone="neutral"
            icon={engineBusy?.op === 'unload' ? <FaSpinner className="animate-spin" /> : <FaStop />}
            disabled={engineBusy !== null || !readiness.loaded}
            onClick={onUnload}
            title={readiness.loaded ? 'Drop the loaded model to release GPU / RAM.' : 'No model is currently loaded.'}
          >
            Unload
          </TextActionButton>
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

function DownloadModelDialog({
  isOpen,
  onClose,
  onSubmit,
}: {
  isOpen: boolean;
  onClose: () => void;
  onSubmit: (values: { modelId: string; revision: string }) => Promise<void>;
}) {
  const [modelId, setModelId] = useState('');
  const [revision, setRevision] = useState('');
  const [submitting, setSubmitting] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  const [browsed, setBrowsed] = useState<HuggingFaceRepositoryListingDto | null>(null);
  const [browseError, setBrowseError] = useState<{ code: string | null; message: string } | null>(null);

  useEffect(() => {
    if (isOpen) {
      setModelId('');
      setRevision('');
      setErr(null);
      setSubmitting(false);
      setBrowsed(null);
      setBrowseError(null);
    }
  }, [isOpen]);

  const submit = async () => {
    if (!browsed) {
      setErr('Browse the repository first so the listing can be previewed.');
      return;
    }
    setSubmitting(true);
    setErr(null);
    try {
      await onSubmit({ modelId: browsed.repository, revision });
    } catch (e) {
      setErr(e instanceof Error ? e.message : 'Download failed to start.');
    } finally {
      setSubmitting(false);
    }
  };

  const disableSubmit = submitting || !browsed;
  return (
    <SettingsModal
      isOpen={isOpen}
      title="Add embedding model from Hugging Face"
      onClose={onClose}
      disableDismiss={submitting}
      footer={(
        <>
          <TextActionButton tone="neutral" disabled={submitting} onClick={onClose}>
            Cancel
          </TextActionButton>
          <TextActionButton
            tone="primary"
            icon={submitting ? <FaSpinner className="animate-spin" /> : <FaDownload />}
            disabled={disableSubmit}
            onClick={() => void submit()}
            title={
              browsed
                ? 'Download the Hugging Face snapshot to this embeddings service.'
                : 'Browse the repository first — the embeddings service refuses to download without a verified listing.'
            }
          >
            Download snapshot
          </TextActionButton>
        </>
      )}
    >
      <div className="space-y-3 text-sm">
        <p className="text-xs text-gray-600">
          The embeddings service downloads the whole Hugging Face snapshot into its model directory. Browse a repo
          (for example <span className="font-mono">microsoft/harrier-oss-v1-0.6b</span>), confirm the file list,
          then start the download.
        </p>

        <RepositoryFilePicker
          repository={modelId}
          onRepositoryChange={(next) => {
            setModelId(next);
            setBrowsed(null);
          }}
          previewOnly
          classifyPreview={snapshotPreviewClassifier}
          onBrowseResolved={(listing) => {
            setBrowsed(listing);
            setErr(null);
          }}
          onBrowseError={(e) => {
            setBrowseError(e);
            if (e) {
              setBrowsed(null);
            }
          }}
          serviceOrigin="Embeddings"
          repoInputLabel="Hugging Face model id"
          repoInputPlaceholder="microsoft/harrier-oss-v1-0.6b"
          repoInputHint="Paste owner/repo or a Hugging Face model URL."
          disabled={submitting}
        />

        <label className="block">
          <span className="mb-1 block text-xs font-medium text-gray-700">Revision (optional)</span>
          <input
            type="text"
            value={revision}
            onChange={(e) => setRevision(e.target.value)}
            disabled={submitting}
            className="w-full rounded border border-gray-300 px-2 py-1.5 text-sm disabled:bg-gray-100"
            placeholder="main"
          />
          <span className="mt-1 block text-[11px] text-gray-500">
            Leave blank to use the default branch.
          </span>
        </label>

        {err ? <p className="text-xs text-red-700">{err}</p> : null}
        {browseError && !err ? (
          <p className="text-xs text-red-700">
            Browse reported: {browseError.message} Fix the repository before starting the download.
          </p>
        ) : null}
      </div>
    </SettingsModal>
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
