import { describe, expect, it } from 'vitest';
import {
  buildFragmentFromModel,
  detectInjectedParametersFromPreview,
  isAdvancedFragmentDirty,
  isNonRoundtrippableOperation,
  normalizeFragmentJson,
  parseFragmentToModel,
  syncExecutionPath,
  validateToolDefinitionModel,
} from '../operationFragmentBuilder';
import { createEmptyToolDefinitionModel } from '../toolDefinitionModel';

describe('operationFragmentBuilder extended', () => {
  it('flags non-roundtrippable operations with unsupported schema features', () => {
    const reason = isNonRoundtrippableOperation({
      operationId: 'broken',
      requestBody: {
        content: {
          'application/json': {
            schema: { $ref: '#/components/schemas/Thing' },
          },
        },
      },
    });

    expect(reason).toMatch(/\$ref/i);
  });

  it('enters custom mode when parsing fragments with unsupported schema', () => {
    const fragmentJson = JSON.stringify({
      path: '/custom',
      method: 'post',
      operation: {
        operationId: 'customOp',
        requestBody: {
          content: {
            'application/json': {
              schema: { oneOf: [{ type: 'string' }, { type: 'number' }] },
            },
          },
        },
        responses: { '200': { description: 'OK' } },
      },
    });

    const parsed = parseFragmentToModel(fragmentJson, 'web-api');
    expect(parsed.isCustomMode).toBe(true);
    expect(parsed.customReason).toMatch(/composition/i);
  });

  it('returns an empty injected-parameter list from preview JSON', () => {
    expect(
      detectInjectedParametersFromPreview(
        JSON.stringify({ function: { parameters: { properties: { hidden: { type: 'boolean' } } } } }),
      ),
    ).toEqual([]);
    expect(detectInjectedParametersFromPreview('not-json')).toEqual([]);
  });

  it('validates duplicate parameter names and invalid raw responses', () => {
    const model = createEmptyToolDefinitionModel('web-api');
    model.operationId = 'demo';
    model.parameters = [
      { name: 'shared', type: 'string', required: true },
      { name: 'shared', type: 'string', required: false },
    ];
    model.response.mode = 'raw';
    model.response.rawJson = '{bad-json';

    const errors = validateToolDefinitionModel(model);
    expect(errors['param-shared']).toMatch(/duplicate/i);
    expect(errors.responseRaw).toMatch(/invalid/i);
  });

  it('syncs client action execution paths and normalizes fragment JSON', () => {
    const model = createEmptyToolDefinitionModel('client-actions');
    model.execution.clientActionKey = 'refresh_view';
    model.operationId = 'refresh_view';

    const synced = syncExecutionPath(model, 'client-actions');
    const fragment = buildFragmentFromModel(synced);
    const normalized = normalizeFragmentJson(JSON.stringify(fragment));

    expect(synced.execution.path).toBe('refresh_view');
    expect(JSON.parse(normalized).operation.operationId).toBe('refresh_view');
  });

  it('detects advanced fragment dirty state after JSON edits', () => {
    const baseline = JSON.stringify({ path: '/ping', method: 'get', operation: { operationId: 'ping' } });
    const edited = JSON.stringify({ path: '/ping', method: 'get', operation: { operationId: 'ping-v2' } });

    expect(isAdvancedFragmentDirty(baseline, edited)).toBe(true);
    expect(isAdvancedFragmentDirty(baseline, baseline)).toBe(false);
  });
});
