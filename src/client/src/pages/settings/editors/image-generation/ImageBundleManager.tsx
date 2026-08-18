import { useEffect, useMemo, useRef, useState, type ChangeEvent } from 'react';
import { FaDownload, FaEdit, FaEye, FaPlay, FaSpinner, FaTrash, FaUpload, FaTimes } from 'react-icons/fa';
import { ConfirmationDialog } from '../../../../components/common/ConfirmationDialog';
import { api } from '../../../../services/api';
import { IconActionButton, TextActionButton } from '../../components/shared/ActionButtons';
import { LocalCapabilityFrame, type LocalCapabilityPhase } from '../../components/shared/LocalCapabilityFrame';
import { SettingsModal } from '../../components/shared/SettingsModal';
import type { LocalModelsUpstreamFailure } from '../../../../types/settings';
import type { ImageGenerationBundleDefinitionDto } from '../../../../types/settings';
import { RepositoryFilePicker, type RolePickerSpec } from '../common';
import {
  isOperationFailedStatus,
  isOperationInFlight,
  isOperationTerminalStatus,
  LOCAL_OPERATION_UNREACHABLE_MESSAGE,
  type LocalDownloadOperationState,
  type LocalRuntimeReadinessState,
  startLocalOperationPoll,
} from '../common/localOperationPolling';
import {
  sdDiffusionClassifier,
  sdTextEncoderClassifier,
  sdVaeClassifier,
} from './sdClassifier';

type BundleRoleState = { path?: string; ready?: boolean };

type BundleDefinitionRole = {
  repo?: string;
  file?: string;
};

type BundleDefinitionSampling = {
  steps?: number;
  cfgScale?: number;
  samplingMethod?: string;
};

type BundleDefinition = {
  revision?: string | null;
  updatedAtUtc?: string | null;
  sampling?: BundleDefinitionSampling;
  roles?: {
    diffusion?: BundleDefinitionRole;
    vae?: BundleDefinitionRole;
    textEncoder?: BundleDefinitionRole;
  };
};

type BundleListItem = {
  bundleId: string;
  active: boolean;
  complete: boolean;
  loaded?: boolean;
  definition?: BundleDefinition;
  roles: {
    diffusion: BundleRoleState;
    vae: BundleRoleState;
    textEncoder: BundleRoleState;
  };
};

type EngineState = {
  processAlive?: boolean;
  loadedBundleId?: string | null;
  loadedAtUtc?: string | null;
  startedAtUtc?: string | null;
  pid?: number | null;
  exitCode?: number | null;
  lastError?: string | null;
  config?: {
    diffusionModelPath?: string;
    vaePath?: string;
    llmPath?: string;
    engineBaseUrl?: string;
  } | null;
};

type BundleListPayload = {
  modelDir?: string;
  selectedBundleId?: string | null;
  activeBundleId?: string | null;
  activeBundleMarkerId?: string | null;
  loadedBundleId?: string | null;
  engine?: EngineState;
  items?: BundleListItem[];
};

type OperationState = {
  operationId: string;
  bundleId?: string;
  status: string; // queued | running | completed | failed
  roles?: {
    diffusion?: string;
    vae?: string;
    textEncoder?: string;
  };
  error?: string | null;
};

const SERVICE_ID = 'ImageGeneration';

interface DownloadFormValues {
  bundleId: string;
  diffusionRepo: string;
  diffusionFile: string;
  vaeRepo: string;
  vaeFile: string;
  textEncoderRepo: string;
  textEncoderFile: string;
  samplingSteps: string;
  samplingCfgScale: string;
  samplingMethod: string;
  revision: string;
}

const EMPTY_DOWNLOAD_FORM: DownloadFormValues = {
  bundleId: '',
  diffusionRepo: '',
  diffusionFile: '',
  vaeRepo: '',
  vaeFile: '',
  textEncoderRepo: '',
  textEncoderFile: '',
  samplingSteps: '',
  samplingCfgScale: '',
  samplingMethod: '',
  revision: '',
};

const ROLES: Array<{
  key: 'diffusion' | 'vae' | 'textEncoder';
  label: string;
  repoHint: string;
  fileHint: string;
}> = [
  {
    key: 'diffusion',
    label: 'Diffusion',
    repoHint: 'Flux / SD diffusion weights repo (Hugging Face org/name).',
    fileHint: 'Exact filename of the diffusion file in that repo (e.g. flux-2-klein-9b-Q5_K_M.gguf). No wildcards.',
  },
  {
    key: 'vae',
    label: 'VAE',
    repoHint: 'Variational autoencoder repo (Hugging Face org/name).',
    fileHint: 'Exact filename of the VAE file in that repo (e.g. full_encoder_small_decoder.safetensors). No wildcards.',
  },
  {
    key: 'textEncoder',
    label: 'Text encoder',
    repoHint: 'CLIP / LLM text encoder repo (Hugging Face org/name).',
    fileHint: 'Exact filename of the text encoder file in that repo (e.g. Qwen3-8B-Q5_K_M.gguf). No wildcards.',
  },
];

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function asString(value: unknown): string {
  return typeof value === 'string' ? value : '';
}

function asStringOrNumber(value: unknown): string {
  if (typeof value === 'string') {
    return value;
  }
  if (typeof value === 'number' && Number.isFinite(value)) {
    return String(value);
  }
  return '';
}

function definitionToDownloadForm(definition: ImageGenerationBundleDefinitionDto): DownloadFormValues {
  return {
    bundleId: definition.bundleId,
    diffusionRepo: definition.roles.diffusion.repo,
    diffusionFile: definition.roles.diffusion.file,
    vaeRepo: definition.roles.vae.repo,
    vaeFile: definition.roles.vae.file,
    textEncoderRepo: definition.roles.textEncoder.repo,
    textEncoderFile: definition.roles.textEncoder.file,
    samplingSteps: String(definition.sampling.steps),
    samplingCfgScale: String(definition.sampling.cfgScale),
    samplingMethod: definition.sampling.samplingMethod,
    revision: definition.revision ?? '',
  };
}

function downloadFormToDefinition(form: DownloadFormValues): ImageGenerationBundleDefinitionDto {
  return {
    bundleId: form.bundleId.trim(),
    revision: form.revision.trim() || null,
    roles: {
      diffusion: { repo: form.diffusionRepo.trim(), file: form.diffusionFile.trim() },
      vae: { repo: form.vaeRepo.trim(), file: form.vaeFile.trim() },
      textEncoder: { repo: form.textEncoderRepo.trim(), file: form.textEncoderFile.trim() },
    },
    sampling: {
      steps: Number(form.samplingSteps),
      cfgScale: Number(form.samplingCfgScale),
      samplingMethod: form.samplingMethod.trim(),
    },
  };
}

function mergeDefinitionWithRuntime(
  definition: ImageGenerationBundleDefinitionDto,
  runtime: BundleListItem | undefined,
  selectedBundleId: string | null | undefined,
): BundleListItem {
  const isSelected = Boolean(selectedBundleId && definition.bundleId === selectedBundleId);
  const roles = runtime?.roles ?? {
    diffusion: { ready: false },
    vae: { ready: false },
    textEncoder: { ready: false },
  };
  const complete = runtime?.complete ?? false;
  return {
    bundleId: definition.bundleId,
    active: isSelected,
    complete,
    loaded: runtime?.loaded,
    definition: {
      revision: definition.revision,
      updatedAtUtc: definition.updatedAtUtc,
      sampling: definition.sampling,
      roles: definition.roles,
    },
    roles,
  };
}

function hasCanonicalBundleDefinition(definition: ImageGenerationBundleDefinitionDto | null | undefined): boolean {
  if (!definition) {
    return false;
  }
  return Boolean(
    definition.roles.diffusion.repo
    && definition.roles.diffusion.file
    && definition.roles.vae.repo
    && definition.roles.vae.file
    && definition.roles.textEncoder.repo
    && definition.roles.textEncoder.file
    && definition.sampling.steps > 0
    && definition.sampling.cfgScale > 0
    && definition.sampling.samplingMethod.trim().length > 0
  );
}

function buildDownloadFormFromBundle(bundle: BundleListItem): DownloadFormValues {
  const definition = bundle.definition;
  if (
    !definition?.roles?.diffusion?.repo
    || !definition.roles.diffusion.file
    || !definition.roles.vae?.repo
    || !definition.roles.vae.file
    || !definition.roles.textEncoder?.repo
    || !definition.roles.textEncoder.file
    || typeof definition.sampling?.steps !== 'number'
    || typeof definition.sampling.cfgScale !== 'number'
    || typeof definition.sampling.samplingMethod !== 'string'
  ) {
    return { ...EMPTY_DOWNLOAD_FORM, bundleId: bundle.bundleId };
  }

  return definitionToDownloadForm({
    bundleId: bundle.bundleId,
    revision: definition.revision ?? null,
    updatedAtUtc: definition.updatedAtUtc ?? null,
    roles: {
      diffusion: { repo: definition.roles.diffusion.repo, file: definition.roles.diffusion.file },
      vae: { repo: definition.roles.vae.repo, file: definition.roles.vae.file },
      textEncoder: { repo: definition.roles.textEncoder.repo, file: definition.roles.textEncoder.file },
    },
    sampling: {
      steps: definition.sampling.steps,
      cfgScale: definition.sampling.cfgScale,
      samplingMethod: definition.sampling.samplingMethod,
    },
  });
}

function parseBundleDefinitionUpload(text: string): DownloadFormValues {
  let parsed: unknown;
  try {
    parsed = JSON.parse(text);
  } catch {
    throw new Error('Bundle definition file is not valid JSON.');
  }
  if (!isRecord(parsed)) {
    throw new Error('Bundle definition JSON must be an object.');
  }

  const roles = isRecord(parsed.roles) ? parsed.roles : {};
  const diffusion = isRecord(roles.diffusion) ? roles.diffusion : {};
  const vae = isRecord(roles.vae) ? roles.vae : {};
  const textEncoder = isRecord(roles.textEncoder)
    ? roles.textEncoder
    : isRecord(roles.text_encoder)
    ? roles.text_encoder
    : {};
  const sampling = isRecord(parsed.sampling) ? parsed.sampling : {};

  const form: DownloadFormValues = {
    bundleId: asString(parsed.bundleId) || asString(parsed.bundle_id),
    diffusionRepo: asString(diffusion.repo) || asString(parsed.diffusionRepo) || asString(parsed.diffusion_repo),
    diffusionFile: asString(diffusion.file) || asString(parsed.diffusionFile) || asString(parsed.diffusion_file),
    vaeRepo: asString(vae.repo) || asString(parsed.vaeRepo) || asString(parsed.vae_repo),
    vaeFile: asString(vae.file) || asString(parsed.vaeFile) || asString(parsed.vae_file),
    textEncoderRepo:
      asString(textEncoder.repo) || asString(parsed.textEncoderRepo) || asString(parsed.text_encoder_repo),
    textEncoderFile:
      asString(textEncoder.file) || asString(parsed.textEncoderFile) || asString(parsed.text_encoder_file),
    samplingSteps: asStringOrNumber(sampling.steps) || asStringOrNumber(parsed.samplingSteps) || asStringOrNumber(parsed.sampling_steps),
    samplingCfgScale: asStringOrNumber(sampling.cfgScale) || asStringOrNumber(parsed.samplingCfgScale) || asStringOrNumber(parsed.sampling_cfg_scale),
    samplingMethod: asStringOrNumber(sampling.samplingMethod) || asStringOrNumber(parsed.samplingMethod) || asStringOrNumber(parsed.sampling_method),
    revision: asString(parsed.revision),
  };

  const missing: string[] = [];
  if (!form.bundleId.trim()) missing.push('bundleId');
  if (!form.diffusionRepo.trim()) missing.push('roles.diffusion.repo');
  if (!form.diffusionFile.trim()) missing.push('roles.diffusion.file');
  if (!form.vaeRepo.trim()) missing.push('roles.vae.repo');
  if (!form.vaeFile.trim()) missing.push('roles.vae.file');
  if (!form.textEncoderRepo.trim()) missing.push('roles.textEncoder.repo');
  if (!form.textEncoderFile.trim()) missing.push('roles.textEncoder.file');
  if (!form.samplingSteps.trim()) missing.push('sampling.steps');
  if (!form.samplingCfgScale.trim()) missing.push('sampling.cfgScale');
  if (!form.samplingMethod.trim()) missing.push('sampling.samplingMethod');
  if (missing.length > 0) {
    throw new Error(`Bundle definition is missing required field(s): ${missing.join(', ')}.`);
  }

  return form;
}

function bundleDefinitionFilename(bundleId: string): string {
  const safe = bundleId.trim().replace(/[^a-z0-9._-]+/gi, '-').replace(/-+/g, '-');
  const normalized = safe.length > 0 ? safe : 'bundle';
  return `${normalized}.bundle-definition.json`;
}

function triggerJsonDownload(fileName: string, text: string): void {
  const blob = new Blob([text], { type: 'application/json' });
  const objectUrl = URL.createObjectURL(blob);
  const anchor = document.createElement('a');
  anchor.href = objectUrl;
  anchor.download = fileName;
  document.body.appendChild(anchor);
  anchor.click();
  document.body.removeChild(anchor);
  URL.revokeObjectURL(objectUrl);
}

interface ImageBundleManagerProps {
  enabled: boolean;
  onDownloadOperationChange?: (state: LocalDownloadOperationState | null) => void;
  onRuntimeReadinessChange?: (state: LocalRuntimeReadinessState | null) => void;
}

type BundleDialogMode = 'create' | 'view' | 'edit';

export function ImageBundleManager({ enabled, onDownloadOperationChange, onRuntimeReadinessChange }: ImageBundleManagerProps) {
  const [phase, setPhase] = useState<LocalCapabilityPhase>('loading');
  const [canonicalDefinitions, setCanonicalDefinitions] = useState<ImageGenerationBundleDefinitionDto[]>([]);
  const [runtimePayload, setRuntimePayload] = useState<BundleListPayload | undefined>(undefined);
  const [runtimeUnavailable, setRuntimeUnavailable] = useState<
    { message: string; upstream?: LocalModelsUpstreamFailure } | undefined
  >(undefined);
  const [errorMessage, setErrorMessage] = useState<string | undefined>(undefined);
  const [errorUpstream, setErrorUpstream] = useState<LocalModelsUpstreamFailure | undefined>(undefined);
  const [actionError, setActionError] = useState<string | null>(null);
  const [bundleDialogMode, setBundleDialogMode] = useState<BundleDialogMode | null>(null);
  const [bundleDialogItem, setBundleDialogItem] = useState<BundleListItem | null>(null);
  const [bundleDialogPrefill, setBundleDialogPrefill] = useState<DownloadFormValues | null>(null);
  const [bundleDialogLoading, setBundleDialogLoading] = useState(false);
  const [busyBundleId, setBusyBundleId] = useState<string | null>(null);
  const [exportingBundleId, setExportingBundleId] = useState<string | null>(null);
  const [pendingRemoveId, setPendingRemoveId] = useState<string | null>(null);
  const [removing, setRemoving] = useState(false);
  const [activeOperation, setActiveOperation] = useState<OperationState | null>(null);

  const pollRef = useRef<number | null>(null);
  const definitionUploadInputRef = useRef<HTMLInputElement | null>(null);
  const hasInFlightOperation = activeOperation !== null && isOperationInFlight(activeOperation.status);

  const stopPolling = () => {
    if (pollRef.current != null) {
      window.clearInterval(pollRef.current);
      pollRef.current = null;
    }
  };

  const refresh = async (): Promise<void> => {
    setActionError(null);
    setPhase((prev) => (prev === 'available' ? 'available' : 'loading'));
    try {
      const list = await api.settings.imageGenerationBundles.list();
      setCanonicalDefinitions(list.items);
      setPhase('available');
      setErrorMessage(undefined);
      setErrorUpstream(undefined);
    } catch (e) {
      setPhase('error');
      setCanonicalDefinitions([]);
      setRuntimePayload(undefined);
      setRuntimeUnavailable(undefined);
      setErrorMessage(e instanceof Error ? e.message : 'Failed to load bundle definitions.');
      setErrorUpstream(undefined);
      return;
    }

    const outcome = await api.settings.localModels.listOutcome(SERVICE_ID);
    if (outcome.kind === 'available') {
      setRuntimePayload(outcome.payload as BundleListPayload);
      setRuntimeUnavailable(undefined);
    } else {
      setRuntimePayload(undefined);
      setRuntimeUnavailable({ message: outcome.message, upstream: outcome.upstream });
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
    if (!activeOperation) {
      onDownloadOperationChange(null);
      return;
    }
    onDownloadOperationChange({
      serviceId: SERVICE_ID,
      operationId: activeOperation.operationId,
      status: activeOperation.status,
      inFlight: isOperationInFlight(activeOperation.status),
      error: activeOperation.error ?? null,
    });
  }, [activeOperation, onDownloadOperationChange]);

  const bundles = useMemo(() => {
    const runtimeById = new Map((runtimePayload?.items ?? []).map((item) => [item.bundleId, item]));
    const selectedBundleId =
      runtimePayload?.selectedBundleId
      ?? runtimePayload?.activeBundleId
      ?? null;
    return canonicalDefinitions.map((definition) =>
      mergeDefinitionWithRuntime(definition, runtimeById.get(definition.bundleId), selectedBundleId)
    );
  }, [canonicalDefinitions, runtimePayload]);
  const engine = runtimePayload?.engine;
  const engineAlive = Boolean(engine?.processAlive);
  const loadedBundleId = runtimePayload?.loadedBundleId ?? engine?.loadedBundleId ?? null;
  const selectedBundleId =
    runtimePayload?.selectedBundleId
    ?? runtimePayload?.activeBundleId
    ?? null;

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
        status: 'Local SD service unavailable',
        detail: errorMessage ?? null,
      });
      return;
    }
    const lastError = engine?.lastError?.trim() || null;
    if (hasInFlightOperation && activeOperation) {
      onRuntimeReadinessChange({
        serviceId: SERVICE_ID,
        ready: false,
        status: `Bundle operation ${activeOperation.status}`,
        detail: activeOperation.error ?? null,
      });
      return;
    }
    if (engineAlive && loadedBundleId) {
      onRuntimeReadinessChange({
        serviceId: SERVICE_ID,
        ready: true,
        status: 'Ready',
      });
      return;
    }
    if (bundles.length === 0) {
      onRuntimeReadinessChange({
        serviceId: SERVICE_ID,
        ready: false,
        status: 'No bundles installed',
        detail: lastError,
      });
      return;
    }
    if (!selectedBundleId) {
      onRuntimeReadinessChange({
        serviceId: SERVICE_ID,
        ready: false,
        status: 'No active bundle selected',
        detail: lastError,
      });
      return;
    }
    if (!engineAlive) {
      onRuntimeReadinessChange({
        serviceId: SERVICE_ID,
        ready: false,
        status: 'Engine is not loaded',
        detail: lastError,
      });
      return;
    }
    onRuntimeReadinessChange({
      serviceId: SERVICE_ID,
      ready: false,
      status: 'Engine running without loaded bundle',
      detail: lastError,
    });
  }, [
    activeOperation,
    bundles.length,
    enabled,
    engine?.lastError,
    engineAlive,
    errorMessage,
    hasInFlightOperation,
    loadedBundleId,
    onRuntimeReadinessChange,
    selectedBundleId,
    phase,
  ]);

  const startOperationPoll = (operationId: string, initial: OperationState) => {
    setActiveOperation(initial);
    stopPolling();
    pollRef.current = startLocalOperationPoll<OperationState>({
      poll: () => api.settings.localModels.getOperation(SERVICE_ID, operationId) as Promise<OperationState>,
      onUpdate: (op) => {
        setActiveOperation(op);
        if (isOperationInFlight(op.status)) {
          // Keep role readiness fresh while files are mutating on disk.
          void refresh();
        }
      },
      onTerminal: () => {
        stopPolling();
        void refresh();
      },
      onPollFailureThreshold: () => {
        stopPolling();
        setActionError(LOCAL_OPERATION_UNREACHABLE_MESSAGE);
        setActiveOperation((previous) =>
          previous
            ? { ...previous, status: 'error', error: LOCAL_OPERATION_UNREACHABLE_MESSAGE }
            : previous
        );
      },
    });
  };

  const openBundleDialog = async (
    mode: BundleDialogMode,
    bundle?: BundleListItem,
    options?: { prefill?: DownloadFormValues | null }
  ) => {
    setActionError(null);
    if (mode === 'create') {
      setBundleDialogMode('create');
      setBundleDialogItem(null);
      setBundleDialogPrefill(options?.prefill ?? null);
      setBundleDialogLoading(false);
      return;
    }
    if (!bundle) {
      return;
    }
    setBundleDialogMode(mode);
    setBundleDialogItem(bundle);
    setBundleDialogPrefill(null);
    setBundleDialogLoading(true);
    try {
      const definition = await api.settings.imageGenerationBundles.get(bundle.bundleId);
      const runtimeItem = bundles.find((item) => item.bundleId === bundle.bundleId);
      setBundleDialogItem(mergeDefinitionWithRuntime(definition, runtimeItem, selectedBundleId));
    } catch (e) {
      setActionError(e instanceof Error ? e.message : 'Failed to load bundle details.');
    } finally {
      setBundleDialogLoading(false);
    }
  };

  const closeBundleDialog = () => {
    setBundleDialogMode(null);
    setBundleDialogItem(null);
    setBundleDialogPrefill(null);
    setBundleDialogLoading(false);
  };

  const handleDownloadDefinition = async (bundleId: string) => {
    setActionError(null);
    setExportingBundleId(bundleId);
    try {
      const definition = await api.settings.imageGenerationBundles.export(bundleId);
      if (!hasCanonicalBundleDefinition(definition)) {
        throw new Error(
          `Bundle "${bundleId}" does not have a complete saved definition yet. Open Edit, save the recipe once, then try download definition again.`
        );
      }
      triggerJsonDownload(bundleDefinitionFilename(definition.bundleId), JSON.stringify(definition, null, 2));
    } catch (e) {
      setActionError(e instanceof Error ? e.message : 'Failed to download bundle definition.');
    } finally {
      setExportingBundleId(null);
    }
  };

  const handleUploadDefinitionClick = () => {
    if (hasInFlightOperation) {
      return;
    }
    setActionError(null);
    definitionUploadInputRef.current?.click();
  };

  const handleUploadDefinitionFile = async (event: ChangeEvent<HTMLInputElement>) => {
    const file = event.target.files?.[0];
    event.target.value = '';
    if (!file) {
      return;
    }
    try {
      const text = await file.text();
      const parsed = parseBundleDefinitionUpload(text);
      const definition = downloadFormToDefinition(parsed);
      await api.settings.imageGenerationBundles.import(definition);
      await openBundleDialog('create', undefined, { prefill: parsed });
    } catch (e) {
      setActionError(e instanceof Error ? e.message : 'Failed to upload bundle definition.');
    }
  };

  const handleDownload = async (
    form: DownloadFormValues,
    options?: { forceRedownload?: boolean },
  ) => {
    setActionError(null);
    try {
      const definition = downloadFormToDefinition(form);
      await api.settings.imageGenerationBundles.upsert(definition.bundleId, definition);
      const body: Record<string, unknown> = {
        bundle_id: form.bundleId.trim(),
        diffusion_repo: form.diffusionRepo.trim(),
        diffusion_file: form.diffusionFile.trim(),
        vae_repo: form.vaeRepo.trim(),
        vae_file: form.vaeFile.trim(),
        text_encoder_repo: form.textEncoderRepo.trim(),
        text_encoder_file: form.textEncoderFile.trim(),
        sampling_steps: Number(form.samplingSteps),
        sampling_cfg_scale: Number(form.samplingCfgScale),
        sampling_method: form.samplingMethod.trim(),
      };
      if (form.revision.trim()) {
        body.revision = form.revision.trim();
      }
      if (options?.forceRedownload) {
        body.force_redownload = true;
      }
      const op = (await api.settings.localModels.startDownload(SERVICE_ID, body)) as OperationState;
      closeBundleDialog();
      startOperationPoll(op.operationId, op);
    } catch (e) {
      setActionError(e instanceof Error ? e.message : 'Download failed to start.');
    }
  };

  const handleActivate = async (bundleId: string) => {
    // ServiceModes is authoritative. The web API submits a complete lifecycle
    // plan, and the container only executes that explicit API command.
    setActionError(null);
    setBusyBundleId(bundleId);
    try {
      await api.settings.localModels.selectActive(SERVICE_ID, bundleId);
      await refresh();
    } catch (e) {
      setActionError(e instanceof Error ? e.message : 'Activation failed.');
    } finally {
      setBusyBundleId(null);
    }
  };

  const handleRemoveConfirmed = async () => {
    if (!pendingRemoveId) {
      return;
    }
    setRemoving(true);
    setActionError(null);
    try {
      await api.settings.localModels.remove(SERVICE_ID, pendingRemoveId);
      setPendingRemoveId(null);
      await refresh();
    } catch (e) {
      setActionError(e instanceof Error ? e.message : 'Remove failed.');
    } finally {
      setRemoving(false);
    }
  };

  const handleCancelOperation = async () => {
    if (!activeOperation || !isOperationInFlight(activeOperation.status)) {
      return;
    }
    setActionError(null);
    try {
      const latest = (await api.settings.localModels.cancelOperation(SERVICE_ID, activeOperation.operationId)) as OperationState;
      setActiveOperation(latest);
      if (isOperationTerminalStatus(latest.status)) {
        stopPolling();
        await refresh();
      }
    } catch (e) {
      setActionError(e instanceof Error ? e.message : 'Cancel failed.');
    }
  };

  return (
    <>
      <LocalCapabilityFrame
        title="Local SD bundles"
        phase={phase}
        errorMessage={errorMessage}
        upstream={errorUpstream}
        onRefresh={phase === 'available' || phase === 'error' ? () => void refresh() : undefined}
      >
        {runtimePayload?.modelDir ? (
          <p className="text-xs text-gray-500">
            Model directory on the SD container: <span className="font-mono">{runtimePayload.modelDir}</span>
          </p>
        ) : null}

        {runtimeUnavailable ? (
          <div className="rounded border border-amber-200 bg-amber-50 p-3 text-xs text-amber-950">
            <p className="font-semibold">Local runtime unavailable</p>
            <p className="mt-1">
              Bundle recipes are loaded from API settings. File readiness and engine status will update when the SD
              service responds.
            </p>
          </div>
        ) : null}

        {runtimePayload ? (
          <EngineStatusPanel
            engine={engine}
            loadedBundleId={loadedBundleId}
          />
        ) : null}

        {activeOperation ? (
          <DownloadOperationStatus
            operation={activeOperation}
            onCancel={isOperationInFlight(activeOperation.status) ? () => void handleCancelOperation() : undefined}
          />
        ) : null}

        <div className="overflow-hidden rounded border border-gray-200">
          <table className="w-full table-fixed text-sm">
            <colgroup>
              <col className="w-[28%]" />
              <col />
              <col />
              <col />
              <col className="w-[18%]" />
              <col className="w-[22%]" />
            </colgroup>
            <thead className="bg-gray-50 text-xs uppercase tracking-wide text-gray-600">
              <tr>
                <th className="px-3 py-2 text-left">Bundle id</th>
                <th className="px-3 py-2 text-left">Diffusion</th>
                <th className="px-3 py-2 text-left">VAE</th>
                <th className="px-3 py-2 text-left">Text encoder</th>
                <th className="px-3 py-2 text-left">Status</th>
                <th className="px-3 py-2 text-right">Actions</th>
              </tr>
            </thead>
            <tbody>
              {bundles.length === 0 ? (
                <tr>
                  <td colSpan={6} className="px-3 py-4 text-center text-sm text-gray-500">
                    No bundles configured. Click <span className="font-medium">Add bundle</span> to define one.
                  </td>
                </tr>
              ) : null}
              {bundles.map((b) => {
                const bundleOperation = activeOperation?.bundleId === b.bundleId ? activeOperation : null;
                const bundleOperationBusy = bundleOperation !== null && isOperationInFlight(bundleOperation.status);
                const bundleExportBusy = exportingBundleId === b.bundleId;
                const operationRoles = bundleOperation?.roles;
                return (
                  <tr key={b.bundleId} className="border-t border-gray-100">
                    <td className="px-3 py-2 font-mono text-xs text-gray-900">{b.bundleId}</td>
                    <td className="px-3 py-2">
                      <RoleCell
                        role={b.roles.diffusion}
                        operationStatus={bundleOperationBusy ? operationRoles?.diffusion : undefined}
                      />
                    </td>
                    <td className="px-3 py-2">
                      <RoleCell
                        role={b.roles.vae}
                        operationStatus={bundleOperationBusy ? operationRoles?.vae : undefined}
                      />
                    </td>
                    <td className="px-3 py-2">
                      <RoleCell
                        role={b.roles.textEncoder}
                        operationStatus={bundleOperationBusy ? operationRoles?.textEncoder : undefined}
                      />
                    </td>
                    <td className="px-3 py-2">
                      <div className="flex flex-wrap items-center gap-1">
                        {bundleOperationBusy ? (
                          <span
                            className="inline-flex items-center rounded bg-blue-100 px-2 py-0.5 text-xs font-semibold text-blue-800"
                            title={`Bundle update is in progress (${bundleOperation?.status ?? 'running'}).`}
                          >
                            Updating…
                          </span>
                        ) : null}
                        {b.loaded ? (
                          <span
                            className="inline-flex items-center rounded bg-blue-100 px-2 py-0.5 text-xs font-semibold text-blue-800"
                            title="This bundle's weights are currently loaded in the SD runtime."
                          >
                            Loaded
                          </span>
                        ) : null}
                        {b.active ? (
                          <span
                            className="inline-flex items-center rounded bg-green-100 px-2 py-0.5 text-xs font-semibold text-green-800"
                            title="This bundle is selected in ServiceModes for local image generation."
                          >
                            Selected
                          </span>
                        ) : null}
                        {!bundleOperationBusy && b.complete ? (
                          <span className="inline-flex items-center rounded bg-gray-100 px-2 py-0.5 text-xs text-gray-700">
                            Ready
                          </span>
                        ) : !bundleOperationBusy && !b.complete ? (
                          <span className="inline-flex items-center rounded bg-red-100 px-2 py-0.5 text-xs text-red-800">
                            Incomplete
                          </span>
                        ) : null}
                      </div>
                    </td>
                    <td className="px-3 py-2 text-right">
                      <div
                        className="flex flex-wrap items-center justify-end gap-1.5"
                        role="group"
                        aria-label={`Actions for bundle ${b.bundleId}`}
                      >
                        <IconActionButton
                          label="View details"
                          tone="neutral"
                          icon={<FaEye />}
                          onClick={() => void openBundleDialog('view', b)}
                          title="View bundle recipe, role status, and on-disk paths."
                        />
                        <IconActionButton
                          label={bundleExportBusy ? 'Downloading definition' : 'Download definition'}
                          tone="accent"
                          icon={bundleExportBusy ? <FaSpinner className="animate-spin" /> : <FaDownload />}
                          disabled={bundleOperationBusy || bundleExportBusy}
                          onClick={() => void handleDownloadDefinition(b.bundleId)}
                          title={
                            bundleOperationBusy
                              ? 'Cannot download definition while this bundle is being updated.'
                              : 'Download this bundle recipe as JSON.'
                          }
                        />
                        <IconActionButton
                          label="Edit bundle"
                          tone="info"
                          icon={<FaEdit />}
                          disabled={bundleOperationBusy || bundleExportBusy}
                          onClick={() => void openBundleDialog('edit', b)}
                          title={
                            bundleOperationBusy
                              ? 'Cannot edit while this bundle is being updated.'
                              : bundleExportBusy
                              ? 'Wait for definition download to finish.'
                              : b.definition
                              ? 'Edit the saved bundle recipe; only changed roles are re-downloaded.'
                              : 'Edit this bundle recipe; changed roles are re-downloaded. No saved recipe metadata is present yet.'
                          }
                        />
                        <IconActionButton
                          label={busyBundleId === b.bundleId ? (engineAlive ? 'Hot-swapping bundle' : 'Activating bundle') : 'Activate bundle'}
                          tone="success"
                          icon={busyBundleId === b.bundleId ? <FaSpinner className="animate-spin" /> : <FaPlay />}
                          disabled={
                            !b.complete
                            || b.active
                            || bundleOperationBusy
                            || bundleExportBusy
                            || busyBundleId === b.bundleId
                          }
                          onClick={() => void handleActivate(b.bundleId)}
                          title={
                            bundleOperationBusy
                              ? 'Cannot activate while this bundle is being updated.'
                              : bundleExportBusy
                              ? 'Cannot activate while definition download is in progress.'
                              : b.complete
                              ? engineAlive
                                ? 'Set as active bundle and hot-swap into the running SD engine (no container restart).'
                                : 'Set as active bundle (loads on next Load Engine).'
                              : 'Incomplete bundles cannot be activated'
                          }
                        />
                        <IconActionButton
                          label="Remove bundle"
                          tone="danger"
                          icon={<FaTrash />}
                          disabled={bundleOperationBusy || bundleExportBusy || b.active || b.loaded}
                          onClick={() => setPendingRemoveId(b.bundleId)}
                          title={
                            bundleOperationBusy
                              ? 'Cannot remove while this bundle is being updated.'
                              : bundleExportBusy
                              ? 'Cannot remove while definition download is in progress.'
                              : b.active
                              ? 'Cannot remove the active bundle — activate another first.'
                              : b.loaded
                              ? 'Cannot remove a bundle whose weights are loaded in the SD engine. Unload first.'
                              : 'Delete this bundle from disk'
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

        <div className="flex flex-wrap justify-end gap-2">
          <TextActionButton
            tone="neutral"
            icon={<FaUpload />}
            disabled={hasInFlightOperation}
            onClick={handleUploadDefinitionClick}
            title={hasInFlightOperation ? 'Wait for the current bundle download/update to finish.' : 'Upload a bundle definition JSON and prefill the download form.'}
          >
            Upload definition
          </TextActionButton>
          <TextActionButton
            tone="primary"
            icon={<FaDownload />}
            disabled={hasInFlightOperation}
            onClick={() => void openBundleDialog('create')}
            title={hasInFlightOperation ? 'Wait for the current bundle download/update to finish.' : 'Add a new SD bundle by downloading from Hugging Face.'}
          >
            Add bundle
          </TextActionButton>
        </div>
        <input
          ref={definitionUploadInputRef}
          type="file"
          accept="application/json,.json"
          onChange={(event) => void handleUploadDefinitionFile(event)}
          className="hidden"
          aria-label="Upload bundle definition file"
        />

        {actionError ? <div className="text-xs text-red-700">{actionError}</div> : null}
      </LocalCapabilityFrame>

      <DownloadBundleDialog
        isOpen={bundleDialogMode !== null}
        mode={bundleDialogMode ?? 'create'}
        bundle={bundleDialogItem}
        prefill={bundleDialogPrefill}
        loadingBundle={bundleDialogLoading}
        onClose={closeBundleDialog}
        onSubmit={handleDownload}
      />

      <ConfirmationDialog
        isOpen={pendingRemoveId !== null}
        title="Remove bundle"
        message={
          pendingRemoveId
            ? `Remove local bundle "${pendingRemoveId}"? This deletes the diffusion, VAE, and text encoder files from the SD container's model directory. This cannot be undone.`
            : ''
        }
        confirmText="Remove"
        onClose={() => (removing ? undefined : setPendingRemoveId(null))}
        onConfirm={() => void handleRemoveConfirmed()}
        isLoading={removing}
      />
    </>
  );
}

function EngineStatusPanel({
  engine,
  loadedBundleId,
}: {
  engine: EngineState | undefined;
  loadedBundleId: string | null | undefined;
}) {
  // Read-only status: whether the sd-server subprocess is alive, which bundle
  // is loaded, and the last engine error if a previous load failed. The engine
  // is warmed by activating a bundle (selectActive) through the server
  // reconciler — there is no standalone load/unload control here.
  const alive = Boolean(engine?.processAlive);
  const lastError = engine?.lastError ?? null;

  return (
    <div
      className={`rounded border p-3 text-xs ${
        alive ? 'border-blue-200 bg-blue-50 text-blue-900' : 'border-gray-200 bg-gray-50 text-gray-800'
      }`}
    >
      <div className="flex flex-wrap items-center justify-between gap-2">
        <div className="flex flex-wrap items-center gap-2">
          <span className="font-semibold">SD engine:</span>
          {alive ? (
            <span className="inline-flex items-center rounded bg-blue-100 px-2 py-0.5 font-semibold text-blue-800">
              Loaded
            </span>
          ) : (
            <span className="inline-flex items-center rounded bg-gray-200 px-2 py-0.5 font-semibold text-gray-700">
              Unloaded
            </span>
          )}
          {loadedBundleId ? (
            <span className="text-gray-700">
              bundle <span className="font-mono">{loadedBundleId}</span>
            </span>
          ) : null}
          {alive && typeof engine?.pid === 'number' ? (
            <span className="text-gray-500">pid {engine.pid}</span>
          ) : null}
        </div>
      </div>
      {lastError ? (
        <div className="mt-2 break-words font-mono text-[11px] text-red-700">
          Last engine error: {lastError}
        </div>
      ) : null}
    </div>
  );
}

function RoleCell({
  role,
  operationStatus,
}: {
  role: BundleRoleState | undefined;
  operationStatus?: string;
}) {
  if (operationStatus) {
    const normalized = operationStatus.toLowerCase();
    if (normalized === 'downloading') {
      return (
        <div className="flex items-center gap-2 text-xs">
          <span className="inline-block h-2 w-2 rounded-full bg-blue-500" aria-hidden="true" />
          <span className="text-blue-700">Downloading</span>
        </div>
      );
    }
    if (normalized === 'queued') {
      return (
        <div className="flex items-center gap-2 text-xs">
          <span className="inline-block h-2 w-2 rounded-full bg-gray-400" aria-hidden="true" />
          <span className="text-gray-700">Queued</span>
        </div>
      );
    }
    if (normalized === 'failed') {
      return (
        <div className="flex items-center gap-2 text-xs">
          <span className="inline-block h-2 w-2 rounded-full bg-red-500" aria-hidden="true" />
          <span className="text-red-700">Failed</span>
        </div>
      );
    }
    if (normalized === 'ready') {
      return (
        <div className="flex items-center gap-2 text-xs">
          <span className="inline-block h-2 w-2 rounded-full bg-green-500" aria-hidden="true" />
          <span className="text-gray-700">Ready</span>
        </div>
      );
    }
    return (
      <div className="flex items-center gap-2 text-xs">
        <span className="inline-block h-2 w-2 rounded-full bg-blue-500" aria-hidden="true" />
        <span className="text-blue-700">{operationStatus}</span>
      </div>
    );
  }
  if (!role) {
    return <span className="text-xs text-gray-400">—</span>;
  }
  return (
    <div className="flex items-center gap-2 text-xs">
      <span
        className={`inline-block h-2 w-2 rounded-full ${role.ready ? 'bg-green-500' : 'bg-red-500'}`}
        aria-hidden="true"
      />
      <span className={role.ready ? 'text-gray-700' : 'text-red-700'}>{role.ready ? 'Ready' : 'Missing'}</span>
    </div>
  );
}

function DownloadOperationStatus({ operation, onCancel }: { operation: OperationState; onCancel?: () => void }) {
  const failed = isOperationFailedStatus(operation.status);
  const terminal = isOperationTerminalStatus(operation.status);
  return (
    <div
      className={`rounded border p-3 text-xs ${
        failed
          ? 'border-red-300 bg-red-50 text-red-800'
          : terminal
          ? 'border-green-300 bg-green-50 text-green-800'
          : 'border-blue-300 bg-blue-50 text-blue-800'
      }`}
    >
      <div className="flex items-center justify-between gap-2">
        <div className="font-semibold">
          Download {operation.bundleId ? `"${operation.bundleId}"` : ''}: {operation.status}
          {!terminal ? '…' : ''}
        </div>
        {onCancel ? (
          <TextActionButton tone="danger" icon={<FaTimes />} onClick={onCancel} title="Cancel this bundle operation.">
            Cancel
          </TextActionButton>
        ) : null}
      </div>
      {operation.roles ? (
        <ul className="mt-1 list-inside list-disc">
          <li>Diffusion: {operation.roles.diffusion ?? 'pending'}</li>
          <li>VAE: {operation.roles.vae ?? 'pending'}</li>
          <li>Text encoder: {operation.roles.textEncoder ?? 'pending'}</li>
        </ul>
      ) : null}
      {operation.error ? <div className="mt-1 font-mono">{operation.error}</div> : null}
    </div>
  );
}

function formFromBundle(bundle: BundleListItem | null | undefined): DownloadFormValues {
  if (!bundle) {
    return EMPTY_DOWNLOAD_FORM;
  }
  return buildDownloadFormFromBundle(bundle);
}

const DIFFUSION_ROLE_SPEC: RolePickerSpec = {
  id: 'diffusion',
  label: 'Diffusion weights',
  hint: 'Pick the diffusion/unet file. Sharded safetensors are not supported.',
  required: true,
  manualPlaceholder: 'flux-2-klein-9b-Q5_K_M.gguf',
  placeholder: 'Select diffusion file…',
};

const VAE_ROLE_SPEC: RolePickerSpec = {
  id: 'vae',
  label: 'VAE weights',
  hint: 'Pick the VAE file. Usually ends in .safetensors.',
  required: true,
  manualPlaceholder: 'full_encoder_small_decoder.safetensors',
  placeholder: 'Select VAE file…',
};

const TEXT_ENCODER_ROLE_SPEC: RolePickerSpec = {
  id: 'textEncoder',
  label: 'Text encoder weights',
  hint: 'Pick the CLIP / T5 / LLM text-encoder file.',
  required: true,
  manualPlaceholder: 'Qwen3-8B-Q5_K_M.gguf',
  placeholder: 'Select text encoder file…',
};

function DownloadBundleDialog({
  isOpen,
  mode,
  bundle,
  prefill,
  loadingBundle,
  onClose,
  onSubmit,
}: {
  isOpen: boolean;
  mode: BundleDialogMode;
  bundle: BundleListItem | null;
  prefill: DownloadFormValues | null;
  loadingBundle: boolean;
  onClose: () => void;
  onSubmit: (values: DownloadFormValues, options?: { forceRedownload?: boolean }) => Promise<void>;
}) {
  const [form, setForm] = useState<DownloadFormValues>(EMPTY_DOWNLOAD_FORM);
  const [submitting, setSubmitting] = useState(false);
  const [localError, setLocalError] = useState<string | null>(null);
  const [advancedMode, setAdvancedMode] = useState(false);
  const [forceRedownload, setForceRedownload] = useState(false);
  const readOnly = mode === 'view';
  const title =
    mode === 'create'
      ? 'Add SD bundle from Hugging Face'
      : mode === 'edit'
      ? `Edit SD bundle${bundle?.bundleId ? `: ${bundle.bundleId}` : ''}`
      : `SD bundle details${bundle?.bundleId ? `: ${bundle.bundleId}` : ''}`;
  const hasDefinition = Boolean(
    bundle?.definition?.roles?.diffusion?.repo
    || bundle?.definition?.roles?.vae?.repo
    || bundle?.definition?.roles?.textEncoder?.repo
  );

  useEffect(() => {
    if (isOpen) {
      if (mode === 'create') {
        setForm(prefill ?? EMPTY_DOWNLOAD_FORM);
      } else {
        setForm(formFromBundle(bundle));
      }
      setLocalError(null);
      setSubmitting(false);
      setForceRedownload(false);
      // Default to the browse-first picker flow for new bundles; edit mode
      // starts in free-text because the operator is usually tweaking an
      // existing recipe and the repo file listing may not match anymore.
      // Uploads (prefill) also start free-text to show exactly what the JSON
      // carried.
      setAdvancedMode(mode !== 'create' || Boolean(prefill));
    }
  }, [isOpen, mode, bundle, prefill]);

  const updateForm = (patch: Partial<DownloadFormValues>) => {
    setForm((previous) => ({ ...previous, ...patch }));
  };

  const submit = async () => {
    if (readOnly || loadingBundle) {
      return;
    }
    // All seven fields are required. No defaults, no inference — the bundle
    // is the single authority for what gets pulled.
    const required: Array<[keyof DownloadFormValues, string]> = [
      ['bundleId', 'Bundle id'],
      ['diffusionRepo', 'Diffusion repo'],
      ['diffusionFile', 'Diffusion file'],
      ['vaeRepo', 'VAE repo'],
      ['vaeFile', 'VAE file'],
      ['textEncoderRepo', 'Text encoder repo'],
      ['textEncoderFile', 'Text encoder file'],
      ['samplingSteps', 'Sampling steps'],
      ['samplingCfgScale', 'CFG scale'],
      ['samplingMethod', 'Sampling method'],
    ];
    for (const [key, label] of required) {
      if (!form[key].trim()) {
        setLocalError(`${label} is required.`);
        return;
      }
    }
    // Filename fields must be literal filenames — no globs — to match the
    // server-side contract in sd_service.py / SettingsEndpoints.cs.
    const filenameFields: Array<[keyof DownloadFormValues, string]> = [
      ['diffusionFile', 'Diffusion file'],
      ['vaeFile', 'VAE file'],
      ['textEncoderFile', 'Text encoder file'],
    ];
    for (const [key, label] of filenameFields) {
      const value = form[key].trim();
      if (value.includes('*') || value.includes('?')) {
        setLocalError(`${label} must be a single filename (no '*' or '?' glob metacharacters).`);
        return;
      }
    }
    const parsedSteps = Number(form.samplingSteps);
    if (!Number.isInteger(parsedSteps) || parsedSteps <= 0) {
      setLocalError('Sampling steps must be a positive integer.');
      return;
    }
    const parsedCfgScale = Number(form.samplingCfgScale);
    if (!Number.isFinite(parsedCfgScale) || parsedCfgScale <= 0) {
      setLocalError('CFG scale must be a positive number.');
      return;
    }
    setSubmitting(true);
    setLocalError(null);
    try {
      await onSubmit(form, mode === 'edit' && forceRedownload ? { forceRedownload: true } : undefined);
    } catch (e) {
      setLocalError(e instanceof Error ? e.message : 'Failed to start download.');
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <SettingsModal
      isOpen={isOpen}
      title={title}
      onClose={onClose}
      disableDismiss={submitting}
      footer={
        readOnly ? (
          <TextActionButton
            tone="neutral"
            icon={<FaTimes />}
            onClick={onClose}
          >
            Close
          </TextActionButton>
        ) : (
          <>
            <TextActionButton
              tone="neutral"
              icon={<FaTimes />}
              onClick={onClose}
              disabled={submitting}
            >
              Cancel
            </TextActionButton>
            <TextActionButton
              tone="primary"
              icon={submitting ? <FaSpinner className="animate-spin" /> : <FaDownload />}
              onClick={() => void submit()}
              disabled={submitting || loadingBundle}
            >
              {submitting ? 'Starting…' : mode === 'edit' ? 'Save & update' : 'Download snapshot'}
            </TextActionButton>
          </>
        )
      }
    >
      <div className="space-y-3 text-sm">
        <p className="text-xs text-gray-600">
          {mode === 'create'
            ? (
              <>
                A bundle has three artifact roles (diffusion, VAE, text encoder). For each role you must name both
                the Hugging Face repo and the exact filename inside that repo to download — a bundle is always (repo,
                file) triples with no defaults. Files land under{' '}
                <span className="font-mono">/bundles/&lt;bundle id&gt;</span> on the SD container. The bundle must
                finish all three roles before it can be activated.
              </>
            )
            : mode === 'edit'
            ? 'Editing reuses the same bundle id. Files already on disk with the exact filename are kept unless you check Re-download files. Only missing or changed roles are fetched from Hugging Face.'
            : 'Read-only view of the bundle recipe and role readiness on disk.'}
        </p>
        {mode === 'edit' && !readOnly ? (
          <label className="flex items-start gap-2 rounded border border-amber-200 bg-amber-50 px-3 py-2 text-xs text-amber-950">
            <input
              type="checkbox"
              className="mt-0.5"
              checked={forceRedownload}
              onChange={(event) => setForceRedownload(event.target.checked)}
              disabled={submitting || loadingBundle}
            />
            <span>
              <span className="font-semibold">Re-download files even if already on disk.</span>{' '}
              Use after a failed or corrupt download. Leave unchecked when the correct file is already on disk (for example after fixing a filename typo).
            </span>
          </label>
        ) : null}
        {mode !== 'create' && bundle ? (
          <div className="rounded border border-gray-200 bg-gray-50 p-2 text-[11px] text-gray-700">
            <p className="font-semibold text-gray-800">Current bundle state</p>
            <div className="mt-1 space-y-0.5">
              <div>
                Active: <span className="font-medium">{bundle.active ? 'Yes (selected in ServiceModes)' : 'No'}</span>
              </div>
              <div>
                Loaded: <span className="font-medium">{bundle.loaded ? 'Yes' : 'No'}</span>
              </div>
              {bundle.definition?.updatedAtUtc ? (
                <div>
                  Recipe last updated: <span className="font-mono">{bundle.definition.updatedAtUtc}</span>
                </div>
              ) : null}
              {ROLES.map((role) => {
                const state = bundle.roles[role.key];
                return (
                  <div key={role.key}>
                    {role.label}: <span className={state?.ready ? 'text-green-700' : 'text-red-700'}>
                      {state?.ready ? 'ready' : 'missing'}
                    </span>{' '}
                    {state?.path ? <span className="font-mono text-gray-600">{state.path}</span> : null}
                  </div>
                );
              })}
            </div>
          </div>
        ) : null}
        {mode !== 'create' && !hasDefinition ? (
          <p className="text-xs text-amber-700">
            This bundle does not have a saved source recipe yet. Fill the fields below and save once to persist it.
          </p>
        ) : null}
        {loadingBundle ? (
          <p className="rounded border border-blue-200 bg-blue-50 px-2 py-1 text-xs text-blue-800">
            Loading latest bundle details…
          </p>
        ) : null}
        <Field
          label="Bundle id"
          hint={
            mode === 'create'
              ? 'A local name you pick to identify this bundle on disk (e.g. flux2-klein-9b).'
              : 'Bundle id is fixed when reading or editing an existing bundle.'
          }
          value={form.bundleId}
          onChange={(v) => setForm({ ...form, bundleId: v })}
          dataField="bundleId"
          disabled={mode !== 'create' || readOnly || loadingBundle || submitting}
        />

        {!readOnly ? (
          <div className="flex items-center justify-end">
            <button
              type="button"
              onClick={() => setAdvancedMode((previous) => !previous)}
              disabled={loadingBundle || submitting}
              className="text-[11px] text-blue-700 hover:underline disabled:text-gray-400"
            >
              {advancedMode ? 'Use repository picker' : 'Advanced: free-text repo/file'}
            </button>
          </div>
        ) : null}

        {advancedMode || readOnly
          ? ROLES.map((role) => {
              const repoKey: keyof DownloadFormValues =
                role.key === 'diffusion'
                  ? 'diffusionRepo'
                  : role.key === 'vae'
                  ? 'vaeRepo'
                  : 'textEncoderRepo';
              const fileKey: keyof DownloadFormValues =
                role.key === 'diffusion'
                  ? 'diffusionFile'
                  : role.key === 'vae'
                  ? 'vaeFile'
                  : 'textEncoderFile';
              return (
                <div key={role.key} className="rounded border border-gray-200 p-2">
                  <p className="mb-2 text-xs font-semibold text-gray-800">{role.label}</p>
                  <Field
                    label={`${role.label} repo`}
                    hint={role.repoHint}
                    value={form[repoKey]}
                    onChange={(v) => updateForm({ [repoKey]: v } as Partial<DownloadFormValues>)}
                    dataField={repoKey}
                    disabled={readOnly || loadingBundle || submitting}
                  />
                  <div className="mt-2">
                    <Field
                      label={`${role.label} file`}
                      hint={role.fileHint}
                      value={form[fileKey]}
                      onChange={(v) => updateForm({ [fileKey]: v } as Partial<DownloadFormValues>)}
                      dataField={fileKey}
                      disabled={readOnly || loadingBundle || submitting}
                    />
                  </div>
                </div>
              );
            })
          : (
            <div className="space-y-2">
              <RepositoryFilePicker
                repository={form.diffusionRepo}
                onRepositoryChange={(next) => updateForm({ diffusionRepo: next })}
                roles={[DIFFUSION_ROLE_SPEC]}
                classify={sdDiffusionClassifier}
                initialValues={{ diffusion: form.diffusionFile }}
                onChange={(values) => {
                  const next = values.diffusion ?? '';
                  if (next !== form.diffusionFile) {
                    updateForm({ diffusionFile: next });
                  }
                }}
                serviceOrigin="ImageGeneration.diffusion"
                repoInputLabel="Diffusion repository"
                repoInputHint="Hugging Face repo containing the diffusion/unet weights."
                disabled={loadingBundle || submitting}
              />
              <RepositoryFilePicker
                repository={form.vaeRepo}
                onRepositoryChange={(next) => updateForm({ vaeRepo: next })}
                roles={[VAE_ROLE_SPEC]}
                classify={sdVaeClassifier}
                initialValues={{ vae: form.vaeFile }}
                onChange={(values) => {
                  const next = values.vae ?? '';
                  if (next !== form.vaeFile) {
                    updateForm({ vaeFile: next });
                  }
                }}
                serviceOrigin="ImageGeneration.vae"
                repoInputLabel="VAE repository"
                repoInputHint="Hugging Face repo containing the VAE weights."
                disabled={loadingBundle || submitting}
              />
              <RepositoryFilePicker
                repository={form.textEncoderRepo}
                onRepositoryChange={(next) => updateForm({ textEncoderRepo: next })}
                roles={[TEXT_ENCODER_ROLE_SPEC]}
                classify={sdTextEncoderClassifier}
                initialValues={{ textEncoder: form.textEncoderFile }}
                onChange={(values) => {
                  const next = values.textEncoder ?? '';
                  if (next !== form.textEncoderFile) {
                    updateForm({ textEncoderFile: next });
                  }
                }}
                serviceOrigin="ImageGeneration.textEncoder"
                repoInputLabel="Text encoder repository"
                repoInputHint="Hugging Face repo containing the CLIP/T5/LLM text encoder."
                disabled={loadingBundle || submitting}
              />
            </div>
          )}

        <Field
          label="Sampling steps"
          hint="Per-bundle step count used for this model family."
          value={form.samplingSteps}
          onChange={(v) => setForm({ ...form, samplingSteps: v })}
          dataField="samplingSteps"
          disabled={readOnly || loadingBundle || submitting}
        />
        <Field
          label="CFG scale"
          hint="Per-bundle CFG scale (use model-recommended value)."
          value={form.samplingCfgScale}
          onChange={(v) => setForm({ ...form, samplingCfgScale: v })}
          dataField="samplingCfgScale"
          disabled={readOnly || loadingBundle || submitting}
        />
        <Field
          label="Sampling method"
          hint="Per-bundle sampler method from the bundle definition."
          value={form.samplingMethod}
          onChange={(v) => setForm({ ...form, samplingMethod: v })}
          dataField="samplingMethod"
          disabled={readOnly || loadingBundle || submitting}
        />
        <Field
          label="Revision (optional)"
          hint="Hugging Face revision / branch. Blank uses the default branch."
          value={form.revision}
          onChange={(v) => setForm({ ...form, revision: v })}
          dataField="revision"
          disabled={readOnly || loadingBundle || submitting}
        />
        {localError ? <p className="text-xs text-red-700">{localError}</p> : null}
      </div>
    </SettingsModal>
  );
}

function Field({
  label,
  hint,
  value,
  onChange,
  dataField,
  disabled,
}: {
  label: string;
  hint: string;
  value: string;
  onChange: (value: string) => void;
  dataField?: string;
  disabled?: boolean;
}) {
  return (
    <label className="block">
      <span className="mb-1 block text-xs font-medium text-gray-700">{label}</span>
      <input
        type="text"
        value={value}
        onChange={(e) => onChange(e.target.value)}
        data-field={dataField}
        disabled={disabled}
        className="w-full rounded border border-gray-300 px-2 py-1.5 text-sm focus:outline-none focus:ring-2 focus:ring-blue-500 disabled:cursor-not-allowed disabled:bg-gray-100 disabled:text-gray-600"
      />
      <span className="mt-1 block text-[11px] text-gray-500">{hint}</span>
    </label>
  );
}
