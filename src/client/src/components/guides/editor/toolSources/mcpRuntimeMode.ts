import type { McpPackageDescriptor, McpRuntimeExecution } from './mcpToolSourceTypes';
import { defaultDiscoveryTransport } from './mcpToolSourceTypes';

const LEGACY_CLIENT_MCP_HOST_PREFIX = `mcp-${'bridge'}-`;

export function buildMcpDispatchUrl(bridgeId: string, runtimeExecution: McpRuntimeExecution): string {
  const normalizedBridgeId = bridgeId.startsWith(LEGACY_CLIENT_MCP_HOST_PREFIX)
    ? bridgeId.slice(LEGACY_CLIENT_MCP_HOST_PREFIX.length)
    : bridgeId;
  return runtimeExecution === 'sandbox_subprocess'
    ? `mcp+sandbox://${normalizedBridgeId}`
    : `mcp+api://${normalizedBridgeId}`;
}

export function deriveDiscoveryTransport(runtimeExecution: McpRuntimeExecution) {
  return defaultDiscoveryTransport(runtimeExecution);
}

export function isLoopbackMcpApiUrl(url: string): boolean {
  if (!url.trim()) {
    return false;
  }

  try {
    const parsed = new URL(url);
    const host = parsed.hostname.toLowerCase();
    return host === 'localhost' || host === '127.0.0.1' || host === '::1';
  } catch {
    return false;
  }
}

export function loopbackReachabilityWarning(): string {
  return 'This URL uses loopback (localhost / 127.0.0.1). Docker cannot reach it unless you use host.docker.internal or an equivalent reachable host.';
}

export function runtimeExecutionLabel(runtimeExecution: McpRuntimeExecution): string {
  return runtimeExecution === 'sandbox_subprocess' ? 'Sandbox' : 'API';
}

export function defaultPackageDescriptor(): McpPackageDescriptor {
  return {
    registryType: 'npm',
    identifier: '',
    command: 'npx',
    args: [],
  };
}

export function sandboxPackagesFingerprint(packages: McpPackageDescriptor[]): string {
  return JSON.stringify(
    packages.map((pkg) => ({
      registryType: pkg.registryType.trim().toLowerCase(),
      identifier: pkg.identifier.trim(),
      command: pkg.command.trim(),
      args: pkg.args ?? [],
    })),
  );
}

export type McpSandboxSetupStatusKind = 'staged' | 'applied' | 'drift' | 'unknown';

export function mapSetupStatusFromResponse(data: unknown): McpSandboxSetupStatusKind {
  if (!data || typeof data !== 'object') {
    return 'unknown';
  }

  const status = data as Record<string, unknown>;
  const overallStatus = typeof status.overallStatus === 'string' ? status.overallStatus : '';
  const requirementsPending = isSectionPending(status.requirements);
  const installScriptsPending = isSectionPending(status.installScripts);

  if (overallStatus === 'ready' && !requirementsPending && !installScriptsPending) {
    return 'applied';
  }

  if (requirementsPending || installScriptsPending || overallStatus === 'pending') {
    return 'drift';
  }

  if (overallStatus === 'ready') {
    return 'applied';
  }

  return 'unknown';
}

function isSectionPending(section: unknown): boolean {
  if (!section || typeof section !== 'object') {
    return false;
  }

  return (section as Record<string, unknown>).pendingApply === true;
}

export function sandboxSetupStatusLabel(kind: McpSandboxSetupStatusKind): string {
  switch (kind) {
    case 'staged':
      return 'Staged (not applied)';
    case 'applied':
      return 'Applied';
    case 'drift':
      return 'Drift (re-apply needed)';
    default:
      return 'Unknown';
  }
}

export function sandboxSetupStatusChipClassName(kind: McpSandboxSetupStatusKind): string {
  switch (kind) {
    case 'applied':
      return 'bg-green-100 text-green-800';
    case 'staged':
      return 'bg-amber-100 text-amber-800';
    case 'drift':
      return 'bg-orange-100 text-orange-800';
    default:
      return 'bg-gray-100 text-gray-600';
  }
}
