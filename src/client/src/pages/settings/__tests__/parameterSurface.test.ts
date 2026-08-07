import { describe, expect, it } from 'vitest';
import { resolveParameterSurfaceSeed } from '../parameterSurfaceSeeds';
import {
  normalizeParameterSurface,
  parseReasoningChoicesJson,
  providerSupportsRowOwnedRequestShaping,
  validateOptionalJsonObject,
} from '../parameterSurface';

describe('parameter surface contract', () => {
  it('resolves known cloud model seed shapes without runtime profile pointers', () => {
    const chat = resolveParameterSurfaceSeed('openai_chat_standard');
    expect(chat.samplingParametersJson).toContain('temperature');
    expect(chat.reasoningChoicesJson).toBe('');

    const reasoning = resolveParameterSurfaceSeed('openai_responses_reasoning');
    expect(reasoning.samplingParametersJson).toBe('{}');
    expect(parseReasoningChoicesJson(reasoning.reasoningChoicesJson)).toEqual([
      'none',
      'low',
      'medium',
      'high',
      'xhigh',
    ]);
  });

  it('normalizes reasoning choices json to a stable array string', () => {
    const normalized = normalizeParameterSurface({
      samplingParametersJson: '{}',
      reasoningChoicesJson: '[" high ", "low", "low"]',
    });
    expect(normalized.reasoningChoicesJson).toBe('["high","low"]');
  });

  it('offers row-owned request shaping only for providers whose clients apply it', () => {
    expect(providerSupportsRowOwnedRequestShaping('openrouter-chat')).toBe(true);
    expect(providerSupportsRowOwnedRequestShaping('hf-inference-chat')).toBe(true);
    expect(providerSupportsRowOwnedRequestShaping('openai-chat')).toBe(false);
    expect(providerSupportsRowOwnedRequestShaping('anthropic')).toBe(false);
  });

  it('treats blank behavior json as unconfigured and rejects non-objects', () => {
    expect(validateOptionalJsonObject('', 'Thinking control JSON')).toBeNull();
    expect(validateOptionalJsonObject('{}', 'Thinking control JSON')).toBeNull();
    expect(validateOptionalJsonObject('{"defaultChoice":"none"}', 'Thinking control JSON')).toBeNull();
    expect(validateOptionalJsonObject('[1,2]', 'Thinking control JSON')).toBe(
      'Thinking control JSON must be a JSON object.',
    );
    expect(validateOptionalJsonObject('{oops', 'Thinking control JSON')).toBe(
      'Thinking control JSON must be valid JSON.',
    );
  });
});
