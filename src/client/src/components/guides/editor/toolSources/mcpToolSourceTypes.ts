export type McpTransport = 'streamable_http' | 'client_bridge';

export type McpConnectionPanelState =
  | 'idle'
  | 'testing'
  | 'connected'
  | 'discovering'
  | 'discovery-failed';

export type McpToolDiffState = 'added' | 'changed' | 'removed' | 'disabled' | 'unchanged';

export interface McpToolSourceMetadata {
  kind: 'mcp';
  transport: McpTransport;
  url?: string;
  bridgeId?: string;
  toolNamePrefix?: string;
  headers?: Record<string, string>;
}

export interface McpToolOperationMetadata {
  backingToolId: string;
  schemaHash: string;
  enabled: boolean;
  diffState?: McpToolDiffState;
}

export interface McpConnectionSettings {
  transport: McpTransport;
  url: string;
  bridgeId: string;
  toolNamePrefix: string;
  headers: Record<string, string>;
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
