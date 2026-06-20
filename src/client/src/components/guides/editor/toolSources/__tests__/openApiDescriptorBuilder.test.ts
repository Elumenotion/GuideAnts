import { describe, it, expect } from 'vitest';
import {
  buildEmptyOpenApiDescriptor,
  createDraftCustomTool,
  updateConnectorKeyInTool,
  buildServerUrlForConnectorKey,
} from '../openApiDescriptorBuilder';

describe('openApiDescriptorBuilder', () => {
  it('generates web API descriptor snapshot', () => {
    const spec = JSON.parse(buildEmptyOpenApiDescriptor('web-api'));
    expect(spec.openapi).toBe('3.0.1');
    expect(spec.servers[0].url).toBe('https://api.example.com');
    expect(spec.paths).toEqual({});
    expect(spec['x-guideants-tool-source']).toEqual({ kind: 'web-api' });
  });

  it('generates client actions descriptor snapshot', () => {
    const spec = JSON.parse(buildEmptyOpenApiDescriptor('client-actions'));
    expect(spec.servers[0].url).toBe('client://my-client-bridge');
    expect(spec.info.title).toBe('Client Actions');
  });

  it('generates sandbox module descriptor snapshot', () => {
    const spec = JSON.parse(buildEmptyOpenApiDescriptor('sandbox-module'));
    expect(spec.servers[0].url).toBe('sandbox://__init__.py');
    expect(spec.servers[0].description).toContain('Sandbox');
  });

  it('generates local function descriptor snapshot', () => {
    const spec = JSON.parse(buildEmptyOpenApiDescriptor('local-function'));
    expect(spec.servers[0].url).toBe('tool://localhost');
  });

  it('generates MCP connection descriptor snapshot', () => {
    const spec = JSON.parse(buildEmptyOpenApiDescriptor('mcp-connection'));
    expect(spec.servers[0].url).toMatch(/^client:\/\/mcp-bridge-/);
    expect(spec['x-guideants-tool-source'].kind).toBe('mcp');
    expect(spec['x-guideants-tool-source'].transport).toBe('streamable_http');
  });

  it('marks raw OpenAPI as custom descriptor', () => {
    const spec = JSON.parse(buildEmptyOpenApiDescriptor('raw-openapi'));
    expect(spec['x-guideants-custom-descriptor']).toBe(true);
  });

  it('creates draft with unique name', () => {
    const existing = [{ name: 'api.example.com', openApiSpec: '{}', apiHost: 'api.example.com' }];
    const { tool, focusFieldId } = createDraftCustomTool('web-api', existing);
    expect(tool.name).toBe('api.example.com 2');
    expect(focusFieldId).toBe('server-url');
  });

  it('creates client draft with bridge focus field', () => {
    const { focusFieldId } = createDraftCustomTool('client-actions', []);
    expect(focusFieldId).toBe('client-bridge-id');
  });

  it('updates connector key in tool for client bridge', () => {
    const tool = {
      name: 'old',
      openApiSpec: buildEmptyOpenApiDescriptor('client-actions'),
      apiHost: 'my-client-bridge',
    };
    const updated = updateConnectorKeyInTool(tool, 'client-actions', 'new-bridge');
    const spec = JSON.parse(updated.openApiSpec);
    expect(spec.servers[0].url).toBe('client://new-bridge');
    expect(updated.apiHost).toBe('new-bridge');
  });

  it('builds server URLs per scheme', () => {
    expect(buildServerUrlForConnectorKey('client-actions', 'bridge')).toBe('client://bridge');
    expect(buildServerUrlForConnectorKey('sandbox-module', '__init__.py')).toBe('sandbox://__init__.py');
    expect(buildServerUrlForConnectorKey('web-api', 'api.example.com')).toBe('https://api.example.com');
  });
});
