import { useCallback, useEffect, useMemo, useState } from 'react';
import { api } from '../../../services/api';
import {
  FOUNDRY_CORE_SECTION,
  FOUNDRY_DOCUMENT_INTELLIGENCE_SECTION,
  FOUNDRY_EMBEDDINGS_SECTION,
  FOUNDRY_IMAGES_SECTION,
  FOUNDRY_SERVICE_PROVIDER_IDS,
  FOUNDRY_SPEECH_SECTION,
  SECRET_MASK,
} from './constants';
import type {
  FoundryCoreConnectionFormState,
  FoundryModelDraft,
  FoundryModelProviderLabel,
  FoundryOptionalServicesFormState,
  WizardLoadSnapshot,
} from './types';
import {
  buildAddModelRequest,
  buildFoundryCoreForm,
  buildFoundryOptionalServicesForm,
  deriveEndpointFromResource,
  hasModelId,
  hasModelTuple,
  makeDraftModel,
  persistGlobalDefaultModel,
  updateWizardSection,
  withSecretPreserved,
} from './utils';

export interface UseFoundryWizardStateResult {
  coreForm: FoundryCoreConnectionFormState;
  optionalForm: FoundryOptionalServicesFormState;
  draftModelId: string;
  draftModelProvider: FoundryModelProviderLabel;
  draftAsGlobalDefault: boolean;
  draftModels: FoundryModelDraft[];
  coreErrors: Partial<Record<'resource' | 'apiKey' | 'apiVersion', string>>;
  optionalErrors: Record<string, string>;
  modelAddError: string | null;
  modelStepError: string | null;
  derivedCoreEndpoint: string;

  setCoreForm: (patch: Partial<FoundryCoreConnectionFormState>) => void;
  setOptionalForm: (patch: Partial<FoundryOptionalServicesFormState>) => void;
  setDraftModelId: (id: string) => void;
  setDraftModelProvider: (provider: FoundryModelProviderLabel) => void;
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

export function useFoundryWizardState(): UseFoundryWizardStateResult {
  const [coreForm, setCoreFormState] = useState<FoundryCoreConnectionFormState>({
    resource: '',
    apiKey: '',
    apiVersion: '',
    apiKeyHasStoredValue: false,
  });

  const [optionalForm, setOptionalFormState] = useState<FoundryOptionalServicesFormState>({
    enableEmbeddings: false,
    embeddingsEndpoint: '',
    embeddingsApiKey: '',
    embeddingsApiKeyHasStoredValue: false,
    embeddingsDeployment: '',
    linkEmbeddingsEndpointToCore: true,
    enableImages: false,
    imagesEndpoint: '',
    imagesApiKey: '',
    imagesApiKeyHasStoredValue: false,
    imagesApiVersion: '',
    imagesDeployment: '',
    imagesEditDeployment: '',
    linkImagesEndpointToCore: true,
    enableSpeech: false,
    speechEndpoint: '',
    speechApiKey: '',
    speechApiKeyHasStoredValue: false,
    speechRegion: 'eastus',
    enableDocumentIntelligence: false,
    documentIntelligenceEndpoint: '',
    documentIntelligenceApiKey: '',
    documentIntelligenceApiKeyHasStoredValue: false,
  });

  const [draftModelId, setDraftModelId] = useState('');
  const [draftModelProvider, setDraftModelProvider] = useState<FoundryModelProviderLabel>('Completions');
  const [draftAsGlobalDefault, setDraftAsGlobalDefault] = useState(true);
  const [draftModels, setDraftModels] = useState<FoundryModelDraft[]>([]);

  const [coreErrors, setCoreErrors] = useState<Partial<Record<'resource' | 'apiKey' | 'apiVersion', string>>>({});
  const [optionalErrors, setOptionalErrors] = useState<Record<string, string>>({});
  const [modelAddError, setModelAddError] = useState<string | null>(null);
  const [modelStepError, setModelStepError] = useState<string | null>(null);

  const derivedCoreEndpoint = useMemo(() => deriveEndpointFromResource(coreForm.resource), [coreForm.resource]);

  useEffect(() => {
    if (!derivedCoreEndpoint) {
      return;
    }
    setOptionalFormState((prev) => ({
      ...prev,
      embeddingsEndpoint: prev.linkEmbeddingsEndpointToCore ? derivedCoreEndpoint : prev.embeddingsEndpoint,
      imagesEndpoint: prev.linkImagesEndpointToCore ? derivedCoreEndpoint : prev.imagesEndpoint,
    }));
  }, [derivedCoreEndpoint]);

  const setCoreForm = useCallback((patch: Partial<FoundryCoreConnectionFormState>) => {
    setCoreFormState((prev) => ({ ...prev, ...patch }));
  }, []);

  const setOptionalForm = useCallback((patch: Partial<FoundryOptionalServicesFormState>) => {
    setOptionalFormState((prev) => ({ ...prev, ...patch }));
  }, []);

  const removeDraftModel = useCallback((localId: string) => {
    setDraftModels((prev) => prev.filter((m) => m.localId !== localId));
  }, []);

  const resetWithSnapshot = useCallback((snapshot: WizardLoadSnapshot) => {
    setCoreFormState(buildFoundryCoreForm(snapshot));
    setOptionalFormState(buildFoundryOptionalServicesForm(snapshot));
    setDraftModels([]);
    setDraftModelId('');
    setDraftModelProvider('Completions');
    setDraftAsGlobalDefault(snapshot.models.length === 0);
    setCoreErrors({});
    setOptionalErrors({});
    setModelAddError(null);
    setModelStepError(null);
  }, []);

  const validateConnection = useCallback((): boolean => {
    const errors: Partial<Record<'resource' | 'apiKey' | 'apiVersion', string>> = {};

    if (!coreForm.resource.trim()) {
      errors.resource = 'Resource is required.';
    }

    const keyValue = coreForm.apiKey.trim();
    if (!keyValue && !coreForm.apiKeyHasStoredValue) {
      errors.apiKey = 'API key is required.';
    }
    if (keyValue && keyValue !== SECRET_MASK && keyValue.length < 8) {
      errors.apiKey = 'API key looks too short.';
    }

    if (!coreForm.apiVersion.trim()) {
      errors.apiVersion = 'API version is required.';
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

    const patchedApiVersion = coreForm.apiVersion.trim() || snapshot.defaults.azureOpenAiApiVersion;
    const payload = {
      Resource: coreForm.resource.trim(),
      ApiKey: withSecretPreserved(coreForm.apiKey, coreForm.apiKeyHasStoredValue),
      ApiVersion: patchedApiVersion,
    };

    let nextSections = snapshot.sectionsByName;
    nextSections = await updateWizardSection(FOUNDRY_CORE_SECTION, payload, nextSections);

    const [sectionSummaries, models] = await Promise.all([
      api.settings.getSections(),
      api.settings.getModels(),
    ]);

    setSnapshot({ ...snapshot, sectionsByName: nextSections, sectionSummaries, models });
    setCoreFormState((prev) => ({
      ...prev,
      apiVersion: patchedApiVersion,
      apiKey: payload.ApiKey,
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
      return [...next, makeDraftModel(localId, normalizedId, draftModelProvider, shouldSetAsGlobalDefault)];
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
      setModelStepError('At least one model is required.');
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
      await api.settings.addModel(buildAddModelRequest(model.modelId, model.provider));
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
    const getEndpoint = (value: string, linked: boolean) => (linked ? derivedCoreEndpoint : value).trim();
    const requireSecret = (value: string, hasStoredValue: boolean) => value.trim().length > 0 || hasStoredValue;

    if (optionalForm.enableEmbeddings) {
      if (!getEndpoint(optionalForm.embeddingsEndpoint, optionalForm.linkEmbeddingsEndpointToCore)) {
        errors.embeddingsEndpoint = 'Endpoint is required.';
      }
      if (!requireSecret(optionalForm.embeddingsApiKey, optionalForm.embeddingsApiKeyHasStoredValue)) {
        errors.embeddingsApiKey = 'API key is required.';
      }
      if (!optionalForm.embeddingsDeployment.trim()) {
        errors.embeddingsDeployment = 'Deployment is required.';
      }
    }

    if (optionalForm.enableImages) {
      if (!getEndpoint(optionalForm.imagesEndpoint, optionalForm.linkImagesEndpointToCore)) {
        errors.imagesEndpoint = 'Endpoint is required.';
      }
      if (!requireSecret(optionalForm.imagesApiKey, optionalForm.imagesApiKeyHasStoredValue)) {
        errors.imagesApiKey = 'API key is required.';
      }
      if (!optionalForm.imagesApiVersion.trim()) {
        errors.imagesApiVersion = 'API version is required.';
      }
      if (!optionalForm.imagesDeployment.trim()) {
        errors.imagesDeployment = 'Generation deployment is required.';
      }
      if (!optionalForm.imagesEditDeployment.trim()) {
        errors.imagesEditDeployment = 'Edit deployment is required.';
      }
    }

    if (optionalForm.enableSpeech) {
      if (!optionalForm.speechEndpoint.trim()) {
        errors.speechEndpoint = 'Endpoint is required.';
      }
      if (!requireSecret(optionalForm.speechApiKey, optionalForm.speechApiKeyHasStoredValue)) {
        errors.speechApiKey = 'API key is required.';
      }
      if (!optionalForm.speechRegion.trim()) {
        errors.speechRegion = 'Region is required.';
      }
    }

    if (optionalForm.enableDocumentIntelligence) {
      if (!optionalForm.documentIntelligenceEndpoint.trim()) {
        errors.documentIntelligenceEndpoint = 'Endpoint is required.';
      }
      if (!requireSecret(optionalForm.documentIntelligenceApiKey, optionalForm.documentIntelligenceApiKeyHasStoredValue)) {
        errors.documentIntelligenceApiKey = 'API key is required.';
      }
    }

    setOptionalErrors(errors);
    return Object.keys(errors).length === 0;
  }, [derivedCoreEndpoint, optionalForm]);

  const persistOptionalServices = useCallback(async (
    snapshot: WizardLoadSnapshot,
    loadSnapshot: () => Promise<WizardLoadSnapshot>,
    setSnapshot: (s: WizardLoadSnapshot) => void
  ): Promise<void> => {
    if (!validateOptionalServices()) {
      throw new Error('Optional service inputs are incomplete.');
    }

    let nextSections = snapshot.sectionsByName;
    const ensureEndpoint = (value: string, linked: boolean) => (linked ? derivedCoreEndpoint : value).trim();

    if (optionalForm.enableEmbeddings) {
      nextSections = await updateWizardSection(
        FOUNDRY_EMBEDDINGS_SECTION,
        {
          Endpoint: ensureEndpoint(optionalForm.embeddingsEndpoint, optionalForm.linkEmbeddingsEndpointToCore),
          ApiKey: withSecretPreserved(optionalForm.embeddingsApiKey, optionalForm.embeddingsApiKeyHasStoredValue),
        },
        nextSections
      );
      await api.settings.services.updateProviderFields('Embeddings', FOUNDRY_SERVICE_PROVIDER_IDS.Embeddings, {
        Deployment: optionalForm.embeddingsDeployment.trim(),
      });
      await api.settings.services.updateActiveProvider('Embeddings', FOUNDRY_SERVICE_PROVIDER_IDS.Embeddings);
    }

    if (optionalForm.enableImages) {
      nextSections = await updateWizardSection(
        FOUNDRY_IMAGES_SECTION,
        {
          Endpoint: ensureEndpoint(optionalForm.imagesEndpoint, optionalForm.linkImagesEndpointToCore),
          ApiKey: withSecretPreserved(optionalForm.imagesApiKey, optionalForm.imagesApiKeyHasStoredValue),
          ApiVersion: optionalForm.imagesApiVersion.trim(),
        },
        nextSections
      );
      await api.settings.services.updateProviderFields('ImageGeneration', FOUNDRY_SERVICE_PROVIDER_IDS.ImageGeneration, {
        Deployment: optionalForm.imagesDeployment.trim(),
        EditModelDeployment: optionalForm.imagesEditDeployment.trim(),
      });
      await api.settings.services.updateActiveProvider('ImageGeneration', FOUNDRY_SERVICE_PROVIDER_IDS.ImageGeneration);
    }

    if (optionalForm.enableSpeech) {
      nextSections = await updateWizardSection(
        FOUNDRY_SPEECH_SECTION,
        {
          Endpoint: optionalForm.speechEndpoint.trim(),
          ApiKey: withSecretPreserved(optionalForm.speechApiKey, optionalForm.speechApiKeyHasStoredValue),
          Region: optionalForm.speechRegion.trim(),
        },
        nextSections
      );
      await api.settings.services.updateActiveProvider('SpeechTranscription', FOUNDRY_SERVICE_PROVIDER_IDS.SpeechTranscription);
      await api.settings.services.updateActiveProvider('SpeechSynthesis', FOUNDRY_SERVICE_PROVIDER_IDS.SpeechSynthesis);
    }

    if (optionalForm.enableDocumentIntelligence) {
      nextSections = await updateWizardSection(
        FOUNDRY_DOCUMENT_INTELLIGENCE_SECTION,
        {
          Endpoint: optionalForm.documentIntelligenceEndpoint.trim(),
          ApiKey: withSecretPreserved(
            optionalForm.documentIntelligenceApiKey,
            optionalForm.documentIntelligenceApiKeyHasStoredValue
          ),
        },
        nextSections
      );
      await api.settings.services.updateActiveProvider('DocumentIntelligence', FOUNDRY_SERVICE_PROVIDER_IDS.DocumentIntelligence);
    }

    const refreshed = await loadSnapshot();
    setSnapshot(refreshed);
    setOptionalFormState((prev) => ({
      ...prev,
      embeddingsApiKey: prev.enableEmbeddings ? SECRET_MASK : prev.embeddingsApiKey,
      embeddingsApiKeyHasStoredValue: prev.enableEmbeddings ? true : prev.embeddingsApiKeyHasStoredValue,
      imagesApiKey: prev.enableImages ? SECRET_MASK : prev.imagesApiKey,
      imagesApiKeyHasStoredValue: prev.enableImages ? true : prev.imagesApiKeyHasStoredValue,
      speechApiKey: prev.enableSpeech ? SECRET_MASK : prev.speechApiKey,
      speechApiKeyHasStoredValue: prev.enableSpeech ? true : prev.speechApiKeyHasStoredValue,
      documentIntelligenceApiKey: prev.enableDocumentIntelligence ? SECRET_MASK : prev.documentIntelligenceApiKey,
      documentIntelligenceApiKeyHasStoredValue: prev.enableDocumentIntelligence ? true : prev.documentIntelligenceApiKeyHasStoredValue,
    }));
    setOptionalErrors({});
  }, [derivedCoreEndpoint, optionalForm, validateOptionalServices]);

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
    derivedCoreEndpoint,
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
