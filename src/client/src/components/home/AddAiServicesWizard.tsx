import { useCallback, useEffect, useMemo, useState } from 'react';
import { api } from '../../services/api';
import type { ServiceEditorStateDto, SettingsSchemaDto, SettingsSectionDto } from '../../types/settings';
import { SettingsModal } from '../../pages/settings/components/shared/SettingsModal';
import {
  AZURE_FOUNDATION_SECTION,
  DOCUMENT_INTELLIGENCE_SECTION,
  EMBEDDINGS_SECTION,
  IMAGES_SECTION,
  SECRET_MASK,
  SERVICE_PROVIDER_IDS,
  SPEECH_SECTION,
  WIZARD_STEPS,
} from './addAiServicesWizard/constants';
import { CoreConnectionStep } from './addAiServicesWizard/steps/CoreConnectionStep';
import { FinishStep } from './addAiServicesWizard/steps/FinishStep';
import { ModelsStep } from './addAiServicesWizard/steps/ModelsStep';
import { OptionalServicesStep } from './addAiServicesWizard/steps/OptionalServicesStep';
import { ProviderStep } from './addAiServicesWizard/steps/ProviderStep';
import type {
  AddAiServicesWizardStep,
  CoreConnectionFormState,
  FoundryModelDraft,
  FoundryModelProviderLabel,
  OptionalServiceKey,
  OptionalServicesFormState,
  WizardLoadSnapshot,
} from './addAiServicesWizard/types';
import {
  buildAddModelRequest,
  deriveEndpointFromResource,
  hasModelId,
  hasModelTuple,
  makeDraftModel,
  summarizeOptionalServiceWarnings,
  toExistingFoundryModels,
} from './addAiServicesWizard/utils';

interface AddAiServicesWizardProps {
  isOpen: boolean;
  onDismiss: (persistDismissal: boolean) => void;
  onOpenSettings: (persistDismissal: boolean) => void;
}

function getSchemaDefault(schema: SettingsSchemaDto, sectionName: string, fieldName: string, fallback: string): string {
  const section = schema.sections.find((item) => item.sectionName === sectionName);
  const property = section?.properties.find((item) => item.name === fieldName);
  const defaultValue = property?.defaultValue;
  if (typeof defaultValue === 'string' && defaultValue.trim().length > 0) {
    return defaultValue;
  }
  return fallback;
}

function buildCoreForm(snapshot: WizardLoadSnapshot): CoreConnectionFormState {
  const section = snapshot.sectionsByName[AZURE_FOUNDATION_SECTION];
  return {
    resource: String(section?.payload.Resource ?? ''),
    apiKey: String(section?.payload.ApiKey ?? ''),
    apiVersion: String(section?.payload.ApiVersion ?? snapshot.defaults.azureOpenAiApiVersion),
    apiKeyHasStoredValue: Boolean(section?.secretHasValue?.ApiKey),
  };
}

function getAzureProviderField(
  state: ServiceEditorStateDto | undefined,
  providerId: string,
  fieldName: string
): string {
  const provider = state?.providers.find((item) => item.providerId === providerId);
  const value = provider?.fields?.[fieldName]?.value;
  return typeof value === 'string' ? value : '';
}

function getSectionStringValue(section: SettingsSectionDto | undefined, fieldName: string): string {
  const value = section?.payload?.[fieldName];
  return typeof value === 'string' ? value : '';
}

function hasStoredSecret(section: SettingsSectionDto | undefined, fieldName: string): boolean {
  return Boolean(section?.secretHasValue?.[fieldName]);
}

function buildOptionalServicesForm(snapshot: WizardLoadSnapshot): OptionalServicesFormState {
  const coreResource = getSectionStringValue(snapshot.sectionsByName[AZURE_FOUNDATION_SECTION], 'Resource');
  const derivedEndpoint = deriveEndpointFromResource(coreResource);

  const embeddingsSection = snapshot.sectionsByName[EMBEDDINGS_SECTION];
  const imagesSection = snapshot.sectionsByName[IMAGES_SECTION];
  const speechSection = snapshot.sectionsByName[SPEECH_SECTION];
  const documentSection = snapshot.sectionsByName[DOCUMENT_INTELLIGENCE_SECTION];

  const embeddingsEndpoint = getSectionStringValue(embeddingsSection, 'Endpoint');
  const imagesEndpoint = getSectionStringValue(imagesSection, 'Endpoint');

  const embeddingsLink = Boolean(derivedEndpoint) && (embeddingsEndpoint.length === 0 || embeddingsEndpoint === derivedEndpoint);
  const imagesLink = Boolean(derivedEndpoint) && (imagesEndpoint.length === 0 || imagesEndpoint === derivedEndpoint);

  return {
    enableEmbeddings: snapshot.sectionSummaries.some((section) => section.sectionName === EMBEDDINGS_SECTION && section.readinessStatus === 'configured'),
    embeddingsEndpoint: embeddingsLink ? derivedEndpoint : embeddingsEndpoint,
    embeddingsApiKey: getSectionStringValue(embeddingsSection, 'ApiKey'),
    embeddingsApiKeyHasStoredValue: hasStoredSecret(embeddingsSection, 'ApiKey'),
    embeddingsDeployment: getAzureProviderField(
      snapshot.serviceStates.Embeddings,
      SERVICE_PROVIDER_IDS.Embeddings,
      'Deployment'
    ),
    linkEmbeddingsEndpointToCore: embeddingsLink,

    enableImages: snapshot.sectionSummaries.some((section) => section.sectionName === IMAGES_SECTION && section.readinessStatus === 'configured'),
    imagesEndpoint: imagesLink ? derivedEndpoint : imagesEndpoint,
    imagesApiKey: getSectionStringValue(imagesSection, 'ApiKey'),
    imagesApiKeyHasStoredValue: hasStoredSecret(imagesSection, 'ApiKey'),
    imagesApiVersion: getSectionStringValue(imagesSection, 'ApiVersion') || snapshot.defaults.azureOpenAiImagesApiVersion,
    imagesDeployment: getAzureProviderField(
      snapshot.serviceStates.ImageGeneration,
      SERVICE_PROVIDER_IDS.ImageGeneration,
      'Deployment'
    ),
    imagesEditDeployment: getAzureProviderField(
      snapshot.serviceStates.ImageGeneration,
      SERVICE_PROVIDER_IDS.ImageGeneration,
      'EditModelDeployment'
    ),
    linkImagesEndpointToCore: imagesLink,

    enableSpeech: snapshot.sectionSummaries.some((section) => section.sectionName === SPEECH_SECTION && section.readinessStatus === 'configured'),
    speechEndpoint: getSectionStringValue(speechSection, 'Endpoint'),
    speechApiKey: getSectionStringValue(speechSection, 'ApiKey'),
    speechApiKeyHasStoredValue: hasStoredSecret(speechSection, 'ApiKey'),
    speechRegion: getSectionStringValue(speechSection, 'Region') || 'eastus',

    enableDocumentIntelligence: snapshot.sectionSummaries.some(
      (section) => section.sectionName === DOCUMENT_INTELLIGENCE_SECTION && section.readinessStatus === 'configured'
    ),
    documentIntelligenceEndpoint: getSectionStringValue(documentSection, 'Endpoint'),
    documentIntelligenceApiKey: getSectionStringValue(documentSection, 'ApiKey'),
    documentIntelligenceApiKeyHasStoredValue: hasStoredSecret(documentSection, 'ApiKey'),
  };
}

function nextStep(current: AddAiServicesWizardStep): AddAiServicesWizardStep {
  if (current === 'provider') {
    return 'connection';
  }
  if (current === 'connection') {
    return 'models';
  }
  if (current === 'models') {
    return 'optionalServices';
  }
  if (current === 'optionalServices') {
    return 'finish';
  }
  return 'finish';
}

function previousStep(current: AddAiServicesWizardStep): AddAiServicesWizardStep {
  if (current === 'finish') {
    return 'optionalServices';
  }
  if (current === 'optionalServices') {
    return 'models';
  }
  if (current === 'models') {
    return 'connection';
  }
  if (current === 'connection') {
    return 'provider';
  }
  return 'provider';
}

const OPTIONAL_SERVICE_KEYS: OptionalServiceKey[] = [
  'Embeddings',
  'ImageGeneration',
  'SpeechTranscription',
  'SpeechSynthesis',
  'DocumentIntelligence',
];

export default function AddAiServicesWizard({ isOpen, onDismiss, onOpenSettings }: AddAiServicesWizardProps) {
  const [step, setStep] = useState<AddAiServicesWizardStep>('provider');
  const [provider, setProvider] = useState('microsot-foundry');
  const [dontAutoOpenAgain, setDontAutoOpenAgain] = useState(false);

  const [snapshot, setSnapshot] = useState<WizardLoadSnapshot | null>(null);
  const [coreForm, setCoreForm] = useState<CoreConnectionFormState>({
    resource: '',
    apiKey: '',
    apiVersion: '',
    apiKeyHasStoredValue: false,
  });
  const [optionalForm, setOptionalForm] = useState<OptionalServicesFormState>({
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
  const [draftModels, setDraftModels] = useState<FoundryModelDraft[]>([]);

  const [coreErrors, setCoreErrors] = useState<Partial<Record<'resource' | 'apiKey' | 'apiVersion', string>>>({});
  const [optionalErrors, setOptionalErrors] = useState<Record<string, string>>({});
  const [modelAddError, setModelAddError] = useState<string | null>(null);
  const [modelStepError, setModelStepError] = useState<string | null>(null);
  const [finishWarnings, setFinishWarnings] = useState<string[]>([]);

  const [loading, setLoading] = useState(false);
  const [saving, setSaving] = useState(false);
  const [globalError, setGlobalError] = useState<string | null>(null);

  const existingFoundryModels = useMemo(
    () => (snapshot ? toExistingFoundryModels(snapshot.models) : []),
    [snapshot]
  );
  const totalModelCount = existingFoundryModels.length + draftModels.length;
  const savedFoundryModelCount = useMemo(
    () => (snapshot ? toExistingFoundryModels(snapshot.models).length : 0),
    [snapshot]
  );
  const coreConnectionConfigured = useMemo(
    () => Boolean(
      snapshot?.sectionSummaries.some(
        (section) => section.sectionName === AZURE_FOUNDATION_SECTION && section.readinessStatus === 'configured'
      )
    ),
    [snapshot]
  );
  const derivedCoreEndpoint = useMemo(() => deriveEndpointFromResource(coreForm.resource), [coreForm.resource]);
  const readyForBasicChat = useMemo(
    () => coreConnectionConfigured && savedFoundryModelCount > 0,
    [coreConnectionConfigured, savedFoundryModelCount]
  );

  const loadServiceState = useCallback(async (serviceId: OptionalServiceKey): Promise<ServiceEditorStateDto | undefined> => {
    try {
      return await api.settings.services.get(serviceId);
    } catch {
      return undefined;
    }
  }, []);

  const loadSnapshot = useCallback(async (): Promise<WizardLoadSnapshot> => {
    const sectionNames = [
      AZURE_FOUNDATION_SECTION,
      EMBEDDINGS_SECTION,
      IMAGES_SECTION,
      SPEECH_SECTION,
      DOCUMENT_INTELLIGENCE_SECTION,
    ];
    const [sectionSummaries, schema, models, ...sections] = await Promise.all([
      api.settings.getSections(),
      api.settings.getSchema(),
      api.settings.getModels(),
      ...sectionNames.map((name) => api.settings.getSection(name)),
    ]);

    const serviceStatesArray = await Promise.all(OPTIONAL_SERVICE_KEYS.map((serviceId) => loadServiceState(serviceId)));
    const serviceStates: Partial<Record<OptionalServiceKey, ServiceEditorStateDto>> = {};
    OPTIONAL_SERVICE_KEYS.forEach((serviceId, index) => {
      const value = serviceStatesArray[index];
      if (value) {
        serviceStates[serviceId] = value;
      }
    });

    const sectionsByName: Record<string, SettingsSectionDto> = {};
    sections.forEach((section) => {
      sectionsByName[section.sectionName] = section;
    });

    return {
      sectionSummaries,
      sectionsByName,
      models,
      serviceStates,
      defaults: {
        azureOpenAiApiVersion: getSchemaDefault(schema, AZURE_FOUNDATION_SECTION, 'ApiVersion', '2025-04-01-preview'),
        azureOpenAiImagesApiVersion: getSchemaDefault(schema, IMAGES_SECTION, 'ApiVersion', '2025-04-01-preview'),
      },
    };
  }, [loadServiceState]);

  const resetWithSnapshot = useCallback((nextSnapshot: WizardLoadSnapshot) => {
    setSnapshot(nextSnapshot);
    setCoreForm(buildCoreForm(nextSnapshot));
    setOptionalForm(buildOptionalServicesForm(nextSnapshot));
    setDraftModels([]);
    setDraftModelId('');
    setDraftModelProvider('Completions');
    setCoreErrors({});
    setOptionalErrors({});
    setModelAddError(null);
    setModelStepError(null);
    setFinishWarnings(summarizeOptionalServiceWarnings(nextSnapshot));
  }, []);

  useEffect(() => {
    if (!isOpen) {
      return;
    }
    let cancelled = false;
    setLoading(true);
    setGlobalError(null);
    setStep('provider');
    setProvider('microsot-foundry');
    setDontAutoOpenAgain(false);
    void (async () => {
      try {
        const initialSnapshot = await loadSnapshot();
        if (cancelled) {
          return;
        }
        resetWithSnapshot(initialSnapshot);
      } catch (error) {
        if (!cancelled) {
          const message = error instanceof Error ? error.message : 'Failed to load wizard data.';
          setGlobalError(message);
        }
      } finally {
        if (!cancelled) {
          setLoading(false);
        }
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [isOpen, loadSnapshot, resetWithSnapshot]);

  useEffect(() => {
    if (!isOpen || !derivedCoreEndpoint) {
      return;
    }
    setOptionalForm((previous) => ({
      ...previous,
      embeddingsEndpoint: previous.linkEmbeddingsEndpointToCore ? derivedCoreEndpoint : previous.embeddingsEndpoint,
      imagesEndpoint: previous.linkImagesEndpointToCore ? derivedCoreEndpoint : previous.imagesEndpoint,
    }));
  }, [derivedCoreEndpoint, isOpen]);

  const closeWizard = useCallback(() => {
    onDismiss(dontAutoOpenAgain);
  }, [dontAutoOpenAgain, onDismiss]);

  const openSettings = useCallback(() => {
    onOpenSettings(dontAutoOpenAgain);
  }, [dontAutoOpenAgain, onOpenSettings]);

  const withSecretPreserved = (value: string, hasStoredValue: boolean): string => {
    const trimmed = value.trim();
    if (trimmed.length > 0) {
      return trimmed;
    }
    return hasStoredValue ? SECRET_MASK : '';
  };

  const updateSection = async (
    sectionName: string,
    payload: Record<string, unknown>,
    currentSectionsByName: Record<string, SettingsSectionDto>
  ): Promise<Record<string, SettingsSectionDto>> => {
    const section = currentSectionsByName[sectionName];
    if (!section) {
      return currentSectionsByName;
    }
    const updated = await api.settings.updateSection(sectionName, {
      rowVersion: section.rowVersion,
      payload,
    });
    return {
      ...currentSectionsByName,
      [sectionName]: updated,
    };
  };

  const validateCoreConnection = (): boolean => {
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
  };

  const persistCoreConnection = useCallback(async () => {
    if (!snapshot) {
      throw new Error('Wizard state is not loaded.');
    }
    if (!validateCoreConnection()) {
      throw new Error('Connection details are incomplete.');
    }

    const patchedApiVersion = coreForm.apiVersion.trim() || snapshot.defaults.azureOpenAiApiVersion;
    const payload = {
      Resource: coreForm.resource.trim(),
      ApiKey: withSecretPreserved(coreForm.apiKey, coreForm.apiKeyHasStoredValue),
      ApiVersion: patchedApiVersion,
    };

    let nextSections = snapshot.sectionsByName;
    nextSections = await updateSection(AZURE_FOUNDATION_SECTION, payload, nextSections);
    const [sectionSummaries, models] = await Promise.all([
      api.settings.getSections(),
      api.settings.getModels(),
    ]);

    const nextSnapshot: WizardLoadSnapshot = {
      ...snapshot,
      sectionsByName: nextSections,
      sectionSummaries,
      models,
    };
    setSnapshot(nextSnapshot);
    setCoreForm((previous) => ({
      ...previous,
      apiVersion: patchedApiVersion,
      apiKey: payload.ApiKey,
      apiKeyHasStoredValue: true,
    }));
    setFinishWarnings(summarizeOptionalServiceWarnings(nextSnapshot));
  }, [coreForm.apiKey, coreForm.apiKeyHasStoredValue, coreForm.apiVersion, coreForm.resource, snapshot]);

  const addDraftModel = useCallback(() => {
    const normalizedId = draftModelId.trim();
    if (!normalizedId) {
      setModelAddError('Model is required.');
      return;
    }
    const existingModel = snapshot?.models.find(
      (model) => model.modelId.trim().toLowerCase() === normalizedId.toLowerCase()
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
    const localId = `draft-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`;
    setDraftModels((previous) => [...previous, makeDraftModel(localId, normalizedId, draftModelProvider)]);
    setDraftModelId('');
    setModelAddError(null);
    setModelStepError(null);
  }, [draftModelId, draftModelProvider, draftModels, snapshot]);

  const persistModels = useCallback(async () => {
    if (!snapshot) {
      throw new Error('Wizard state is not loaded.');
    }
    const existingCount = toExistingFoundryModels(snapshot.models).length;
    const pendingDrafts = draftModels.filter((model) => !model.persisted);
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
    const existingById = new Map(latestModels.map((model) => [model.modelId.trim().toLowerCase(), model]));
    const existingConflict = pendingDrafts.find((model) => existingById.has(model.modelId.trim().toLowerCase()));
    if (existingConflict) {
      const conflict = existingById.get(existingConflict.modelId.trim().toLowerCase());
      const provider = conflict?.provider ?? 'unknown';
      setModelStepError(
        `Model '${existingConflict.modelId}' already exists with provider '${provider}'. Choose a different model id.`
      );
      throw new Error('Model id conflict detected.');
    }

    for (const model of pendingDrafts) {
      const request = buildAddModelRequest(model.modelId, model.provider);
      await api.settings.addModel(request);
    }

    const refreshed = await loadSnapshot();
    setSnapshot(refreshed);
    setDraftModels([]);
    setFinishWarnings(summarizeOptionalServiceWarnings(refreshed));
    setModelStepError(null);
  }, [draftModels, loadSnapshot, snapshot]);

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

  const persistOptionalServices = useCallback(async () => {
    if (!snapshot) {
      throw new Error('Wizard state is not loaded.');
    }
    if (!validateOptionalServices()) {
      throw new Error('Optional service inputs are incomplete.');
    }

    let nextSections = snapshot.sectionsByName;
    const ensureEndpoint = (value: string, linked: boolean) => (linked ? derivedCoreEndpoint : value).trim();

    if (optionalForm.enableEmbeddings) {
      nextSections = await updateSection(
        EMBEDDINGS_SECTION,
        {
          Endpoint: ensureEndpoint(optionalForm.embeddingsEndpoint, optionalForm.linkEmbeddingsEndpointToCore),
          ApiKey: withSecretPreserved(optionalForm.embeddingsApiKey, optionalForm.embeddingsApiKeyHasStoredValue),
        },
        nextSections
      );
      await api.settings.services.updateProviderFields('Embeddings', SERVICE_PROVIDER_IDS.Embeddings, {
        Deployment: optionalForm.embeddingsDeployment.trim(),
      });
      await api.settings.services.updateActiveProvider('Embeddings', SERVICE_PROVIDER_IDS.Embeddings);
    }

    if (optionalForm.enableImages) {
      nextSections = await updateSection(
        IMAGES_SECTION,
        {
          Endpoint: ensureEndpoint(optionalForm.imagesEndpoint, optionalForm.linkImagesEndpointToCore),
          ApiKey: withSecretPreserved(optionalForm.imagesApiKey, optionalForm.imagesApiKeyHasStoredValue),
          ApiVersion: optionalForm.imagesApiVersion.trim(),
        },
        nextSections
      );
      await api.settings.services.updateProviderFields('ImageGeneration', SERVICE_PROVIDER_IDS.ImageGeneration, {
        Deployment: optionalForm.imagesDeployment.trim(),
        EditModelDeployment: optionalForm.imagesEditDeployment.trim(),
      });
      await api.settings.services.updateActiveProvider('ImageGeneration', SERVICE_PROVIDER_IDS.ImageGeneration);
    }

    if (optionalForm.enableSpeech) {
      nextSections = await updateSection(
        SPEECH_SECTION,
        {
          Endpoint: optionalForm.speechEndpoint.trim(),
          ApiKey: withSecretPreserved(optionalForm.speechApiKey, optionalForm.speechApiKeyHasStoredValue),
          Region: optionalForm.speechRegion.trim(),
        },
        nextSections
      );
      await api.settings.services.updateActiveProvider('SpeechTranscription', SERVICE_PROVIDER_IDS.SpeechTranscription);
      await api.settings.services.updateActiveProvider('SpeechSynthesis', SERVICE_PROVIDER_IDS.SpeechSynthesis);
    }

    if (optionalForm.enableDocumentIntelligence) {
      nextSections = await updateSection(
        DOCUMENT_INTELLIGENCE_SECTION,
        {
          Endpoint: optionalForm.documentIntelligenceEndpoint.trim(),
          ApiKey: withSecretPreserved(
            optionalForm.documentIntelligenceApiKey,
            optionalForm.documentIntelligenceApiKeyHasStoredValue
          ),
        },
        nextSections
      );
      await api.settings.services.updateActiveProvider('DocumentIntelligence', SERVICE_PROVIDER_IDS.DocumentIntelligence);
    }

    const refreshed = await loadSnapshot();
    setSnapshot(refreshed);
    setOptionalForm((previous) => ({
      ...previous,
      embeddingsApiKey: previous.enableEmbeddings
        ? SECRET_MASK
        : previous.embeddingsApiKey,
      embeddingsApiKeyHasStoredValue: previous.enableEmbeddings
        ? true
        : previous.embeddingsApiKeyHasStoredValue,
      imagesApiKey: previous.enableImages
        ? SECRET_MASK
        : previous.imagesApiKey,
      imagesApiKeyHasStoredValue: previous.enableImages
        ? true
        : previous.imagesApiKeyHasStoredValue,
      speechApiKey: previous.enableSpeech
        ? SECRET_MASK
        : previous.speechApiKey,
      speechApiKeyHasStoredValue: previous.enableSpeech
        ? true
        : previous.speechApiKeyHasStoredValue,
      documentIntelligenceApiKey: previous.enableDocumentIntelligence
        ? SECRET_MASK
        : previous.documentIntelligenceApiKey,
      documentIntelligenceApiKeyHasStoredValue: previous.enableDocumentIntelligence
        ? true
        : previous.documentIntelligenceApiKeyHasStoredValue,
    }));
    setFinishWarnings(summarizeOptionalServiceWarnings(refreshed));
    setOptionalErrors({});
  }, [derivedCoreEndpoint, loadSnapshot, optionalForm, snapshot, validateOptionalServices]);

  const handleNext = useCallback(async () => {
    if (saving) {
      return;
    }
    setGlobalError(null);
    setSaving(true);
    try {
      if (step === 'connection') {
        await persistCoreConnection();
      } else if (step === 'models') {
        await persistModels();
      } else if (step === 'optionalServices') {
        await persistOptionalServices();
      }
      setStep((previous) => nextStep(previous));
    } catch (error) {
      const message = error instanceof Error ? error.message : 'Could not continue to the next step.';
      setGlobalError(message);
    } finally {
      setSaving(false);
    }
  }, [persistCoreConnection, persistModels, persistOptionalServices, saving, step]);

  const handleBack = useCallback(() => {
    if (saving) {
      return;
    }
    setGlobalError(null);
    setStep((previous) => previousStep(previous));
  }, [saving]);

  const isNextDisabled = useMemo(() => {
    if (loading || saving) {
      return true;
    }
    if (step === 'provider') {
      return provider.trim().length === 0;
    }
    if (step === 'models') {
      return totalModelCount === 0;
    }
    if (step === 'finish') {
      return !readyForBasicChat;
    }
    return false;
  }, [loading, provider, readyForBasicChat, saving, step, totalModelCount]);

  const currentStepLabel = useMemo(() => {
    const index = WIZARD_STEPS.findIndex((item) => item.id === step);
    return `${index + 1} of ${WIZARD_STEPS.length}`;
  }, [step]);

  return (
    <SettingsModal
      isOpen={isOpen}
      title="Add AI Services Wizard"
      onClose={closeWizard}
      maxWidthClass="max-w-4xl"
      disableDismiss={saving}
      footer={(
        <div className="flex w-full flex-wrap items-center justify-between gap-2">
          <label className="inline-flex items-center gap-2 text-xs text-gray-700">
            <input
              type="checkbox"
              className="h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500"
              checked={dontAutoOpenAgain}
              onChange={(event) => setDontAutoOpenAgain(event.target.checked)}
            />
            Don&apos;t auto-open this again on this device
          </label>
          <div className="flex flex-wrap gap-2">
            <button
              type="button"
              onClick={closeWizard}
              className="rounded border border-gray-300 px-3 py-1.5 text-sm text-gray-700 hover:bg-gray-50"
            >
              Not now
            </button>
            <button
              type="button"
              onClick={openSettings}
              className="rounded border border-gray-300 px-3 py-1.5 text-sm text-gray-700 hover:bg-gray-50"
            >
              Configure manually
            </button>
            {step !== 'provider' ? (
              <button
                type="button"
                onClick={handleBack}
                disabled={saving}
                className="rounded border border-gray-300 px-3 py-1.5 text-sm text-gray-700 hover:bg-gray-50 disabled:opacity-50"
              >
                Back
              </button>
            ) : null}
            {step !== 'finish' ? (
              <button
                type="button"
                onClick={() => void handleNext()}
                disabled={isNextDisabled}
                className="rounded bg-blue-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-blue-700 disabled:bg-blue-400"
              >
                {saving ? 'Saving...' : 'Next'}
              </button>
            ) : (
              <button
                type="button"
                onClick={closeWizard}
                disabled={isNextDisabled}
                className="rounded bg-blue-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-blue-700 disabled:bg-blue-400"
              >
                Done
              </button>
            )}
          </div>
        </div>
      )}
    >
      <div className="space-y-4">
        <div className="flex flex-wrap items-center justify-between gap-2">
          <div className="text-xs text-gray-500">Step {currentStepLabel}</div>
          <div className="flex flex-wrap gap-2 text-xs">
            {WIZARD_STEPS.map((item) => (
              <span
                key={item.id}
                className={`rounded-full px-2 py-0.5 ${
                  item.id === step ? 'bg-blue-100 text-blue-800' : 'bg-gray-100 text-gray-500'
                }`}
              >
                {item.label}
              </span>
            ))}
          </div>
        </div>

        {loading ? <p className="text-sm text-gray-600">Loading wizard data...</p> : null}
        {globalError ? <div className="rounded border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">{globalError}</div> : null}

        {!loading && step === 'provider' ? (
          <ProviderStep value={provider} onChange={setProvider} />
        ) : null}

        {!loading && step === 'connection' ? (
          <CoreConnectionStep
            resource={coreForm.resource}
            apiKey={coreForm.apiKey}
            apiVersion={coreForm.apiVersion}
            apiKeyHasStoredValue={coreForm.apiKeyHasStoredValue}
            errors={coreErrors}
            onChange={(patch) => setCoreForm((previous) => ({ ...previous, ...patch }))}
          />
        ) : null}

        {!loading && step === 'models' ? (
          <ModelsStep
            existingModels={existingFoundryModels}
            draftModels={draftModels}
            draftModelId={draftModelId}
            draftProvider={draftModelProvider}
            addError={modelAddError}
            validationError={modelStepError}
            onDraftModelIdChange={setDraftModelId}
            onDraftProviderChange={setDraftModelProvider}
            onAddModel={addDraftModel}
            onRemoveDraftModel={(localId) => {
              setDraftModels((previous) => previous.filter((model) => model.localId !== localId));
            }}
          />
        ) : null}

        {!loading && step === 'optionalServices' ? (
          <OptionalServicesStep
            value={optionalForm}
            derivedCoreEndpoint={derivedCoreEndpoint}
            errors={optionalErrors}
            onChange={(patch) => setOptionalForm((previous) => ({ ...previous, ...patch }))}
          />
        ) : null}

        {!loading && step === 'finish' ? (
          <FinishStep
            readyForBasicChat={readyForBasicChat}
            totalModelCount={savedFoundryModelCount}
            warningItems={finishWarnings}
          />
        ) : null}
      </div>
    </SettingsModal>
  );
}
