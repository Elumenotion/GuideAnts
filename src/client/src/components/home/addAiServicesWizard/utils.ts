import { api } from '../../../services/api';
import type {
  AddModelRequest,
  ProviderEditorStateDto,
  ServiceEditorStateDto,
  SettingsModelDto,
  SettingsSectionDto,
  SettingsSectionSummaryDto,
  SettingsSchemaDto,
} from '../../../types/settings';
import {
  FOUNDRY_CORE_SECTION,
  FOUNDRY_DOCUMENT_INTELLIGENCE_SECTION,
  FOUNDRY_EMBEDDINGS_SECTION,
  FOUNDRY_IMAGES_SECTION,
  FOUNDRY_SERVICE_PROVIDER_IDS,
  FOUNDRY_SPEECH_SECTION,
  GEMINI_CORE_SECTION,
  GEMINI_OPTIONAL_SERVICE_DEFAULTS,
  GEMINI_SERVICE_PROVIDER_IDS,
  HUGGINGFACE_CHAT_MODEL_PROVIDER_ID,
  HUGGINGFACE_OPTIONAL_SERVICE_DEFAULTS,
  HUGGINGFACE_SECTION,
  HUGGINGFACE_SERVICE_PROVIDER_IDS,
  GEMINI_MODEL_PROVIDER_ID,
  LOCAL_AI_SERVICE_PROVIDER_IDS,
  MODEL_PROVIDER_ID_TO_LABEL,
  MODEL_PROVIDER_LABEL_TO_ID,
  OPENAI_CORE_SECTION,
  OPENAI_MODEL_PROVIDER_ID_TO_LABEL,
  OPENAI_MODEL_PROVIDER_LABEL_TO_ID,
  OPENAI_OPTIONAL_SERVICE_DEFAULTS,
  OPENAI_SERVICE_PROVIDER_IDS,
  SECRET_MASK,
} from './constants';
import type {
  ExistingGeminiModel,
  ExistingHuggingFaceModel,
  ExistingFoundryModel,
  ExistingOpenAiModel,
  FoundryCoreConnectionFormState,
  FoundryModelDraft,
  FoundryModelProviderLabel,
  FoundryOptionalServicesFormState,
  GeminiCoreConnectionFormState,
  GeminiModelDraft,
  GeminiOptionalServiceKey,
  GeminiOptionalServicesFormState,
  HuggingFaceCoreConnectionFormState,
  HuggingFaceModelDraft,
  HuggingFaceOptionalServicesFormState,
  LocalAiModelDraft,
  LocalAiOptionalServiceKey,
  OpenAiCoreConnectionFormState,
  OpenAiModelDraft,
  OpenAiModelProviderLabel,
  OpenAiOptionalServiceKey,
  OpenAiOptionalServicesFormState,
  OptionalServiceKey,
  WizardLoadSnapshot,
} from './types';

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

export function toExistingHuggingFaceModels(models: SettingsModelDto[]): ExistingHuggingFaceModel[] {
  return models
    .filter((model) => model.provider === HUGGINGFACE_CHAT_MODEL_PROVIDER_ID)
    .map((model) => ({
      modelId: model.modelId,
      raw: model,
    }))
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

export function makeHuggingFaceDraftModel(
  localId: string,
  modelId: string,
  setAsGlobalDefault: boolean
): HuggingFaceModelDraft {
  return {
    localId,
    modelId: modelId.trim(),
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

export function buildAddHuggingFaceModelRequest(modelId: string): AddModelRequest {
  const trimmed = modelId.trim();
  return {
    provider: HUGGINGFACE_CHAT_MODEL_PROVIDER_ID,
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

function huggingFaceOptionalServiceStatus(
  serviceKey: OpenAiOptionalServiceKey,
  snapshot: WizardLoadSnapshot
): { complete: boolean; message: string } {
  const hfConnection = findSectionSummary(snapshot.sectionSummaries, HUGGINGFACE_SECTION);
  if (!hfConnection || hfConnection.readinessStatus !== 'configured') {
    if (serviceKey === 'Embeddings') {
      return { complete: false, message: 'Hugging Face connection is not configured for Embeddings.' };
    }
    if (serviceKey === 'ImageGeneration') {
      return { complete: false, message: 'Hugging Face connection is not configured for Image Generation.' };
    }
    if (serviceKey === 'SpeechTranscription') {
      return { complete: false, message: 'Hugging Face connection is not configured for Speech Transcription.' };
    }
    return { complete: false, message: 'Hugging Face connection is not configured for Speech Synthesis.' };
  }

  if (serviceKey === 'Embeddings') {
    const state = snapshot.serviceStates.Embeddings;
    const providerId = HUGGINGFACE_SERVICE_PROVIDER_IDS.Embeddings;
    const provider = getProviderState(state, providerId);
    if (!state || !provider || state.activeProviderId !== providerId) {
      return { complete: false, message: 'Embeddings service is not set to Hugging Face.' };
    }
    if (!provider.canActivate) {
      return { complete: false, message: 'Embeddings service has unresolved activation blockers.' };
    }
    return { complete: true, message: 'Embeddings is ready.' };
  }

  if (serviceKey === 'ImageGeneration') {
    const state = snapshot.serviceStates.ImageGeneration;
    const providerId = HUGGINGFACE_SERVICE_PROVIDER_IDS.ImageGeneration;
    const provider = getProviderState(state, providerId);
    if (!state || !provider || state.activeProviderId !== providerId) {
      return { complete: false, message: 'Image Generation service is not set to Hugging Face.' };
    }
    if (!provider.canActivate) {
      return { complete: false, message: 'Image Generation service has unresolved activation blockers.' };
    }
    return { complete: true, message: 'Image Generation is ready.' };
  }

  if (serviceKey === 'SpeechTranscription') {
    const state = snapshot.serviceStates.SpeechTranscription;
    const providerId = HUGGINGFACE_SERVICE_PROVIDER_IDS.SpeechTranscription;
    const provider = getProviderState(state, providerId);
    if (!state || !provider || state.activeProviderId !== providerId) {
      return { complete: false, message: 'Speech Transcription service is not set to Hugging Face.' };
    }
    if (!provider.canActivate) {
      return { complete: false, message: 'Speech Transcription service has unresolved activation blockers.' };
    }
    return { complete: true, message: 'Speech Transcription is ready.' };
  }

  const state = snapshot.serviceStates.SpeechSynthesis;
  const providerId = HUGGINGFACE_SERVICE_PROVIDER_IDS.SpeechSynthesis;
  const provider = getProviderState(state, providerId);
  if (!state || !provider || state.activeProviderId !== providerId) {
    return { complete: false, message: 'Speech Synthesis service is not set to Hugging Face.' };
  }
  if (!provider.canActivate) {
    return { complete: false, message: 'Speech Synthesis service has unresolved activation blockers.' };
  }
  return { complete: true, message: 'Speech Synthesis is ready.' };
}

export function summarizeHuggingFaceOptionalServiceWarnings(snapshot: WizardLoadSnapshot): string[] {
  const keys: OpenAiOptionalServiceKey[] = [
    'Embeddings',
    'ImageGeneration',
    'SpeechTranscription',
    'SpeechSynthesis',
  ];
  const warnings: string[] = [];
  for (const key of keys) {
    const result = huggingFaceOptionalServiceStatus(key, snapshot);
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
  const runtimeProfileId = draft.runtimeProfileId.trim();
  if (!runtimeProfileId) {
    throw new Error('Runtime profile is required for local AI model installation.');
  }
  const catalogModelId = draft.catalogModelId.trim();
  if (!catalogModelId) {
    throw new Error('Model ID is required.');
  }
  const routerKnobs: Record<string, number> = {};
  const ctxSize = parseInt(draft.routerContextSize.trim(), 10);
  if (!isNaN(ctxSize) && ctxSize > 0) {
    routerKnobs.routerContextSize = ctxSize;
  }
  const cacheRam = parseInt(draft.routerCacheRamMib.trim(), 10);
  if (!isNaN(cacheRam) && cacheRam > 0) {
    routerKnobs.routerCacheRamMib = cacheRam;
  }

  if (draft.installSource === 'existingAlias') {
    const alias = draft.existingAliasRouterModelId.trim();
    if (!alias) {
      throw new Error('Existing alias is required.');
    }
    return {
      provider: 'llama-cpp',
      catalog: {
        modelId: catalogModelId,
        displayName: (draft.catalogDisplayName.trim() || catalogModelId),
        isActive: true,
      },
      install: {
        source: 'existingAlias',
        routerModelId: alias,
        runtimeProfileId,
        existingAlias: { routerModelId: alias },
        ...routerKnobs,
      },
    };
  }

  const routerModelId = draft.routerModelId.trim();
  if (!routerModelId) {
    throw new Error('Router alias is required for Hugging Face install.');
  }
  const repository = draft.huggingFaceRepository.trim();
  const quantPattern = draft.huggingFaceQuantIncludePattern.trim();
  const mmprojPattern = draft.huggingFaceMmprojIncludePattern.trim();
  const targetDirectory = draft.huggingFaceTargetDirectory.trim();
  if (!repository || !quantPattern) {
    throw new Error('Repository and model file selection are required.');
  }
  return {
    provider: 'llama-cpp',
    catalog: {
      modelId: catalogModelId,
      displayName: (draft.catalogDisplayName.trim() || catalogModelId),
      isActive: true,
    },
    install: {
      source: 'huggingface',
      routerModelId,
      runtimeProfileId,
      huggingFace: {
        repository,
        quantIncludePattern: quantPattern,
        mmprojIncludePattern: mmprojPattern || quantPattern,
        targetDirectory: targetDirectory || routerModelId,
      },
      ...routerKnobs,
    },
  };
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

// ---------------------------------------------------------------------------
// Shared form helpers
// ---------------------------------------------------------------------------

export function withSecretPreserved(value: string, hasStoredValue: boolean): string {
  const trimmed = value.trim();
  if (trimmed.length > 0) {
    return trimmed;
  }
  return hasStoredValue ? SECRET_MASK : '';
}

export function isPositiveIntegerValue(value: string): boolean {
  const parsed = Number(value);
  return Number.isInteger(parsed) && parsed > 0;
}

export function getSectionStringValue(section: SettingsSectionDto | undefined, fieldName: string): string {
  const value = section?.payload?.[fieldName];
  return typeof value === 'string' ? value : '';
}

export function hasStoredSecret(section: SettingsSectionDto | undefined, fieldName: string): boolean {
  return Boolean(section?.secretHasValue?.[fieldName]);
}

export function getServiceProviderFieldValue(
  state: ServiceEditorStateDto | undefined,
  providerId: string,
  fieldName: string
): string {
  const provider = state?.providers.find((item) => item.providerId === providerId);
  const value = provider?.fields?.[fieldName]?.value;
  return typeof value === 'string' ? value : '';
}

export function getSchemaDefault(
  schema: SettingsSchemaDto,
  sectionName: string,
  fieldName: string,
  fallback: string
): string {
  const section = schema.sections.find((item) => item.sectionName === sectionName);
  const property = section?.properties.find((item) => item.name === fieldName);
  const defaultValue = property?.defaultValue;
  if (typeof defaultValue === 'string' && defaultValue.trim().length > 0) {
    return defaultValue;
  }
  return fallback;
}

// ---------------------------------------------------------------------------
// Shared API helpers
// ---------------------------------------------------------------------------

export async function persistGlobalDefaultModel(modelId: string): Promise<void> {
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
}

export async function updateWizardSection(
  sectionName: string,
  payload: Record<string, unknown>,
  currentSectionsByName: Record<string, SettingsSectionDto>
): Promise<Record<string, SettingsSectionDto>> {
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
}

// ---------------------------------------------------------------------------
// Form builders (snapshot → initial form state)
// ---------------------------------------------------------------------------

export function buildFoundryCoreForm(snapshot: WizardLoadSnapshot): FoundryCoreConnectionFormState {
  const section = snapshot.sectionsByName[FOUNDRY_CORE_SECTION];
  return {
    resource: String(section?.payload.Resource ?? ''),
    apiKey: String(section?.payload.ApiKey ?? ''),
    apiVersion: String(section?.payload.ApiVersion ?? snapshot.defaults.azureOpenAiApiVersion),
    apiKeyHasStoredValue: Boolean(section?.secretHasValue?.ApiKey),
  };
}

export function buildGeminiCoreForm(snapshot: WizardLoadSnapshot): GeminiCoreConnectionFormState {
  const section = snapshot.sectionsByName[GEMINI_CORE_SECTION];
  return {
    apiKey: String(section?.payload.ApiKey ?? ''),
    apiKeyHasStoredValue: Boolean(section?.secretHasValue?.ApiKey),
  };
}

export function buildOpenAiCoreForm(snapshot: WizardLoadSnapshot): OpenAiCoreConnectionFormState {
  const section = snapshot.sectionsByName[OPENAI_CORE_SECTION];
  return {
    apiKey: String(section?.payload.ApiKey ?? ''),
    endpoint: String(section?.payload.Endpoint ?? ''),
    apiKeyHasStoredValue: Boolean(section?.secretHasValue?.ApiKey),
  };
}

export function buildHuggingFaceCoreForm(snapshot: WizardLoadSnapshot): HuggingFaceCoreConnectionFormState {
  const section = snapshot.sectionsByName[HUGGINGFACE_SECTION];
  return {
    token: String(section?.payload.Token ?? ''),
    routerBaseUrl: String(section?.payload.RouterBaseUrl ?? HUGGINGFACE_OPTIONAL_SERVICE_DEFAULTS.routerBaseUrl),
    tokenHasStoredValue: Boolean(section?.secretHasValue?.Token),
  };
}

export function buildFoundryOptionalServicesForm(snapshot: WizardLoadSnapshot): FoundryOptionalServicesFormState {
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
      (s) => s.sectionName === FOUNDRY_EMBEDDINGS_SECTION && s.readinessStatus === 'configured'
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
      (s) => s.sectionName === FOUNDRY_IMAGES_SECTION && s.readinessStatus === 'configured'
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
      (s) => s.sectionName === FOUNDRY_SPEECH_SECTION && s.readinessStatus === 'configured'
    ),
    speechEndpoint: getSectionStringValue(speechSection, 'Endpoint'),
    speechApiKey: getSectionStringValue(speechSection, 'ApiKey'),
    speechApiKeyHasStoredValue: hasStoredSecret(speechSection, 'ApiKey'),
    speechRegion: getSectionStringValue(speechSection, 'Region') || 'eastus',

    enableDocumentIntelligence: snapshot.sectionSummaries.some(
      (s) => s.sectionName === FOUNDRY_DOCUMENT_INTELLIGENCE_SECTION && s.readinessStatus === 'configured'
    ),
    documentIntelligenceEndpoint: getSectionStringValue(documentSection, 'Endpoint'),
    documentIntelligenceApiKey: getSectionStringValue(documentSection, 'ApiKey'),
    documentIntelligenceApiKeyHasStoredValue: hasStoredSecret(documentSection, 'ApiKey'),
  };
}

export function buildGeminiOptionalServicesForm(snapshot: WizardLoadSnapshot): GeminiOptionalServicesFormState {
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
      getServiceProviderFieldValue(snapshot.serviceStates.SpeechTranscription, GEMINI_SERVICE_PROVIDER_IDS.SpeechTranscription, 'ModelId') ||
      GEMINI_OPTIONAL_SERVICE_DEFAULTS.speechTranscriptionModelId,
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

export function buildOpenAiOptionalServicesForm(snapshot: WizardLoadSnapshot): OpenAiOptionalServicesFormState {
  return {
    enableSpeechTranscription: true,
    speechTranscriptionModelId:
      getServiceProviderFieldValue(snapshot.serviceStates.SpeechTranscription, OPENAI_SERVICE_PROVIDER_IDS.SpeechTranscription, 'ModelId') ||
      OPENAI_OPTIONAL_SERVICE_DEFAULTS.speechTranscriptionModelId,
    speechTranscriptionTimeoutSeconds:
      getServiceProviderFieldValue(snapshot.serviceStates.SpeechTranscription, OPENAI_SERVICE_PROVIDER_IDS.SpeechTranscription, 'TimeoutSeconds') ||
      OPENAI_OPTIONAL_SERVICE_DEFAULTS.speechTranscriptionTimeoutSeconds,

    enableSpeechSynthesis: true,
    speechSynthesisModelId:
      getServiceProviderFieldValue(snapshot.serviceStates.SpeechSynthesis, OPENAI_SERVICE_PROVIDER_IDS.SpeechSynthesis, 'ModelId') ||
      OPENAI_OPTIONAL_SERVICE_DEFAULTS.speechSynthesisModelId,
    speechSynthesisVoiceName:
      getServiceProviderFieldValue(snapshot.serviceStates.SpeechSynthesis, OPENAI_SERVICE_PROVIDER_IDS.SpeechSynthesis, 'VoiceName') ||
      OPENAI_OPTIONAL_SERVICE_DEFAULTS.speechSynthesisVoiceName,
    speechSynthesisTimeoutSeconds:
      getServiceProviderFieldValue(snapshot.serviceStates.SpeechSynthesis, OPENAI_SERVICE_PROVIDER_IDS.SpeechSynthesis, 'TimeoutSeconds') ||
      OPENAI_OPTIONAL_SERVICE_DEFAULTS.speechSynthesisTimeoutSeconds,

    enableImages: true,
    imagesModelId:
      getServiceProviderFieldValue(snapshot.serviceStates.ImageGeneration, OPENAI_SERVICE_PROVIDER_IDS.ImageGeneration, 'ModelId') ||
      OPENAI_OPTIONAL_SERVICE_DEFAULTS.imagesModelId,
    imagesTimeoutSeconds:
      getServiceProviderFieldValue(snapshot.serviceStates.ImageGeneration, OPENAI_SERVICE_PROVIDER_IDS.ImageGeneration, 'TimeoutSeconds') ||
      OPENAI_OPTIONAL_SERVICE_DEFAULTS.imagesTimeoutSeconds,

    enableEmbeddings: true,
    embeddingsModelId:
      getServiceProviderFieldValue(snapshot.serviceStates.Embeddings, OPENAI_SERVICE_PROVIDER_IDS.Embeddings, 'ModelId') ||
      OPENAI_OPTIONAL_SERVICE_DEFAULTS.embeddingsModelId,
    embeddingsDimensions:
      getServiceProviderFieldValue(snapshot.serviceStates.Embeddings, OPENAI_SERVICE_PROVIDER_IDS.Embeddings, 'Dimensions') ||
      OPENAI_OPTIONAL_SERVICE_DEFAULTS.embeddingsDimensions,
    embeddingsTimeoutSeconds:
      getServiceProviderFieldValue(snapshot.serviceStates.Embeddings, OPENAI_SERVICE_PROVIDER_IDS.Embeddings, 'TimeoutSeconds') ||
      OPENAI_OPTIONAL_SERVICE_DEFAULTS.embeddingsTimeoutSeconds,
  };
}

export function buildHuggingFaceOptionalServicesForm(snapshot: WizardLoadSnapshot): HuggingFaceOptionalServicesFormState {
  return {
    enableEmbeddings: true,
    embeddingsModelId:
      getServiceProviderFieldValue(snapshot.serviceStates.Embeddings, HUGGINGFACE_SERVICE_PROVIDER_IDS.Embeddings, 'ModelId') ||
      HUGGINGFACE_OPTIONAL_SERVICE_DEFAULTS.embeddingsModelId,
    embeddingsTimeoutSeconds:
      getServiceProviderFieldValue(snapshot.serviceStates.Embeddings, HUGGINGFACE_SERVICE_PROVIDER_IDS.Embeddings, 'TimeoutSeconds') ||
      HUGGINGFACE_OPTIONAL_SERVICE_DEFAULTS.embeddingsTimeoutSeconds,

    enableImages: true,
    imagesTextToImageModelId:
      getServiceProviderFieldValue(snapshot.serviceStates.ImageGeneration, HUGGINGFACE_SERVICE_PROVIDER_IDS.ImageGeneration, 'TextToImageModelId') ||
      HUGGINGFACE_OPTIONAL_SERVICE_DEFAULTS.imagesTextToImageModelId,
    imagesImageToImageModelId:
      getServiceProviderFieldValue(snapshot.serviceStates.ImageGeneration, HUGGINGFACE_SERVICE_PROVIDER_IDS.ImageGeneration, 'ImageToImageModelId') ||
      HUGGINGFACE_OPTIONAL_SERVICE_DEFAULTS.imagesImageToImageModelId,
    imagesTimeoutSeconds:
      getServiceProviderFieldValue(snapshot.serviceStates.ImageGeneration, HUGGINGFACE_SERVICE_PROVIDER_IDS.ImageGeneration, 'TimeoutSeconds') ||
      HUGGINGFACE_OPTIONAL_SERVICE_DEFAULTS.imagesTimeoutSeconds,

    enableSpeechTranscription: true,
    speechTranscriptionModelId:
      getServiceProviderFieldValue(snapshot.serviceStates.SpeechTranscription, HUGGINGFACE_SERVICE_PROVIDER_IDS.SpeechTranscription, 'ModelId') ||
      HUGGINGFACE_OPTIONAL_SERVICE_DEFAULTS.speechTranscriptionModelId,
    speechTranscriptionTimeoutSeconds:
      getServiceProviderFieldValue(snapshot.serviceStates.SpeechTranscription, HUGGINGFACE_SERVICE_PROVIDER_IDS.SpeechTranscription, 'TimeoutSeconds') ||
      HUGGINGFACE_OPTIONAL_SERVICE_DEFAULTS.speechTranscriptionTimeoutSeconds,

    enableSpeechSynthesis: true,
    speechSynthesisModelId:
      getServiceProviderFieldValue(snapshot.serviceStates.SpeechSynthesis, HUGGINGFACE_SERVICE_PROVIDER_IDS.SpeechSynthesis, 'ModelId') ||
      HUGGINGFACE_OPTIONAL_SERVICE_DEFAULTS.speechSynthesisModelId,
    speechSynthesisTimeoutSeconds:
      getServiceProviderFieldValue(snapshot.serviceStates.SpeechSynthesis, HUGGINGFACE_SERVICE_PROVIDER_IDS.SpeechSynthesis, 'TimeoutSeconds') ||
      HUGGINGFACE_OPTIONAL_SERVICE_DEFAULTS.speechSynthesisTimeoutSeconds,
  };
}
