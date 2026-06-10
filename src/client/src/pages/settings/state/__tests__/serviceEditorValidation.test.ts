import { describe, expect, it } from 'vitest';
import type { ProviderEditorStateDto, ProviderFieldMetadataDto } from '../../../../types/settings';
import {
  buildSavePayload,
  hasValidationErrors,
  validateOperativeProviderFields,
} from '../serviceEditorValidation';

function makeProvider(
  operativeNames: string[],
  metadata: ProviderFieldMetadataDto[],
  fields: ProviderEditorStateDto['fields']
): ProviderEditorStateDto {
  return {
    providerId: 'Embeddings.AzureOpenAI.Embedding',
    providerKind: 'Cloud',
    providerSection: 'AzureOpenAiEmbedding',
    hasExplicitMode: true,
    isDefaultMode: true,
    connectionConfigured: true,
    connectionMissingFields: [],
    canActivate: true,
    activationBlockers: [],
    fields,
    runtimeDependencies: [],
    operativeFields: operativeNames,
    diagnosticFields: [],
    fieldMetadata: metadata,
  };
}

describe('validateOperativeProviderFields', () => {
  it('requires non-secret operative fields from stored values', () => {
    const provider = makeProvider(
      ['Endpoint'],
      [
        {
          name: 'Endpoint',
          kind: 'url',
          required: true,
          enumOptions: null,
          operative: true,
        },
      ],
      {
        Endpoint: { name: 'Endpoint', value: '', isSecret: false, hasValue: false },
      },
    );
    expect(validateOperativeProviderFields(provider, {}).Endpoint).toBeDefined();
  });

  it('rejects non-finite integer operative fields', () => {
    const provider = makeProvider(
      ['TimeoutSeconds'],
      [
        {
          name: 'TimeoutSeconds',
          kind: 'int',
          required: false,
          enumOptions: null,
          operative: true,
        },
      ],
      {
        TimeoutSeconds: { name: 'TimeoutSeconds', value: null, isSecret: false, hasValue: false },
      },
    );
    expect(validateOperativeProviderFields(provider, { TimeoutSeconds: 'abc' }).TimeoutSeconds).toBeDefined();
  });

  it('requires secret when no stored value', () => {
    const provider = makeProvider(
      ['ApiKey'],
      [
        {
          name: 'ApiKey',
          kind: 'secret',
          required: true,
          enumOptions: null,
          operative: true,
        },
      ],
      {
        ApiKey: { name: 'ApiKey', value: null, isSecret: true, hasValue: false },
      }
    );
    const errors = validateOperativeProviderFields(provider, {});
    expect(errors.ApiKey).toBeDefined();
  });

  it('accepts secret when stored without new value', () => {
    const provider = makeProvider(
      ['ApiKey'],
      [
        {
          name: 'ApiKey',
          kind: 'secret',
          required: true,
          enumOptions: null,
          operative: true,
        },
      ],
      {
        ApiKey: { name: 'ApiKey', value: null, isSecret: true, hasValue: true },
      }
    );
    const errors = validateOperativeProviderFields(provider, {});
    expect(errors.ApiKey).toBeUndefined();
  });

  it('accepts empty optional Dimensions', () => {
    const provider = makeProvider(
      ['Dimensions'],
      [
        {
          name: 'Dimensions',
          kind: 'int',
          required: false,
          enumOptions: null,
          operative: true,
        },
      ],
      {
        Dimensions: { name: 'Dimensions', value: null, isSecret: false, hasValue: false },
      }
    );
    expect(validateOperativeProviderFields(provider, {}).Dimensions).toBeUndefined();
    expect(validateOperativeProviderFields(provider, { Dimensions: '' }).Dimensions).toBeUndefined();
  });

  it('rejects non-positive Dimensions', () => {
    const provider = makeProvider(
      ['Dimensions'],
      [
        {
          name: 'Dimensions',
          kind: 'int',
          required: false,
          enumOptions: null,
          operative: true,
        },
      ],
      {
        Dimensions: { name: 'Dimensions', value: null, isSecret: false, hasValue: false },
      }
    );
    expect(validateOperativeProviderFields(provider, { Dimensions: '0' }).Dimensions).toBeDefined();
    expect(validateOperativeProviderFields(provider, { Dimensions: '-5' }).Dimensions).toBeDefined();
    expect(validateOperativeProviderFields(provider, { Dimensions: '1536' }).Dimensions).toBeUndefined();
  });

  it('validates url, enum, and numeric operative fields', () => {
    const provider = makeProvider(
      ['Endpoint', 'Mode', 'TimeoutSeconds', 'LocalMinIntervalMs', 'MaxConcurrentConversions'],
      [
        { name: 'Endpoint', kind: 'url', required: true, enumOptions: null, operative: true },
        { name: 'Mode', kind: 'enum', required: true, enumOptions: ['fast', 'accurate'], operative: true },
        { name: 'TimeoutSeconds', kind: 'int', required: false, enumOptions: null, operative: true },
        { name: 'LocalMinIntervalMs', kind: 'int', required: false, enumOptions: null, operative: true },
        { name: 'MaxConcurrentConversions', kind: 'int', required: false, enumOptions: null, operative: true },
      ],
      {
        Endpoint: { name: 'Endpoint', value: '', isSecret: false, hasValue: false },
        Mode: { name: 'Mode', value: 'fast', isSecret: false, hasValue: true },
        TimeoutSeconds: { name: 'TimeoutSeconds', value: null, isSecret: false, hasValue: false },
        LocalMinIntervalMs: { name: 'LocalMinIntervalMs', value: null, isSecret: false, hasValue: false },
        MaxConcurrentConversions: {
          name: 'MaxConcurrentConversions',
          value: null,
          isSecret: false,
          hasValue: false,
        },
      },
    );

    const errors = validateOperativeProviderFields(provider, {
      Endpoint: 'not-a-url',
      Mode: 'slow',
      TimeoutSeconds: '0',
      LocalMinIntervalMs: '-1',
      MaxConcurrentConversions: '0',
    });

    expect(errors.Endpoint).toBeDefined();
    expect(errors.Mode).toContain('fast');
    expect(errors.TimeoutSeconds).toBeDefined();
    expect(errors.LocalMinIntervalMs).toBeDefined();
    expect(errors.MaxConcurrentConversions).toBeDefined();
  });

  it('validates ApiVersion pattern when non-empty', () => {
    const provider = makeProvider(
      ['ApiVersion'],
      [
        {
          name: 'ApiVersion',
          kind: 'text',
          required: false,
          enumOptions: null,
          operative: true,
        },
      ],
      {
        ApiVersion: { name: 'ApiVersion', value: '2024-11-30', isSecret: false, hasValue: true },
      }
    );
    expect(validateOperativeProviderFields(provider, { ApiVersion: 'bad' }).ApiVersion).toBeDefined();
    expect(validateOperativeProviderFields(provider, { ApiVersion: '2025-04-01-preview' }).ApiVersion).toBeUndefined();
  });
});

describe('hasValidationErrors', () => {
  it('returns true only when errors exist', () => {
    expect(hasValidationErrors({})).toBe(false);
    expect(hasValidationErrors({ Endpoint: 'Required' })).toBe(true);
  });
});

describe('buildSavePayload', () => {
  it('omits unchanged secret when hasValue', () => {
    const provider = makeProvider(
      ['ApiKey'],
      [
        {
          name: 'ApiKey',
          kind: 'secret',
          required: true,
          enumOptions: null,
          operative: true,
        },
      ],
      {
        ApiKey: { name: 'ApiKey', value: null, isSecret: true, hasValue: true },
      }
    );
    const payload = buildSavePayload(provider, {});
    expect(payload.ApiKey).toBeUndefined();
  });

  it('includes new secret values and required empty secrets without stored value', () => {
    const provider = makeProvider(
      ['ApiKey'],
      [
        {
          name: 'ApiKey',
          kind: 'secret',
          required: true,
          enumOptions: null,
          operative: true,
        },
      ],
      {
        ApiKey: { name: 'ApiKey', value: null, isSecret: true, hasValue: false },
      },
    );

    expect(buildSavePayload(provider, { ApiKey: 'new-secret' }).ApiKey).toBe('new-secret');
    expect(buildSavePayload(provider, {}).ApiKey).toBe('');
  });

  it('sets null for optional non-secret secrets without stored value', () => {
    const provider = makeProvider(
      ['ApiKey'],
      [
        {
          name: 'ApiKey',
          kind: 'secret',
          required: false,
          enumOptions: null,
          operative: true,
        },
      ],
      {
        ApiKey: { name: 'ApiKey', value: null, isSecret: true, hasValue: false },
      },
    );

    expect(buildSavePayload(provider, {}).ApiKey).toBeNull();
  });

  it('maps non-secret draft values to null when cleared', () => {
    const provider = makeProvider(
      ['Endpoint'],
      [
        {
          name: 'Endpoint',
          kind: 'url',
          required: false,
          enumOptions: null,
          operative: true,
        },
      ],
      {
        Endpoint: { name: 'Endpoint', value: 'https://example.invalid', isSecret: false, hasValue: true },
      },
    );

    expect(buildSavePayload(provider, { Endpoint: 'https://updated.invalid' }).Endpoint).toBe(
      'https://updated.invalid',
    );
    expect(buildSavePayload(provider, { Endpoint: '' }).Endpoint).toBeNull();
  });
});
