import { describe, expect, it } from 'vitest';
import type { ModelDto } from '../../../types/guides';
import type { ChatDefaultsDto } from '../../../types/settings';
import {
  buildChatDefaultsModelChangeRequest,
  buildChatDefaultsUpdateRequest,
  buildChatModelConfigFromModelDefaults,
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

  it('builds config from model recommended defaults on model selection', () => {
    const model: ModelDto = {
      modelId: 'qwen',
      displayName: 'Qwen',
      samplingParameterPolicy: [
        { key: 'temperature', label: 'Temperature', description: '', min: 0, max: 2, step: 0.1, recommendedDefault: 0.7, displayOrder: 0 },
        { key: 'top_p', label: 'Top P', description: '', min: 0, max: 1, step: 0.05, recommendedDefault: 0.8, displayOrder: 1 },
        { key: 'top_k', label: 'Top K', description: '', min: 1, max: 100, step: 1, recommendedDefault: 20, displayOrder: 2 },
        { key: 'presence_penalty', label: 'Presence', description: '', min: 0, max: 2, step: 0.1, recommendedDefault: 1.5, displayOrder: 3 },
      ],
      reasoningChoices: ['none', 'medium'],
      defaultReasoningChoice: 'medium',
    } as ModelDto;

    expect(buildChatModelConfigFromModelDefaults('qwen', model)).toEqual({
      modelId: 'qwen',
      temperature: 0.7,
      topP: 0.8,
      reasoningEffort: 'medium',
      samplingOverrides: {
        top_k: 20,
        presence_penalty: 1.5,
      },
    });
  });

  it('normalizes config against declared sampling policy keys', () => {
    const model: ModelDto = {
      id: 'gpt-4o',
      samplingParameterPolicy: [{ key: 'frequency_penalty', recommendedDefault: 0 }],
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

  it('seeds missing sampling override keys from model recommended defaults', () => {
    const model: ModelDto = {
      modelId: 'qwen',
      displayName: 'Qwen',
      samplingParameterPolicy: [
        { key: 'temperature', recommendedDefault: 0.7 },
        { key: 'top_p', recommendedDefault: 0.8 },
        { key: 'top_k', recommendedDefault: 20 },
        { key: 'presence_penalty', recommendedDefault: 1.5 },
      ],
    } as ModelDto;

    const normalized = normalizeChatModelConfigForModel(
      {
        modelId: 'qwen',
        temperature: null,
        topP: null,
        samplingOverrides: {},
      },
      model
    );

    expect(normalized.temperature).toBe(0.7);
    expect(normalized.topP).toBe(0.8);
    expect(normalized.samplingOverrides).toEqual({
      top_k: 20,
      presence_penalty: 1.5,
    });
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

    const qwenModel: ModelDto = {
      modelId: 'qwen',
      displayName: 'Qwen',
      samplingParameterPolicy: [
        { key: 'temperature', recommendedDefault: 0.7 },
        { key: 'top_p', recommendedDefault: 0.8 },
        { key: 'presence_penalty', recommendedDefault: 1.5 },
      ],
    } as ModelDto;

    const modelChange = buildChatDefaultsModelChangeRequest(
      baseDefaults,
      'qwen',
      qwenModel,
      false
    );
    expect(modelChange.defaultModelId).toBe('qwen');
    expect(modelChange.overrideAllChatModels).toBe(false);
    expect(modelChange.temperature).toBe(0.7);
    expect(modelChange.topP).toBe(0.8);
    expect(modelChange.samplingParametersJson).toBe('{"presence_penalty":1.5}');

    const clearedModel = buildChatDefaultsUpdateRequest(
      baseDefaults,
      { modelId: '', temperature: null, topP: null, samplingOverrides: {} },
      false
    );
    expect(clearedModel.defaultModelId).toBeNull();
    expect(clearedModel.samplingParametersJson).toBeNull();
  });
});
