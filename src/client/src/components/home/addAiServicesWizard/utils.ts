import type { AddModelRequest, ProviderEditorStateDto, ServiceEditorStateDto, SettingsModelDto, SettingsSectionSummaryDto } from '../../../types/settings';
import {
  FOUNDRY_DOCUMENT_INTELLIGENCE_SECTION,
  FOUNDRY_EMBEDDINGS_SECTION,
  FOUNDRY_IMAGES_SECTION,
  FOUNDRY_SERVICE_PROVIDER_IDS,
  FOUNDRY_SPEECH_SECTION,
  GEMINI_CORE_SECTION,
  GEMINI_MODEL_PROVIDER_ID,
  GEMINI_SERVICE_PROVIDER_IDS,
  LOCAL_AI_SERVICE_PROVIDER_IDS,
  MODEL_PROVIDER_ID_TO_LABEL,
  MODEL_PROVIDER_LABEL_TO_ID,
  OPENAI_CORE_SECTION,
  OPENAI_MODEL_PROVIDER_ID_TO_LABEL,
  OPENAI_MODEL_PROVIDER_LABEL_TO_ID,
  OPENAI_SERVICE_PROVIDER_IDS,
} from './constants';
import type {
  ExistingGeminiModel,
  ExistingFoundryModel,
  ExistingOpenAiModel,
  FoundryModelDraft,
  FoundryModelProviderLabel,
  GeminiModelDraft,
  GeminiOptionalServiceKey,
  LocalAiModelDraft,
  LocalAiOptionalServiceKey,
  OpenAiModelDraft,
  OpenAiModelProviderLabel,
  OpenAiOptionalServiceKey,
  OptionalServiceKey,
  WizardLoadSnapshot,
} from './types';
import { buildLocalModelOnboardingRequest } from '../../../features/localModelOnboarding/buildCommand';
import { validateLocalModelOnboardingDraft } from '../../../features/localModelOnboarding/validateDraft';

export function mapProviderLabelToModelProviderId(value: FoundryModelProviderLabel): string {
  return MODEL_PROVIDER_LABEL_TO_ID[value];
}

export function mapModelProviderIdToLabel(value: string): FoundryModelProviderLabel | null {
  return MODEL_PROVIDER_ID_TO_LABEL[value] ?? null;
}

export function deriveEndpointFromResource(resource: string): string {
  const trimmed = resource.trim();
  if (!trimmed) {
    return '';
  }
  return `https://${trimmed}.openai.azure.com/`;
}

export function toExistingFoundryModels(models: SettingsModelDto[]): ExistingFoundryModel[] {
  return models
    .map((model) => {
      const providerLabel = mapModelProviderIdToLabel(model.provider);
      if (!providerLabel) {
        return null;
      }
      return {
        modelId: model.modelId,
        provider: providerLabel,
        raw: model,
      } satisfies ExistingFoundryModel;
    })
    .filter((item): item is ExistingFoundryModel => item !== null)
    .sort((left, right) => left.modelId.localeCompare(right.modelId));
}

export function toExistingGeminiModels(models: SettingsModelDto[]): ExistingGeminiModel[] {
  return models
    .filter((model) => model.provider === GEMINI_MODEL_PROVIDER_ID)
    .map((model) => ({
      modelId: model.modelId,
      raw: model,
    }))
    .sort((left, right) => left.modelId.localeCompare(right.modelId));
}

export function toExistingOpenAiModels(models: SettingsModelDto[]): ExistingOpenAiModel[] {
  return models
    .map((model) => {
      const providerLabel = OPENAI_MODEL_PROVIDER_ID_TO_LABEL[model.provider];
      if (!providerLabel) {
        return null;
      }
      return {
        modelId: model.modelId,
        provider: providerLabel,
        raw: model,
      } satisfies ExistingOpenAiModel;
    })
    .filter((item): item is ExistingOpenAiModel => item !== null)
    .sort((left, right) => left.modelId.localeCompare(right.modelId));
}

export function makeDraftModel(
  localId: string,
  modelId: string,
  provider: FoundryModelProviderLabel,
  setAsGlobalDefault: boolean
): FoundryModelDraft {
  return {
    localId,
    modelId: modelId.trim(),
    provider,
    setAsGlobalDefault,
    persisted: false,
  };
}

export function makeGeminiDraftModel(
  localId: string,
  modelId: string,
  setAsGlobalDefault: boolean
): GeminiModelDraft {
  return {
    localId,
    modelId: modelId.trim(),
    setAsGlobalDefault,
    persisted: false,
  };
}

export function makeOpenAiDraftModel(
  localId: string,
  modelId: string,
  provider: OpenAiModelProviderLabel,
  setAsGlobalDefault: boolean
): OpenAiModelDraft {
  return {
    localId,
    modelId: modelId.trim(),
    provider,
    setAsGlobalDefault,
    persisted: false,
  };
}

export function hasModelTuple(
  models: readonly Pick<FoundryModelDraft, 'modelId' | 'provider'>[],
  candidate: Pick<FoundryModelDraft, 'modelId' | 'provider'>
): boolean {
  return models.some(
    (model) =>
      model.modelId.trim().toLowerCase() === candidate.modelId.trim().toLowerCase()
      && model.provider === candidate.provider
  );
}

export function hasModelId(models: readonly Pick<FoundryModelDraft, 'modelId'>[], modelId: string): boolean {
  const normalized = modelId.trim().toLowerCase();
  return models.some((model) => model.modelId.trim().toLowerCase() === normalized);
}

export function buildAddModelRequest(modelId: string, provider: FoundryModelProviderLabel): AddModelRequest {
  const trimmed = modelId.trim();
  return {
    provider: mapProviderLabelToModelProviderId(provider),
    catalog: {
      modelId: trimmed,
      displayName: trimmed,
      isActive: true,
    },
  };
}

export function buildAddGeminiModelRequest(modelId: string): AddModelRequest {
  const trimmed = modelId.trim();
  return {
    provider: GEMINI_MODEL_PROVIDER_ID,
    catalog: {
      modelId: trimmed,
      displayName: trimmed,
      isActive: true,
    },
  };
}

export function buildAddOpenAiModelRequest(modelId: string, provider: OpenAiModelProviderLabel): AddModelRequest {
  const trimmed = modelId.trim();
  return {
    provider: OPENAI_MODEL_PROVIDER_LABEL_TO_ID[provider],
    catalog: {
      modelId: trimmed,
      displayName: trimmed,
      isActive: true,
    },
  };
}

function findSectionSummary(
  sectionSummaries: readonly SettingsSectionSummaryDto[],
  sectionName: string
): SettingsSectionSummaryDto | null {
  return sectionSummaries.find((section) => section.sectionName === sectionName) ?? null;
}

function getProviderState(state: ServiceEditorStateDto | undefined, providerId: string): ProviderEditorStateDto | null {
  if (!state) {
    return null;
  }
  return state.providers.find((provider) => provider.providerId === providerId) ?? null;
}

function optionalServiceStatus(
  serviceKey: OptionalServiceKey,
  snapshot: WizardLoadSnapshot
): { complete: boolean; message: string } {
  if (serviceKey === 'Embeddings') {
    const section = findSectionSummary(snapshot.sectionSummaries, FOUNDRY_EMBEDDINGS_SECTION);
    if (!section || section.readinessStatus !== 'configured') {
      return { complete: false, message: 'Embeddings connection is not configured.' };
    }

    const state = snapshot.serviceStates.Embeddings;
    const providerId = FOUNDRY_SERVICE_PROVIDER_IDS.Embeddings;
    const provider = getProviderState(state, providerId);
    if (!state || !provider || state.activeProviderId !== providerId) {
      return { complete: false, message: 'Embeddings service is not set to Microsoft Foundry.' };
    }
    if (!provider.canActivate) {
      return { complete: false, message: 'Embeddings service has unresolved activation blockers.' };
    }
    return { complete: true, message: 'Embeddings is ready.' };
  }

  if (serviceKey === 'ImageGeneration') {
    const section = findSectionSummary(snapshot.sectionSummaries, FOUNDRY_IMAGES_SECTION);
    if (!section || section.readinessStatus !== 'configured') {
      return { complete: false, message: 'Image Generation connection is not configured.' };
    }

    const state = snapshot.serviceStates.ImageGeneration;
    const providerId = FOUNDRY_SERVICE_PROVIDER_IDS.ImageGeneration;
    const provider = getProviderState(state, providerId);
    if (!state || !provider || state.activeProviderId !== providerId) {
      return { complete: false, message: 'Image Generation service is not set to Microsoft Foundry.' };
    }
    if (!provider.canActivate) {
      return { complete: false, message: 'Image Generation service has unresolved activation blockers.' };
    }
    return { complete: true, message: 'Image Generation is ready.' };
  }

  if (serviceKey === 'SpeechTranscription') {
    const section = findSectionSummary(snapshot.sectionSummaries, FOUNDRY_SPEECH_SECTION);
    if (!section || section.readinessStatus !== 'configured') {
      return { complete: false, message: 'Speech connection is not configured for transcription.' };
    }

    const state = snapshot.serviceStates.SpeechTranscription;
    const providerId = FOUNDRY_SERVICE_PROVIDER_IDS.SpeechTranscription;
    const provider = getProviderState(state, providerId);
    if (!state || !provider || state.activeProviderId !== providerId) {
      return { complete: false, message: 'Speech Transcription service is not set to Microsoft Foundry.' };
    }
    if (!provider.canActivate) {
      return { complete: false, message: 'Speech Transcription service has unresolved activation blockers.' };
    }
    return { complete: true, message: 'Speech Transcription is ready.' };
  }

  if (serviceKey === 'SpeechSynthesis') {
    const section = findSectionSummary(snapshot.sectionSummaries, FOUNDRY_SPEECH_SECTION);
    if (!section || section.readinessStatus !== 'configured') {
      return { complete: false, message: 'Speech connection is not configured for synthesis.' };
    }

    const state = snapshot.serviceStates.SpeechSynthesis;
    const providerId = FOUNDRY_SERVICE_PROVIDER_IDS.SpeechSynthesis;
    const provider = getProviderState(state, providerId);
    if (!state || !provider || state.activeProviderId !== providerId) {
      return { complete: false, message: 'Speech Synthesis service is not set to Microsoft Foundry.' };
    }
    if (!provider.canActivate) {
      return { complete: false, message: 'Speech Synthesis service has unresolved activation blockers.' };
    }
    return { complete: true, message: 'Speech Synthesis is ready.' };
  }

  const section = findSectionSummary(snapshot.sectionSummaries, FOUNDRY_DOCUMENT_INTELLIGENCE_SECTION);
  if (!section || section.readinessStatus !== 'configured') {
    return { complete: false, message: 'Document Intelligence connection is not configured.' };
  }

  const state = snapshot.serviceStates.DocumentIntelligence;
  const providerId = FOUNDRY_SERVICE_PROVIDER_IDS.DocumentIntelligence;
  const provider = getProviderState(state, providerId);
  if (!state || !provider || state.activeProviderId !== providerId) {
    return { complete: false, message: 'Document Intelligence service is not set to Microsoft Foundry.' };
  }
  if (!provider.canActivate) {
    return { complete: false, message: 'Document Intelligence service has unresolved activation blockers.' };
  }
  return { complete: true, message: 'Document Intelligence is ready.' };
}

function geminiOptionalServiceStatus(
  serviceKey: GeminiOptionalServiceKey,
  snapshot: WizardLoadSnapshot
): { complete: boolean; message: string } {
  const geminiConnection = findSectionSummary(snapshot.sectionSummaries, GEMINI_CORE_SECTION);
  if (!geminiConnection || geminiConnection.readinessStatus !== 'configured') {
    if (serviceKey === 'Embeddings') {
      return { complete: false, message: 'Google Gemini API connection is not configured for Embeddings.' };
    }
    if (serviceKey === 'ImageGeneration') {
      return { complete: false, message: 'Google Gemini API connection is not configured for Image Generation.' };
    }
    if (serviceKey === 'SpeechTranscription') {
      return { complete: false, message: 'Google Gemini API connection is not configured for Speech Transcription.' };
    }
    return { complete: false, message: 'Google Gemini API connection is not configured for Speech Synthesis.' };
  }

  if (serviceKey === 'Embeddings') {
    const state = snapshot.serviceStates.Embeddings;
    const providerId = GEMINI_SERVICE_PROVIDER_IDS.Embeddings;
    const provider = getProviderState(state, providerId);
    if (!state || !provider || state.activeProviderId !== providerId) {
      return { complete: false, message: 'Embeddings service is not set to Google Gemini.' };
    }
    if (!provider.canActivate) {
      return { complete: false, message: 'Embeddings service has unresolved activation blockers.' };
    }
    return { complete: true, message: 'Embeddings is ready.' };
  }

  if (serviceKey === 'ImageGeneration') {
    const state = snapshot.serviceStates.ImageGeneration;
    const providerId = GEMINI_SERVICE_PROVIDER_IDS.ImageGeneration;
    const provider = getProviderState(state, providerId);
    if (!state || !provider || state.activeProviderId !== providerId) {
      return { complete: false, message: 'Image Generation service is not set to Google Gemini.' };
    }
    if (!provider.canActivate) {
      return { complete: false, message: 'Image Generation service has unresolved activation blockers.' };
    }
    return { complete: true, message: 'Image Generation is ready.' };
  }

  if (serviceKey === 'SpeechTranscription') {
    const state = snapshot.serviceStates.SpeechTranscription;
    const providerId = GEMINI_SERVICE_PROVIDER_IDS.SpeechTranscription;
    const provider = getProviderState(state, providerId);
    if (!state || !provider || state.activeProviderId !== providerId) {
      return { complete: false, message: 'Speech Transcription service is not set to Google Gemini.' };
    }
    if (!provider.canActivate) {
      return { complete: false, message: 'Speech Transcription service has unresolved activation blockers.' };
    }
    return { complete: true, message: 'Speech Transcription is ready.' };
  }

  const state = snapshot.serviceStates.SpeechSynthesis;
  const providerId = GEMINI_SERVICE_PROVIDER_IDS.SpeechSynthesis;
  const provider = getProviderState(state, providerId);
  if (!state || !provider || state.activeProviderId !== providerId) {
    return { complete: false, message: 'Speech Synthesis service is not set to Google Gemini.' };
  }
  if (!provider.canActivate) {
    return { complete: false, message: 'Speech Synthesis service has unresolved activation blockers.' };
  }
  return { complete: true, message: 'Speech Synthesis is ready.' };
}

export function summarizeFoundryOptionalServiceWarnings(snapshot: WizardLoadSnapshot): string[] {
  const keys: OptionalServiceKey[] = [
    'Embeddings',
    'ImageGeneration',
    'SpeechTranscription',
    'SpeechSynthesis',
    'DocumentIntelligence',
  ];
  const warnings: string[] = [];
  for (const key of keys) {
    const result = optionalServiceStatus(key, snapshot);
    if (!result.complete) {
      warnings.push(result.message);
    }
  }
  return warnings;
}

export function summarizeGeminiOptionalServiceWarnings(snapshot: WizardLoadSnapshot): string[] {
  const keys: GeminiOptionalServiceKey[] = [
    'Embeddings',
    'ImageGeneration',
    'SpeechTranscription',
    'SpeechSynthesis',
  ];
  const warnings: string[] = [];
  for (const key of keys) {
    const result = geminiOptionalServiceStatus(key, snapshot);
    if (!result.complete) {
      warnings.push(result.message);
    }
  }
  return warnings;
}

function openAiOptionalServiceStatus(
  serviceKey: OpenAiOptionalServiceKey,
  snapshot: WizardLoadSnapshot
): { complete: boolean; message: string } {
  const openAiConnection = findSectionSummary(snapshot.sectionSummaries, OPENAI_CORE_SECTION);
  if (!openAiConnection || openAiConnection.readinessStatus !== 'configured') {
    if (serviceKey === 'Embeddings') {
      return { complete: false, message: 'OpenAI connection is not configured for Embeddings.' };
    }
    if (serviceKey === 'ImageGeneration') {
      return { complete: false, message: 'OpenAI connection is not configured for Image Generation.' };
    }
    if (serviceKey === 'SpeechTranscription') {
      return { complete: false, message: 'OpenAI connection is not configured for Speech Transcription.' };
    }
    return { complete: false, message: 'OpenAI connection is not configured for Speech Synthesis.' };
  }

  if (serviceKey === 'Embeddings') {
    const state = snapshot.serviceStates.Embeddings;
    const providerId = OPENAI_SERVICE_PROVIDER_IDS.Embeddings;
    const provider = getProviderState(state, providerId);
    if (!state || !provider || state.activeProviderId !== providerId) {
      return { complete: false, message: 'Embeddings service is not set to OpenAI.' };
    }
    if (!provider.canActivate) {
      return { complete: false, message: 'Embeddings service has unresolved activation blockers.' };
    }
    return { complete: true, message: 'Embeddings is ready.' };
  }

  if (serviceKey === 'ImageGeneration') {
    const state = snapshot.serviceStates.ImageGeneration;
    const providerId = OPENAI_SERVICE_PROVIDER_IDS.ImageGeneration;
    const provider = getProviderState(state, providerId);
    if (!state || !provider || state.activeProviderId !== providerId) {
      return { complete: false, message: 'Image Generation service is not set to OpenAI.' };
    }
    if (!provider.canActivate) {
      return { complete: false, message: 'Image Generation service has unresolved activation blockers.' };
    }
    return { complete: true, message: 'Image Generation is ready.' };
  }

  if (serviceKey === 'SpeechTranscription') {
    const state = snapshot.serviceStates.SpeechTranscription;
    const providerId = OPENAI_SERVICE_PROVIDER_IDS.SpeechTranscription;
    const provider = getProviderState(state, providerId);
    if (!state || !provider || state.activeProviderId !== providerId) {
      return { complete: false, message: 'Speech Transcription service is not set to OpenAI.' };
    }
    if (!provider.canActivate) {
      return { complete: false, message: 'Speech Transcription service has unresolved activation blockers.' };
    }
    return { complete: true, message: 'Speech Transcription is ready.' };
  }

  const state = snapshot.serviceStates.SpeechSynthesis;
  const providerId = OPENAI_SERVICE_PROVIDER_IDS.SpeechSynthesis;
  const provider = getProviderState(state, providerId);
  if (!state || !provider || state.activeProviderId !== providerId) {
    return { complete: false, message: 'Speech Synthesis service is not set to OpenAI.' };
  }
  if (!provider.canActivate) {
    return { complete: false, message: 'Speech Synthesis service has unresolved activation blockers.' };
  }
  return { complete: true, message: 'Speech Synthesis is ready.' };
}

export function summarizeOpenAiOptionalServiceWarnings(snapshot: WizardLoadSnapshot): string[] {
  const keys: OpenAiOptionalServiceKey[] = [
    'Embeddings',
    'ImageGeneration',
    'SpeechTranscription',
    'SpeechSynthesis',
  ];
  const warnings: string[] = [];
  for (const key of keys) {
    const result = openAiOptionalServiceStatus(key, snapshot);
    if (!result.complete) {
      warnings.push(result.message);
    }
  }
  return warnings;
}

export function summarizeOptionalServiceWarnings(snapshot: WizardLoadSnapshot): string[] {
  return summarizeFoundryOptionalServiceWarnings(snapshot);
}

export function toExistingLocalModels(models: SettingsModelDto[]): SettingsModelDto[] {
  return models
    .filter((model) => model.provider === 'llama-cpp')
    .sort((left, right) => left.modelId.localeCompare(right.modelId));
}

export function buildLocalAiModelRequest(draft: LocalAiModelDraft): AddModelRequest {
  const onboardingDraft = {
    installSource: draft.installSource,
    runtimeProfileId: draft.runtimeProfileId,
    routerModelId: draft.routerModelId,
    huggingFaceRepository: draft.huggingFaceRepository,
    huggingFaceQuantIncludePattern: draft.huggingFaceQuantIncludePattern,
    huggingFaceMmprojIncludePattern: draft.huggingFaceMmprojIncludePattern,
    huggingFaceTargetDirectory: draft.huggingFaceTargetDirectory,
    existingAliasRouterModelId: draft.existingAliasRouterModelId,
    routerContextSize: draft.routerContextSize,
    routerCacheRamMib: draft.routerCacheRamMib,
    catalogModelId: draft.catalogModelId,
    catalogDisplayName: draft.catalogDisplayName,
    catalogIsActive: true,
  } as const;
  const validationErrors = validateLocalModelOnboardingDraft(onboardingDraft, {
    defaultCatalogModelId: draft.installSource === 'existingAlias'
      ? draft.existingAliasRouterModelId
      : draft.routerModelId,
    defaultCatalogDisplayName: draft.installSource === 'existingAlias'
      ? draft.existingAliasRouterModelId
      : draft.routerModelId,
    defaultTargetDirectory: draft.routerModelId,
  });
  if (validationErrors.length > 0) {
    throw new Error(validationErrors[0]);
  }
  return buildLocalModelOnboardingRequest(onboardingDraft, {
    onboardingUi: 'wizard',
    defaultCatalogModelId: draft.installSource === 'existingAlias'
      ? draft.existingAliasRouterModelId
      : draft.routerModelId,
    defaultCatalogDisplayName: draft.installSource === 'existingAlias'
      ? draft.existingAliasRouterModelId
      : draft.routerModelId,
    defaultTargetDirectory: draft.routerModelId,
    defaultCatalogIsActive: true,
  });
}

function localAiOptionalServiceStatus(
  serviceKey: LocalAiOptionalServiceKey,
  snapshot: WizardLoadSnapshot
): { complete: boolean; message: string } {
  const labels: Record<LocalAiOptionalServiceKey, string> = {
    Embeddings: 'Embeddings',
    ImageGeneration: 'Image Generation',
    SpeechTranscription: 'Speech Transcription',
    SpeechSynthesis: 'Speech Synthesis',
    DocumentIntelligence: 'Document Intelligence',
  };
  const label = labels[serviceKey];
  const providerId = LOCAL_AI_SERVICE_PROVIDER_IDS[serviceKey];
  const state = snapshot.serviceStates[serviceKey];
  const provider = getProviderState(state, providerId);
  if (!state || !provider || state.activeProviderId !== providerId) {
    return { complete: false, message: `${label} service is not set to Local AI.` };
  }
  if (!provider.canActivate) {
    return { complete: false, message: `${label} service has unresolved activation blockers (check Infrastructure tab for required local service URLs).` };
  }
  return { complete: true, message: `${label} is ready.` };
}

export function summarizeLocalAiOptionalServiceWarnings(snapshot: WizardLoadSnapshot): string[] {
  const keys: LocalAiOptionalServiceKey[] = [
    'Embeddings',
    'ImageGeneration',
    'SpeechTranscription',
    'SpeechSynthesis',
    'DocumentIntelligence',
  ];
  const warnings: string[] = [];
  for (const key of keys) {
    const result = localAiOptionalServiceStatus(key, snapshot);
    if (!result.complete) {
      warnings.push(result.message);
    }
  }
  return warnings;
}
