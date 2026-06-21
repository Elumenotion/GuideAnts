import type { EnvironmentVariableDto } from '../../../../types/guides';
import {
  formatSecretRef,
  normalizeHeaderValueForStorage,
  parseSecretRef,
  resolveHeaderValues,
} from './environmentVariableRefs';
import type {
  McpConnectionSettings,
  McpDiscoveredToolRow,
  McpHeaderRow,
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

export function parseMcpHeaderRows(headers: Record<string, string>): McpHeaderRow[] {
  return Object.entries(headers).map(([key, value]) => {
    const secretRefName = parseSecretRef(value);
    if (secretRefName) {
      return { key, secretRefName, literalValue: '', useLiteral: false };
    }

    if (value === '***') {
      return { key, secretRefName: '', literalValue: '', useLiteral: false };
    }

    return { key, secretRefName: '', literalValue: value, useLiteral: true };
  });
}

export function mcpHeaderRowsToHeaders(rows: McpHeaderRow[]): Record<string, string> {
  const headers: Record<string, string> = {};
  for (const row of rows) {
    const key = row.key.trim();
    if (!key) continue;

    if (row.useLiteral) {
      if (row.literalValue) {
        headers[key] = row.literalValue;
      }
      continue;
    }

    if (row.secretRefName.trim()) {
      headers[key] = formatSecretRef(row.secretRefName);
    }
  }
  return headers;
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

export function buildResolvedMcpConnectionPayload(
  settings: McpConnectionSettings,
  environmentVariables: EnvironmentVariableDto[]
): {
  transport: McpConnectionSettings['transport'];
  url?: string;
  bridgeId: string;
  headers: Record<string, string>;
  toolNamePrefix: string;
  missingSecretRefs: string[];
} {
  const { resolved, missingRefs } = resolveHeaderValues(settings.headers, environmentVariables);
  return {
    transport: settings.transport,
    url: settings.transport === 'streamable_http' ? settings.url : undefined,
    bridgeId: settings.bridgeId,
    headers: resolved,
    toolNamePrefix: settings.toolNamePrefix,
    missingSecretRefs: missingRefs,
  };
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
      ? {
          headers: Object.fromEntries(
            Object.entries(settings.headers).map(([key, value]) => [
              key,
              normalizeHeaderValueForStorage(value),
            ])
          ),
        }
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

export function headersContainUnresolvedSecrets(
  headers: Record<string, string>,
  environmentVariables: EnvironmentVariableDto[]
): string[] {
  return resolveHeaderValues(headers, environmentVariables).missingRefs;
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
