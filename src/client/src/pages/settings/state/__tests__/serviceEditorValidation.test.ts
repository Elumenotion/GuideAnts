import { describe, expect, it } from 'vitest';
import type { ProviderEditorStateDto, ProviderFieldMetadataDto } from '../../../../types/settings';
import { buildSavePayload, validateOperativeProviderFields } from '../serviceEditorValidation';

function makeProvider(
  operativeNames: string[],
  metadata: ProviderFieldMetadataDto[],
  fields: ProviderEditorStateDto['fields']
): ProviderEditorStateDto {
  return {
    providerId: 'test',
    providerKind: 'Cloud',
    displayName: 'Test',
    fields,
    runtimeDependencies: [],
    operativeFields: operativeNames,
    diagnosticFields: [],
    fieldMetadata: metadata,
  };
}

describe('validateOperativeProviderFields', () => {
  it('requires secret when no stored value', () => {
    const provider = makeProvider(
      ['ApiKey'],
      [
        {
          name: 'ApiKey',
          label: 'API Key',
          kind: 'secret',
          required: true,
          helpText: '',
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
          label: 'API Key',
          kind: 'secret',
          required: true,
          helpText: '',
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

  it('validates ApiVersion pattern when non-empty', () => {
    const provider = makeProvider(
      ['ApiVersion'],
      [
        {
          name: 'ApiVersion',
          label: 'API Version',
          kind: 'text',
          required: false,
          helpText: '',
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

describe('buildSavePayload', () => {
  it('omits unchanged secret when hasValue', () => {
    const provider = makeProvider(
      ['ApiKey'],
      [
        {
          name: 'ApiKey',
          label: 'API Key',
          kind: 'secret',
          required: true,
          helpText: '',
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
});
