import { describe, expect, it } from 'vitest';
import {
  buildMcpDispatchUrl,
  defaultPackageDescriptor,
  deriveDiscoveryTransport,
  isLoopbackMcpApiUrl,
  loopbackReachabilityWarning,
  mapSetupStatusFromResponse,
  runtimeExecutionLabel,
  sandboxPackagesFingerprint,
  sandboxSetupStatusChipClassName,
  sandboxSetupStatusLabel,
} from '../mcpRuntimeMode';

describe('mcpRuntimeMode extended', () => {
  it('normalizes legacy client bridge ids in dispatch URLs', () => {
    expect(buildMcpDispatchUrl('mcp-bridge-legacy-id', 'api')).toBe('mcp+api://legacy-id');
  });

  it('derives discovery transport from runtime execution', () => {
    expect(deriveDiscoveryTransport('api')).toBe('streamable_http');
    expect(deriveDiscoveryTransport('sandbox_subprocess')).toBe('stdio');
  });

  it('returns false for invalid loopback URLs', () => {
    expect(isLoopbackMcpApiUrl('')).toBe(false);
    expect(isLoopbackMcpApiUrl('not-a-url')).toBe(false);
  });

  it('documents loopback reachability limitations', () => {
    expect(loopbackReachabilityWarning()).toMatch(/host\.docker\.internal/i);
  });

  it('creates a default npm package descriptor', () => {
    expect(defaultPackageDescriptor()).toEqual({
      registryType: 'npm',
      identifier: '',
      command: 'npx',
      args: [],
    });
  });

  it('fingerprints sandbox package descriptors deterministically', () => {
    const left = sandboxPackagesFingerprint([
      { registryType: 'npm', identifier: '@example/mcp', command: 'npx', args: ['-y'] },
    ]);
    const right = sandboxPackagesFingerprint([
      { registryType: ' NPM ', identifier: ' @example/mcp ', command: ' npx ', args: ['-y'] },
    ]);

    expect(left).toBe(right);
  });

  it('maps setup status responses to staged/applied/unknown states', () => {
    expect(mapSetupStatusFromResponse(null)).toBe('unknown');
    expect(
      mapSetupStatusFromResponse({
        overallStatus: 'ready',
        requirements: { pendingApply: false },
        installScripts: { pendingApply: false },
      }),
    ).toBe('applied');
    expect(
      mapSetupStatusFromResponse({
        overallStatus: 'ready',
        requirements: { pendingApply: true },
      }),
    ).toBe('drift');
  });

  it('labels runtime execution modes and sandbox subprocess dispatch URLs', () => {
    expect(runtimeExecutionLabel('api')).toBe('API');
    expect(runtimeExecutionLabel('sandbox_subprocess')).toBe('Sandbox');
    expect(buildMcpDispatchUrl('bridge-id', 'sandbox_subprocess')).toBe('mcp+sandbox://bridge-id');
  });

  it('labels and styles sandbox setup status chips', () => {
    expect(sandboxSetupStatusLabel('staged')).toMatch(/staged/i);
    expect(sandboxSetupStatusLabel('applied')).toBe('Applied');
    expect(sandboxSetupStatusLabel('drift')).toMatch(/re-apply/i);
    expect(sandboxSetupStatusChipClassName('drift')).toContain('orange');
    expect(sandboxSetupStatusChipClassName('unknown')).toContain('gray');
  });
});
