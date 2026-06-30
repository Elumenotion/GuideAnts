export type McpRuntimeExecution = 'api' | 'sandbox_subprocess';

export type McpDiscoveryTransport = 'streamable_http' | 'stdio';

export type McpConnectionPanelState =
  | 'idle'
  | 'testing'
  | 'connected'
  | 'discovering'
  | 'discovery-failed'
  | 'applying'
  | 'apply-failed';

export type McpToolDiffState = 'added' | 'changed' | 'removed' | 'disabled' | 'unchanged';

export interface McpPackageDescriptor {
  registryType: string;
  identifier: string;
  command: string;
  args?: string[];
}

export interface McpEnvironmentVariableRef {
  name: string;
  secretRef: string;
}

export interface McpToolSourceMetadata {
  kind: 'mcp';
  runtimeExecution: McpRuntimeExecution;
  discoveryTransport: McpDiscoveryTransport;
  url?: string;
  bridgeId?: string;
  toolNamePrefix?: string;
  headers?: Record<string, string>;
  package?: McpPackageDescriptor;
  environmentVariables?: McpEnvironmentVariableRef[];
}

export interface McpToolOperationMetadata {
  backingToolId: string;
  schemaHash: string;
  enabled: boolean;
  diffState?: McpToolDiffState;
}

export interface McpConnectionSettings {
  runtimeExecution: McpRuntimeExecution;
  discoveryTransport: McpDiscoveryTransport;
  url: string;
  bridgeId: string;
  toolNamePrefix: string;
  headers: Record<string, string>;
  package?: McpPackageDescriptor;
  environmentVariables?: McpEnvironmentVariableRef[];
}

export interface McpHeaderRow {
  key: string;
  secretRefName: string;
  literalValue: string;
  useLiteral: boolean;
}

export interface McpDiscoveredToolRow {
  backingToolId: string;
  name: string;
  title?: string;
  description?: string;
  schemaHash: string;
  selected: boolean;
  diffState: McpToolDiffState | string;
  operationId: string;
  path: string;
  method: string;
  schemaFragmentJson: string;
}

export interface McpDiscoverDiffSummary {
  added: number;
  changed: number;
  removed: number;
  disabled: number;
}

export interface McpDiscoverToolsResponse {
  success: boolean;
  message: string;
  tools: McpDiscoveredToolRow[];
  diff: McpDiscoverDiffSummary;
}

export interface McpTestConnectionResponse {
  connected: boolean;
  message: string;
  serverName?: string;
  serverVersion?: string;
}

export function defaultDiscoveryTransport(
  runtimeExecution: McpRuntimeExecution
): McpDiscoveryTransport {
  return runtimeExecution === 'sandbox_subprocess' ? 'stdio' : 'streamable_http';
}
