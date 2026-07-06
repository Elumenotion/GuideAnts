export interface ConnectionOwnershipCategory {
  key: string;
  label: string;
  description: string;
  sectionNames: string[];
}

/**
 * Client-owned taxonomy for Settings -> Connections.
 * Keep first-launch auto-open detection aligned with this list.
 */
export const HIDDEN_CLOUD_PROVIDER_SECTIONS = new Set<string>();
export const HIDDEN_CHAT_MODEL_PROVIDERS = new Set<string>();

export const CONNECTION_OWNERSHIP_CATEGORIES: readonly ConnectionOwnershipCategory[] = [
  {
    key: 'chat',
    label: 'Chat / LLM Providers',
    description: 'Connections used when dispatching chat completions to a catalog model.',
    sectionNames: ['AzureOpenAI', 'OpenAI', 'Anthropic', 'GoogleGeminiApi', 'OpenRouter'],
  },
  {
    key: 'service',
    label: 'Service Providers',
    description: 'Connections referenced by non-chat service routing modes (speech, images, embeddings, markdown extraction).',
    sectionNames: [
      'AzureSpeechService',
      'AzureOpenAiImages',
      'AzureOpenAiEmbedding',
      'AzureDocumentIntelligence',
      'GoogleGeminiApi',
      'OpenAI',
      'OpenRouter',
    ],
  },
  {
    key: 'huggingface',
    label: 'Hugging Face',
    description: 'Single token used for every Hugging Face download in the app (llama, Stable Diffusion bundles, ASR, TTS, embeddings).',
    sectionNames: ['HuggingFace'],
  },
] as const;

/**
 * Sections never shown on Connections; they represent routed services/modes,
 * not provider credential stores.
 */
export const CONNECTION_EXCLUDED_SECTION_NAMES = new Set<string>([
  'ServiceModes',
  'Embeddings',
  'ImageGeneration',
  'SpeechTranscription',
  'SpeechSynthesis',
  'DocumentIntelligence',
]);

export const CONNECTION_SECTION_NAMES = Array.from(
  new Set(CONNECTION_OWNERSHIP_CATEGORIES.flatMap((category) => category.sectionNames))
);

export const CONNECTION_SECTION_NAME_SET = new Set(CONNECTION_SECTION_NAMES);
