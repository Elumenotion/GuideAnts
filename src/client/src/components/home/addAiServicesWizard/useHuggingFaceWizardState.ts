import { useCallback, useState } from 'react';
import { api } from '../../../services/api';
import {
  HUGGINGFACE_DEFAULT_CHAT_MODEL_ID,
  HUGGINGFACE_OPTIONAL_SERVICE_DEFAULTS,
  HUGGINGFACE_SECTION,
  HUGGINGFACE_SERVICE_PROVIDER_IDS,
  SECRET_MASK,
} from './constants';
import type {
  HuggingFaceCoreConnectionFormState,
  HuggingFaceModelDraft,
  HuggingFaceOptionalServicesFormState,
  WizardLoadSnapshot,
} from './types';
import {
  buildAddHuggingFaceModelRequest,
  buildHuggingFaceCoreForm,
  buildHuggingFaceOptionalServicesForm,
  hasModelId,
  isPositiveIntegerValue,
  makeHuggingFaceDraftModel,
  persistGlobalDefaultModel,
  updateWizardSection,
  withSecretPreserved,
} from './utils';

export interface UseHuggingFaceWizardStateResult {
  coreForm: HuggingFaceCoreConnectionFormState;
  optionalForm: HuggingFaceOptionalServicesFormState;
  draftModelId: string;
  draftAsGlobalDefault: boolean;
  draftModels: HuggingFaceModelDraft[];
  coreErrors: Partial<Record<'token' | 'routerBaseUrl', string>>;
  optionalErrors: Record<string, string>;
  modelAddError: string | null;
  modelStepError: string | null;

  setCoreForm: (patch: Partial<HuggingFaceCoreConnectionFormState>) => void;
  setOptionalForm: (patch: Partial<HuggingFaceOptionalServicesFormState>) => void;
  setDraftModelId: (id: string) => void;
  setDraftAsGlobalDefault: (value: boolean) => void;
  removeDraftModel: (localId: string) => void;
  resetWithSnapshot: (snapshot: WizardLoadSnapshot) => void;
  persistConnection: (
    snapshot: WizardLoadSnapshot,
    loadSnapshot: () => Promise<WizardLoadSnapshot>,
    setSnapshot: (s: WizardLoadSnapshot) => void
  ) => Promise<void>;
  addDraftModel: (snapshot: WizardLoadSnapshot, existingCatalogCount: number, otherProviderDraftCount: number) => void;
  persistModels: (
    snapshot: WizardLoadSnapshot,
    loadSnapshot: () => Promise<WizardLoadSnapshot>,
    setSnapshot: (s: WizardLoadSnapshot) => void,
    onWarning?: (message: string) => void
  ) => Promise<void>;
  persistOptionalServices: (
    snapshot: WizardLoadSnapshot,
    loadSnapshot: () => Promise<WizardLoadSnapshot>,
    setSnapshot: (s: WizardLoadSnapshot) => void
  ) => Promise<void>;
}

export function useHuggingFaceWizardState(): UseHuggingFaceWizardStateResult {
  const [coreForm, setCoreFormState] = useState<HuggingFaceCoreConnectionFormState>({
    token: '',
    routerBaseUrl: HUGGINGFACE_OPTIONAL_SERVICE_DEFAULTS.routerBaseUrl,
    tokenHasStoredValue: false,
  });

  const [optionalForm, setOptionalFormState] = useState<HuggingFaceOptionalServicesFormState>({
    enableEmbeddings: true,
    embeddingsModelId: HUGGINGFACE_OPTIONAL_SERVICE_DEFAULTS.embeddingsModelId,
    embeddingsTimeoutSeconds: HUGGINGFACE_OPTIONAL_SERVICE_DEFAULTS.embeddingsTimeoutSeconds,
    enableImages: true,
    imagesTextToImageModelId: HUGGINGFACE_OPTIONAL_SERVICE_DEFAULTS.imagesTextToImageModelId,
    imagesImageToImageModelId: HUGGINGFACE_OPTIONAL_SERVICE_DEFAULTS.imagesImageToImageModelId,
    imagesTimeoutSeconds: HUGGINGFACE_OPTIONAL_SERVICE_DEFAULTS.imagesTimeoutSeconds,
    enableSpeechTranscription: true,
    speechTranscriptionModelId: HUGGINGFACE_OPTIONAL_SERVICE_DEFAULTS.speechTranscriptionModelId,
    speechTranscriptionTimeoutSeconds: HUGGINGFACE_OPTIONAL_SERVICE_DEFAULTS.speechTranscriptionTimeoutSeconds,
    enableSpeechSynthesis: true,
    speechSynthesisModelId: HUGGINGFACE_OPTIONAL_SERVICE_DEFAULTS.speechSynthesisModelId,
    speechSynthesisTimeoutSeconds: HUGGINGFACE_OPTIONAL_SERVICE_DEFAULTS.speechSynthesisTimeoutSeconds,
  });

  const [draftModelId, setDraftModelId] = useState(HUGGINGFACE_DEFAULT_CHAT_MODEL_ID);
  const [draftAsGlobalDefault, setDraftAsGlobalDefault] = useState(true);
  const [draftModels, setDraftModels] = useState<HuggingFaceModelDraft[]>([]);

  const [coreErrors, setCoreErrors] = useState<Partial<Record<'token' | 'routerBaseUrl', string>>>({});
  const [optionalErrors, setOptionalErrors] = useState<Record<string, string>>({});
  const [modelAddError, setModelAddError] = useState<string | null>(null);
  const [modelStepError, setModelStepError] = useState<string | null>(null);

  const setCoreForm = useCallback((patch: Partial<HuggingFaceCoreConnectionFormState>) => {
    setCoreFormState((prev) => ({ ...prev, ...patch }));
  }, []);

  const setOptionalForm = useCallback((patch: Partial<HuggingFaceOptionalServicesFormState>) => {
    setOptionalFormState((prev) => ({ ...prev, ...patch }));
  }, []);

  const removeDraftModel = useCallback((localId: string) => {
    setDraftModels((prev) => prev.filter((m) => m.localId !== localId));
  }, []);

  const resetWithSnapshot = useCallback((snapshot: WizardLoadSnapshot) => {
    setCoreFormState(buildHuggingFaceCoreForm(snapshot));
    setOptionalFormState(buildHuggingFaceOptionalServicesForm(snapshot));
    setDraftModels([]);
    setDraftModelId(HUGGINGFACE_DEFAULT_CHAT_MODEL_ID);
    setDraftAsGlobalDefault(snapshot.models.length === 0);
    setCoreErrors({});
    setOptionalErrors({});
    setModelAddError(null);
    setModelStepError(null);
  }, []);

  const validateConnection = useCallback((): boolean => {
    const errors: Partial<Record<'token' | 'routerBaseUrl', string>> = {};

    const tokenValue = coreForm.token.trim();
    if (!tokenValue && !coreForm.tokenHasStoredValue) {
      errors.token = 'Token is required.';
    }
    if (tokenValue && tokenValue !== SECRET_MASK && tokenValue.length < 8) {
      errors.token = 'Token looks too short.';
    }

    if (!coreForm.routerBaseUrl.trim()) {
      errors.routerBaseUrl = 'Router Base URL is required.';
    }

    setCoreErrors(errors);
    return Object.keys(errors).length === 0;
  }, [coreForm]);

  const persistConnection = useCallback(async (
    snapshot: WizardLoadSnapshot,
    _loadSnapshot: () => Promise<WizardLoadSnapshot>,
    setSnapshot: (s: WizardLoadSnapshot) => void
  ): Promise<void> => {
    if (!validateConnection()) {
      throw new Error('Connection details are incomplete.');
    }

    const payload = {
      Token: withSecretPreserved(coreForm.token, coreForm.tokenHasStoredValue),
      RouterBaseUrl: coreForm.routerBaseUrl.trim(),
    };

    let nextSections = snapshot.sectionsByName;
    nextSections = await updateWizardSection(HUGGINGFACE_SECTION, payload, nextSections);

    const [sectionSummaries, models] = await Promise.all([
      api.settings.getSections(),
      api.settings.getModels(),
    ]);

    setSnapshot({ ...snapshot, sectionsByName: nextSections, sectionSummaries, models });
    setCoreFormState((prev) => ({
      ...prev,
      token: payload.Token,
      tokenHasStoredValue: true,
      routerBaseUrl: payload.RouterBaseUrl,
    }));
  }, [coreForm, validateConnection]);

  const addDraftModel = useCallback((
    snapshot: WizardLoadSnapshot,
    existingCatalogCount: number,
    otherProviderDraftCount: number
  ) => {
    const normalizedId = draftModelId.trim();
    if (!normalizedId) {
      setModelAddError('Model is required.');
      return;
    }

    const existingModel = snapshot.models.find(
      (m) => m.modelId.trim().toLowerCase() === normalizedId.toLowerCase()
    );
    if (existingModel) {
      setModelAddError(`Model '${normalizedId}' already exists with provider '${existingModel.provider}'.`);
      return;
    }

    if (hasModelId(draftModels, normalizedId)) {
      setModelAddError(`Model '${normalizedId}' is already queued.`);
      return;
    }

    const isFirstModelOverall = existingCatalogCount === 0 && draftModels.length === 0 && otherProviderDraftCount === 0;
    const shouldSetAsGlobalDefault = isFirstModelOverall || draftAsGlobalDefault;

    const localId = `draft-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`;
    setDraftModels((prev) => {
      const next = shouldSetAsGlobalDefault ? prev.map((m) => ({ ...m, setAsGlobalDefault: false })) : prev;
      return [...next, makeHuggingFaceDraftModel(localId, normalizedId, shouldSetAsGlobalDefault)];
    });

    setDraftModelId(HUGGINGFACE_DEFAULT_CHAT_MODEL_ID);
    setDraftAsGlobalDefault(false);
    setModelAddError(null);
    setModelStepError(null);
  }, [draftModelId, draftModels, draftAsGlobalDefault]);

  const persistModels = useCallback(async (
    snapshot: WizardLoadSnapshot,
    loadSnapshot: () => Promise<WizardLoadSnapshot>,
    setSnapshot: (s: WizardLoadSnapshot) => void,
    onWarning?: (message: string) => void
  ): Promise<void> => {
    const existingCount = snapshot.models.length;
    const pendingDrafts = draftModels.filter((m) => !m.persisted);

    if (existingCount + pendingDrafts.length === 0) {
      setModelStepError('At least one Hugging Face model is required.');
      throw new Error('Model requirement not met.');
    }

    const seenModelIds = new Set<string>();
    for (const model of pendingDrafts) {
      const normalized = model.modelId.trim().toLowerCase();
      if (seenModelIds.has(normalized)) {
        setModelStepError(`Model '${model.modelId}' is queued more than once. Use distinct model ids.`);
        throw new Error('Duplicate model ids were queued.');
      }
      seenModelIds.add(normalized);
    }

    const latestModels = await api.settings.getModels();
    const existingById = new Map(latestModels.map((m) => [m.modelId.trim().toLowerCase(), m]));
    const existingConflict = pendingDrafts.find((m) => existingById.has(m.modelId.trim().toLowerCase()));
    if (existingConflict) {
      const conflict = existingById.get(existingConflict.modelId.trim().toLowerCase());
      setModelStepError(
        `Model '${existingConflict.modelId}' already exists with provider '${conflict?.provider ?? 'unknown'}'. Choose a different model id.`
      );
      throw new Error('Model id conflict detected.');
    }

    for (const model of pendingDrafts) {
      await api.settings.addModel(buildAddHuggingFaceModelRequest(model.modelId));
    }

    const forcedDefaultModelId = existingCount === 0 && pendingDrafts.length > 0 ? pendingDrafts[0].modelId : null;
    const selectedDefaultModelId = pendingDrafts.find((m) => m.setAsGlobalDefault)?.modelId ?? null;
    const targetDefaultModelId = forcedDefaultModelId ?? selectedDefaultModelId;

    if (targetDefaultModelId) {
      try {
        await persistGlobalDefaultModel(targetDefaultModelId);
      } catch (error) {
        const detail = error instanceof Error ? error.message : 'Unknown error.';
        onWarning?.(`Model was added, but setting '${targetDefaultModelId}' as global default failed: ${detail}`);
      }
    }

    const refreshed = await loadSnapshot();
    setSnapshot(refreshed);
    setDraftModels([]);
    setDraftAsGlobalDefault(refreshed.models.length === 0);
    setModelStepError(null);
  }, [draftModels]);

  const validateOptionalServices = useCallback((): boolean => {
    const errors: Record<string, string> = {};

    if (optionalForm.enableEmbeddings) {
      if (!optionalForm.embeddingsModelId.trim()) {
        errors.embeddingsModelId = 'Embedding model id is required.';
      }
      if (!isPositiveIntegerValue(optionalForm.embeddingsTimeoutSeconds)) {
        errors.embeddingsTimeoutSeconds = 'Timeout must be a positive integer.';
      }
    }

    if (optionalForm.enableImages) {
      if (!optionalForm.imagesTextToImageModelId.trim()) {
        errors.imagesTextToImageModelId = 'Text-to-image model id is required.';
      }
      if (!optionalForm.imagesImageToImageModelId.trim()) {
        errors.imagesImageToImageModelId = 'Image-to-image model id is required.';
      }
      if (!isPositiveIntegerValue(optionalForm.imagesTimeoutSeconds)) {
        errors.imagesTimeoutSeconds = 'Timeout must be a positive integer.';
      }
    }

    if (optionalForm.enableSpeechTranscription) {
      if (!optionalForm.speechTranscriptionModelId.trim()) {
        errors.speechTranscriptionModelId = 'Transcription model id is required.';
      }
      if (!isPositiveIntegerValue(optionalForm.speechTranscriptionTimeoutSeconds)) {
        errors.speechTranscriptionTimeoutSeconds = 'Timeout must be a positive integer.';
      }
    }

    if (optionalForm.enableSpeechSynthesis) {
      if (!optionalForm.speechSynthesisModelId.trim()) {
        errors.speechSynthesisModelId = 'TTS model id is required.';
      }
      if (!isPositiveIntegerValue(optionalForm.speechSynthesisTimeoutSeconds)) {
        errors.speechSynthesisTimeoutSeconds = 'Timeout must be a positive integer.';
      }
    }

    setOptionalErrors(errors);
    return Object.keys(errors).length === 0;
  }, [optionalForm]);

  const persistOptionalServices = useCallback(async (
    _snapshot: WizardLoadSnapshot,
    loadSnapshot: () => Promise<WizardLoadSnapshot>,
    setSnapshot: (s: WizardLoadSnapshot) => void
  ): Promise<void> => {
    if (!validateOptionalServices()) {
      throw new Error('Optional service inputs are incomplete.');
    }

    if (optionalForm.enableEmbeddings) {
      await api.settings.services.updateProviderFields('Embeddings', HUGGINGFACE_SERVICE_PROVIDER_IDS.Embeddings, {
        ModelId: optionalForm.embeddingsModelId.trim(),
        TimeoutSeconds: optionalForm.embeddingsTimeoutSeconds.trim(),
      });
      await api.settings.services.updateActiveProvider('Embeddings', HUGGINGFACE_SERVICE_PROVIDER_IDS.Embeddings);
    }

    if (optionalForm.enableImages) {
      await api.settings.services.updateProviderFields('ImageGeneration', HUGGINGFACE_SERVICE_PROVIDER_IDS.ImageGeneration, {
        TextToImageModelId: optionalForm.imagesTextToImageModelId.trim(),
        ImageToImageModelId: optionalForm.imagesImageToImageModelId.trim(),
        TimeoutSeconds: optionalForm.imagesTimeoutSeconds.trim(),
      });
      await api.settings.services.updateActiveProvider('ImageGeneration', HUGGINGFACE_SERVICE_PROVIDER_IDS.ImageGeneration);
    }

    if (optionalForm.enableSpeechTranscription) {
      await api.settings.services.updateProviderFields('SpeechTranscription', HUGGINGFACE_SERVICE_PROVIDER_IDS.SpeechTranscription, {
        ModelId: optionalForm.speechTranscriptionModelId.trim(),
        TimeoutSeconds: optionalForm.speechTranscriptionTimeoutSeconds.trim(),
      });
      await api.settings.services.updateActiveProvider('SpeechTranscription', HUGGINGFACE_SERVICE_PROVIDER_IDS.SpeechTranscription);
    }

    if (optionalForm.enableSpeechSynthesis) {
      await api.settings.services.updateProviderFields('SpeechSynthesis', HUGGINGFACE_SERVICE_PROVIDER_IDS.SpeechSynthesis, {
        ModelId: optionalForm.speechSynthesisModelId.trim(),
        TimeoutSeconds: optionalForm.speechSynthesisTimeoutSeconds.trim(),
      });
      await api.settings.services.updateActiveProvider('SpeechSynthesis', HUGGINGFACE_SERVICE_PROVIDER_IDS.SpeechSynthesis);
    }

    const refreshed = await loadSnapshot();
    setSnapshot(refreshed);
    setOptionalErrors({});
  }, [optionalForm, validateOptionalServices]);

  return {
    coreForm,
    optionalForm,
    draftModelId,
    draftAsGlobalDefault,
    draftModels,
    coreErrors,
    optionalErrors,
    modelAddError,
    modelStepError,
    setCoreForm,
    setOptionalForm,
    setDraftModelId,
    setDraftAsGlobalDefault,
    removeDraftModel,
    resetWithSnapshot,
    persistConnection,
    addDraftModel,
    persistModels,
    persistOptionalServices,
  };
}
