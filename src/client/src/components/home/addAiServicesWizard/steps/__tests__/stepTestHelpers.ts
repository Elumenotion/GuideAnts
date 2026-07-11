import { screen, within } from '@testing-library/react';
import {
  GEMINI_OPTIONAL_SERVICE_DEFAULTS,
  HUGGINGFACE_OPTIONAL_SERVICE_DEFAULTS,
  OPENAI_OPTIONAL_SERVICE_DEFAULTS,
  OPENROUTER_OPTIONAL_SERVICE_DEFAULTS,
} from '../../constants';
import type {
  FoundryOptionalServicesFormState,
  GeminiOptionalServicesFormState,
  HuggingFaceOptionalServicesFormState,
  LocalAiModelDraft,
  LocalAiPrerequisitesFormState,
  OpenAiOptionalServicesFormState,
  OpenRouterOptionalServicesFormState,
} from '../../types';

export function getServiceCardCheckbox(title: string): HTMLInputElement {
  const titleEl = screen.getByText(title);
  const card = titleEl.closest('.rounded');
  if (!card) {
    throw new Error(`Service card not found for title: ${title}`);
  }
  return within(card as HTMLElement).getByRole('checkbox', { name: /configure now/i });
}

export function createFoundryOptionalServicesForm(
  overrides: Partial<FoundryOptionalServicesFormState> = {},
): FoundryOptionalServicesFormState {
  return {
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
    imagesApiVersion: '2025-04-01-preview',
    imagesDeployment: '',
    imagesEditDeployment: '',
    linkImagesEndpointToCore: true,
    enableSpeech: false,
    speechEndpoint: '',
    speechApiKey: '',
    speechApiKeyHasStoredValue: false,
    speechRegion: '',
    enableDocumentIntelligence: false,
    documentIntelligenceEndpoint: '',
    documentIntelligenceApiKey: '',
    documentIntelligenceApiKeyHasStoredValue: false,
    ...overrides,
  };
}

export function createOpenAiOptionalServicesForm(
  overrides: Partial<OpenAiOptionalServicesFormState> = {},
): OpenAiOptionalServicesFormState {
  return {
    enableSpeechTranscription: false,
    speechTranscriptionModelId: OPENAI_OPTIONAL_SERVICE_DEFAULTS.speechTranscriptionModelId,
    speechTranscriptionTimeoutSeconds: OPENAI_OPTIONAL_SERVICE_DEFAULTS.speechTranscriptionTimeoutSeconds,
    enableSpeechSynthesis: false,
    speechSynthesisModelId: OPENAI_OPTIONAL_SERVICE_DEFAULTS.speechSynthesisModelId,
    speechSynthesisVoiceName: OPENAI_OPTIONAL_SERVICE_DEFAULTS.speechSynthesisVoiceName,
    speechSynthesisTimeoutSeconds: OPENAI_OPTIONAL_SERVICE_DEFAULTS.speechSynthesisTimeoutSeconds,
    enableImages: false,
    imagesModelId: OPENAI_OPTIONAL_SERVICE_DEFAULTS.imagesModelId,
    imagesTimeoutSeconds: OPENAI_OPTIONAL_SERVICE_DEFAULTS.imagesTimeoutSeconds,
    enableEmbeddings: false,
    embeddingsModelId: OPENAI_OPTIONAL_SERVICE_DEFAULTS.embeddingsModelId,
    embeddingsDimensions: OPENAI_OPTIONAL_SERVICE_DEFAULTS.embeddingsDimensions,
    embeddingsTimeoutSeconds: OPENAI_OPTIONAL_SERVICE_DEFAULTS.embeddingsTimeoutSeconds,
    ...overrides,
  };
}

export function createGeminiOptionalServicesForm(
  overrides: Partial<GeminiOptionalServicesFormState> = {},
): GeminiOptionalServicesFormState {
  return {
    enableEmbeddings: false,
    embeddingsModelId: GEMINI_OPTIONAL_SERVICE_DEFAULTS.embeddingsModelId,
    embeddingsTimeoutSeconds: GEMINI_OPTIONAL_SERVICE_DEFAULTS.embeddingsTimeoutSeconds,
    enableImages: false,
    imagesModelId: GEMINI_OPTIONAL_SERVICE_DEFAULTS.imagesModelId,
    imagesTimeoutSeconds: GEMINI_OPTIONAL_SERVICE_DEFAULTS.imagesTimeoutSeconds,
    enableSpeechTranscription: false,
    speechTranscriptionModelId: GEMINI_OPTIONAL_SERVICE_DEFAULTS.speechTranscriptionModelId,
    speechTranscriptionTimeoutSeconds: GEMINI_OPTIONAL_SERVICE_DEFAULTS.speechTranscriptionTimeoutSeconds,
    enableSpeechSynthesis: false,
    speechSynthesisModelId: GEMINI_OPTIONAL_SERVICE_DEFAULTS.speechSynthesisModelId,
    speechSynthesisVoiceName: GEMINI_OPTIONAL_SERVICE_DEFAULTS.speechSynthesisVoiceName,
    speechSynthesisTimeoutSeconds: GEMINI_OPTIONAL_SERVICE_DEFAULTS.speechSynthesisTimeoutSeconds,
    ...overrides,
  };
}

export function createHuggingFaceOptionalServicesForm(
  overrides: Partial<HuggingFaceOptionalServicesFormState> = {},
): HuggingFaceOptionalServicesFormState {
  return {
    enableEmbeddings: false,
    embeddingsModelId: HUGGINGFACE_OPTIONAL_SERVICE_DEFAULTS.embeddingsModelId,
    embeddingsTimeoutSeconds: HUGGINGFACE_OPTIONAL_SERVICE_DEFAULTS.embeddingsTimeoutSeconds,
    enableImages: false,
    imagesTextToImageModelId: HUGGINGFACE_OPTIONAL_SERVICE_DEFAULTS.imagesTextToImageModelId,
    imagesImageToImageModelId: HUGGINGFACE_OPTIONAL_SERVICE_DEFAULTS.imagesImageToImageModelId,
    imagesTimeoutSeconds: HUGGINGFACE_OPTIONAL_SERVICE_DEFAULTS.imagesTimeoutSeconds,
    enableSpeechTranscription: false,
    speechTranscriptionModelId: HUGGINGFACE_OPTIONAL_SERVICE_DEFAULTS.speechTranscriptionModelId,
    speechTranscriptionTimeoutSeconds: HUGGINGFACE_OPTIONAL_SERVICE_DEFAULTS.speechTranscriptionTimeoutSeconds,
    enableSpeechSynthesis: false,
    speechSynthesisModelId: HUGGINGFACE_OPTIONAL_SERVICE_DEFAULTS.speechSynthesisModelId,
    speechSynthesisTimeoutSeconds: HUGGINGFACE_OPTIONAL_SERVICE_DEFAULTS.speechSynthesisTimeoutSeconds,
    ...overrides,
  };
}

export function createOpenRouterOptionalServicesForm(
  overrides: Partial<OpenRouterOptionalServicesFormState> = {},
): OpenRouterOptionalServicesFormState {
  return {
    enableEmbeddings: false,
    embeddingsModelId: OPENROUTER_OPTIONAL_SERVICE_DEFAULTS.embeddingsModelId,
    embeddingsTimeoutSeconds: OPENROUTER_OPTIONAL_SERVICE_DEFAULTS.embeddingsTimeoutSeconds,
    enableImages: false,
    imagesModelId: OPENROUTER_OPTIONAL_SERVICE_DEFAULTS.imagesModelId,
    imagesTimeoutSeconds: OPENROUTER_OPTIONAL_SERVICE_DEFAULTS.imagesTimeoutSeconds,
    enableSpeechTranscription: false,
    speechTranscriptionModelId: OPENROUTER_OPTIONAL_SERVICE_DEFAULTS.speechTranscriptionModelId,
    speechTranscriptionTimeoutSeconds: OPENROUTER_OPTIONAL_SERVICE_DEFAULTS.speechTranscriptionTimeoutSeconds,
    enableSpeechSynthesis: false,
    speechSynthesisModelId: OPENROUTER_OPTIONAL_SERVICE_DEFAULTS.speechSynthesisModelId,
    speechSynthesisTimeoutSeconds: OPENROUTER_OPTIONAL_SERVICE_DEFAULTS.speechSynthesisTimeoutSeconds,
    ...overrides,
  };
}

export function createLocalAiPrereqsForm(
  overrides: Partial<LocalAiPrerequisitesFormState> = {},
): LocalAiPrerequisitesFormState {
  return {
    huggingFaceToken: '',
    huggingFaceTokenHasStoredValue: false,
    ...overrides,
  };
}

export function createLocalAiModelDraft(overrides: Partial<LocalAiModelDraft> = {}): LocalAiModelDraft {
  return {
    localId: 'draft-1',
    installSource: 'huggingface',
    routerModelId: 'qwen3-9b',
    runtimeProfileId: 'profile-1',
    huggingFaceRepository: 'Qwen/Qwen3-9B',
    huggingFaceResolvedRevision: 'rev-1',
    huggingFaceArtifactGroupId: 'group-1',
    huggingFaceModelFiles: ['model.gguf'],
    huggingFaceMmprojFiles: [],
    huggingFaceTargetDirectory: 'qwen3-9b',
    huggingFaceRouterPresetRows: [{ key: 'ctx-size', value: '8192' }],
    huggingFacePresetMode: 'replace',
    existingAliasRouterModelId: '',
    catalogModelId: 'qwen3-9b',
    catalogDisplayName: 'Qwen3 9B',
    setAsGlobalDefault: false,
    persisted: false,
    asyncOperationId: null,
    asyncStatus: 'queued',
    asyncProgress: null,
    asyncError: null,
    ...overrides,
  };
}
