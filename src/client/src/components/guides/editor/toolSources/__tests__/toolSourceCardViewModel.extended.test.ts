import { describe, expect, it } from 'vitest';
import {
  buildToolSourceCardViewModel,
  deriveToolSourceStatus,
  mcpMigrationNoticeClassName,
  mcpRuntimeSubBadgeClassName,
  sourceKindBadgeClassName,
  statusChipClassName,
} from '../toolSourceCardViewModel';
import type { CustomToolDto } from '../../../../../types/guides';
import { buildEmptyOpenApiDescriptor } from '../openApiDescriptorBuilder';

describe('toolSourceCardViewModel extended', () => {
  it('marks connector key conflicts as needs-attention', () => {
    const tool: CustomToolDto = {
      name: 'api.example.com',
      openApiSpec: buildEmptyOpenApiDescriptor('web-api'),
      apiHost: 'api.example.com',
    };

    const vm = buildToolSourceCardViewModel(tool, [tool, tool], 1);
    expect(vm.connectorKeyConflict).toBe(true);
    expect(vm.status).toBe('needs-attention');
  });

  it('exposes MCP runtime badges and migration notices', () => {
    const legacyMcpSpec = JSON.stringify({
      ...JSON.parse(buildEmptyOpenApiDescriptor('mcp-connection')),
      servers: [{ url: 'client://mcp-bridge-legacy' }],
    });

    const tool: CustomToolDto = {
      name: 'legacy',
      openApiSpec: legacyMcpSpec,
      apiHost: 'legacy',
      authConfig: { type: 'apiKey', in: 'header', name: 'Authorization' },
    };

    const vm = buildToolSourceCardViewModel(tool, [tool], 0);
    expect(vm.mcpRuntimeSubBadge).toBe('API');
    expect(vm.showMcpMigrationNotice).toBe(true);
    expect(vm.hasAuth).toBe(true);
  });

  it('styles status, source kind, and MCP helper chips', () => {
    expect(deriveToolSourceStatus('{}', null, true, false)).toBe('needs-attention');
    expect(statusChipClassName('valid')).toContain('green');
    expect(statusChipClassName('invalid-json')).toContain('red');
    expect(sourceKindBadgeClassName('sandbox-module')).toContain('orange');
    expect(sourceKindBadgeClassName('unknown')).toContain('gray');
    expect(mcpRuntimeSubBadgeClassName()).toContain('teal');
    expect(mcpMigrationNoticeClassName()).toContain('blue');
  });
});
