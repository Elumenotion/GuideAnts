import { describe, expect, it } from 'vitest';
import {
  buildEmptyOpenApiDescriptor,
  buildServerUrlForConnectorKey,
  createDraftCustomTool,
  updateConnectorKeyInTool,
} from '../openApiDescriptorBuilder';

describe('openApiDescriptorBuilder extended', () => {
  it('creates sandbox and MCP drafts with expected focus fields', () => {
    const sandbox = createDraftCustomTool('sandbox-module', []);
    expect(sandbox.focusFieldId).toBe('init-module');
    expect(JSON.parse(sandbox.tool.openApiSpec).servers[0].url).toBe('sandbox://__init__.py');

    const mcp = createDraftCustomTool('mcp-connection', []);
    expect(mcp.focusFieldId).toBe('mcp-dispatch-id');
    expect(mcp.tool.apiHost).toMatch(/^[a-z0-9]+$/i);
  });

  it('creates local-function drafts with local target focus', () => {
    const { focusFieldId, tool } = createDraftCustomTool('local-function', []);
    expect(focusFieldId).toBe('local-target');
    expect(JSON.parse(tool.openApiSpec)['x-guideants-tool-source']).toEqual({ kind: 'local-function' });
  });

  it('updates connector keys for MCP and sandbox tools', () => {
    const mcpTool = createDraftCustomTool('mcp-connection', []).tool;
    const updatedMcp = updateConnectorKeyInTool(mcpTool, 'mcp-connection', 'bridge-42');
    expect(updatedMcp.apiHost).toBe('bridge-42');
    expect(JSON.parse(updatedMcp.openApiSpec).servers[0].url).toBe('mcp+api://bridge-42');

    const sandboxTool = createDraftCustomTool('sandbox-module', []).tool;
    const updatedSandbox = updateConnectorKeyInTool(sandboxTool, 'sandbox-module', 'jobs.py');
    expect(JSON.parse(updatedSandbox.openApiSpec).servers[0].url).toBe('sandbox://jobs.py');
  });

  it('builds MCP and sandbox server URLs from connector keys', () => {
    expect(buildServerUrlForConnectorKey('mcp-connection', 'bridge-1')).toBe('mcp+api://bridge-1');
    expect(buildServerUrlForConnectorKey('sandbox-module', 'jobs.py')).toBe('sandbox://jobs.py');
    expect(buildServerUrlForConnectorKey('sandbox-module', 'sandbox://already.py')).toBe(
      'sandbox://already.py',
    );
  });

  it('returns the original tool when connector updates receive invalid JSON', () => {
    const broken = {
      name: 'broken',
      openApiSpec: '{not-json',
      apiHost: 'broken',
    };

    expect(updateConnectorKeyInTool(broken, 'web-api', 'api.example.com')).toBe(broken);
  });

  it('marks custom descriptors when requested explicitly', () => {
    const spec = JSON.parse(buildEmptyOpenApiDescriptor('web-api', { customDescriptor: true }));
    expect(spec['x-guideants-custom-descriptor']).toBe(true);
  });
});
