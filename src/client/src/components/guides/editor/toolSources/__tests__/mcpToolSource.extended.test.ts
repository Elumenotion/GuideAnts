import { describe, expect, it } from 'vitest';
import type { EnvironmentVariableDto } from '../../../../../types/guides';
import { buildEmptyOpenApiDescriptor } from '../openApiDescriptorBuilder';
import {
  buildResolvedMcpConnectionPayload,
  diffStateChipClassName,
  diffStateLabel,
  extractMcpBridgeIdFromServerUrl,
  headersContainUnresolvedSecrets,
  isLegacyClientBridgeMcpSource,
  mcpHeaderRowsToHeaders,
  parseMcpConnectionSettings,
  parseMcpHeaderRows,
  validateMcpToolNamePrefixCollision,
} from '../mcpToolSource';

describe('mcpToolSource extended', () => {
  it('extracts bridge ids from modern and legacy server URLs', () => {
    expect(extractMcpBridgeIdFromServerUrl('mcp+api://abc123')).toBe('abc123');
    expect(extractMcpBridgeIdFromServerUrl('mcp+sandbox://sandbox-id')).toBe('sandbox-id');
    expect(extractMcpBridgeIdFromServerUrl('client://mcp-bridge-legacy')).toBe('legacy');
    expect(extractMcpBridgeIdFromServerUrl('not-a-url')).toBeNull();
  });

  it('round-trips MCP header rows with secret refs and literals', () => {
    const rows = parseMcpHeaderRows({
      Authorization: '{{secret:MCP_API_KEY}}',
      'X-Debug': 'plain',
      'X-Redacted': '***',
    });

    expect(rows).toEqual([
      { key: 'Authorization', secretRefName: 'MCP_API_KEY', literalValue: '', useLiteral: false },
      { key: 'X-Debug', secretRefName: '', literalValue: 'plain', useLiteral: true },
      { key: 'X-Redacted', secretRefName: '', literalValue: '', useLiteral: false },
    ]);

    expect(
      mcpHeaderRowsToHeaders([
        { key: ' Authorization ', secretRefName: 'MCP_API_KEY', literalValue: '', useLiteral: false },
        { key: 'X-Debug', secretRefName: '', literalValue: 'plain', useLiteral: true },
        { key: '', secretRefName: 'ignored', literalValue: '', useLiteral: false },
      ]),
    ).toEqual({
      Authorization: '{{secret:MCP_API_KEY}}',
      'X-Debug': 'plain',
    });
  });

  it('builds resolved MCP connection payload and reports missing secret refs', () => {
    const settings = parseMcpConnectionSettings(buildEmptyOpenApiDescriptor('mcp-connection'));
    const env: EnvironmentVariableDto[] = [
      { name: 'MCP_API_KEY', value: 'secret-value', isSecret: true },
    ];

    const payload = buildResolvedMcpConnectionPayload(
      {
        ...settings,
        url: 'http://localhost:8080/mcp',
        headers: { Authorization: '{{secret:MCP_API_KEY}}', 'X-Missing': '{{secret:MISSING}}' },
      },
      env,
    );

    expect(payload.headers.Authorization).toBe('secret-value');
    expect(payload.missingSecretRefs).toEqual(['MISSING']);
    expect(
      headersContainUnresolvedSecrets(
        { Authorization: '{{secret:MCP_API_KEY}}', 'X-Missing': '{{secret:MISSING}}' },
        env,
      ),
    ).toEqual(['MISSING']);
  });

  it('labels diff states for chips and badges', () => {
    expect(diffStateChipClassName('added')).toContain('green');
    expect(diffStateChipClassName('changed')).toContain('amber');
    expect(diffStateChipClassName('removed')).toContain('red');
    expect(diffStateChipClassName('disabled')).toContain('gray');
    expect(diffStateChipClassName('unknown')).toContain('gray');

    expect(diffStateLabel('added')).toBe('Added');
    expect(diffStateLabel('unchanged')).toBe('');
    expect(diffStateLabel('custom')).toBe('custom');
  });

  it('detects legacy client bridge MCP sources', () => {
    const legacySpec = JSON.stringify({
      servers: [{ url: 'client://mcp-bridge-abc' }],
      'x-guideants-tool-source': { kind: 'mcp', transport: 'client_bridge' },
    });

    expect(isLegacyClientBridgeMcpSource(legacySpec)).toBe(true);
    expect(isLegacyClientBridgeMcpSource(buildEmptyOpenApiDescriptor('mcp-connection'))).toBe(false);
  });

  it('validates MCP tool name prefix collisions across sources', () => {
    const customTools = [
      { name: 'alpha', openApiSpec: buildEmptyOpenApiDescriptor('mcp-connection') },
      {
        name: 'beta',
        openApiSpec: JSON.stringify({
          servers: [{ url: 'mcp+api://beta' }],
          'x-guideants-tool-source': { kind: 'mcp', toolNamePrefix: 'shared' },
        }),
      },
    ];

    expect(validateMcpToolNamePrefixCollision(customTools, 0, 'shared')).toMatch(/already used/i);
    expect(validateMcpToolNamePrefixCollision(customTools, 0, 'unique')).toBeNull();
  });
});
