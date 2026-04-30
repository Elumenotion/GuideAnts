import { useCallback, useEffect, useMemo, useState } from 'react';
import { api } from '../../services/api';
import type { ServiceEditorStateDto, SettingsSchemaDto, SettingsSectionDto } from '../../types/settings';
import { SettingsModal } from '../../pages/settings/components/shared/SettingsModal';
import {
  FOUNDRY_CORE_SECTION,
  FOUNDRY_DOCUMENT_INTELLIGENCE_SECTION,
  FOUNDRY_EMBEDDINGS_SECTION,
  FOUNDRY_IMAGES_SECTION,
  FOUNDRY_SERVICE_PROVIDER_IDS,
  FOUNDRY_SPEECH_SECTION,
  GEMINI_CORE_SECTION,
  GEMINI_DEFAULT_CHAT_MODEL_ID,
  GEMINI_OPTIONAL_SERVICE_DEFAULTS,
  GEMINI_SERVICE_PROVIDER_IDS,
  SECRET_MASK,
  WIZARD_STEPS,
} from './addAiServicesWizard/constants';
import { CoreConnectionStep } from './addAiServicesWizard/steps/CoreConnectionStep';
import { FinishStep } from './addAiServicesWizard/steps/FinishStep';
import { GeminiConnectionStep } from './addAiServicesWizard/steps/GeminiConnectionStep';
import { GeminiModelsStep } from './addAiServicesWizard/steps/GeminiModelsStep';
import { GeminiOptionalServicesStep } from './addAiServicesWizard/steps/GeminiOptionalServicesStep';
import { ModelsStep } from './addAiServicesWizard/steps/ModelsStep';
import { OptionalServicesStep } from './addAiServicesWizard/steps/OptionalServicesStep';
import { ProviderStep } from './addAiServicesWizard/steps/ProviderStep';
import type {
  AddAiServicesWizardProvider,
  AddAiServicesWizardStep,
  FoundryCoreConnectionFormState,
  FoundryModelDraft,
  FoundryModelProviderLabel,
  FoundryOptionalServicesFormState,
  GeminiCoreConnectionFormState,
  GeminiModelDraft,
  GeminiOptionalServicesFormState,
  OptionalServiceKey,
  WizardLoadSnapshot,
} from './addAiServicesWizard/types';
import {
  buildAddGeminiModelRequest,
  buildAddModelRequest,
  deriveEndpointFromResource,
  hasModelId,
  hasModelTuple,
  makeDraftModel,
  makeGeminiDraftModel,
  summarizeFoundryOptionalServiceWarnings,
  summarizeGeminiOptionalServiceWarnings,
  toExistingFoundryModels,
  toExistingGeminiModels,
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

function buildFoundryCoreForm(snapshot: WizardLoadSnapshot): FoundryCoreConnectionFormState {
  const section = snapshot.sectionsByName[FOUNDRY_CORE_SECTION];
  return {
    resource: String(section?.payload.Resource ?? ''),
    apiKey: String(section?.payload.ApiKey ?? ''),
    apiVersion: String(section?.payload.ApiVersion ?? snapshot.defaults.azureOpenAiApiVersion),
    apiKeyHasStoredValue: Boolean(section?.secretHasValue?.ApiKey),
  };
}

function buildGeminiCoreForm(snapshot: WizardLoadSnapshot): GeminiCoreConnectionFormState {
  const section = snapshot.sectionsByName[GEMINI_CORE_SECTION];
  return {
    apiKey: String(section?.payload.ApiKey ?? ''),
    apiKeyHasStoredValue: Boolean(section?.secretHasValue?.ApiKey),
  };
}

function getServiceProviderFieldValue(
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

function buildFoundryOptionalServicesForm(snapshot: WizardLoadSnapshot): FoundryOptionalServicesFormState {
  const coreResource = getSectionStringValue(snapshot.sectionsByName[FOUNDRY_CORE_SECTION], 'Resource');
  const derivedEndpoint = deriveEndpointFromResource(coreResource);

  const embeddingsSection = snapshot.sectionsByName[FOUNDRY_EMBEDDINGS_SECTION];
  const imagesSection = snapshot.sectionsByName[FOUNDRY_IMAGES_SECTION];
  const speechSection = snapshot.sectionsByName[FOUNDRY_SPEECH_SECTION];
  const documentSection = snapshot.sectionsByName[FOUNDRY_DOCUMENT_INTELLIGENCE_SECTION];

  const embeddingsEndpoint = getSectionStringValue(embeddingsSection, 'Endpoint');
  const imagesEndpoint = getSectionStringValue(imagesSection, 'Endpoint');

  const embeddingsLink = Boolean(derivedEndpoint) && (embeddingsEndpoint.length === 0 || embeddingsEndpoint === derivedEndpoint);
  const imagesLink = Boolean(derivedEndpoint) && (imagesEndpoint.length === 0 || imagesEndpoint === derivedEndpoint);

  return {
    enableEmbeddings: snapshot.sectionSummaries.some(
      (section) => section.sectionName === FOUNDRY_EMBEDDINGS_SECTION && section.readinessStatus === 'configured'
    ),
    embeddingsEndpoint: embeddingsLink ? derivedEndpoint : embeddingsEndpoint,
    embeddingsApiKey: getSectionStringValue(embeddingsSection, 'ApiKey'),
    embeddingsApiKeyHasStoredValue: hasStoredSecret(embeddingsSection, 'ApiKey'),
    embeddingsDeployment: getServiceProviderFieldValue(
      snapshot.serviceStates.Embeddings,
      FOUNDRY_SERVICE_PROVIDER_IDS.Embeddings,
      'Deployment'
    ),
    linkEmbeddingsEndpointToCore: embeddingsLink,

    enableImages: snapshot.sectionSummaries.some(
      (section) => section.sectionName === FOUNDRY_IMAGES_SECTION && section.readinessStatus === 'configured'
    ),
    imagesEndpoint: imagesLink ? derivedEndpoint : imagesEndpoint,
    imagesApiKey: getSectionStringValue(imagesSection, 'ApiKey'),
    imagesApiKeyHasStoredValue: hasStoredSecret(imagesSection, 'ApiKey'),
    imagesApiVersion: getSectionStringValue(imagesSection, 'ApiVersion') || snapshot.defaults.azureOpenAiImagesApiVersion,
    imagesDeployment: getServiceProviderFieldValue(
      snapshot.serviceStates.ImageGeneration,
      FOUNDRY_SERVICE_PROVIDER_IDS.ImageGeneration,
      'Deployment'
    ),
    imagesEditDeployment: getServiceProviderFieldValue(
      snapshot.serviceStates.ImageGeneration,
      FOUNDRY_SERVICE_PROVIDER_IDS.ImageGeneration,
      'EditModelDeployment'
    ),
    linkImagesEndpointToCore: imagesLink,

    enableSpeech: snapshot.sectionSummaries.some(
      (section) => section.sectionName === FOUNDRY_SPEECH_SECTION && section.readinessStatus === 'configured'
    ),
    speechEndpoint: getSectionStringValue(speechSection, 'Endpoint'),
    speechApiKey: getSectionStringValue(speechSection, 'ApiKey'),
    speechApiKeyHasStoredValue: hasStoredSecret(speechSection, 'ApiKey'),
    speechRegion: getSectionStringValue(speechSection, 'Region') || 'eastus',

    enableDocumentIntelligence: snapshot.sectionSummaries.some(
      (section) => section.sectionName === FOUNDRY_DOCUMENT_INTELLIGENCE_SECTION && section.readinessStatus === 'configured'
    ),
    documentIntelligenceEndpoint: getSectionStringValue(documentSection, 'Endpoint'),
    documentIntelligenceApiKey: getSectionStringValue(documentSection, 'ApiKey'),
    documentIntelligenceApiKeyHasStoredValue: hasStoredSecret(documentSection, 'ApiKey'),
  };
}

function buildGeminiOptionalServicesForm(snapshot: WizardLoadSnapshot): GeminiOptionalServicesFormState {
  return {
    enableEmbeddings: true,
    embeddingsModelId:
      getServiceProviderFieldValue(snapshot.serviceStates.Embeddings, GEMINI_SERVICE_PROVIDER_IDS.Embeddings, 'ModelId') ||
      GEMINI_OPTIONAL_SERVICE_DEFAULTS.embeddingsModelId,
    embeddingsTimeoutSeconds:
      getServiceProviderFieldValue(snapshot.serviceStates.Embeddings, GEMINI_SERVICE_PROVIDER_IDS.Embeddings, 'TimeoutSeconds') ||
      GEMINI_OPTIONAL_SERVICE_DEFAULTS.embeddingsTimeoutSeconds,

    enableImages: true,
    imagesModelId:
      getServiceProviderFieldValue(snapshot.serviceStates.ImageGeneration, GEMINI_SERVICE_PROVIDER_IDS.ImageGeneration, 'ModelId') ||
      GEMINI_OPTIONAL_SERVICE_DEFAULTS.imagesModelId,
    imagesTimeoutSeconds:
      getServiceProviderFieldValue(snapshot.serviceStates.ImageGeneration, GEMINI_SERVICE_PROVIDER_IDS.ImageGeneration, 'TimeoutSeconds') ||
      GEMINI_OPTIONAL_SERVICE_DEFAULTS.imagesTimeoutSeconds,

    enableSpeechTranscription: true,
    speechTranscriptionModelId:
      getServiceProviderFieldValue(
        snapshot.serviceStates.SpeechTranscription,
        GEMINI_SERVICE_PROVIDER_IDS.SpeechTranscription,
        'ModelId'
      ) || GEMINI_OPTIONAL_SERVICE_DEFAULTS.speechTranscriptionModelId,
    speechTranscriptionTimeoutSeconds:
      getServiceProviderFieldValue(snapshot.serviceStates.SpeechTranscription, GEMINI_SERVICE_PROVIDER_IDS.SpeechTranscription, 'TimeoutSeconds') ||
      GEMINI_OPTIONAL_SERVICE_DEFAULTS.speechTranscriptionTimeoutSeconds,

    enableSpeechSynthesis: true,
    speechSynthesisModelId:
      getServiceProviderFieldValue(snapshot.serviceStates.SpeechSynthesis, GEMINI_SERVICE_PROVIDER_IDS.SpeechSynthesis, 'ModelId') ||
      GEMINI_OPTIONAL_SERVICE_DEFAULTS.speechSynthesisModelId,
    speechSynthesisVoiceName:
      getServiceProviderFieldValue(snapshot.serviceStates.SpeechSynthesis, GEMINI_SERVICE_PROVIDER_IDS.SpeechSynthesis, 'VoiceName') ||
      GEMINI_OPTIONAL_SERVICE_DEFAULTS.speechSynthesisVoiceName,
    speechSynthesisTimeoutSeconds:
      getServiceProviderFieldValue(snapshot.serviceStates.SpeechSynthesis, GEMINI_SERVICE_PROVIDER_IDS.SpeechSynthesis, 'TimeoutSeconds') ||
      GEMINI_OPTIONAL_SERVICE_DEFAULTS.speechSynthesisTimeoutSeconds,
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
  const [provider, setProvider] = useState<AddAiServicesWizardProvider>('foundry');
  const [dontAutoOpenAgain, setDontAutoOpenAgain] = useState(false);

  const [snapshot, setSnapshot] = useState<WizardLoadSnapshot | null>(null);
  const [foundryCoreForm, setFoundryCoreForm] = useState<FoundryCoreConnectionFormState>({
    resource: '',
    apiKey: '',
    apiVersion: '',
    apiKeyHasStoredValue: false,
  });
  const [geminiCoreForm, setGeminiCoreForm] = useState<GeminiCoreConnectionFormState>({
    apiKey: '',
    apiKeyHasStoredValue: false,
  });

  const [foundryOptionalForm, setFoundryOptionalForm] = useState<FoundryOptionalServicesFormState>({
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

  const [geminiOptionalForm, setGeminiOptionalForm] = useState<GeminiOptionalServicesFormState>({
    enableEmbeddings: true,
    embeddingsModelId: GEMINI_OPTIONAL_SERVICE_DEFAULTS.embeddingsModelId,
    embeddingsTimeoutSeconds: GEMINI_OPTIONAL_SERVICE_DEFAULTS.embeddingsTimeoutSeconds,

    enableImages: true,
    imagesModelId: GEMINI_OPTIONAL_SERVICE_DEFAULTS.imagesModelId,
    imagesTimeoutSeconds: GEMINI_OPTIONAL_SERVICE_DEFAULTS.imagesTimeoutSeconds,

    enableSpeechTranscription: true,
    speechTranscriptionModelId: GEMINI_OPTIONAL_SERVICE_DEFAULTS.speechTranscriptionModelId,
    speechTranscriptionTimeoutSeconds: GEMINI_OPTIONAL_SERVICE_DEFAULTS.speechTranscriptionTimeoutSeconds,

    enableSpeechSynthesis: true,
    speechSynthesisModelId: GEMINI_OPTIONAL_SERVICE_DEFAULTS.speechSynthesisModelId,
    speechSynthesisVoiceName: GEMINI_OPTIONAL_SERVICE_DEFAULTS.speechSynthesisVoiceName,
    speechSynthesisTimeoutSeconds: GEMINI_OPTIONAL_SERVICE_DEFAULTS.speechSynthesisTimeoutSeconds,
  });

  const [foundryDraftModelId, setFoundryDraftModelId] = useState('');
  const [foundryDraftModelProvider, setFoundryDraftModelProvider] = useState<FoundryModelProviderLabel>('Completions');
  const [setFoundryDraftAsGlobalDefault, setSetFoundryDraftAsGlobalDefault] = useState(true);
  const [foundryDraftModels, setFoundryDraftModels] = useState<FoundryModelDraft[]>([]);

  const [geminiDraftModelId, setGeminiDraftModelId] = useState(GEMINI_DEFAULT_CHAT_MODEL_ID);
  const [setGeminiDraftAsGlobalDefault, setSetGeminiDraftAsGlobalDefault] = useState(true);
  const [geminiDraftModels, setGeminiDraftModels] = useState<GeminiModelDraft[]>([]);

  const [foundryCoreErrors, setFoundryCoreErrors] = useState<Partial<Record<'resource' | 'apiKey' | 'apiVersion', string>>>({});
  const [geminiCoreErrors, setGeminiCoreErrors] = useState<Partial<Record<'apiKey', string>>>({});
  const [foundryOptionalErrors, setFoundryOptionalErrors] = useState<Record<string, string>>({});
  const [geminiOptionalErrors, setGeminiOptionalErrors] = useState<Record<string, string>>({});
  const [modelAddError, setModelAddError] = useState<string | null>(null);
  const [modelStepError, setModelStepError] = useState<string | null>(null);

  const [loading, setLoading] = useState(false);
  const [saving, setSaving] = useState(false);
  const [globalError, setGlobalError] = useState<string | null>(null);

  const existingFoundryModels = useMemo(
    () => (snapshot ? toExistingFoundryModels(snapshot.models) : []),
    [snapshot]
  );
  const existingGeminiModels = useMemo(
    () => (snapshot ? toExistingGeminiModels(snapshot.models) : []),
    [snapshot]
  );

  const existingCatalogModelCount = snapshot?.models.length ?? 0;

  const lockFoundryDraftAsGlobalDefault =
    existingCatalogModelCount === 0 && foundryDraftModels.length === 0 && geminiDraftModels.length === 0;
  const effectiveSetFoundryDraftAsGlobalDefault = lockFoundryDraftAsGlobalDefault ? true : setFoundryDraftAsGlobalDefault;

  const lockGeminiDraftAsGlobalDefault =
    existingCatalogModelCount === 0 && geminiDraftModels.length === 0 && foundryDraftModels.length === 0;
  const effectiveSetGeminiDraftAsGlobalDefault = lockGeminiDraftAsGlobalDefault ? true : setGeminiDraftAsGlobalDefault;

  const foundryTotalModelCount = existingFoundryModels.length + foundryDraftModels.length;
  const geminiTotalModelCount = existingGeminiModels.length + geminiDraftModels.length;

  const savedFoundryModelCount = useMemo(
    () => (snapshot ? toExistingFoundryModels(snapshot.models).length : 0),
    [snapshot]
  );
  const savedGeminiModelCount = useMemo(
    () => (snapshot ? toExistingGeminiModels(snapshot.models).length : 0),
    [snapshot]
  );

  const foundryConnectionConfigured = useMemo(
    () =>
      Boolean(
        snapshot?.sectionSummaries.some(
          (section) => section.sectionName === FOUNDRY_CORE_SECTION && section.readinessStatus === 'configured'
        )
      ),
    [snapshot]
  );
  const geminiConnectionConfigured = useMemo(
    () =>
      Boolean(
        snapshot?.sectionSummaries.some(
          (section) => section.sectionName === GEMINI_CORE_SECTION && section.readinessStatus === 'configured'
        )
      ),
    [snapshot]
  );

  const derivedFoundryCoreEndpoint = useMemo(() => deriveEndpointFromResource(foundryCoreForm.resource), [foundryCoreForm.resource]);

  const readyForBasicChat = useMemo(() => {
    if (provider === 'foundry') {
      return foundryConnectionConfigured && savedFoundryModelCount > 0;
    }
    return geminiConnectionConfigured && savedGeminiModelCount > 0;
  }, [provider, foundryConnectionConfigured, geminiConnectionConfigured, savedFoundryModelCount, savedGeminiModelCount]);

  const finishWarnings = useMemo(() => {
    if (!snapshot) {
      return [];
    }
    return provider === 'foundry'
      ? summarizeFoundryOptionalServiceWarnings(snapshot)
      : summarizeGeminiOptionalServiceWarnings(snapshot);
  }, [provider, snapshot]);

  const providerLabel = provider === 'foundry' ? 'Microsoft Foundry' : 'Google Gemini';

  const loadServiceState = useCallback(async (serviceId: OptionalServiceKey): Promise<ServiceEditorStateDto | undefined> => {
    try {
      return await api.settings.services.get(serviceId);
    } catch {
      return undefined;
    }
  }, []);

  const loadSnapshot = useCallback(async (): Promise<WizardLoadSnapshot> => {
    const sectionNames = [
      FOUNDRY_CORE_SECTION,
      FOUNDRY_EMBEDDINGS_SECTION,
      FOUNDRY_IMAGES_SECTION,
      FOUNDRY_SPEECH_SECTION,
      FOUNDRY_DOCUMENT_INTELLIGENCE_SECTION,
      GEMINI_CORE_SECTION,
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
        azureOpenAiApiVersion: getSchemaDefault(schema, FOUNDRY_CORE_SECTION, 'ApiVersion', '2025-04-01-preview'),
        azureOpenAiImagesApiVersion: getSchemaDefault(schema, FOUNDRY_IMAGES_SECTION, 'ApiVersion', '2025-04-01-preview'),
      },
    };
  }, [loadServiceState]);

  const resetWithSnapshot = useCallback((nextSnapshot: WizardLoadSnapshot) => {
    const existingCatalogCount = nextSnapshot.models.length;

    setSnapshot(nextSnapshot);

    setFoundryCoreForm(buildFoundryCoreForm(nextSnapshot));
    setGeminiCoreForm(buildGeminiCoreForm(nextSnapshot));
    setFoundryOptionalForm(buildFoundryOptionalServicesForm(nextSnapshot));
    setGeminiOptionalForm(buildGeminiOptionalServicesForm(nextSnapshot));

    setFoundryDraftModels([]);
    setFoundryDraftModelId('');
    setFoundryDraftModelProvider('Completions');
    setSetFoundryDraftAsGlobalDefault(existingCatalogCount === 0);

    setGeminiDraftModels([]);
    setGeminiDraftModelId(GEMINI_DEFAULT_CHAT_MODEL_ID);
    setSetGeminiDraftAsGlobalDefault(existingCatalogCount === 0);

    setFoundryCoreErrors({});
    setGeminiCoreErrors({});
    setFoundryOptionalErrors({});
    setGeminiOptionalErrors({});
    setModelAddError(null);
    setModelStepError(null);
  }, []);

  useEffect(() => {
    if (!isOpen) {
      return;
    }
    let cancelled = false;
    setLoading(true);
    setGlobalError(null);
    setStep('provider');
    setProvider('foundry');
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
    if (!isOpen || !derivedFoundryCoreEndpoint) {
      return;
    }
    setFoundryOptionalForm((previous) => ({
      ...previous,
      embeddingsEndpoint: previous.linkEmbeddingsEndpointToCore ? derivedFoundryCoreEndpoint : previous.embeddingsEndpoint,
      imagesEndpoint: previous.linkImagesEndpointToCore ? derivedFoundryCoreEndpoint : previous.imagesEndpoint,
    }));
  }, [derivedFoundryCoreEndpoint, isOpen]);

  useEffect(() => {
    setModelAddError(null);
    setModelStepError(null);
  }, [provider]);

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

  const validateFoundryCoreConnection = (): boolean => {
    const errors: Partial<Record<'resource' | 'apiKey' | 'apiVersion', string>> = {};

    if (!foundryCoreForm.resource.trim()) {
      errors.resource = 'Resource is required.';
    }

    const keyValue = foundryCoreForm.apiKey.trim();
    if (!keyValue && !foundryCoreForm.apiKeyHasStoredValue) {
      errors.apiKey = 'API key is required.';
    }
    if (keyValue && keyValue !== SECRET_MASK && keyValue.length < 8) {
      errors.apiKey = 'API key looks too short.';
    }

    if (!foundryCoreForm.apiVersion.trim()) {
      errors.apiVersion = 'API version is required.';
    }

    setFoundryCoreErrors(errors);
    return Object.keys(errors).length === 0;
  };

  const validateGeminiCoreConnection = (): boolean => {
    const errors: Partial<Record<'apiKey', string>> = {};

    const keyValue = geminiCoreForm.apiKey.trim();
    if (!keyValue && !geminiCoreForm.apiKeyHasStoredValue) {
      errors.apiKey = 'API key is required.';
    }
    if (keyValue && keyValue !== SECRET_MASK && keyValue.length < 8) {
      errors.apiKey = 'API key looks too short.';
    }

    setGeminiCoreErrors(errors);
    return Object.keys(errors).length === 0;
  };

  const persistFoundryCoreConnection = useCallback(async () => {
    if (!snapshot) {
      throw new Error('Wizard state is not loaded.');
    }
    if (!validateFoundryCoreConnection()) {
      throw new Error('Connection details are incomplete.');
    }

    const patchedApiVersion = foundryCoreForm.apiVersion.trim() || snapshot.defaults.azureOpenAiApiVersion;
    const payload = {
      Resource: foundryCoreForm.resource.trim(),
      ApiKey: withSecretPreserved(foundryCoreForm.apiKey, foundryCoreForm.apiKeyHasStoredValue),
      ApiVersion: patchedApiVersion,
    };

    let nextSections = snapshot.sectionsByName;
    nextSections = await updateSection(FOUNDRY_CORE_SECTION, payload, nextSections);

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
    setFoundryCoreForm((previous) => ({
      ...previous,
      apiVersion: patchedApiVersion,
      apiKey: payload.ApiKey,
      apiKeyHasStoredValue: true,
    }));
  }, [foundryCoreForm.apiKey, foundryCoreForm.apiKeyHasStoredValue, foundryCoreForm.apiVersion, foundryCoreForm.resource, snapshot]);

  const persistGeminiCoreConnection = useCallback(async () => {
    if (!snapshot) {
      throw new Error('Wizard state is not loaded.');
    }
    if (!validateGeminiCoreConnection()) {
      throw new Error('Connection details are incomplete.');
    }

    const payload = {
      ApiKey: withSecretPreserved(geminiCoreForm.apiKey, geminiCoreForm.apiKeyHasStoredValue),
    };

    let nextSections = snapshot.sectionsByName;
    nextSections = await updateSection(GEMINI_CORE_SECTION, payload, nextSections);

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
    setGeminiCoreForm((previous) => ({
      ...previous,
      apiKey: payload.ApiKey,
      apiKeyHasStoredValue: true,
    }));
  }, [geminiCoreForm.apiKey, geminiCoreForm.apiKeyHasStoredValue, snapshot]);

  const addFoundryDraftModel = useCallback(() => {
    const normalizedId = foundryDraftModelId.trim();
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

    if (hasModelId(foundryDraftModels, normalizedId)) {
      setModelAddError(`Model '${normalizedId}' is already queued.`);
      return;
    }

    if (hasModelTuple(foundryDraftModels, { modelId: normalizedId, provider: foundryDraftModelProvider })) {
      setModelAddError('This model/provider combination is already queued.');
      return;
    }

    const isFirstModelOverall =
      existingCatalogModelCount === 0
      && foundryDraftModels.length === 0
      && geminiDraftModels.length === 0;
    const shouldSetAsGlobalDefault = isFirstModelOverall || setFoundryDraftAsGlobalDefault;

    const localId = `draft-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`;
    setFoundryDraftModels((previous) => {
      const next = shouldSetAsGlobalDefault
        ? previous.map((model) => ({ ...model, setAsGlobalDefault: false }))
        : previous;
      return [...next, makeDraftModel(localId, normalizedId, foundryDraftModelProvider, shouldSetAsGlobalDefault)];
    });

    setFoundryDraftModelId('');
    setSetFoundryDraftAsGlobalDefault(false);
    setModelAddError(null);
    setModelStepError(null);
  }, [
    existingCatalogModelCount,
    foundryDraftModelId,
    foundryDraftModelProvider,
    foundryDraftModels,
    geminiDraftModels.length,
    setFoundryDraftAsGlobalDefault,
    snapshot,
  ]);

  const addGeminiDraftModel = useCallback(() => {
    const normalizedId = geminiDraftModelId.trim();
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

    if (hasModelId(geminiDraftModels, normalizedId)) {
      setModelAddError(`Model '${normalizedId}' is already queued.`);
      return;
    }

    const isFirstModelOverall =
      existingCatalogModelCount === 0
      && geminiDraftModels.length === 0
      && foundryDraftModels.length === 0;
    const shouldSetAsGlobalDefault = isFirstModelOverall || setGeminiDraftAsGlobalDefault;

    const localId = `draft-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`;
    setGeminiDraftModels((previous) => {
      const next = shouldSetAsGlobalDefault
        ? previous.map((model) => ({ ...model, setAsGlobalDefault: false }))
        : previous;
      return [...next, makeGeminiDraftModel(localId, normalizedId, shouldSetAsGlobalDefault)];
    });

    setGeminiDraftModelId(GEMINI_DEFAULT_CHAT_MODEL_ID);
    setSetGeminiDraftAsGlobalDefault(false);
    setModelAddError(null);
    setModelStepError(null);
  }, [
    existingCatalogModelCount,
    foundryDraftModels.length,
    geminiDraftModelId,
    geminiDraftModels,
    setGeminiDraftAsGlobalDefault,
    snapshot,
  ]);

  const persistGlobalDefaultModel = useCallback(async (modelId: string) => {
    const chatDefaults = await api.settings.chatDefaults.get();
    await api.settings.chatDefaults.update({
      rowVersion: chatDefaults.rowVersion,
      defaultModelId: modelId,
      overrideAllChatModels: chatDefaults.overrideAllChatModels,
      temperature: chatDefaults.temperature ?? null,
      topP: chatDefaults.topP ?? null,
      reasoningEffort: chatDefaults.reasoningEffort ?? null,
      samplingParametersJson: chatDefaults.samplingParametersJson ?? null,
    });
  }, []);

  const persistFoundryModels = useCallback(async () => {
    if (!snapshot) {
      throw new Error('Wizard state is not loaded.');
    }

    const existingCount = snapshot.models.length;
    const pendingDrafts = foundryDraftModels.filter((model) => !model.persisted);

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
      const providerName = conflict?.provider ?? 'unknown';
      setModelStepError(
        `Model '${existingConflict.modelId}' already exists with provider '${providerName}'. Choose a different model id.`
      );
      throw new Error('Model id conflict detected.');
    }

    for (const model of pendingDrafts) {
      const request = buildAddModelRequest(model.modelId, model.provider);
      await api.settings.addModel(request);
    }

    const forcedDefaultModelId = existingCount === 0 && pendingDrafts.length > 0
      ? pendingDrafts[0].modelId
      : null;
    const selectedDefaultModelId = pendingDrafts.find((model) => model.setAsGlobalDefault)?.modelId ?? null;
    const targetDefaultModelId = forcedDefaultModelId ?? selectedDefaultModelId;

    let defaultSaveWarning: string | null = null;
    if (targetDefaultModelId) {
      try {
        await persistGlobalDefaultModel(targetDefaultModelId);
      } catch (error) {
        const detail = error instanceof Error ? error.message : 'Unknown error.';
        defaultSaveWarning = `Model was added, but setting '${targetDefaultModelId}' as global default failed: ${detail}`;
      }
    }

    const refreshed = await loadSnapshot();
    setSnapshot(refreshed);
    setFoundryDraftModels([]);
    setSetFoundryDraftAsGlobalDefault(refreshed.models.length === 0);
    setModelStepError(null);
    if (defaultSaveWarning) {
      setGlobalError(defaultSaveWarning);
    }
  }, [foundryDraftModels, loadSnapshot, persistGlobalDefaultModel, snapshot]);

  const persistGeminiModels = useCallback(async () => {
    if (!snapshot) {
      throw new Error('Wizard state is not loaded.');
    }

    const existingCount = snapshot.models.length;
    const pendingDrafts = geminiDraftModels.filter((model) => !model.persisted);

    if (existingCount + pendingDrafts.length === 0) {
      setModelStepError('At least one Gemini model is required.');
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
      const providerName = conflict?.provider ?? 'unknown';
      setModelStepError(
        `Model '${existingConflict.modelId}' already exists with provider '${providerName}'. Choose a different model id.`
      );
      throw new Error('Model id conflict detected.');
    }

    for (const model of pendingDrafts) {
      const request = buildAddGeminiModelRequest(model.modelId);
      await api.settings.addModel(request);
    }

    const forcedDefaultModelId = existingCount === 0 && pendingDrafts.length > 0
      ? pendingDrafts[0].modelId
      : null;
    const selectedDefaultModelId = pendingDrafts.find((model) => model.setAsGlobalDefault)?.modelId ?? null;
    const targetDefaultModelId = forcedDefaultModelId ?? selectedDefaultModelId;

    let defaultSaveWarning: string | null = null;
    if (targetDefaultModelId) {
      try {
        await persistGlobalDefaultModel(targetDefaultModelId);
      } catch (error) {
        const detail = error instanceof Error ? error.message : 'Unknown error.';
        defaultSaveWarning = `Model was added, but setting '${targetDefaultModelId}' as global default failed: ${detail}`;
      }
    }

    const refreshed = await loadSnapshot();
    setSnapshot(refreshed);
    setGeminiDraftModels([]);
    setSetGeminiDraftAsGlobalDefault(refreshed.models.length === 0);
    setModelStepError(null);
    if (defaultSaveWarning) {
      setGlobalError(defaultSaveWarning);
    }
  }, [geminiDraftModels, loadSnapshot, persistGlobalDefaultModel, snapshot]);

  const validateFoundryOptionalServices = useCallback((): boolean => {
    const errors: Record<string, string> = {};

    const getEndpoint = (value: string, linked: boolean) => (linked ? derivedFoundryCoreEndpoint : value).trim();
    const requireSecret = (value: string, hasStoredValue: boolean) => value.trim().length > 0 || hasStoredValue;

    if (foundryOptionalForm.enableEmbeddings) {
      if (!getEndpoint(foundryOptionalForm.embeddingsEndpoint, foundryOptionalForm.linkEmbeddingsEndpointToCore)) {
        errors.embeddingsEndpoint = 'Endpoint is required.';
      }
      if (!requireSecret(foundryOptionalForm.embeddingsApiKey, foundryOptionalForm.embeddingsApiKeyHasStoredValue)) {
        errors.embeddingsApiKey = 'API key is required.';
      }
      if (!foundryOptionalForm.embeddingsDeployment.trim()) {
        errors.embeddingsDeployment = 'Deployment is required.';
      }
    }

    if (foundryOptionalForm.enableImages) {
      if (!getEndpoint(foundryOptionalForm.imagesEndpoint, foundryOptionalForm.linkImagesEndpointToCore)) {
        errors.imagesEndpoint = 'Endpoint is required.';
      }
      if (!requireSecret(foundryOptionalForm.imagesApiKey, foundryOptionalForm.imagesApiKeyHasStoredValue)) {
        errors.imagesApiKey = 'API key is required.';
      }
      if (!foundryOptionalForm.imagesApiVersion.trim()) {
        errors.imagesApiVersion = 'API version is required.';
      }
      if (!foundryOptionalForm.imagesDeployment.trim()) {
        errors.imagesDeployment = 'Generation deployment is required.';
      }
      if (!foundryOptionalForm.imagesEditDeployment.trim()) {
        errors.imagesEditDeployment = 'Edit deployment is required.';
      }
    }

    if (foundryOptionalForm.enableSpeech) {
      if (!foundryOptionalForm.speechEndpoint.trim()) {
        errors.speechEndpoint = 'Endpoint is required.';
      }
      if (!requireSecret(foundryOptionalForm.speechApiKey, foundryOptionalForm.speechApiKeyHasStoredValue)) {
        errors.speechApiKey = 'API key is required.';
      }
      if (!foundryOptionalForm.speechRegion.trim()) {
        errors.speechRegion = 'Region is required.';
      }
    }

    if (foundryOptionalForm.enableDocumentIntelligence) {
      if (!foundryOptionalForm.documentIntelligenceEndpoint.trim()) {
        errors.documentIntelligenceEndpoint = 'Endpoint is required.';
      }
      if (
        !requireSecret(
          foundryOptionalForm.documentIntelligenceApiKey,
          foundryOptionalForm.documentIntelligenceApiKeyHasStoredValue
        )
      ) {
        errors.documentIntelligenceApiKey = 'API key is required.';
      }
    }

    setFoundryOptionalErrors(errors);
    return Object.keys(errors).length === 0;
  }, [derivedFoundryCoreEndpoint, foundryOptionalForm]);

  const isPositiveIntegerValue = (value: string): boolean => {
    const parsed = Number(value);
    return Number.isInteger(parsed) && parsed > 0;
  };

  const validateGeminiOptionalServices = useCallback((): boolean => {
    const errors: Record<string, string> = {};

    if (geminiOptionalForm.enableEmbeddings) {
      if (!geminiOptionalForm.embeddingsModelId.trim()) {
        errors.embeddingsModelId = 'Embedding model id is required.';
      }
      if (!isPositiveIntegerValue(geminiOptionalForm.embeddingsTimeoutSeconds)) {
        errors.embeddingsTimeoutSeconds = 'Timeout must be a positive integer.';
      }
    }

    if (geminiOptionalForm.enableImages) {
      if (!geminiOptionalForm.imagesModelId.trim()) {
        errors.imagesModelId = 'Image model id is required.';
      }
      if (!isPositiveIntegerValue(geminiOptionalForm.imagesTimeoutSeconds)) {
        errors.imagesTimeoutSeconds = 'Timeout must be a positive integer.';
      }
    }

    if (geminiOptionalForm.enableSpeechTranscription) {
      if (!geminiOptionalForm.speechTranscriptionModelId.trim()) {
        errors.speechTranscriptionModelId = 'Transcription model id is required.';
      }
      if (!isPositiveIntegerValue(geminiOptionalForm.speechTranscriptionTimeoutSeconds)) {
        errors.speechTranscriptionTimeoutSeconds = 'Timeout must be a positive integer.';
      }
    }

    if (geminiOptionalForm.enableSpeechSynthesis) {
      if (!geminiOptionalForm.speechSynthesisModelId.trim()) {
        errors.speechSynthesisModelId = 'TTS model id is required.';
      }
      if (!geminiOptionalForm.speechSynthesisVoiceName.trim()) {
        errors.speechSynthesisVoiceName = 'Voice name is required.';
      }
      if (!isPositiveIntegerValue(geminiOptionalForm.speechSynthesisTimeoutSeconds)) {
        errors.speechSynthesisTimeoutSeconds = 'Timeout must be a positive integer.';
      }
    }

    setGeminiOptionalErrors(errors);
    return Object.keys(errors).length === 0;
  }, [geminiOptionalForm]);

  const persistFoundryOptionalServices = useCallback(async () => {
    if (!snapshot) {
      throw new Error('Wizard state is not loaded.');
    }
    if (!validateFoundryOptionalServices()) {
      throw new Error('Optional service inputs are incomplete.');
    }

    let nextSections = snapshot.sectionsByName;
    const ensureEndpoint = (value: string, linked: boolean) => (linked ? derivedFoundryCoreEndpoint : value).trim();

    if (foundryOptionalForm.enableEmbeddings) {
      nextSections = await updateSection(
        FOUNDRY_EMBEDDINGS_SECTION,
        {
          Endpoint: ensureEndpoint(foundryOptionalForm.embeddingsEndpoint, foundryOptionalForm.linkEmbeddingsEndpointToCore),
          ApiKey: withSecretPreserved(foundryOptionalForm.embeddingsApiKey, foundryOptionalForm.embeddingsApiKeyHasStoredValue),
        },
        nextSections
      );
      await api.settings.services.updateProviderFields('Embeddings', FOUNDRY_SERVICE_PROVIDER_IDS.Embeddings, {
        Deployment: foundryOptionalForm.embeddingsDeployment.trim(),
      });
      await api.settings.services.updateActiveProvider('Embeddings', FOUNDRY_SERVICE_PROVIDER_IDS.Embeddings);
    }

    if (foundryOptionalForm.enableImages) {
      nextSections = await updateSection(
        FOUNDRY_IMAGES_SECTION,
        {
          Endpoint: ensureEndpoint(foundryOptionalForm.imagesEndpoint, foundryOptionalForm.linkImagesEndpointToCore),
          ApiKey: withSecretPreserved(foundryOptionalForm.imagesApiKey, foundryOptionalForm.imagesApiKeyHasStoredValue),
          ApiVersion: foundryOptionalForm.imagesApiVersion.trim(),
        },
        nextSections
      );
      await api.settings.services.updateProviderFields('ImageGeneration', FOUNDRY_SERVICE_PROVIDER_IDS.ImageGeneration, {
        Deployment: foundryOptionalForm.imagesDeployment.trim(),
        EditModelDeployment: foundryOptionalForm.imagesEditDeployment.trim(),
      });
      await api.settings.services.updateActiveProvider('ImageGeneration', FOUNDRY_SERVICE_PROVIDER_IDS.ImageGeneration);
    }

    if (foundryOptionalForm.enableSpeech) {
      nextSections = await updateSection(
        FOUNDRY_SPEECH_SECTION,
        {
          Endpoint: foundryOptionalForm.speechEndpoint.trim(),
          ApiKey: withSecretPreserved(foundryOptionalForm.speechApiKey, foundryOptionalForm.speechApiKeyHasStoredValue),
          Region: foundryOptionalForm.speechRegion.trim(),
        },
        nextSections
      );
      await api.settings.services.updateActiveProvider('SpeechTranscription', FOUNDRY_SERVICE_PROVIDER_IDS.SpeechTranscription);
      await api.settings.services.updateActiveProvider('SpeechSynthesis', FOUNDRY_SERVICE_PROVIDER_IDS.SpeechSynthesis);
    }

    if (foundryOptionalForm.enableDocumentIntelligence) {
      nextSections = await updateSection(
        FOUNDRY_DOCUMENT_INTELLIGENCE_SECTION,
        {
          Endpoint: foundryOptionalForm.documentIntelligenceEndpoint.trim(),
          ApiKey: withSecretPreserved(
            foundryOptionalForm.documentIntelligenceApiKey,
            foundryOptionalForm.documentIntelligenceApiKeyHasStoredValue
          ),
        },
        nextSections
      );
      await api.settings.services.updateActiveProvider('DocumentIntelligence', FOUNDRY_SERVICE_PROVIDER_IDS.DocumentIntelligence);
    }

    const refreshed = await loadSnapshot();
    setSnapshot(refreshed);
    setFoundryOptionalForm((previous) => ({
      ...previous,
      embeddingsApiKey: previous.enableEmbeddings ? SECRET_MASK : previous.embeddingsApiKey,
      embeddingsApiKeyHasStoredValue: previous.enableEmbeddings ? true : previous.embeddingsApiKeyHasStoredValue,
      imagesApiKey: previous.enableImages ? SECRET_MASK : previous.imagesApiKey,
      imagesApiKeyHasStoredValue: previous.enableImages ? true : previous.imagesApiKeyHasStoredValue,
      speechApiKey: previous.enableSpeech ? SECRET_MASK : previous.speechApiKey,
      speechApiKeyHasStoredValue: previous.enableSpeech ? true : previous.speechApiKeyHasStoredValue,
      documentIntelligenceApiKey: previous.enableDocumentIntelligence ? SECRET_MASK : previous.documentIntelligenceApiKey,
      documentIntelligenceApiKeyHasStoredValue: previous.enableDocumentIntelligence
        ? true
        : previous.documentIntelligenceApiKeyHasStoredValue,
    }));
    setFoundryOptionalErrors({});
  }, [derivedFoundryCoreEndpoint, foundryOptionalForm, loadSnapshot, snapshot, validateFoundryOptionalServices]);

  const persistGeminiOptionalServices = useCallback(async () => {
    if (!snapshot) {
      throw new Error('Wizard state is not loaded.');
    }
    if (!validateGeminiOptionalServices()) {
      throw new Error('Optional service inputs are incomplete.');
    }

    if (geminiOptionalForm.enableEmbeddings) {
      await api.settings.services.updateProviderFields('Embeddings', GEMINI_SERVICE_PROVIDER_IDS.Embeddings, {
        ModelId: geminiOptionalForm.embeddingsModelId.trim(),
        TimeoutSeconds: geminiOptionalForm.embeddingsTimeoutSeconds.trim(),
      });
      await api.settings.services.updateActiveProvider('Embeddings', GEMINI_SERVICE_PROVIDER_IDS.Embeddings);
    }

    if (geminiOptionalForm.enableImages) {
      await api.settings.services.updateProviderFields('ImageGeneration', GEMINI_SERVICE_PROVIDER_IDS.ImageGeneration, {
        ModelId: geminiOptionalForm.imagesModelId.trim(),
        TimeoutSeconds: geminiOptionalForm.imagesTimeoutSeconds.trim(),
      });
      await api.settings.services.updateActiveProvider('ImageGeneration', GEMINI_SERVICE_PROVIDER_IDS.ImageGeneration);
    }

    if (geminiOptionalForm.enableSpeechTranscription) {
      await api.settings.services.updateProviderFields('SpeechTranscription', GEMINI_SERVICE_PROVIDER_IDS.SpeechTranscription, {
        ModelId: geminiOptionalForm.speechTranscriptionModelId.trim(),
        TimeoutSeconds: geminiOptionalForm.speechTranscriptionTimeoutSeconds.trim(),
      });
      await api.settings.services.updateActiveProvider('SpeechTranscription', GEMINI_SERVICE_PROVIDER_IDS.SpeechTranscription);
    }

    if (geminiOptionalForm.enableSpeechSynthesis) {
      await api.settings.services.updateProviderFields('SpeechSynthesis', GEMINI_SERVICE_PROVIDER_IDS.SpeechSynthesis, {
        ModelId: geminiOptionalForm.speechSynthesisModelId.trim(),
        VoiceName: geminiOptionalForm.speechSynthesisVoiceName.trim(),
        TimeoutSeconds: geminiOptionalForm.speechSynthesisTimeoutSeconds.trim(),
      });
      await api.settings.services.updateActiveProvider('SpeechSynthesis', GEMINI_SERVICE_PROVIDER_IDS.SpeechSynthesis);
    }

    const refreshed = await loadSnapshot();
    setSnapshot(refreshed);
    setGeminiOptionalErrors({});
  }, [geminiOptionalForm, loadSnapshot, snapshot, validateGeminiOptionalServices]);

  const persistCurrentStep = useCallback(async () => {
    if (step === 'connection') {
      if (provider === 'foundry') {
        await persistFoundryCoreConnection();
      } else {
        await persistGeminiCoreConnection();
      }
      return;
    }

    if (step === 'models') {
      if (provider === 'foundry') {
        await persistFoundryModels();
      } else {
        await persistGeminiModels();
      }
      return;
    }

    if (step === 'optionalServices') {
      if (provider === 'foundry') {
        await persistFoundryOptionalServices();
      } else {
        await persistGeminiOptionalServices();
      }
    }
  }, [
    persistFoundryCoreConnection,
    persistGeminiCoreConnection,
    persistFoundryModels,
    persistGeminiModels,
    persistFoundryOptionalServices,
    persistGeminiOptionalServices,
    provider,
    step,
  ]);

  const handleNext = useCallback(async () => {
    if (saving) {
      return;
    }

    setGlobalError(null);
    setSaving(true);
    try {
      await persistCurrentStep();

      setStep((previous) => nextStep(previous));
    } catch (error) {
      const message = error instanceof Error ? error.message : 'Could not continue to the next step.';
      setGlobalError(message);
    } finally {
      setSaving(false);
    }
  }, [
    persistCurrentStep,
    saving,
  ]);

  const handleBack = useCallback(() => {
    if (saving) {
      return;
    }
    setGlobalError(null);
    setStep((previous) => previousStep(previous));
  }, [saving]);

  const handleFinish = useCallback(async () => {
    if (saving) {
      return;
    }

    if (step !== 'finish') {
      setGlobalError(null);
      setSaving(true);
      try {
        await persistCurrentStep();
        setStep('finish');
      } catch (error) {
        const message = error instanceof Error ? error.message : 'Could not continue to the finish step.';
        setGlobalError(message);
      } finally {
        setSaving(false);
      }
      return;
    }

    setGlobalError(null);
    setSaving(true);
    try {
      if (provider === 'foundry') {
        const hasPendingDrafts = foundryDraftModels.some((model) => !model.persisted);
        if (hasPendingDrafts) {
          await persistFoundryModels();
        }
      } else {
        const hasPendingDrafts = geminiDraftModels.some((model) => !model.persisted);
        if (hasPendingDrafts) {
          await persistGeminiModels();
        }
      }

      closeWizard();
    } catch (error) {
      const message = error instanceof Error ? error.message : 'Could not finish setup.';
      setGlobalError(message);
    } finally {
      setSaving(false);
    }
  }, [
    closeWizard,
    foundryDraftModels,
    geminiDraftModels,
    persistCurrentStep,
    persistFoundryModels,
    persistGeminiModels,
    provider,
    saving,
    step,
  ]);

  const isNextDisabled = useMemo(() => {
    if (loading || saving) {
      return true;
    }
    if (step === 'provider') {
      return false;
    }
    if (step === 'models') {
      return provider === 'foundry' ? foundryTotalModelCount === 0 : geminiTotalModelCount === 0;
    }
    if (step === 'finish') {
      return true;
    }
    return false;
  }, [foundryTotalModelCount, geminiTotalModelCount, loading, provider, saving, step]);

  const isFinishDisabled = useMemo(() => {
    if (loading || saving) {
      return true;
    }
    return provider === 'foundry' ? foundryTotalModelCount === 0 : geminiTotalModelCount === 0;
  }, [foundryTotalModelCount, geminiTotalModelCount, loading, provider, saving]);

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
      disableOverlayDismiss
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
            <button
              type="button"
              onClick={handleBack}
              disabled={saving || step === 'provider'}
              className="rounded border border-gray-300 px-3 py-1.5 text-sm text-gray-700 hover:bg-gray-50 disabled:opacity-50"
            >
              Back
            </button>
            <button
              type="button"
              onClick={() => void handleNext()}
              disabled={isNextDisabled}
              className="rounded bg-blue-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-blue-700 disabled:bg-blue-400"
            >
              {saving ? 'Saving...' : 'Next'}
            </button>
            <button
              type="button"
              onClick={() => void handleFinish()}
              disabled={isFinishDisabled}
              className="rounded bg-emerald-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-emerald-700 disabled:bg-emerald-300"
            >
              {saving ? 'Saving...' : 'Finish'}
            </button>
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
          provider === 'foundry' ? (
            <CoreConnectionStep
              resource={foundryCoreForm.resource}
              apiKey={foundryCoreForm.apiKey}
              apiVersion={foundryCoreForm.apiVersion}
              apiKeyHasStoredValue={foundryCoreForm.apiKeyHasStoredValue}
              errors={foundryCoreErrors}
              onChange={(patch) => setFoundryCoreForm((previous) => ({ ...previous, ...patch }))}
            />
          ) : (
            <GeminiConnectionStep
              apiKey={geminiCoreForm.apiKey}
              apiKeyHasStoredValue={geminiCoreForm.apiKeyHasStoredValue}
              errors={geminiCoreErrors}
              onChange={(patch) => setGeminiCoreForm((previous) => ({ ...previous, ...patch }))}
            />
          )
        ) : null}

        {!loading && step === 'models' ? (
          provider === 'foundry' ? (
            <ModelsStep
              existingModels={existingFoundryModels}
              draftModels={foundryDraftModels}
              draftModelId={foundryDraftModelId}
              draftProvider={foundryDraftModelProvider}
              setDraftAsGlobalDefault={effectiveSetFoundryDraftAsGlobalDefault}
              lockDraftAsGlobalDefault={lockFoundryDraftAsGlobalDefault}
              addError={modelAddError}
              validationError={modelStepError}
              onDraftModelIdChange={setFoundryDraftModelId}
              onDraftProviderChange={setFoundryDraftModelProvider}
              onSetDraftAsGlobalDefaultChange={setSetFoundryDraftAsGlobalDefault}
              onAddModel={addFoundryDraftModel}
              onRemoveDraftModel={(localId) => {
                setFoundryDraftModels((previous) => previous.filter((model) => model.localId !== localId));
              }}
            />
          ) : (
            <GeminiModelsStep
              existingModels={existingGeminiModels}
              draftModels={geminiDraftModels}
              draftModelId={geminiDraftModelId}
              setDraftAsGlobalDefault={effectiveSetGeminiDraftAsGlobalDefault}
              lockDraftAsGlobalDefault={lockGeminiDraftAsGlobalDefault}
              addError={modelAddError}
              validationError={modelStepError}
              onDraftModelIdChange={setGeminiDraftModelId}
              onSetDraftAsGlobalDefaultChange={setSetGeminiDraftAsGlobalDefault}
              onAddModel={addGeminiDraftModel}
              onRemoveDraftModel={(localId) => {
                setGeminiDraftModels((previous) => previous.filter((model) => model.localId !== localId));
              }}
            />
          )
        ) : null}

        {!loading && step === 'optionalServices' ? (
          provider === 'foundry' ? (
            <OptionalServicesStep
              value={foundryOptionalForm}
              derivedCoreEndpoint={derivedFoundryCoreEndpoint}
              errors={foundryOptionalErrors}
              onChange={(patch) => setFoundryOptionalForm((previous) => ({ ...previous, ...patch }))}
            />
          ) : (
            <GeminiOptionalServicesStep
              value={geminiOptionalForm}
              errors={geminiOptionalErrors}
              onChange={(patch) => setGeminiOptionalForm((previous) => ({ ...previous, ...patch }))}
            />
          )
        ) : null}

        {!loading && step === 'finish' ? (
          <FinishStep
            providerLabel={providerLabel}
            readyForBasicChat={readyForBasicChat}
            totalModelCount={provider === 'foundry' ? savedFoundryModelCount : savedGeminiModelCount}
            warningItems={finishWarnings}
          />
        ) : null}
      </div>
    </SettingsModal>
  );
}
