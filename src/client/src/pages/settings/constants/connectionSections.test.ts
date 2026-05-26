import { describe, expect, it } from 'vitest';
import { HUGGINGFACE_CHAT_MODEL_PROVIDER_ID, HUGGINGFACE_SECTION } from '../../../components/home/addAiServicesWizard/constants';
import { mapChatProviderToSection } from '../utils';
import { HIDDEN_CHAT_MODEL_PROVIDERS, HIDDEN_CLOUD_PROVIDER_SECTIONS } from './connectionSections';

describe('Hugging Face visibility and mapping parity', () => {
  it('does not hide hf chat provider option', () => {
    expect(HIDDEN_CHAT_MODEL_PROVIDERS.has(HUGGINGFACE_CHAT_MODEL_PROVIDER_ID)).toBe(false);
  });

  it('does not hide Hugging Face connection section', () => {
    expect(HIDDEN_CLOUD_PROVIDER_SECTIONS.has(HUGGINGFACE_SECTION)).toBe(false);
  });

  it('keeps chat provider section mapping synchronized for hf provider', () => {
    expect(mapChatProviderToSection(HUGGINGFACE_CHAT_MODEL_PROVIDER_ID)).toBe(HUGGINGFACE_SECTION);
  });
});
