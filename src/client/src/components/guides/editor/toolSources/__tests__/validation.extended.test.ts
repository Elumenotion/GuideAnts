import { describe, expect, it } from 'vitest';
import { isCustomDescriptor, isInvalidJson, validateOpenApiSpec } from '../validation';

describe('toolSources validation extended', () => {
  it('detects invalid JSON specs', () => {
    expect(isInvalidJson('{bad')).toBe(true);
    expect(isInvalidJson('{"openapi":"3.0.0"}')).toBe(false);
  });

  it('flags missing OpenAPI version fields', () => {
    expect(validateOpenApiSpec(JSON.stringify({ info: { title: 'x', version: '1' } }))).toMatch(
      /missing openapi version/i,
    );
  });

  it('flags missing info metadata and server URLs', () => {
    expect(validateOpenApiSpec(JSON.stringify({ openapi: '3.0.0' }))).toMatch(/missing required "info"/i);
    expect(
      validateOpenApiSpec(
        JSON.stringify({
          openapi: '3.0.0',
          info: { title: 'Demo', version: '1.0.0' },
        }),
      ),
    ).toMatch(/missing server url/i);
  });

  it('accepts swagger 2 host definitions', () => {
    expect(
      validateOpenApiSpec(
        JSON.stringify({
          swagger: '2.0',
          info: { title: 'Demo', version: '1.0.0' },
          host: 'api.example.com',
          paths: {},
        }),
      ),
    ).toBeNull();
  });

  it('flags operations missing operationId values', () => {
    const message = validateOpenApiSpec(
      JSON.stringify({
        openapi: '3.0.0',
        info: { title: 'Demo', version: '1.0.0' },
        servers: [{ url: 'https://api.example.com' }],
        paths: {
          '/ping': {
            get: { responses: { '200': { description: 'OK' } } },
          },
        },
      }),
    );

    expect(message).toMatch(/missing operationid/i);
    expect(message).toMatch(/GET \/ping/i);
  });

  it('detects custom descriptor flags', () => {
    expect(
      isCustomDescriptor(
        JSON.stringify({
          openapi: '3.0.0',
          'x-guideants-custom-descriptor': true,
        }),
      ),
    ).toBe(true);
    expect(isCustomDescriptor('{bad')).toBe(false);
  });
});
