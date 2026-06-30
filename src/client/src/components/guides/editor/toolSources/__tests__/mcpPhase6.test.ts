import { describe, it, expect } from 'vitest';
import { isLegacyClientBridgeMcpSource, validateMcpToolNamePrefixCollision } from '../mcpToolSource';

describe('mcp migration and prefix validation', () => {
  const legacyHost = `mcp-${'bridge'}-legacy1`;
  const legacySpec = JSON.stringify({
    openapi: '3.0.0',
    info: { title: 'MCP', version: '1.0.0' },
    servers: [{ url: `client://${legacyHost}` }],
    'x-guideants-tool-source': { kind: 'mcp', transport: ['client', 'bridge'].join('_'), bridgeId: 'legacy1' },
    paths: {},
  });

  it('detects legacy client-bridge MCP sources for migration notice', () => {
    expect(isLegacyClientBridgeMcpSource(legacySpec)).toBe(true);
  });

  it('reports toolNamePrefix collisions across MCP sources', () => {
    const apiSpec = JSON.stringify({
      openapi: '3.0.0',
      info: { title: 'MCP', version: '1.0.0' },
      servers: [{ url: 'mcp+api://a' }],
      'x-guideants-tool-source': {
        kind: 'mcp',
        runtimeExecution: 'api',
        discoveryTransport: 'streamable_http',
        bridgeId: 'a',
        url: 'https://example.com/mcp',
        toolNamePrefix: 'mcp',
      },
      paths: {},
    });

    const tools = [
      { name: 'source-a', openApiSpec: apiSpec },
      { name: 'source-b', openApiSpec: apiSpec },
    ];

    expect(validateMcpToolNamePrefixCollision(tools, 0, 'mcp')).toContain('source source-b');
  });
});
