import { describe, expect, it } from 'vitest';
import type { ModelDto } from '../../../types/guides';
import {
  getReasoningChoicesForModel,
  modelDeclaresSamplingParameter,
  normalizeReasoningEffortForModel,
  normalizeSamplingValueForModel,
} from '../reasoning';

function makeModel(overrides: Partial<ModelDto> = {}): ModelDto {
  return {
    modelId: 'test-model',
    displayName: 'Test',
    provider: 'openai-chat',
    ...overrides,
  } as ModelDto;
}

describe('getReasoningChoicesForModel', () => {
  it('returns trimmed reasoningChoices when present on the model', () => {
    const model = makeModel({ reasoningChoices: [' low ', 'high', ''] });
    expect(getReasoningChoicesForModel(model)).toEqual(['low', 'high']);
  });

  it('parses reasoningChoicesJson when reasoningChoices is empty', () => {
    const model = makeModel({ reasoningChoicesJson: '["minimal", "medium"]' });
    expect(getReasoningChoicesForModel(model)).toEqual(['minimal', 'medium']);
  });

  it('returns empty array for invalid or non-array json', () => {
    expect(getReasoningChoicesForModel(makeModel({ reasoningChoicesJson: '{"not":"array"}' }))).toEqual([]);
    expect(getReasoningChoicesForModel(makeModel({ reasoningChoicesJson: 'not-json' }))).toEqual([]);
    expect(getReasoningChoicesForModel(undefined)).toEqual([]);
  });
});

describe('normalizeReasoningEffortForModel', () => {
  it('returns undefined when the model has no reasoning choices', () => {
    expect(normalizeReasoningEffortForModel(makeModel(), 'high')).toBeUndefined();
  });

  it('uses defaultReasoningChoice when current is blank', () => {
    const model = makeModel({
      reasoningChoices: ['Low', 'High'],
      defaultReasoningChoice: 'high',
    });
    expect(normalizeReasoningEffortForModel(model, null)).toBe('High');
  });

  it('falls back to first choice when default does not match', () => {
    const model = makeModel({
      reasoningChoices: ['alpha', 'beta'],
      defaultReasoningChoice: 'missing',
    });
    expect(normalizeReasoningEffortForModel(model, '')).toBe('alpha');
  });

  it('matches requested choice case-insensitively', () => {
    const model = makeModel({ reasoningChoices: ['Low', 'High'] });
    expect(normalizeReasoningEffortForModel(model, 'HIGH')).toBe('High');
  });

  it('falls back to first choice when requested value is unknown', () => {
    const model = makeModel({ reasoningChoices: ['Low', 'High'] });
    expect(normalizeReasoningEffortForModel(model, 'extreme')).toBe('Low');
  });
});

describe('modelDeclaresSamplingParameter', () => {
  it('detects declared sampling parameters case-insensitively', () => {
    const model = makeModel({
      samplingParameterPolicy: [{ key: 'Temperature', recommendedDefault: 0.7 }],
    });
    expect(modelDeclaresSamplingParameter(model, 'temperature')).toBe(true);
    expect(modelDeclaresSamplingParameter(model, 'top_p')).toBe(false);
    expect(modelDeclaresSamplingParameter(undefined, 'temperature')).toBe(false);
  });
});

describe('normalizeSamplingValueForModel', () => {
  it('returns null when the parameter is not declared', () => {
    expect(normalizeSamplingValueForModel(makeModel(), 'temperature', 0.5)).toBeNull();
  });

  it('returns current numeric value when valid', () => {
    const model = makeModel({
      samplingParameterPolicy: [{ key: 'temperature', recommendedDefault: 0.7 }],
    });
    expect(normalizeSamplingValueForModel(model, 'temperature', 0.3)).toBe(0.3);
  });

  it('returns recommended default when current is missing or NaN', () => {
    const model = makeModel({
      samplingParameterPolicy: [{ key: 'temperature', recommendedDefault: 0.7 }],
    });
    expect(normalizeSamplingValueForModel(model, 'temperature', null)).toBe(0.7);
    expect(normalizeSamplingValueForModel(model, 'temperature', Number.NaN)).toBe(0.7);
  });
});
