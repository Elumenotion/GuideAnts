import { useCallback, useState } from 'react';
import { api } from '../../../services/api';
import {
  OPENAI_CORE_SECTION,
  OPENAI_DEFAULT_CHAT_MODEL_ID,
  OPENAI_OPTIONAL_SERVICE_DEFAULTS,
  OPENAI_SERVICE_PROVIDER_IDS,
  SECRET_MASK,
} from './constants';
import type {
  OpenAiCoreConnectionFormState,
  OpenAiModelDraft,
  OpenAiModelProviderLabel,
  OpenAiOptionalServicesFormState,
  WizardLoadSnapshot,
} from './types';
import {
  buildAddOpenAiModelRequest,
  buildOpenAiCoreForm,
  buildOpenAiOptionalServicesForm,
  hasModelId,
  hasModelTuple,
  isPositiveIntegerValue,
  makeOpenAiDraftModel,
  persistGlobalDefaultModel,
  updateWizardSection,
  withSecretPreserved,
} from './utils';

export interface UseOpenAiWizardStateResult {
  coreForm: OpenAiCoreConnectionFormState;
  optionalForm: OpenAiOptionalServicesFormState;
  draftModelId: string;
  draftModelProvider: OpenAiModelProviderLabel;
  draftAsGlobalDefault: boolean;
  draftModels: OpenAiModelDraft[];
  coreErrors: Partial<Record<'apiKey' | 'endpoint', string>>;
  optionalErrors: Record<string, string>;
  modelAddError: string | null;
  modelStepError: string | null;

  setCoreForm: (patch: Partial<OpenAiCoreConnectionFormState>) => void;
  setOptionalForm: (patch: Partial<OpenAiOptionalServicesFormState>) => void;
  setDraftModelId: (id: string) => void;
  setDraftModelProvider: (provider: OpenAiModelProviderLabel) => void;
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

export function useOpenAiWizardState(): UseOpenAiWizardStateResult {
  const [coreForm, setCoreFormState] = useState<OpenAiCoreConnectionFormState>({
    apiKey: '',
    endpoint: '',
    apiKeyHasStoredValue: false,
  });

  const [optionalForm, setOptionalFormState] = useState<OpenAiOptionalServicesFormState>({
    enableSpeechTranscription: true,
    speechTranscriptionModelId: OPENAI_OPTIONAL_SERVICE_DEFAULTS.speechTranscriptionModelId,
    speechTranscriptionTimeoutSeconds: OPENAI_OPTIONAL_SERVICE_DEFAULTS.speechTranscriptionTimeoutSeconds,
    enableSpeechSynthesis: true,
    speechSynthesisModelId: OPENAI_OPTIONAL_SERVICE_DEFAULTS.speechSynthesisModelId,
    speechSynthesisVoiceName: OPENAI_OPTIONAL_SERVICE_DEFAULTS.speechSynthesisVoiceName,
    speechSynthesisTimeoutSeconds: OPENAI_OPTIONAL_SERVICE_DEFAULTS.speechSynthesisTimeoutSeconds,
    enableImages: true,
    imagesModelId: OPENAI_OPTIONAL_SERVICE_DEFAULTS.imagesModelId,
    imagesTimeoutSeconds: OPENAI_OPTIONAL_SERVICE_DEFAULTS.imagesTimeoutSeconds,
    enableEmbeddings: true,
    embeddingsModelId: OPENAI_OPTIONAL_SERVICE_DEFAULTS.embeddingsModelId,
    embeddingsDimensions: OPENAI_OPTIONAL_SERVICE_DEFAULTS.embeddingsDimensions,
    embeddingsTimeoutSeconds: OPENAI_OPTIONAL_SERVICE_DEFAULTS.embeddingsTimeoutSeconds,
  });

  const [draftModelId, setDraftModelId] = useState(OPENAI_DEFAULT_CHAT_MODEL_ID);
  const [draftModelProvider, setDraftModelProvider] = useState<OpenAiModelProviderLabel>('Completions');
  const [draftAsGlobalDefault, setDraftAsGlobalDefault] = useState(true);
  const [draftModels, setDraftModels] = useState<OpenAiModelDraft[]>([]);

  const [coreErrors, setCoreErrors] = useState<Partial<Record<'apiKey' | 'endpoint', string>>>({});
  const [optionalErrors, setOptionalErrors] = useState<Record<string, string>>({});
  const [modelAddError, setModelAddError] = useState<string | null>(null);
  const [modelStepError, setModelStepError] = useState<string | null>(null);

  const setCoreForm = useCallback((patch: Partial<OpenAiCoreConnectionFormState>) => {
    setCoreFormState((prev) => ({ ...prev, ...patch }));
  }, []);

  const setOptionalForm = useCallback((patch: Partial<OpenAiOptionalServicesFormState>) => {
    setOptionalFormState((prev) => ({ ...prev, ...patch }));
  }, []);

  const removeDraftModel = useCallback((localId: string) => {
    setDraftModels((prev) => prev.filter((m) => m.localId !== localId));
  }, []);

  const resetWithSnapshot = useCallback((snapshot: WizardLoadSnapshot) => {
    setCoreFormState(buildOpenAiCoreForm(snapshot));
    setOptionalFormState(buildOpenAiOptionalServicesForm(snapshot));
    setDraftModels([]);
    setDraftModelId(OPENAI_DEFAULT_CHAT_MODEL_ID);
    setDraftModelProvider('Completions');
    setDraftAsGlobalDefault(snapshot.models.length === 0);
    setCoreErrors({});
    setOptionalErrors({});
    setModelAddError(null);
    setModelStepError(null);
  }, []);

  const validateConnection = useCallback((): boolean => {
    const errors: Partial<Record<'apiKey' | 'endpoint', string>> = {};

    const keyValue = coreForm.apiKey.trim();
    if (!keyValue && !coreForm.apiKeyHasStoredValue) {
      errors.apiKey = 'API key is required.';
    }
    if (keyValue && keyValue !== SECRET_MASK && keyValue.length < 8) {
      errors.apiKey = 'API key looks too short.';
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

    const payload: Record<string, unknown> = {
      ApiKey: withSecretPreserved(coreForm.apiKey, coreForm.apiKeyHasStoredValue),
    };
    const endpointTrimmed = coreForm.endpoint.trim();
    if (endpointTrimmed) {
      payload.Endpoint = endpointTrimmed;
    }

    let nextSections = snapshot.sectionsByName;
    nextSections = await updateWizardSection(OPENAI_CORE_SECTION, payload, nextSections);

    const [sectionSummaries, models] = await Promise.all([
      api.settings.getSections(),
      api.settings.getModels(),
    ]);

    setSnapshot({ ...snapshot, sectionsByName: nextSections, sectionSummaries, models });
    setCoreFormState((prev) => ({
      ...prev,
      apiKey: payload.ApiKey as string,
      apiKeyHasStoredValue: true,
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

    if (hasModelTuple(draftModels, { modelId: normalizedId, provider: draftModelProvider })) {
      setModelAddError('This model/provider combination is already queued.');
      return;
    }

    const isFirstModelOverall = existingCatalogCount === 0 && draftModels.length === 0 && otherProviderDraftCount === 0;
    const shouldSetAsGlobalDefault = isFirstModelOverall || draftAsGlobalDefault;

    const localId = `draft-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`;
    setDraftModels((prev) => {
      const next = shouldSetAsGlobalDefault ? prev.map((m) => ({ ...m, setAsGlobalDefault: false })) : prev;
      return [...next, makeOpenAiDraftModel(localId, normalizedId, draftModelProvider, shouldSetAsGlobalDefault)];
    });

    setDraftModelId('');
    setDraftAsGlobalDefault(false);
    setModelAddError(null);
    setModelStepError(null);
  }, [draftModelId, draftModelProvider, draftModels, draftAsGlobalDefault]);

  const persistModels = useCallback(async (
    snapshot: WizardLoadSnapshot,
    loadSnapshot: () => Promise<WizardLoadSnapshot>,
    setSnapshot: (s: WizardLoadSnapshot) => void,
    onWarning?: (message: string) => void
  ): Promise<void> => {
    const existingCount = snapshot.models.length;
    const pendingDrafts = draftModels.filter((m) => !m.persisted);

    if (existingCount + pendingDrafts.length === 0) {
      setModelStepError('At least one OpenAI model is required.');
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
      await api.settings.addModel(buildAddOpenAiModelRequest(model.modelId, model.provider));
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
      if (!optionalForm.speechSynthesisVoiceName.trim()) {
        errors.speechSynthesisVoiceName = 'Voice name is required.';
      }
      if (!isPositiveIntegerValue(optionalForm.speechSynthesisTimeoutSeconds)) {
        errors.speechSynthesisTimeoutSeconds = 'Timeout must be a positive integer.';
      }
    }

    if (optionalForm.enableImages) {
      if (!optionalForm.imagesModelId.trim()) {
        errors.imagesModelId = 'Image model id is required.';
      }
      if (!isPositiveIntegerValue(optionalForm.imagesTimeoutSeconds)) {
        errors.imagesTimeoutSeconds = 'Timeout must be a positive integer.';
      }
    }

    if (optionalForm.enableEmbeddings) {
      if (!optionalForm.embeddingsModelId.trim()) {
        errors.embeddingsModelId = 'Embedding model id is required.';
      }
      if (!isPositiveIntegerValue(optionalForm.embeddingsTimeoutSeconds)) {
        errors.embeddingsTimeoutSeconds = 'Timeout must be a positive integer.';
      }
      const dimensionsValue = optionalForm.embeddingsDimensions.trim();
      if (dimensionsValue && !isPositiveIntegerValue(dimensionsValue)) {
        errors.embeddingsDimensions = 'Dimensions must be a positive integer if provided.';
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

    if (optionalForm.enableSpeechTranscription) {
      await api.settings.services.updateProviderFields('SpeechTranscription', OPENAI_SERVICE_PROVIDER_IDS.SpeechTranscription, {
        ModelId: optionalForm.speechTranscriptionModelId.trim(),
        TimeoutSeconds: optionalForm.speechTranscriptionTimeoutSeconds.trim(),
      });
      await api.settings.services.updateActiveProvider('SpeechTranscription', OPENAI_SERVICE_PROVIDER_IDS.SpeechTranscription);
    }

    if (optionalForm.enableSpeechSynthesis) {
      await api.settings.services.updateProviderFields('SpeechSynthesis', OPENAI_SERVICE_PROVIDER_IDS.SpeechSynthesis, {
        ModelId: optionalForm.speechSynthesisModelId.trim(),
        VoiceName: optionalForm.speechSynthesisVoiceName.trim(),
        TimeoutSeconds: optionalForm.speechSynthesisTimeoutSeconds.trim(),
      });
      await api.settings.services.updateActiveProvider('SpeechSynthesis', OPENAI_SERVICE_PROVIDER_IDS.SpeechSynthesis);
    }

    if (optionalForm.enableImages) {
      await api.settings.services.updateProviderFields('ImageGeneration', OPENAI_SERVICE_PROVIDER_IDS.ImageGeneration, {
        ModelId: optionalForm.imagesModelId.trim(),
        TimeoutSeconds: optionalForm.imagesTimeoutSeconds.trim(),
      });
      await api.settings.services.updateActiveProvider('ImageGeneration', OPENAI_SERVICE_PROVIDER_IDS.ImageGeneration);
    }

    if (optionalForm.enableEmbeddings) {
      const fields: Record<string, string> = {
        ModelId: optionalForm.embeddingsModelId.trim(),
        TimeoutSeconds: optionalForm.embeddingsTimeoutSeconds.trim(),
      };
      const dimensionsValue = optionalForm.embeddingsDimensions.trim();
      if (dimensionsValue) {
        fields.Dimensions = dimensionsValue;
      }
      await api.settings.services.updateProviderFields('Embeddings', OPENAI_SERVICE_PROVIDER_IDS.Embeddings, fields);
      await api.settings.services.updateActiveProvider('Embeddings', OPENAI_SERVICE_PROVIDER_IDS.Embeddings);
    }

    const refreshed = await loadSnapshot();
    setSnapshot(refreshed);
    setOptionalErrors({});
  }, [optionalForm, validateOptionalServices]);

  return {
    coreForm,
    optionalForm,
    draftModelId,
    draftModelProvider,
    draftAsGlobalDefault,
    draftModels,
    coreErrors,
    optionalErrors,
    modelAddError,
    modelStepError,
    setCoreForm,
    setOptionalForm,
    setDraftModelId,
    setDraftModelProvider,
    setDraftAsGlobalDefault,
    removeDraftModel,
    resetWithSnapshot,
    persistConnection,
    addDraftModel,
    persistModels,
    persistOptionalServices,
  };
}
