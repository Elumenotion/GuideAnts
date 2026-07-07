import { describe, expect, it } from 'vitest';
import {
  buildFragmentJsonFromModel,
  createEditorBaseline,
  isNonRoundtrippableOperation,
  listInjectedParameterNames,
  normalizeFragmentJson,
  parseFragmentToModel,
  syncExecutionPath,
  validateToolDefinitionModel,
} from '../operationFragmentBuilder';
import { createEmptyToolDefinitionModel } from '../toolDefinitionModel';

describe('operationFragmentBuilder comprehensive', () => {
  it('round-trips nested object and array request parameters', () => {
    const model = createEmptyToolDefinitionModel('web-api');
    model.operationId = 'complexOp';
    model.parameters = [
      {
        name: 'items',
        type: 'array',
        required: true,
        itemType: 'object',
        itemProperties: [{ name: 'id', type: 'string', required: true }],
        itemRequired: ['id'],
      },
      {
        name: 'meta',
        type: 'object',
        required: false,
        objectProperties: [
          { name: 'count', type: 'integer', required: true, default: '1', enumValues: ['1', '2'] },
        ],
        objectRequired: ['count'],
      },
    ];
    model.response = {
      mode: 'object',
      properties: [{ name: 'ok', type: 'boolean', required: true }],
      required: ['ok'],
    };

    const parsed = parseFragmentToModel(buildFragmentJsonFromModel(model), 'web-api');
    expect(parsed.isCustomMode).toBe(false);
    expect(parsed.model.parameters).toHaveLength(2);
    expect(parsed.model.parameters[0].itemProperties?.[0].name).toBe('id');
    expect(parsed.model.parameters[1].objectProperties?.[0].enumValues).toEqual(['1', '2']);
    expect(parsed.model.response.mode).toBe('object');
  });

  it('parses array and raw response schemas from stored fragments', () => {
    const arrayResponse = parseFragmentToModel(
      JSON.stringify({
        path: '/list',
        method: 'get',
        operation: {
          operationId: 'listItems',
          responses: {
            '200': {
              description: 'OK',
              content: {
                'application/json': {
                  schema: { type: 'array', items: { type: 'number' } },
                },
              },
            },
          },
        },
      }),
      'web-api',
    );
    expect(arrayResponse.model.response).toEqual({ mode: 'array', itemType: 'number' });

    const rawResponse = parseFragmentToModel(
      JSON.stringify({
        path: '/raw',
        method: 'get',
        operation: {
          operationId: 'rawShape',
          responses: {
            '200': {
              description: 'OK',
              content: {
                'application/json': {
                  schema: { type: 'string' },
                },
              },
            },
          },
        },
      }),
      'web-api',
    );
    expect(rawResponse.model.response.mode).toBe('raw');
  });

  it('flags OpenAPI parameter arrays and deeply nested schemas as non-roundtrippable', () => {
    expect(
      isNonRoundtrippableOperation({
        operationId: 'legacy',
        parameters: [{ name: 'q', in: 'query' }],
        responses: { '200': { description: 'OK' } },
      }),
    ).toMatch(/parameters array/i);

    expect(
      isNonRoundtrippableOperation({
        operationId: 'deep',
        requestBody: {
          content: {
            'application/json': {
              schema: {
                type: 'object',
                properties: {
                  nested: {
                    type: 'object',
                    properties: {
                      child: {
                        type: 'object',
                        properties: { leaf: { type: 'string' } },
                      },
                    },
                  },
                },
              },
            },
          },
        },
        responses: { '200': { description: 'OK' } },
      }),
    ).toMatch(/nesting exceeds/i);
  });

  it('syncs sandbox execution paths and lists injected parameter names', () => {
    const model = createEmptyToolDefinitionModel('sandbox-module');
    model.execution.sandboxFunctionName = 'run_job';
    model.injectedParameters = [{ name: 'hidden', type: 'boolean', required: false, default: 'true' }];

    const synced = syncExecutionPath(model, 'sandbox-module');
    expect(synced.operationId).toBe('run_job');
    expect(synced.execution.path).toBe('/run_job');
    expect(listInjectedParameterNames(synced)).toEqual(['hidden']);
  });

  it('validates identifier, path, and parameter name requirements', () => {
    const model = createEmptyToolDefinitionModel('web-api');
    model.operationId = '1bad';
    model.execution.path = '   ';
    model.parameters = [{ name: '   ', type: 'string', required: false }];

    const errors = validateToolDefinitionModel(model);
    expect(errors.operationId).toMatch(/identifier/i);
    expect(errors.path).toMatch(/required/i);
    expect(errors['param-   ']).toMatch(/required/i);
  });

  it('keeps custom baseline JSON and normalizes invalid fragment input', () => {
    const customFragment = JSON.stringify({
      path: '/custom',
      method: 'post',
      operation: {
        operationId: 'custom',
        requestBody: {
          content: {
            'application/json': {
              schema: { allOf: [{ type: 'object' }] },
            },
          },
        },
        responses: { '200': { description: 'OK' } },
      },
    });

    const baseline = createEditorBaseline(customFragment, 'web-api');
    expect(baseline.isCustomMode).toBe(true);
    expect(baseline.baselineFragmentJson).toBe(customFragment);
    expect(normalizeFragmentJson('  not-json  ')).toBe('not-json');
  });

  it('parses invalid fragment JSON safely', () => {
    const invalid = parseFragmentToModel('{bad-json', 'web-api');
    expect(invalid.isCustomMode).toBe(true);
    expect(invalid.customReason).toMatch(/invalid fragment json/i);
  });
});
