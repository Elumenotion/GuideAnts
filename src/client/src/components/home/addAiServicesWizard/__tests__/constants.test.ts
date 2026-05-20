import { describe, expect, it } from 'vitest';
import {
  HUGGINGFACE_CHAT_MODEL_PROVIDER_ID,
  HUGGINGFACE_DEFAULT_CHAT_MODEL_ID,
  HUGGINGFACE_OPTIONAL_SERVICE_DEFAULTS,
  HUGGINGFACE_SECTION,
  HUGGINGFACE_SERVICE_PROVIDER_IDS,
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
    expect(HUGGINGFACE_DEFAULT_CHAT_MODEL_ID).toBe('deepseek-ai/DeepSeek-V4-Pro');
  });
});
