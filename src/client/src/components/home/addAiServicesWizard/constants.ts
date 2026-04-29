import type { FoundryModelProviderLabel, OptionalServiceKey } from './types';

export const WIZARD_STEPS: readonly { id: string; label: string }[] = [
  { id: 'provider', label: 'Provider' },
  { id: 'connection', label: 'Connection details' },
  { id: 'models', label: 'Models' },
  { id: 'optionalServices', label: 'Optional services' },
  { id: 'finish', label: 'Finish' },
] as const;

export const AZURE_FOUNDATION_SECTION = 'AzureOpenAI';
export const EMBEDDINGS_SECTION = 'AzureOpenAiEmbedding';
export const IMAGES_SECTION = 'AzureOpenAiImages';
export const SPEECH_SECTION = 'AzureSpeechService';
export const DOCUMENT_INTELLIGENCE_SECTION = 'AzureDocumentIntelligence';

export const SERVICE_PROVIDER_IDS: Readonly<Record<OptionalServiceKey, string>> = {
  Embeddings: 'Embeddings.AzureOpenAI.Embedding',
  ImageGeneration: 'ImageGeneration.AzureOpenAI.Images',
  SpeechTranscription: 'SpeechTranscription.AzureSpeech.Batch',
  SpeechSynthesis: 'SpeechSynthesis.AzureSpeech.Ssml',
  DocumentIntelligence: 'DocumentIntelligence.Azure.DocumentIntelligence',
} as const;

export const MODEL_PROVIDER_LABEL_TO_ID: Readonly<Record<FoundryModelProviderLabel, string>> = {
  Completions: 'azure-openai-chat',
  Responses: 'azure-openai-responses',
} as const;

export const MODEL_PROVIDER_ID_TO_LABEL: Readonly<Record<string, FoundryModelProviderLabel>> = {
  'azure-openai-chat': 'Completions',
  'azure-openai-responses': 'Responses',
} as const;

export const SECRET_MASK = '********';

