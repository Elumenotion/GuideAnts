import type {
  McpConnectionSettings,
  McpDiscoveredToolRow,
  McpToolDiffState,
  McpToolOperationMetadata,
  McpToolSourceMetadata,
} from './mcpToolSourceTypes';

const MCP_BRIDGE_PREFIX = 'mcp-bridge-';

export function generateMcpBridgeId(): string {
  if (typeof crypto !== 'undefined' && 'randomUUID' in crypto) {
    return crypto.randomUUID().replace(/-/g, '').slice(0, 12);
  }
  return `mcp${Date.now().toString(36)}`;
}

export function buildMcpBridgeServerUrl(bridgeId: string): string {
  const normalized = bridgeId.startsWith(MCP_BRIDGE_PREFIX)
    ? bridgeId
    : `${MCP_BRIDGE_PREFIX}${bridgeId}`;
  return `client://${normalized}`;
}

export function extractMcpBridgeIdFromServerUrl(serverUrl: string | null): string | null {
  if (!serverUrl) return null;
  try {
    const url = new URL(serverUrl);
    const host = url.host;
    if (host.startsWith(MCP_BRIDGE_PREFIX)) {
      return host.slice(MCP_BRIDGE_PREFIX.length);
    }
    return host || null;
  } catch {
    return null;
  }
}

export function parseMcpToolSourceMetadata(spec: string): McpToolSourceMetadata | null {
  try {
    const parsed = JSON.parse(spec);
    const meta = parsed['x-guideants-tool-source'];
    if (!meta || meta.kind !== 'mcp') {
      return null;
    }
    return meta as McpToolSourceMetadata;
  } catch {
    return null;
  }
}

export function parseMcpConnectionSettings(spec: string): McpConnectionSettings {
  const meta = parseMcpToolSourceMetadata(spec);
  const serverUrl = (() => {
    try {
      const parsed = JSON.parse(spec);
      return parsed.servers?.[0]?.url as string | undefined;
    } catch {
      return undefined;
    }
  })();

  const bridgeId =
    meta?.bridgeId ??
    extractMcpBridgeIdFromServerUrl(serverUrl ?? null) ??
    generateMcpBridgeId();

  return {
    transport: meta?.transport ?? 'streamable_http',
    url: meta?.url ?? '',
    bridgeId,
    toolNamePrefix: meta?.toolNamePrefix ?? 'mcp',
    headers: meta?.headers ?? {},
  };
}

export function extractExistingMcpToolStates(spec: string): Array<{
  backingToolId: string;
  schemaHash: string;
  enabled: boolean;
  operationId: string;
}> {
  try {
    const parsed = JSON.parse(spec);
    const paths = parsed.paths ?? {};
    const states: Array<{
      backingToolId: string;
      schemaHash: string;
      enabled: boolean;
      operationId: string;
    }> = [];

    for (const [, pathItem] of Object.entries(paths)) {
      if (!pathItem || typeof pathItem !== 'object') continue;
      for (const method of Object.keys(pathItem as Record<string, unknown>)) {
        const operation = (pathItem as Record<string, unknown>)[method];
        if (!operation || typeof operation !== 'object') continue;
        const op = operation as Record<string, unknown>;
        const mcpMeta = op['x-guideants-mcp-tool'] as McpToolOperationMetadata | undefined;
        if (!mcpMeta?.backingToolId) continue;
        states.push({
          backingToolId: mcpMeta.backingToolId,
          schemaHash: mcpMeta.schemaHash,
          enabled: mcpMeta.enabled !== false,
          operationId: typeof op.operationId === 'string' ? op.operationId : mcpMeta.backingToolId,
        });
      }
    }

    return states;
  } catch {
    return [];
  }
}

export function applyMcpDiscoveryToSpec(
  spec: string,
  settings: McpConnectionSettings,
  tools: McpDiscoveredToolRow[]
): string {
  const parsed = JSON.parse(spec);
  const serverUrl = buildMcpBridgeServerUrl(settings.bridgeId);

  parsed.servers = [{ url: serverUrl, description: 'MCP client bridge' }];
  parsed['x-guideants-tool-source'] = {
    kind: 'mcp',
    transport: settings.transport,
    bridgeId: settings.bridgeId,
    toolNamePrefix: settings.toolNamePrefix || undefined,
    ...(settings.transport === 'streamable_http' && settings.url ? { url: settings.url } : {}),
    ...(Object.keys(settings.headers).length > 0
      ? { headers: redactHeadersForStorage(settings.headers) }
      : {}),
  };

  const selectedTools = tools.filter((t) => t.selected && t.diffState !== 'removed');
  const paths: Record<string, Record<string, unknown>> = {};

  for (const tool of selectedTools) {
    const fragment = JSON.parse(tool.schemaFragmentJson) as {
      path: string;
      method: string;
      operation: Record<string, unknown>;
    };
    const path = fragment.path;
    const method = fragment.method.toLowerCase();
    if (!paths[path]) {
      paths[path] = {};
    }
    paths[path][method] = {
      ...fragment.operation,
      'x-guideants-mcp-tool': {
        backingToolId: tool.backingToolId,
        schemaHash: tool.schemaHash,
        enabled: tool.selected,
      },
    };
  }

  parsed.paths = paths;
  return JSON.stringify(parsed, null, 2);
}

export function redactHeadersForStorage(headers: Record<string, string>): Record<string, string> {
  const redacted: Record<string, string> = {};
  for (const [key, value] of Object.entries(headers)) {
    redacted[key] = value ? '***' : value;
  }
  return redacted;
}

export function diffStateChipClassName(state: McpToolDiffState | string): string {
  switch (state) {
    case 'added':
      return 'bg-green-100 text-green-800';
    case 'changed':
      return 'bg-amber-100 text-amber-800';
    case 'removed':
      return 'bg-red-100 text-red-800';
    case 'disabled':
      return 'bg-gray-200 text-gray-700';
    default:
      return 'bg-gray-100 text-gray-600';
  }
}

export function diffStateLabel(state: McpToolDiffState | string): string {
  switch (state) {
    case 'added':
      return 'Added';
    case 'changed':
      return 'Changed';
    case 'removed':
      return 'Removed';
    case 'disabled':
      return 'Disabled';
    case 'unchanged':
      return '';
    default:
      return state;
  }
}

export function validateMcpConnectionSettings(settings: McpConnectionSettings): string | null {
  if (settings.transport === 'streamable_http') {
    if (!settings.url.trim()) {
      return 'MCP server URL is required for streamable HTTP transport.';
    }
    try {
      const uri = new URL(settings.url);
      if (uri.protocol !== 'http:' && uri.protocol !== 'https:') {
        return 'MCP server URL must use http or https.';
      }
    } catch {
      return 'MCP server URL must be a valid absolute URL.';
    }
  }

  if (settings.transport === 'client_bridge' && !settings.bridgeId.trim()) {
    return 'Client bridge id is required for client bridge transport.';
  }

  return null;
}
