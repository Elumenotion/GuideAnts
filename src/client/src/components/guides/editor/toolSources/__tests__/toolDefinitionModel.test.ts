import { describe, expect, it } from 'vitest';
import {
  createEmptyToolDefinitionModel,
  isInjectedParameter,
  mergeParameters,
  partitionParameters,
} from '../toolDefinitionModel';

describe('toolDefinitionModel', () => {
  it('creates source-kind-specific defaults', () => {
    expect(createEmptyToolDefinitionModel('web-api').execution).toEqual({
      method: 'get',
      path: '/new-endpoint',
    });
    expect(createEmptyToolDefinitionModel('client-actions').execution.clientActionKey).toBe(
      'Bridge.NewAction',
    );
    expect(createEmptyToolDefinitionModel('sandbox-module').execution.sandboxFunctionName).toBe(
      'new_function',
    );
    expect(createEmptyToolDefinitionModel('local-function').operationId).toBe('Invoke');
    expect(createEmptyToolDefinitionModel('mcp-connection').execution.path).toBe('/tools/example');
  });

  it('partitions injected parameters by defaults or single-value enums', () => {
    const params = [
      { name: 'visible', type: 'string' as const, required: true },
      { name: 'hiddenDefault', type: 'boolean' as const, required: false, default: 'true' },
      { name: 'hiddenEnum', type: 'string' as const, required: false, enumValues: ['only'] },
    ];

    expect(isInjectedParameter({ default: 'x' })).toBe(true);
    expect(isInjectedParameter({ enumValues: ['only'] })).toBe(true);
    expect(isInjectedParameter({ enumValues: ['a', 'b'] })).toBe(false);

    const partitioned = partitionParameters(params);
    expect(partitioned.visible.map((p) => p.name)).toEqual(['visible']);
    expect(partitioned.injected.map((p) => p.name)).toEqual(['hiddenDefault', 'hiddenEnum']);
    expect(mergeParameters(partitioned.visible, partitioned.injected)).toHaveLength(3);
  });
});
