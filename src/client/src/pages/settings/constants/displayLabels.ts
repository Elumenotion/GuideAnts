const CONNECTION_SECTION_LABELS: Record<string, string> = {
  AzureOpenAI: 'Microsoft Foundry',
  AzureSpeechService: 'Microsoft Foundry Speech Services',
  AzureOpenAiImages: 'Microsoft Foundry Images',
  AzureOpenAiEmbedding: 'Microsoft Foundry Embeddings',
  AzureDocumentIntelligence: 'Microsoft Foundry Document Intelligence',
  OpenAI: 'OpenAI',
};

const SERVICE_LABELS: Record<string, string> = {
  SpeechTranscription: 'Speech Transcription',
  ImageGeneration: 'Image Generation',
  SpeechSynthesis: 'Speech Synthesis',
  DocumentIntelligence: 'Document Intelligence',
  Embeddings: 'Embeddings',
};

const SERVICE_PROVIDER_LABELS: Record<string, string> = {
  'SpeechTranscription.AzureSpeech.Batch': 'Microsoft Foundry Speech Services (Batch)',
  'SpeechSynthesis.AzureSpeech.Ssml': 'Microsoft Foundry Speech Services (SSML)',
  'ImageGeneration.AzureOpenAI.Images': 'Microsoft Foundry Images',
  'Embeddings.AzureOpenAI.Embedding': 'Microsoft Foundry Embeddings',
  'DocumentIntelligence.Azure.DocumentIntelligence': 'Microsoft Foundry Document Intelligence',
  'SpeechTranscription.LocalAsr.Http': 'Local ASR HTTP',
  'SpeechSynthesis.LocalTts.Http': 'Local Kokoro TTS',
  'ImageGeneration.LocalSd.Http': 'Local Stable Diffusion HTTP',
  'Embeddings.LocalEmb.Http': 'Local Embedding HTTP',
  'DocumentIntelligence.LocalDocling.Http': 'Local Docling HTTP',
  'SpeechTranscription.Google.SpeechToText': 'Google Gemini Audio',
  'SpeechSynthesis.Google.TextToSpeech': 'Google Gemini TTS',
  'ImageGeneration.Google.Imagen': 'Google Gemini Image',
  'Embeddings.Google.Embedding': 'Google Gemini Embeddings',
  'SpeechTranscription.HuggingFace.Inference': 'Hugging Face ASR',
  'SpeechSynthesis.HuggingFace.Inference': 'Hugging Face TTS',
  'ImageGeneration.HuggingFace.Inference': 'Hugging Face Image',
  'Embeddings.HuggingFace.Inference': 'Hugging Face Embeddings',
  'SpeechTranscription.OpenRouter.Audio': 'OpenRouter Audio',
  'SpeechSynthesis.OpenRouter.Tts': 'OpenRouter TTS',
  'ImageGeneration.OpenRouter.Image': 'OpenRouter Image',
  'Embeddings.OpenRouter.Embeddings': 'OpenRouter Embeddings',
  'SpeechTranscription.OpenAI.Audio': 'OpenAI Whisper',
  'SpeechSynthesis.OpenAI.Tts': 'OpenAI TTS',
  'ImageGeneration.OpenAI.Images': 'OpenAI Images',
  'Embeddings.OpenAI.Embedding': 'OpenAI Embeddings',
};

const CATALOG_PROVIDER_LABELS: Record<string, string> = {
  'openai-chat': 'OpenAI (Completions)',
  'openai-responses': 'OpenAI (Responses)',
  'azure-openai-chat': 'Microsoft Foundry (Completions)',
  'azure-openai-responses': 'Microsoft Foundry (Responses)',
  anthropic: 'Anthropic',
  'llama-cpp': 'Llama.cpp',
  'google-gemini-chat': 'Google Gemini API',
  'hf-inference-chat': 'Hugging Face Inference',
  'openrouter-chat': 'OpenRouter',
};

const COMMON_FIELD_LABELS: Record<string, string> = {
  ApiVersion: 'API Version',
  AsyncStatusPollIntervalMs: 'Poll Interval (ms)',
  Deployment: 'Deployment',
  Dimensions: 'Dimensions',
  DoclingDoOcr: 'Do OCR',
  DoclingDoCodeEnrichment: 'Code Enrichment',
  DoclingDoFormulaEnrichment: 'Formula Enrichment',
  DoclingDoPictureClassification: 'Picture Classification',
  DoclingDoPictureDescription: 'Picture Description',
  DoclingApiKey: 'Docling API Key',
  DoclingForceOcr: 'Force OCR',
  DoclingImageExportMode: 'Image Export Mode',
  DoclingOcrLang: 'OCR Language',
  DoclingOcrPreset: 'OCR Preset',
  DoclingPdfBackend: 'PDF Backend',
  DoclingPictureDescriptionPreset: 'Picture Description Preset',
  DoclingPipeline: 'Pipeline',
  DoclingTableCellMatching: 'Table Cell Matching',
  DoclingTableMode: 'Table Mode',
  EditModelDeployment: 'Edit Model Deployment',
  LocalMinIntervalMs: 'Local Min Interval (ms)',
  LocalOutputFormat: 'Default output format',
  LanguageCode: 'Language Code',
  MaxAudioBytes: 'Max Audio Bytes',
  MaxConcurrentConversions: 'Max Concurrent Conversions',
  MaxRetries: 'Max Retries',
  ModelId: 'Model ID',
  RequestPresetJson: 'Request Preset JSON',
  TimeoutSeconds: 'Timeout Seconds',
  VoiceName: 'Voice Name',
  Speed: 'Speed',
  language: 'Language',
  modelHint: 'Model Hint',
  modelId: 'Model',
  rate: 'Rate',
  requestPresetJson: 'Request Preset',
  voice: 'Voice',
};

const PROVIDER_FIELD_LABEL_OVERRIDES: Record<string, Record<string, string>> = {
  'Embeddings.Google.Embedding': { ModelId: 'Embedding Model ID' },
  'Embeddings.HuggingFace.Inference': { ModelId: 'Embedding Model ID' },
  'Embeddings.OpenRouter.Embeddings': { ModelId: 'Embedding Model ID' },
  'ImageGeneration.Google.Imagen': { ModelId: 'Image Model ID' },
  'ImageGeneration.HuggingFace.Inference': { ModelId: 'Text-to-Image Model ID', TextToImageModelId: 'Text-to-Image Model ID', ImageToImageModelId: 'Image-to-Image Model ID' },
  'ImageGeneration.OpenRouter.Image': { ModelId: 'Image Model ID' },
  'SpeechTranscription.Google.SpeechToText': { ModelId: 'Transcription Model ID' },
  'SpeechTranscription.HuggingFace.Inference': { ModelId: 'ASR Model ID' },
  'SpeechTranscription.OpenRouter.Audio': { ModelId: 'Transcription Model ID' },
  'SpeechSynthesis.Google.TextToSpeech': { ModelId: 'TTS Model ID' },
  'SpeechSynthesis.LocalTts.Http': { VoiceName: 'Kokoro Voice' },
  'SpeechSynthesis.HuggingFace.Inference': { ModelId: 'TTS Model ID' },
  'SpeechSynthesis.OpenRouter.Tts': { ModelId: 'TTS Model ID' },
  'SpeechTranscription.OpenAI.Audio': { ModelId: 'Transcription Model ID' },
  'SpeechSynthesis.OpenAI.Tts': { ModelId: 'TTS Model ID' },
  'ImageGeneration.OpenAI.Images': { ModelId: 'Image Model ID' },
  'Embeddings.OpenAI.Embedding': { ModelId: 'Embedding Model ID' },
};

const COMMON_FIELD_HELP_TEXT: Record<string, string> = {
  ApiVersion: 'Date-style API version (for example 2025-04-01-preview).',
  AsyncStatusPollIntervalMs: 'Polling interval for async operations in milliseconds.',
  Deployment: 'Deployment name used by this route.',
  Dimensions: 'Output embedding dimensions. Supported by text-embedding-3-* models only.',
  EditModelDeployment: 'Deployment name used for edit requests.',
  LocalMinIntervalMs: 'Minimum delay between local requests in milliseconds.',
  LocalOutputFormat: 'Output format for local image generation.',
  MaxAudioBytes: 'Maximum accepted audio payload size in bytes.',
  MaxConcurrentConversions: 'Maximum concurrent conversion jobs.',
  MaxRetries: 'Retry count for transient failures.',
  TextToImageModelId: 'Hugging Face model used for text-to-image generation (e.g. Tongyi-MAI/Z-Image-Turbo).',
  ImageToImageModelId: 'Hugging Face model used for image-to-image editing (e.g. black-forest-labs/FLUX.2-dev).',
  ModelId: 'Provider model id routed through this mode.',
  TimeoutSeconds: 'Request timeout in seconds.',
  VoiceName: 'Voice identifier used for synthesis.',
  LanguageCode: 'Language code used for synthesis.',
  Speed: 'Speech speed multiplier.',
};

const PROVIDER_FIELD_HELP_OVERRIDES: Record<string, Record<string, string>> = {
  'SpeechSynthesis.LocalTts.Http': {
    VoiceName: 'Local TTS uses Kokoro. The language is inferred from the selected voice.',
  },
};

const RUNTIME_DEPENDENCY_LABELS: Record<string, string> = {
  'LlamaCpp:BaseUrl': 'Llama.cpp Server Base URL',
  'LocalServiceHosts:SpeechTranscriptionBaseUrl': 'Speech Transcription Base URL',
  'LocalServiceHosts:SpeechSynthesisBaseUrl': 'Speech Synthesis Base URL',
  'LocalServiceHosts:ImageGenerationBaseUrl': 'Image Generation Base URL',
  'LocalServiceHosts:EmbeddingsBaseUrl': 'Embeddings Base URL',
  'LocalServiceHosts:MediaBaseUrl': 'Media Extraction Base URL',
  'LocalServiceHosts:DocumentIntelligenceBaseUrl': 'Markdown Extraction Base URL',
};

const RUNTIME_CHANGE_HINT_DEFAULT =
  'Runtime-owned value. Change in appsettings, environment variables, or docker-compose LocalServiceHosts__* settings.';

const RUNTIME_DEPENDENCY_CHANGE_HINTS: Record<string, string> = {
  'LlamaCpp:BaseUrl': RUNTIME_CHANGE_HINT_DEFAULT,
  'LocalServiceHosts:SpeechTranscriptionBaseUrl': RUNTIME_CHANGE_HINT_DEFAULT,
  'LocalServiceHosts:SpeechSynthesisBaseUrl': RUNTIME_CHANGE_HINT_DEFAULT,
  'LocalServiceHosts:ImageGenerationBaseUrl': RUNTIME_CHANGE_HINT_DEFAULT,
  'LocalServiceHosts:EmbeddingsBaseUrl': RUNTIME_CHANGE_HINT_DEFAULT,
  'LocalServiceHosts:MediaBaseUrl': RUNTIME_CHANGE_HINT_DEFAULT,
  'LocalServiceHosts:DocumentIntelligenceBaseUrl': RUNTIME_CHANGE_HINT_DEFAULT,
};

export function humanizePresentationKey(value: string): string {
  return value
    .replace(/[:._-]+/g, ' ')
    .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
    .replace(/\s+/g, ' ')
    .trim();
}

export function getConnectionSectionDisplayName(sectionName: string, fallback?: string): string {
  return CONNECTION_SECTION_LABELS[sectionName] ?? fallback ?? humanizePresentationKey(sectionName);
}

export function getServiceDisplayName(serviceId: string): string {
  return SERVICE_LABELS[serviceId] ?? humanizePresentationKey(serviceId);
}

export function getServiceProviderDisplayName(providerId: string, fallback?: string): string {
  return SERVICE_PROVIDER_LABELS[providerId] ?? fallback ?? humanizePresentationKey(providerId);
}

export function getCatalogProviderDisplayName(provider: string): string {
  return CATALOG_PROVIDER_LABELS[provider] ?? provider;
}

export function getProviderFieldLabel(providerId: string, fieldName: string): string {
  return (
    PROVIDER_FIELD_LABEL_OVERRIDES[providerId]?.[fieldName]
    ?? COMMON_FIELD_LABELS[fieldName]
    ?? humanizePresentationKey(fieldName)
  );
}

export function getProviderFieldHelpText(providerId: string, fieldName: string): string {
  return (
    PROVIDER_FIELD_HELP_OVERRIDES[providerId]?.[fieldName]
    ?? COMMON_FIELD_HELP_TEXT[fieldName]
    ?? ''
  );
}

export function getRuntimeDependencyDisplayName(key: string): string {
  return RUNTIME_DEPENDENCY_LABELS[key] ?? humanizePresentationKey(key);
}

export function getRuntimeDependencyChangeHint(key: string): string {
  return RUNTIME_DEPENDENCY_CHANGE_HINTS[key] ?? RUNTIME_CHANGE_HINT_DEFAULT;
}
