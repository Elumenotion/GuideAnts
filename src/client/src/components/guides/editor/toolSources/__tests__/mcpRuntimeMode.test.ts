import { describe, it, expect } from 'vitest';
import {
  buildMcpDispatchUrl,
  isLoopbackMcpApiUrl,
  mapSetupStatusFromResponse,
  runtimeExecutionLabel,
} from '../mcpRuntimeMode';
import { composeSandboxStagingArtifacts } from '../mcpSandboxSetupComposer';

describe('mcpRuntimeMode', () => {
  it('buildMcpDispatchUrl uses locked schemes', () => {
    expect(buildMcpDispatchUrl('abc123', 'api')).toBe('mcp+api://abc123');
    expect(buildMcpDispatchUrl('abc123', 'sandbox_subprocess')).toBe('mcp+sandbox://abc123');
  });

  it('detects loopback hosts without rewriting', () => {
    expect(isLoopbackMcpApiUrl('http://localhost:8080/mcp')).toBe(true);
    expect(isLoopbackMcpApiUrl('http://127.0.0.1/mcp')).toBe(true);
    expect(isLoopbackMcpApiUrl('https://mcp.example.com/mcp')).toBe(false);
  });

  it('maps setup-status pending to drift', () => {
    expect(
      mapSetupStatusFromResponse({
        overallStatus: 'pending',
        requirements: { pendingApply: true },
      }),
    ).toBe('drift');
  });

  it('labels runtime execution for badges', () => {
    expect(runtimeExecutionLabel('api')).toBe('API');
    expect(runtimeExecutionLabel('sandbox_subprocess')).toBe('Sandbox');
  });
});

describe('mcpSandboxSetupComposer', () => {
  it('writes npm install script and node apt dependency', () => {
    const artifacts = composeSandboxStagingArtifacts([
      { registryType: 'npm', identifier: '@example/mcp', command: 'npx', args: ['-y', '@example/mcp'] },
    ]);

    expect(artifacts.aptPackagesText).toContain('nodejs');
    expect(artifacts.installScriptsJson).toContain('npm install -g @example/mcp');
  });
});
