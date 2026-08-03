import { describe, expect, it } from 'vitest';
import { resolveParameterSurfaceSeed } from '../parameterSurfaceSeeds';
import { normalizeParameterSurface, parseReasoningChoicesJson } from '../parameterSurface';

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
});
