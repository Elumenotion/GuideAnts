import { describe, it, expect } from 'vitest';
import {
  buildFragmentFromModel,
  buildFragmentJsonFromModel,
  createEditorBaseline,
  isAdvancedFragmentDirty,
  isFragmentDirty,
  parseFragmentToModel,
  isNonRoundtrippableOperation,
  validateToolDefinitionModel,
} from '../operationFragmentBuilder';
import { createEmptyToolDefinitionModel } from '../toolDefinitionModel';

describe('operationFragmentBuilder', () => {
  it('round-trips a simple web operation', () => {
    const model = createEmptyToolDefinitionModel('web-api');
    model.operationId = 'getPing';
    model.summary = 'Ping the API';
    model.parameters = [
      { name: 'q', type: 'string', required: true, description: 'Query' },
    ];

    const fragment = buildFragmentFromModel(model);
    expect(fragment.path).toBe('/new-endpoint');
    expect(fragment.method).toBe('get');
    expect(fragment.operation.operationId).toBe('getPing');

    const parsed = parseFragmentToModel(buildFragmentJsonFromModel(model), 'web-api');
    expect(parsed.isCustomMode).toBe(false);
    expect(parsed.model.operationId).toBe('getPing');
    expect(parsed.model.parameters).toHaveLength(1);
    expect(parsed.model.parameters[0].name).toBe('q');
  });

  it('partitions injected parameters with default or single enum', () => {
    const model = createEmptyToolDefinitionModel('client-actions');
    model.parameters = [
      { name: 'visible', type: 'string', required: true },
    ];
    model.injectedParameters = [
      { name: 'hidden', type: 'boolean', required: false, default: 'true' },
    ];

    const fragment = buildFragmentFromModel(model);
    const schema =
      (fragment.operation.requestBody as Record<string, unknown>)?.content &&
      (
        (
          (fragment.operation.requestBody as Record<string, unknown>).content as Record<
            string,
            unknown
          >
        )['application/json'] as Record<string, unknown>
      ).schema as Record<string, unknown>;

    expect(schema.properties).toHaveProperty('visible');
    expect(schema.properties).toHaveProperty('hidden');

    const parsed = parseFragmentToModel(buildFragmentJsonFromModel(model), 'client-actions');
    expect(parsed.model.parameters.map((p) => p.name)).toEqual(['visible']);
    expect(parsed.model.injectedParameters.map((p) => p.name)).toEqual(['hidden']);
  });

  it('detects non-roundtrippable operations with $ref', () => {
    const reason = isNonRoundtrippableOperation({
      operationId: 'x',
      responses: { '200': { description: 'OK' } },
      requestBody: {
        content: {
          'application/json': {
            schema: { $ref: '#/components/schemas/Foo' },
          },
        },
      },
    });
    expect(reason).toContain('$ref');
  });

  it('enters custom mode when spec has x-guideants-custom-descriptor', () => {
    const model = createEmptyToolDefinitionModel('web-api');
    const fragmentJson = buildFragmentJsonFromModel(model);
    const spec = JSON.stringify({ 'x-guideants-custom-descriptor': true });
    const parsed = parseFragmentToModel(fragmentJson, 'web-api', spec);
    expect(parsed.isCustomMode).toBe(true);
  });

  it('validates required operationId', () => {
    const model = createEmptyToolDefinitionModel('web-api');
    model.operationId = '';
    const errors = validateToolDefinitionModel(model);
    expect(errors.operationId).toBeDefined();
  });

  it('builds sandbox execution mapping', () => {
    const model = createEmptyToolDefinitionModel('sandbox-module');
    model.execution.sandboxFunctionName = 'create_presentation';
    model.execution.path = '/create_presentation';
    model.operationId = 'create_presentation';

    const fragment = buildFragmentFromModel(model);
    expect(fragment.path).toBe('/create_presentation');
    expect(fragment.method).toBe('post');
  });

  it('treats freshly opened guided fragment as not dirty', () => {
    const model = createEmptyToolDefinitionModel('web-api');
    model.operationId = 'doThing';
    model.summary = 'Do a thing';
    const fragmentJson = buildFragmentJsonFromModel(model);

    const baseline = createEditorBaseline(fragmentJson, 'web-api');
    expect(isFragmentDirty(baseline.baselineFragmentJson, baseline.model)).toBe(false);
    expect(isAdvancedFragmentDirty(baseline.baselineFragmentJson, baseline.baselineFragmentJson)).toBe(
      false
    );
  });

  it('detects dirty state after model edit', () => {
    const model = createEmptyToolDefinitionModel('web-api');
    model.operationId = 'doThing';
    const fragmentJson = buildFragmentJsonFromModel(model);
    const baseline = createEditorBaseline(fragmentJson, 'web-api');

    const edited = { ...baseline.model, summary: 'Changed summary' };
    expect(isFragmentDirty(baseline.baselineFragmentJson, edited)).toBe(true);
  });

  it('does not mark stored fragment dirty when parse round-trip normalizes shape', () => {
    const storedFragment = JSON.stringify({
      path: '/ping',
      method: 'get',
      operation: {
        operationId: 'ping',
        summary: 'Ping',
        responses: { '200': { description: 'OK' } },
      },
    });

    const baseline = createEditorBaseline(storedFragment, 'web-api');
    expect(isFragmentDirty(baseline.baselineFragmentJson, baseline.model)).toBe(false);
  });
});
