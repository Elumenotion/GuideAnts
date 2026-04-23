import { useEffect, useMemo, useRef, useState } from 'react';
import { FaDownload, FaPlay, FaSpinner, FaStop, FaTrash } from 'react-icons/fa';
import { ConfirmationDialog } from '../../../../components/common/ConfirmationDialog';
import { api } from '../../../../services/api';
import { IconActionButton, TextActionButton } from '../../components/shared/ActionButtons';
import { LocalCapabilityFrame, type LocalCapabilityPhase } from '../../components/shared/LocalCapabilityFrame';
import { SettingsModal } from '../../components/shared/SettingsModal';
import type {
  HuggingFaceRepositoryFileDto,
  HuggingFaceRepositoryListingDto,
  LocalModelsUpstreamFailure,
} from '../../../../types/settings';
import {
  RepositoryFilePicker,
  snapshotPreviewClassifier,
  withSnapshotExtraBadges,
} from '../common';

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
};

type DownloadOp = {
  operationId: string;
  modelId?: string;
  tokenizerId?: string;
  status: string;
  error?: string | null;
};

const SERVICE_ID = 'SpeechSynthesis';
const isVisibleModelEntry = (modelRef: string): boolean => !modelRef.trim().startsWith('.');

interface TtsModelManagerProps {
  enabled: boolean;
}

export function TtsModelManager({ enabled }: TtsModelManagerProps) {
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
  const [engineBusy, setEngineBusy] = useState<null | { op: 'load' | 'unload'; modelRef?: string }>(null);

  const pollRef = useRef<number | null>(null);

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

  useEffect(() => {
    if (!enabled) {
      setPhase('hidden');
      return;
    }
    void refresh();
  }, [enabled]);

  useEffect(() => {
    return () => {
      if (pollRef.current != null) {
        window.clearInterval(pollRef.current);
        pollRef.current = null;
      }
    };
  }, []);

  const startDownload = async (values: { modelId: string; tokenizerId: string; revision: string }) => {
    setActionError(null);
    const body: Record<string, unknown> = { model_id: values.modelId.trim() };
    if (values.tokenizerId.trim()) {
      body.tokenizer_id = values.tokenizerId.trim();
    }
    if (values.revision.trim()) {
      body.revision = values.revision.trim();
    }
    const op = (await api.settings.localModels.startDownload(SERVICE_ID, body)) as DownloadOp;
    setDownloadOpen(false);
    setActiveDownload(op);
    if (pollRef.current != null) {
      window.clearInterval(pollRef.current);
    }
    pollRef.current = window.setInterval(async () => {
      try {
        const latest = (await api.settings.localModels.getOperation(SERVICE_ID, op.operationId)) as DownloadOp;
        setActiveDownload(latest);
        if (latest.status === 'completed' || latest.status === 'failed') {
          if (pollRef.current != null) {
            window.clearInterval(pollRef.current);
            pollRef.current = null;
          }
          void refresh();
        }
      } catch (e) {
        setActionError(e instanceof Error ? e.message : 'Poll failed.');
        if (pollRef.current != null) {
          window.clearInterval(pollRef.current);
          pollRef.current = null;
        }
      }
    }, 2000);
  };

  const handleLoadRow = async (modelRef: string) => {
    if (engineBusy !== null) return;
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
    if (engineBusy !== null) return;
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

  const items = (list?.items ?? []).filter((m) => isVisibleModelEntry(m.modelRef));

  return (
    <>
      <LocalCapabilityFrame
        title="Local TTS model"
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

        {activeDownload ? <DownloadOperationStatus operation={activeDownload} /> : null}

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
                    No TTS models installed. Click <span className="font-medium">Add model</span> to fetch one from Hugging Face.
                  </td>
                </tr>
              ) : null}
              {items.map((m) => {
                const isLoaded = !!m.activeModel;
                const isBusyRow =
                  engineBusy !== null && engineBusy.op === 'load' && engineBusy.modelRef === m.modelRef;
                const disableLoad = engineBusy !== null || isLoaded;
                const disableRemove = engineBusy !== null || isLoaded || !!m.activeTokenizer;
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
            disabled={engineBusy !== null}
            onClick={() => setDownloadOpen(true)}
            title={engineBusy !== null ? 'Wait for the current engine operation to finish.' : 'Add a new TTS model from Hugging Face.'}
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
  engineBusy,
  onUnload,
}: {
  readiness: TtsReadiness | undefined;
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
  const readyLabel = readiness.ready ? 'Ready' : readiness.loaded ? 'Loaded' : 'Not loaded';
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
              <span className="text-gray-700">
                tokenizer <span className="font-mono">{readiness.tokenizerRef ?? '—'}</span>
              </span>
            </>
          ) : (
            <span className="text-gray-500">No model loaded.</span>
          )}
        </div>
        <div className="flex gap-1">
          <TextActionButton
            tone="neutral"
            icon={engineBusy?.op === 'unload' ? <FaSpinner className="animate-spin" /> : <FaStop />}
            disabled={engineBusy !== null || !readiness.loaded}
            onClick={onUnload}
            title={readiness.loaded ? 'Drop the loaded TTS model to release GPU / RAM.' : 'No model is currently loaded.'}
          >
            Unload
          </TextActionButton>
        </div>
      </div>
    </div>
  );
}

function DownloadOperationStatus({ operation }: { operation: DownloadOp }) {
  return (
    <div
      className={`rounded border p-3 text-xs ${
        operation.status === 'failed'
          ? 'border-red-300 bg-red-50 text-red-800'
          : operation.status === 'completed'
          ? 'border-green-300 bg-green-50 text-green-800'
          : 'border-blue-300 bg-blue-50 text-blue-800'
      }`}
    >
      <div className="font-semibold">
        Download {operation.modelId ? `"${operation.modelId}"` : ''}: {operation.status}
      </div>
      {operation.error ? <div className="mt-1 font-mono">{operation.error}</div> : null}
    </div>
  );
}

function voiceBadgesFor(file: HuggingFaceRepositoryFileDto): string[] {
  const path = file.path.toLowerCase();
  const leaf = path.includes('/') ? path.substring(path.lastIndexOf('/') + 1) : path;
  if (
    path.startsWith('voices/')
    || path.includes('/voices/')
    || leaf.endsWith('.npz')
    || leaf.endsWith('.pt')
    || leaf.includes('voice')
  ) {
    return ['voice'];
  }
  return [];
}

const ttsModelPreviewClassifier = withSnapshotExtraBadges(
  snapshotPreviewClassifier,
  voiceBadgesFor,
);

function DownloadModelDialog({
  isOpen,
  onClose,
  onSubmit,
}: {
  isOpen: boolean;
  onClose: () => void;
  onSubmit: (values: { modelId: string; tokenizerId: string; revision: string }) => Promise<void>;
}) {
  const [modelId, setModelId] = useState('');
  const [tokenizerId, setTokenizerId] = useState('');
  const [revision, setRevision] = useState('');
  const [submitting, setSubmitting] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  const [modelListing, setModelListing] = useState<HuggingFaceRepositoryListingDto | null>(null);
  const [tokenizerListing, setTokenizerListing] = useState<HuggingFaceRepositoryListingDto | null>(null);
  const [showTokenizer, setShowTokenizer] = useState(false);

  useEffect(() => {
    if (isOpen) {
      setModelId('');
      setTokenizerId('');
      setRevision('');
      setErr(null);
      setSubmitting(false);
      setModelListing(null);
      setTokenizerListing(null);
      setShowTokenizer(false);
    }
  }, [isOpen]);

  const tokenizerOk = useMemo(() => {
    // Tokenizer is optional; it is "ready" either when unused entirely or
    // when it has been browsed successfully.
    if (!tokenizerId.trim()) {
      return true;
    }
    return tokenizerListing !== null;
  }, [tokenizerId, tokenizerListing]);

  const submit = async () => {
    if (!modelListing) {
      setErr('Browse the model repository first so the snapshot can be previewed.');
      return;
    }
    if (!tokenizerOk) {
      setErr('Browse the tokenizer repository or clear the tokenizer field.');
      return;
    }
    setSubmitting(true);
    setErr(null);
    try {
      await onSubmit({
        modelId: modelListing.repository,
        tokenizerId: tokenizerListing?.repository ?? '',
        revision,
      });
    } catch (e) {
      setErr(e instanceof Error ? e.message : 'Download failed to start.');
    } finally {
      setSubmitting(false);
    }
  };

  const disableSubmit = submitting || !modelListing || !tokenizerOk;
  return (
    <SettingsModal
      isOpen={isOpen}
      title="Add TTS model from Hugging Face"
      onClose={onClose}
      disableDismiss={submitting}
      footer={
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
              modelListing
                ? 'Download the Hugging Face snapshot to this TTS service.'
                : 'Browse the model repository before starting the download.'
            }
          >
            Download snapshot
          </TextActionButton>
        </>
      }
    >
      <div className="space-y-3 text-sm">
        <p className="text-xs text-gray-600">
          Browse a TTS model repository to preview its snapshot before download. The tokenizer repo is optional — most
          TTS models ship their own tokenizer inside the same snapshot.
        </p>

        <RepositoryFilePicker
          repository={modelId}
          onRepositoryChange={(next) => {
            setModelId(next);
            setModelListing(null);
          }}
          previewOnly
          classifyPreview={ttsModelPreviewClassifier}
          onBrowseResolved={(listing) => {
            setModelListing(listing);
            setErr(null);
          }}
          onBrowseError={(e) => {
            if (e) {
              setModelListing(null);
            }
          }}
          serviceOrigin="SpeechSynthesis.model"
          repoInputLabel="TTS model repository"
          repoInputPlaceholder="org/tts-model"
          repoInputHint="Paste owner/repo or a Hugging Face model URL. Voice files inside voices/, .npz, .pt, and voice-named files are flagged with a voice badge."
          disabled={submitting}
        />

        <div>
          <button
            type="button"
            className="text-xs text-blue-700 hover:underline disabled:text-gray-400"
            onClick={() => setShowTokenizer((previous) => !previous)}
            disabled={submitting}
          >
            {showTokenizer ? 'Hide tokenizer repository' : 'Tokenizer repository (advanced)'}
          </button>
        </div>
        {showTokenizer ? (
          <RepositoryFilePicker
            repository={tokenizerId}
            onRepositoryChange={(next) => {
              setTokenizerId(next);
              setTokenizerListing(null);
            }}
            previewOnly
            classifyPreview={snapshotPreviewClassifier}
            onBrowseResolved={(listing) => {
              setTokenizerListing(listing);
              setErr(null);
            }}
            onBrowseError={(e) => {
              if (e) {
                setTokenizerListing(null);
              }
            }}
            serviceOrigin="SpeechSynthesis.tokenizer"
            repoInputLabel="Tokenizer repository (optional)"
            repoInputPlaceholder="org/tokenizer-repo"
            repoInputHint="Leave blank to use the tokenizer bundled with the model repo."
            disabled={submitting}
          />
        ) : null}

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
            Hugging Face revision / branch. Blank uses the default branch.
          </span>
        </label>

        {err ? <p className="text-xs text-red-700">{err}</p> : null}
      </div>
    </SettingsModal>
  );
}
