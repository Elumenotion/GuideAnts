import type { AddAiServicesWizardProvider, FoundryModelProviderLabel, OptionalServiceKey } from './types';

export const WIZARD_STEPS: readonly { id: string; label: string }[] = [
  { id: 'provider', label: 'Provider' },
  { id: 'connection', label: 'Connection details' },
  { id: 'models', label: 'Models' },
  { id: 'optionalServices', label: 'Optional services' },
  { id: 'finish', label: 'Finish' },
] as const;

export const WIZARD_PROVIDER_OPTIONS: readonly {
  id: AddAiServicesWizardProvider;
  label: string;
  description: string;
}[] = [
  {
    id: 'foundry',
    label: 'Microsoft Foundry',
    description: 'Configure Azure OpenAI plus optional Microsoft cloud services.',
  },
  {
    id: 'google-gemini',
    label: 'Google Gemini',
    description: 'Configure Gemini API key, Gemini chat models, and Gemini service modes.',
  },
] as const;

export const FOUNDRY_CORE_SECTION = 'AzureOpenAI';
export const GEMINI_CORE_SECTION = 'GoogleGeminiApi';

export const FOUNDRY_EMBEDDINGS_SECTION = 'AzureOpenAiEmbedding';
export const FOUNDRY_IMAGES_SECTION = 'AzureOpenAiImages';
export const FOUNDRY_SPEECH_SECTION = 'AzureSpeechService';
export const FOUNDRY_DOCUMENT_INTELLIGENCE_SECTION = 'AzureDocumentIntelligence';

export const FOUNDRY_SERVICE_PROVIDER_IDS: Readonly<Record<OptionalServiceKey, string>> = {
  Embeddings: 'Embeddings.AzureOpenAI.Embedding',
  ImageGeneration: 'ImageGeneration.AzureOpenAI.Images',
  SpeechTranscription: 'SpeechTranscription.AzureSpeech.Batch',
  SpeechSynthesis: 'SpeechSynthesis.AzureSpeech.Ssml',
  DocumentIntelligence: 'DocumentIntelligence.Azure.DocumentIntelligence',
} as const;

export const GEMINI_SERVICE_PROVIDER_IDS: Readonly<Record<
  'Embeddings' | 'ImageGeneration' | 'SpeechTranscription' | 'SpeechSynthesis',
  string
>> = {
  Embeddings: 'Embeddings.Google.Embedding',
  ImageGeneration: 'ImageGeneration.Google.Imagen',
  SpeechTranscription: 'SpeechTranscription.Google.SpeechToText',
  SpeechSynthesis: 'SpeechSynthesis.Google.TextToSpeech',
} as const;

export const MODEL_PROVIDER_LABEL_TO_ID: Readonly<Record<FoundryModelProviderLabel, string>> = {
  Completions: 'azure-openai-chat',
  Responses: 'azure-openai-responses',
} as const;

export const MODEL_PROVIDER_ID_TO_LABEL: Readonly<Record<string, FoundryModelProviderLabel>> = {
  'azure-openai-chat': 'Completions',
  'azure-openai-responses': 'Responses',
} as const;

export const GEMINI_MODEL_PROVIDER_ID = 'google-gemini-chat';
export const GEMINI_DEFAULT_CHAT_MODEL_ID = 'gemini-2.5-flash';

export const GEMINI_OPTIONAL_SERVICE_DEFAULTS = {
  embeddingsModelId: 'gemini-embedding-2',
  embeddingsTimeoutSeconds: '300',
  imagesModelId: 'gemini-2.5-flash-image',
  imagesTimeoutSeconds: '900',
  speechTranscriptionModelId: 'gemini-2.5-flash',
  speechTranscriptionTimeoutSeconds: '300',
  speechSynthesisModelId: 'gemini-3.1-flash-tts-preview',
  speechSynthesisVoiceName: 'Kore',
  speechSynthesisTimeoutSeconds: '300',
} as const;

export const SECRET_MASK = '********';

// Back-compat aliases for existing tests/helpers.
export const AZURE_FOUNDATION_SECTION = FOUNDRY_CORE_SECTION;
export const EMBEDDINGS_SECTION = FOUNDRY_EMBEDDINGS_SECTION;
export const IMAGES_SECTION = FOUNDRY_IMAGES_SECTION;
export const SPEECH_SECTION = FOUNDRY_SPEECH_SECTION;
export const DOCUMENT_INTELLIGENCE_SECTION = FOUNDRY_DOCUMENT_INTELLIGENCE_SECTION;
export const SERVICE_PROVIDER_IDS = FOUNDRY_SERVICE_PROVIDER_IDS;
