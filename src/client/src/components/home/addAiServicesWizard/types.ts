import type { ServiceEditorStateDto, SettingsModelDto, SettingsSectionDto, SettingsSectionSummaryDto } from '../../../types/settings';

export type AddAiServicesWizardStep =
  | 'provider'
  | 'connection'
  | 'models'
  | 'optionalServices'
  | 'finish';

export type AddAiServicesWizardProvider = 'foundry' | 'google-gemini';

export type FoundryModelProviderLabel = 'Completions' | 'Responses';

export interface FoundryModelDraft {
  localId: string;
  modelId: string;
  provider: FoundryModelProviderLabel;
  setAsGlobalDefault: boolean;
  persisted: boolean;
}

export interface GeminiModelDraft {
  localId: string;
  modelId: string;
  setAsGlobalDefault: boolean;
  persisted: boolean;
}

export interface ExistingFoundryModel {
  modelId: string;
  provider: FoundryModelProviderLabel;
  raw: SettingsModelDto;
}

export interface ExistingGeminiModel {
  modelId: string;
  raw: SettingsModelDto;
}

export interface FoundryCoreConnectionFormState {
  resource: string;
  apiKey: string;
  apiVersion: string;
  apiKeyHasStoredValue: boolean;
}

export type CoreConnectionFormState = FoundryCoreConnectionFormState;

export interface GeminiCoreConnectionFormState {
  apiKey: string;
  apiKeyHasStoredValue: boolean;
}

export interface FoundryOptionalServicesFormState {
  enableEmbeddings: boolean;
  embeddingsEndpoint: string;
  embeddingsApiKey: string;
  embeddingsApiKeyHasStoredValue: boolean;
  embeddingsDeployment: string;
  linkEmbeddingsEndpointToCore: boolean;

  enableImages: boolean;
  imagesEndpoint: string;
  imagesApiKey: string;
  imagesApiKeyHasStoredValue: boolean;
  imagesApiVersion: string;
  imagesDeployment: string;
  imagesEditDeployment: string;
  linkImagesEndpointToCore: boolean;

  enableSpeech: boolean;
  speechEndpoint: string;
  speechApiKey: string;
  speechApiKeyHasStoredValue: boolean;
  speechRegion: string;

  enableDocumentIntelligence: boolean;
  documentIntelligenceEndpoint: string;
  documentIntelligenceApiKey: string;
  documentIntelligenceApiKeyHasStoredValue: boolean;
}

export type OptionalServicesFormState = FoundryOptionalServicesFormState;

export interface GeminiOptionalServicesFormState {
  enableEmbeddings: boolean;
  embeddingsModelId: string;
  embeddingsTimeoutSeconds: string;

  enableImages: boolean;
  imagesModelId: string;
  imagesTimeoutSeconds: string;

  enableSpeechTranscription: boolean;
  speechTranscriptionModelId: string;
  speechTranscriptionTimeoutSeconds: string;

  enableSpeechSynthesis: boolean;
  speechSynthesisModelId: string;
  speechSynthesisVoiceName: string;
  speechSynthesisTimeoutSeconds: string;
}

export interface WizardLoadSnapshot {
  sectionSummaries: SettingsSectionSummaryDto[];
  sectionsByName: Record<string, SettingsSectionDto>;
  models: SettingsModelDto[];
  serviceStates: Partial<Record<OptionalServiceKey, ServiceEditorStateDto>>;
  defaults: {
    azureOpenAiApiVersion: string;
    azureOpenAiImagesApiVersion: string;
  };
}

export type OptionalServiceKey =
  | 'Embeddings'
  | 'ImageGeneration'
  | 'SpeechTranscription'
  | 'SpeechSynthesis'
  | 'DocumentIntelligence';

export type GeminiOptionalServiceKey =
  | 'Embeddings'
  | 'ImageGeneration'
  | 'SpeechTranscription'
  | 'SpeechSynthesis';
