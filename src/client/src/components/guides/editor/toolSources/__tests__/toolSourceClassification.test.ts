import { describe, it, expect } from 'vitest';
import {
  classifySchemeFromServerUrl,
  classifyToolSourceFromSpec,
  CONNECTOR_KEY_LABELS,
  extractConnectorKeyFromServerUrl,
  SOURCE_KIND_LABELS,
} from '../toolSourceClassification';

describe('toolSourceClassification', () => {
  it('classifies https as web-api', () => {
    expect(classifySchemeFromServerUrl('https://api.example.com')).toBe('web-api');
    expect(SOURCE_KIND_LABELS['web-api']).toBe('Web API');
    expect(CONNECTOR_KEY_LABELS['web-api']).toBe('API host');
  });

  it('classifies client as client-actions', () => {
    expect(classifySchemeFromServerUrl('client://worm-commander-client')).toBe('client-actions');
    expect(CONNECTOR_KEY_LABELS['client-actions']).toBe('Client bridge');
    expect(extractConnectorKeyFromServerUrl('client://worm-commander-client')).toBe(
      'worm-commander-client'
    );
  });

  it('classifies sandbox as sandbox-module', () => {
    expect(classifySchemeFromServerUrl('sandbox://__init__.py')).toBe('sandbox-module');
    expect(CONNECTOR_KEY_LABELS['sandbox-module']).toBe('Init module');
    expect(extractConnectorKeyFromServerUrl('sandbox://__init__.py')).toBe('__init__.py');
  });

  it('classifies tool as local-function', () => {
    expect(classifySchemeFromServerUrl('tool://localhost')).toBe('local-function');
    expect(CONNECTOR_KEY_LABELS['local-function']).toBe('Local tool host');
  });

  it('classifies mcp-bridge client URLs as mcp-connection', () => {
    expect(classifySchemeFromServerUrl('client://mcp-bridge-server1')).toBe('mcp-connection');
    expect(extractConnectorKeyFromServerUrl('client://mcp-bridge-server1')).toBe('server1');
  });

  it('classifies MCP from descriptor metadata', () => {
    const spec = JSON.stringify({
      servers: [{ url: 'client://mcp-bridge-x' }],
      'x-guideants-tool-source': { kind: 'mcp', transport: 'streamable_http', bridgeId: 'x' },
    });
    expect(classifyToolSourceFromSpec(spec, 'client://mcp-bridge-x')).toBe('mcp-connection');
  });

  it('returns unknown for missing or invalid URL', () => {
    expect(classifySchemeFromServerUrl(null)).toBe('unknown');
    expect(classifySchemeFromServerUrl('not-a-url')).toBe('unknown');
  });
});
