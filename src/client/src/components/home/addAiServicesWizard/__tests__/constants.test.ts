import { describe, expect, it } from 'vitest';
import {
  HUGGINGFACE_CHAT_MODEL_PROVIDER_ID,
  HUGGINGFACE_DEFAULT_CHAT_MODEL_ID,
  HUGGINGFACE_DEFAULT_RUNTIME_PROFILE_ID,
  HUGGINGFACE_OPTIONAL_SERVICE_DEFAULTS,
  HUGGINGFACE_SECTION,
  HUGGINGFACE_SERVICE_PROVIDER_IDS,
  OPENROUTER_CHAT_MODEL_PROVIDER_ID,
  OPENROUTER_DEFAULT_CHAT_MODEL_ID,
  OPENROUTER_DEFAULT_RUNTIME_PROFILE_ID,
  OPENROUTER_OPTIONAL_SERVICE_DEFAULTS,
  OPENROUTER_SECTION,
  OPENROUTER_SERVICE_PROVIDER_IDS,
  WIZARD_PROVIDER_OPTIONS,
} from '../constants';
import { mapChatProviderToSection } from '../../../../pages/settings/utils';

describe('Add AI Services wizard Hugging Face constants', () => {
  it('includes Hugging Face in provider options', () => {
    expect(WIZARD_PROVIDER_OPTIONS.some((option) => option.id === 'huggingface')).toBe(true);
  });

  it('keeps hf chat provider mapping aligned with settings section map', () => {
    expect(mapChatProviderToSection(HUGGINGFACE_CHAT_MODEL_PROVIDER_ID)).toBe(HUGGINGFACE_SECTION);
  });

  it('defines hf non-chat provider ids for all optional services', () => {
    expect(HUGGINGFACE_SERVICE_PROVIDER_IDS).toEqual({
      Embeddings: 'Embeddings.HuggingFace.Inference',
      ImageGeneration: 'ImageGeneration.HuggingFace.Inference',
      SpeechTranscription: 'SpeechTranscription.HuggingFace.Inference',
      SpeechSynthesis: 'SpeechSynthesis.HuggingFace.Inference',
    });
  });

  it('defaults hf speech synthesis model to chatterbox', () => {
    expect(HUGGINGFACE_OPTIONAL_SERVICE_DEFAULTS.speechSynthesisModelId).toBe('ResembleAI/chatterbox');
  });

  it('defaults hf chat model to provider-stack profile seed', () => {
    expect(HUGGINGFACE_DEFAULT_CHAT_MODEL_ID).toBe('zai-org/GLM-5.2');
  });

  it('defines hf default runtime profile id', () => {
    expect(HUGGINGFACE_DEFAULT_RUNTIME_PROFILE_ID).toBe('huggingface_chat_standard');
  });
});

describe('Add AI Services wizard OpenRouter constants', () => {
  it('includes OpenRouter in provider options', () => {
    expect(WIZARD_PROVIDER_OPTIONS.some((option) => option.id === 'openrouter')).toBe(true);
  });

  it('keeps openrouter chat provider mapping aligned with settings section map', () => {
    expect(mapChatProviderToSection(OPENROUTER_CHAT_MODEL_PROVIDER_ID)).toBe(OPENROUTER_SECTION);
  });

  it('defines openrouter non-chat provider ids for all optional services', () => {
    expect(OPENROUTER_SERVICE_PROVIDER_IDS).toEqual({
      Embeddings: 'Embeddings.OpenRouter.Embeddings',
      ImageGeneration: 'ImageGeneration.OpenRouter.Image',
      SpeechTranscription: 'SpeechTranscription.OpenRouter.Audio',
      SpeechSynthesis: 'SpeechSynthesis.OpenRouter.Tts',
    });
  });

  it('locks openrouter optional defaults to the planned model set', () => {
    expect(OPENROUTER_OPTIONAL_SERVICE_DEFAULTS).toMatchObject({
      embeddingsModelId: 'nvidia/llama-nemotron-embed-vl-1b-v2:free',
      imagesModelId: 'recraft/recraft-v4',
      speechTranscriptionModelId: 'nvidia/parakeet-tdt-0.6b-v3',
      speechSynthesisModelId: 'hexgrad/kokoro-82m',
    });
  });

  it('defines openrouter chat defaults', () => {
    expect(OPENROUTER_DEFAULT_CHAT_MODEL_ID).toBe('minimax/minimax-m3');
    expect(OPENROUTER_DEFAULT_RUNTIME_PROFILE_ID).toBe('openai_chat_standard');
  });
});
