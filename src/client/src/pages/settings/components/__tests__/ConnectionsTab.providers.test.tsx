import { describe, expect, it } from 'vitest';
import { mapChatProviderToSection } from '../../utils';

describe('Connections provider mappings', () => {
  it('maps google gemini chat to GoogleGeminiApi section', () => {
    expect(mapChatProviderToSection('google-gemini-chat')).toBe('GoogleGeminiApi');
  });

  it('maps hf inference chat to HuggingFace section', () => {
    expect(mapChatProviderToSection('hf-inference-chat')).toBe('HuggingFace');
  });

  it('maps openrouter chat to OpenRouter section', () => {
    expect(mapChatProviderToSection('openrouter-chat')).toBe('OpenRouter');
  });
});
