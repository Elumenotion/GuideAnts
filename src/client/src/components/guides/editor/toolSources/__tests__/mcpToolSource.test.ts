import { describe, it, expect } from 'vitest';
import {
  applyMcpDiscoveryToSpec,
  buildMcpBridgeServerUrl,
  extractExistingMcpToolStates,
  parseMcpConnectionSettings,
  validateMcpConnectionSettings,
} from '../mcpToolSource';
import { buildEmptyOpenApiDescriptor } from '../openApiDescriptorBuilder';
import { classifyToolSourceFromSpec, classifySchemeFromServerUrl } from '../toolSourceClassification';

describe('mcpToolSource', () => {
  it('buildMcpBridgeServerUrl uses client bridge prefix', () => {
    expect(buildMcpBridgeServerUrl('abc123')).toBe('client://mcp-bridge-abc123');
  });

  it('classifies MCP descriptors via metadata extension', () => {
    const spec = buildEmptyOpenApiDescriptor('mcp-connection');
    expect(classifyToolSourceFromSpec(spec, 'client://mcp-bridge-x')).toBe('mcp-connection');
  });

  it('classifies mcp-bridge host as mcp-connection from URL alone', () => {
    expect(classifySchemeFromServerUrl('client://mcp-bridge-test')).toBe('mcp-connection');
    expect(classifySchemeFromServerUrl('client://worm-commander-client')).toBe('client-actions');
  });

  it('applyMcpDiscoveryToSpec writes operations with stable backing ids', () => {
    const spec = buildEmptyOpenApiDescriptor('mcp-connection');
    const settings = parseMcpConnectionSettings(spec);
    const fragment = {
      path: '/tools/search',
      method: 'post',
      operation: {
        operationId: 'mcp_search',
        summary: 'Search',
        'x-guideants-mcp-tool': {
          backingToolId: 'search',
          schemaHash: 'abc',
          enabled: true,
        },
        requestBody: {
          required: true,
          content: { 'application/json': { schema: { type: 'object' } } },
        },
        responses: { '200': { description: 'ok' } },
      },
    };

    const next = applyMcpDiscoveryToSpec(spec, settings, [
      {
        backingToolId: 'search',
        name: 'search',
        schemaHash: 'abc',
        selected: true,
        diffState: 'added',
        operationId: 'mcp_search',
        path: '/tools/search',
        method: 'post',
        schemaFragmentJson: JSON.stringify(fragment),
      },
    ]);

    const parsed = JSON.parse(next);
    expect(parsed.servers[0].url).toMatch(/^client:\/\/mcp-bridge-/);
    expect(parsed['x-guideants-tool-source'].kind).toBe('mcp');
    expect(parsed.paths['/tools/search'].post.operationId).toBe('mcp_search');
    expect(parsed.paths['/tools/search'].post['x-guideants-mcp-tool'].backingToolId).toBe('search');

    const states = extractExistingMcpToolStates(next);
    expect(states).toHaveLength(1);
    expect(states[0].backingToolId).toBe('search');
  });

  it('applyMcpDiscoveryToSpec stores secret refs in headers', () => {
    const spec = buildEmptyOpenApiDescriptor('mcp-connection');
    const settings = {
      ...parseMcpConnectionSettings(spec),
      headers: { Authorization: '{{secret:MCP_API_KEY}}' },
    };
    const next = applyMcpDiscoveryToSpec(spec, settings, []);
    const parsed = JSON.parse(next);
    expect(parsed['x-guideants-tool-source'].headers.Authorization).toBe('{{secret:MCP_API_KEY}}');
  });

  it('validateMcpConnectionSettings enforces transport rules', () => {
    expect(
      validateMcpConnectionSettings({
        transport: 'streamable_http',
        url: '',
        bridgeId: 'x',
        toolNamePrefix: 'mcp',
        headers: {},
      })
    ).toContain('URL');

    expect(
      validateMcpConnectionSettings({
        transport: 'client_bridge',
        url: '',
        bridgeId: '',
        toolNamePrefix: 'mcp',
        headers: {},
      })
    ).toContain('bridge');
  });
});
