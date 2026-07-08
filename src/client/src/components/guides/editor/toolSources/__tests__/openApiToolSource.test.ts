import { describe, expect, it } from 'vitest';
import {
  extractHostFromUrl,
  extractServerUrl,
  extractTools,
  isConnectorKeyUnique,
  updateServerUrlInSpec,
} from '../openApiToolSource';

const openApiSpec = JSON.stringify({
  openapi: '3.0.0',
  servers: [{ url: 'https://api.example.com/v1' }],
  paths: {
    '/ping': {
      get: {
        operationId: 'getPing',
        summary: 'Ping',
      },
      post: {
        summary: 'Create ping',
      },
    },
  },
});

describe('openApiToolSource', () => {
  it('extracts server URLs from OpenAPI and Swagger specs', () => {
    expect(extractServerUrl(openApiSpec)).toBe('https://api.example.com/v1');
    expect(
      extractServerUrl(
        JSON.stringify({
          swagger: '2.0',
          host: 'legacy.example.com',
          schemes: ['http'],
        }),
      ),
    ).toBe('http://legacy.example.com');
    expect(extractServerUrl('not-json')).toBeNull();
  });

  it('extracts host from absolute URLs', () => {
    expect(extractHostFromUrl('https://api.example.com/v1')).toBe('api.example.com');
    expect(extractHostFromUrl('not-a-url')).toBeNull();
  });

  it('extracts operations from OpenAPI paths', () => {
    const tools = extractTools(openApiSpec);
    expect(tools).toHaveLength(2);
    expect(tools[0]).toMatchObject({ operationId: 'getPing', method: 'GET', path: '/ping' });
    expect(tools[1].operationId).toBe('post__ping');
  });

  it('updates server URLs for OpenAPI and Swagger specs', () => {
    const nextOpenApi = updateServerUrlInSpec(openApiSpec, 'https://api.example.com/v2');
    expect(JSON.parse(nextOpenApi).servers[0].url).toBe('https://api.example.com/v2');

    const swaggerSpec = JSON.stringify({
      swagger: '2.0',
      host: 'legacy.example.com',
      schemes: ['https'],
      basePath: '/v1',
    });
    const nextSwagger = updateServerUrlInSpec(swaggerSpec, 'https://new.example.com/v2/base');
    const parsed = JSON.parse(nextSwagger);
    expect(parsed.host).toBe('new.example.com');
    expect(parsed.schemes).toEqual(['https']);
    expect(parsed.basePath).toBe('/v2/base');
  });

  it('checks connector key uniqueness across tool sources', () => {
    const tools = [{ apiHost: 'alpha' }, { apiHost: 'beta' }];
    expect(isConnectorKeyUnique('alpha', tools, 1)).toBe(false);
    expect(isConnectorKeyUnique('gamma', tools, 0)).toBe(true);
  });
});
