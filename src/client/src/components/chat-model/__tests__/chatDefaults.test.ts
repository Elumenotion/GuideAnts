import { describe, expect, it } from 'vitest';
import type { ModelDto } from '../../../types/guides';
import type { ChatDefaultsDto } from '../../../types/settings';
import {
  buildChatDefaultsModelChangeRequest,
  buildChatDefaultsUpdateRequest,
  chatDefaultsToConfig,
  normalizeChatModelConfigForModel,
  parseSamplingOverrides,
} from '../chatDefaults';

const baseDefaults: ChatDefaultsDto = {
  rowVersion: '1',
  defaultModelId: 'gpt-4o',
  temperature: 0.7,
  topP: 0.9,
  reasoningEffort: 'medium',
  samplingParametersJson: '{"frequency_penalty":0.2,"temperature":0.5}',
};

describe('chatDefaults', () => {
  it('parses sampling overrides and ignores invalid json shapes', () => {
    expect(parseSamplingOverrides(null)).toEqual({});
    expect(parseSamplingOverrides('not-json')).toEqual({});
    expect(parseSamplingOverrides('[]')).toEqual({});
    expect(parseSamplingOverrides('{"bad":"x","good":1}')).toEqual({ good: 1 });
  });

  it('maps defaults dto into config values', () => {
    expect(chatDefaultsToConfig(baseDefaults)).toEqual({
      modelId: 'gpt-4o',
      temperature: 0.7,
      topP: 0.9,
      reasoningEffort: 'medium',
      samplingOverrides: { frequency_penalty: 0.2, temperature: 0.5 },
    });
  });

  it('normalizes config against declared sampling policy keys', () => {
    const model: ModelDto = {
      id: 'gpt-4o',
      samplingParameterPolicy: [{ key: 'frequency_penalty' }],
    } as ModelDto;

    const normalized = normalizeChatModelConfigForModel(
      {
        modelId: 'gpt-4o',
        temperature: undefined,
        topP: undefined,
        samplingOverrides: {
          temperature: 0.4,
          top_p: 0.8,
          frequency_penalty: 0.3,
          ignored_key: 9,
        },
      },
      model
    );

    expect(normalized.samplingOverrides).toEqual({ frequency_penalty: 0.3 });
    expect(normalized.samplingOverrides.ignored_key).toBeUndefined();
  });

  it('builds update requests and model-change requests', () => {
    const update = buildChatDefaultsUpdateRequest(
      baseDefaults,
      {
        modelId: 'gpt-4o-mini',
        temperature: 0.5,
        topP: 0.8,
        reasoningEffort: 'low',
        samplingOverrides: { presence_penalty: 0.1 },
      },
      true
    );

    expect(update).toEqual({
      rowVersion: '1',
      defaultModelId: 'gpt-4o-mini',
      overrideAllChatModels: true,
      temperature: 0.5,
      topP: 0.8,
      reasoningEffort: 'low',
      samplingParametersJson: '{"presence_penalty":0.1}',
    });

    const modelChange = buildChatDefaultsModelChangeRequest(
      baseDefaults,
      'gpt-4o-mini',
      undefined,
      false
    );
    expect(modelChange.defaultModelId).toBe('gpt-4o-mini');
    expect(modelChange.overrideAllChatModels).toBe(false);

    const clearedModel = buildChatDefaultsUpdateRequest(
      baseDefaults,
      { modelId: '', temperature: null, topP: null, samplingOverrides: {} },
      false
    );
    expect(clearedModel.defaultModelId).toBeNull();
    expect(clearedModel.samplingParametersJson).toBeNull();
  });
});
