import { useEffect, useState } from 'react';
import { FaPlay, FaSpinner, FaStop } from 'react-icons/fa';
import { api } from '../../../../services/api';
import type {
  HuggingFaceRepositoryListingDto,
  LocalModelsUpstreamFailure,
} from '../../../../types/settings';
import { TextActionButton } from '../../components/shared/ActionButtons';
import { LocalCapabilityFrame, type LocalCapabilityPhase } from '../../components/shared/LocalCapabilityFrame';
import { RepositoryFilePicker, snapshotPreviewClassifier } from '../common';

type EmbLoadSource = 'default' | 'local' | 'huggingface';

/**
 * Runtime snapshot returned by the Embeddings sub-service /ready endpoint.
 * Mirrors the fields that matter to the UI — other fields are ignored.
 */
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

interface EmbRuntimeManagerProps {
  /** True when the embeddings provider resolves to the local HTTP service. */
  enabled: boolean;
}

/**
 * Local Embeddings runtime controls in the same "engine strip + actions"
 * visual pattern as the other local-service managers.
 */
export function EmbRuntimeManager({ enabled }: EmbRuntimeManagerProps) {
  const [phase, setPhase] = useState<LocalCapabilityPhase>('loading');
  const [readiness, setReadiness] = useState<EmbReadiness | undefined>(undefined);
  const [errorMessage, setErrorMessage] = useState<string | undefined>(undefined);
  const [errorUpstream, setErrorUpstream] = useState<LocalModelsUpstreamFailure | undefined>(undefined);
  const [busy, setBusy] = useState(false);
  const [actionError, setActionError] = useState<string | null>(null);
  const [loadSource, setLoadSource] = useState<EmbLoadSource>('default');
  const [modelPath, setModelPath] = useState('');
  const [hfRepo, setHfRepo] = useState('');
  const [hfListing, setHfListing] = useState<HuggingFaceRepositoryListingDto | null>(null);

  const refresh = async (): Promise<void> => {
    setPhase((p) => (p === 'available' ? 'available' : 'loading'));
    const outcome = await api.settings.localModels.runtimeReadinessOutcome('Embeddings');
    if (outcome.kind === 'available') {
      setReadiness(outcome.payload as EmbReadiness);
      setPhase('available');
      setErrorMessage(undefined);
      setErrorUpstream(undefined);
    } else {
      setReadiness(undefined);
      setPhase('error');
      setErrorMessage(
        outcome.message
          ? `Runtime readiness probe not available. (${outcome.message})`
          : 'Runtime readiness probe not available.'
      );
      setErrorUpstream(outcome.upstream);
    }
  };

  useEffect(() => {
    if (!enabled) {
      setPhase('hidden');
      return;
    }
    void refresh();
  }, [enabled]);

  if (phase === 'hidden') {
    return null;
  }

  const handleLoad = async (): Promise<void> => {
    if (busy) return;
    setActionError(null);
    let body: Record<string, unknown> = {};
    // Branch on the operator's explicit source selection rather than guessing
    // from field contents: an empty text box for local path must error, not
    // silently fall back to the default load. The user's rule is clear:
    // fallback logic hides bugs.
    if (loadSource === 'local') {
      const trimmed = modelPath.trim();
      if (!trimmed) {
        setActionError('Local model path is required when "Local path" is selected.');
        return;
      }
      body = { model_path: trimmed };
    } else if (loadSource === 'huggingface') {
      if (!hfListing) {
        setActionError('Browse the Hugging Face repository before loading.');
        return;
      }
      body = { model_id: hfListing.repository };
    }
    setBusy(true);
    try {
      await api.settings.localModels.load('Embeddings', body);
      await refresh();
    } catch (e) {
      const err = e as { status?: number; body?: { error?: string } };
      setActionError(err?.body?.error ?? (e instanceof Error ? e.message : 'Load failed.'));
    } finally {
      setBusy(false);
    }
  };

  const handleUnload = async (): Promise<void> => {
    if (busy) return;
    setActionError(null);
    setBusy(true);
    try {
      await api.settings.localModels.unload('Embeddings');
      await refresh();
    } catch (e) {
      const err = e as { status?: number; body?: { error?: string } };
      if (err?.status === 409) {
        setActionError('Another load or unload is already in progress. Try again.');
      } else {
        setActionError(err?.body?.error ?? (e instanceof Error ? e.message : 'Unload failed.'));
      }
    } finally {
      setBusy(false);
    }
  };

  const loaded = !!readiness?.loaded;
  const hfReady = loadSource !== 'huggingface' || hfListing !== null;
  const localReady = loadSource !== 'local' || modelPath.trim().length > 0;
  const canLoad = !busy && !loaded && hfReady && localReady;
  const readyLabel = readiness?.ready
    ? 'Ready'
    : readiness?.loading
    ? 'Loading…'
    : loaded
    ? 'Loaded, warmup pending'
    : 'Not loaded';
  const color = readiness?.ready
    ? 'bg-green-100 text-green-800'
    : loaded
    ? 'bg-amber-100 text-amber-800'
    : 'bg-gray-100 text-gray-800';

  return (
    <LocalCapabilityFrame
      title="Local embedding engine"
      phase={phase}
      errorMessage={errorMessage}
      upstream={errorUpstream}
      onRefresh={phase === 'available' || phase === 'error' ? () => void refresh() : undefined}
    >
      <div
        className={`rounded border p-3 text-xs ${
          loaded ? 'border-blue-200 bg-blue-50 text-blue-900' : 'border-gray-200 bg-gray-50 text-gray-800'
        }`}
      >
        <div className="flex flex-wrap items-center justify-between gap-2">
          <div className="flex flex-wrap items-center gap-2">
            <span className="font-semibold">Embedding engine:</span>
            <span className={`inline-flex items-center rounded px-2 py-0.5 font-semibold ${color}`}>{readyLabel}</span>
            {readiness?.modelRef ? (
              <span className="text-gray-700">
                model <span className="font-mono">{readiness.modelRef}</span>
              </span>
            ) : (
              <span className="text-gray-500">No model loaded.</span>
            )}
            {readiness?.device ? <span className="text-gray-500">device {readiness.device}</span> : null}
            {readiness?.dimensions ? <span className="text-gray-500">{readiness.dimensions}-d</span> : null}
          </div>
          <div className="flex gap-1.5">
            <TextActionButton
              tone="primary"
              icon={busy && !loaded ? <FaSpinner className="animate-spin" /> : <FaPlay />}
              disabled={!canLoad}
              onClick={() => void handleLoad()}
              title={
                loaded
                  ? 'A model is already loaded.'
                  : loadSource === 'huggingface' && !hfListing
                    ? 'Browse the Hugging Face repository first.'
                    : loadSource === 'local' && !modelPath.trim()
                      ? 'Enter the local model path first.'
                      : loadSource === 'huggingface'
                        ? 'Load this Hugging Face repository into the embedding service.'
                        : loadSource === 'local'
                          ? 'Load this local model path into the embedding service.'
                          : 'Load the default embedding model for this service.'
              }
            >
              {loadSource === 'huggingface'
                ? 'Load from Hugging Face'
                : loadSource === 'local'
                  ? 'Load from local path'
                  : 'Load default model'}
            </TextActionButton>
            <TextActionButton
              tone="neutral"
              icon={busy && loaded ? <FaSpinner className="animate-spin" /> : <FaStop />}
              disabled={busy || !loaded}
              onClick={() => void handleUnload()}
              title={loaded ? 'Drop the loaded embedding model to release GPU / RAM.' : 'No model is currently loaded.'}
            >
              Unload model
            </TextActionButton>
          </div>
        </div>
        {readiness?.warmupEnabled ? (
          <p className="mt-1 text-[11px] text-gray-600">
            Warmup: {readiness.warmupSucceeded ? 'succeeded' : readiness.warmupRan ? 'failed' : 'pending'}
            {readiness.warmupError ? <span className="ml-1 font-mono text-red-700">— {readiness.warmupError}</span> : null}
          </p>
        ) : null}
        {readiness?.loadError ? (
          <p className="mt-1 font-mono text-[11px] text-red-700">Load error: {readiness.loadError}</p>
        ) : null}
      </div>

      {!loaded ? (
        <div className="space-y-2 rounded border border-gray-200 bg-white p-3 text-xs">
          <div className="flex flex-wrap items-center gap-3">
            <span className="font-semibold text-gray-800">Load from</span>
            <label className="inline-flex items-center gap-1">
              <input
                type="radio"
                name="emb-load-source"
                value="default"
                checked={loadSource === 'default'}
                onChange={() => setLoadSource('default')}
                disabled={busy}
              />
              <span>Default</span>
            </label>
            <label className="inline-flex items-center gap-1">
              <input
                type="radio"
                name="emb-load-source"
                value="local"
                checked={loadSource === 'local'}
                onChange={() => setLoadSource('local')}
                disabled={busy}
              />
              <span>From local path</span>
            </label>
            <label className="inline-flex items-center gap-1">
              <input
                type="radio"
                name="emb-load-source"
                value="huggingface"
                checked={loadSource === 'huggingface'}
                onChange={() => setLoadSource('huggingface')}
                disabled={busy}
              />
              <span>🤗 From Hugging Face</span>
            </label>
          </div>

          {loadSource === 'local' ? (
            <div>
              <label className="block text-[11px] font-semibold text-gray-700" htmlFor="emb-local-model-path">
                Local model path
              </label>
              <input
                id="emb-local-model-path"
                type="text"
                className="mt-1 w-full rounded border border-gray-300 px-2 py-1 font-mono text-xs"
                placeholder="/models-local/emb/harrier-oss-v1-0.6b"
                value={modelPath}
                onChange={(e) => setModelPath(e.target.value)}
                disabled={busy}
              />
              <p className="mt-1 text-[11px] text-gray-500">
                Absolute path inside the Embeddings service container.
              </p>
            </div>
          ) : null}

          {loadSource === 'huggingface' ? (
            <RepositoryFilePicker
              repository={hfRepo}
              onRepositoryChange={(next) => {
                setHfRepo(next);
                setHfListing(null);
              }}
              previewOnly
              classifyPreview={snapshotPreviewClassifier}
              serviceOrigin="Embeddings"
              repoInputPlaceholder="owner/repo (e.g. Qwen/Qwen3-Embedding-0.6B)"
              onBrowseResolved={(listing) => setHfListing(listing)}
              onBrowseError={() => setHfListing(null)}
            />
          ) : null}
        </div>
      ) : null}

      {actionError ? <p className="text-xs text-red-700">{actionError}</p> : null}
    </LocalCapabilityFrame>
  );
}
