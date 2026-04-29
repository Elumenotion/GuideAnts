import type { ServiceEditorStateDto, SettingsModelDto, SettingsSectionDto, SettingsSectionSummaryDto } from '../../../types/settings';

export type AddAiServicesWizardStep =
  | 'provider'
  | 'connection'
  | 'models'
  | 'optionalServices'
  | 'finish';

export type FoundryModelProviderLabel = 'Completions' | 'Responses';

export interface FoundryModelDraft {
  localId: string;
  modelId: string;
  provider: FoundryModelProviderLabel;
  persisted: boolean;
}

export interface ExistingFoundryModel {
  modelId: string;
  provider: FoundryModelProviderLabel;
  raw: SettingsModelDto;
}

export interface CoreConnectionFormState {
  resource: string;
  apiKey: string;
  apiVersion: string;
  apiKeyHasStoredValue: boolean;
}

export interface OptionalServicesFormState {
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

