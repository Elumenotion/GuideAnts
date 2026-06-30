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
  McpRuntimeExecution,
  McpToolDiffState,
  McpToolOperationMetadata,
  McpToolSourceMetadata,
} from './mcpToolSourceTypes';
import { defaultDiscoveryTransport } from './mcpToolSourceTypes';

const LEGACY_CLIENT_MCP_HOST_PREFIX = `mcp-${'bridge'}-`;

export function generateMcpBridgeId(): string {
  if (typeof crypto !== 'undefined' && 'randomUUID' in crypto) {
    return crypto.randomUUID().replace(/-/g, '').slice(0, 12);
  }
  return `mcp${Date.now().toString(36)}`;
}

export function buildMcpServerUrl(bridgeId: string, runtimeExecution: McpRuntimeExecution): string {
  const normalizedBridgeId = bridgeId.startsWith(LEGACY_CLIENT_MCP_HOST_PREFIX)
    ? bridgeId.slice(LEGACY_CLIENT_MCP_HOST_PREFIX.length)
    : bridgeId;
  return runtimeExecution === 'sandbox_subprocess'
    ? `mcp+sandbox://${normalizedBridgeId}`
    : `mcp+api://${normalizedBridgeId}`;
}

/** @deprecated Use buildMcpServerUrl */
export function buildMcpBridgeServerUrl(bridgeId: string): string {
  return buildMcpServerUrl(bridgeId, 'api');
}

export function extractMcpBridgeIdFromServerUrl(serverUrl: string | null): string | null {
  if (!serverUrl) return null;
  try {
    const url = new URL(serverUrl);
    const scheme = url.protocol.replace(':', '');
    if (scheme === 'mcp+api' || scheme === 'mcp+sandbox' || scheme === 'mcp') {
      return url.host || null;
    }
    if (scheme === 'client' && url.host.startsWith(LEGACY_CLIENT_MCP_HOST_PREFIX)) {
      return url.host.slice(LEGACY_CLIENT_MCP_HOST_PREFIX.length);
    }
    return null;
  } catch {
    return null;
  }
}

function normalizeLegacyMetadata(meta: Record<string, unknown>): McpToolSourceMetadata {
  const runtimeExecution =
    (meta.runtimeExecution as McpRuntimeExecution | undefined) ??
    (meta.package ? 'sandbox_subprocess' : 'api');
  const discoveryTransport =
    (meta.discoveryTransport as McpConnectionSettings['discoveryTransport'] | undefined) ??
    defaultDiscoveryTransport(runtimeExecution);

  return {
    kind: 'mcp',
    runtimeExecution,
    discoveryTransport,
    url: typeof meta.url === 'string' ? meta.url : undefined,
    bridgeId: typeof meta.bridgeId === 'string' ? meta.bridgeId : undefined,
    toolNamePrefix: typeof meta.toolNamePrefix === 'string' ? meta.toolNamePrefix : undefined,
    headers:
      meta.headers && typeof meta.headers === 'object'
        ? (meta.headers as Record<string, string>)
        : undefined,
    package:
      meta.package && typeof meta.package === 'object'
        ? (meta.package as McpConnectionSettings['package'])
        : undefined,
    environmentVariables: Array.isArray(meta.environmentVariables)
      ? (meta.environmentVariables as McpConnectionSettings['environmentVariables'])
      : undefined,
  };
}

export function parseMcpToolSourceMetadata(spec: string): McpToolSourceMetadata | null {
  try {
    const parsed = JSON.parse(spec);
    const meta = parsed['x-guideants-tool-source'];
    if (!meta || meta.kind !== 'mcp') {
      return null;
    }
    return normalizeLegacyMetadata(meta as Record<string, unknown>);
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

  const runtimeExecution = meta?.runtimeExecution ?? 'api';

  return {
    runtimeExecution,
    discoveryTransport: meta?.discoveryTransport ?? defaultDiscoveryTransport(runtimeExecution),
    url: meta?.url ?? '',
    bridgeId,
    toolNamePrefix: meta?.toolNamePrefix ?? 'mcp',
    headers: meta?.headers ?? {},
    package: meta?.package,
    environmentVariables: meta?.environmentVariables,
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
  runtimeExecution: McpConnectionSettings['runtimeExecution'];
  discoveryTransport: McpConnectionSettings['discoveryTransport'];
  url?: string;
  bridgeId: string;
  headers: Record<string, string>;
  toolNamePrefix: string;
  package?: McpConnectionSettings['package'];
  environmentVariables?: McpConnectionSettings['environmentVariables'];
  missingSecretRefs: string[];
} {
  const { resolved, missingRefs } = resolveHeaderValues(settings.headers, environmentVariables);
  return {
    runtimeExecution: settings.runtimeExecution,
    discoveryTransport: settings.discoveryTransport,
    url: settings.runtimeExecution === 'api' ? settings.url : undefined,
    bridgeId: settings.bridgeId,
    headers: resolved,
    toolNamePrefix: settings.toolNamePrefix,
    package: settings.package,
    environmentVariables: settings.environmentVariables,
    missingSecretRefs: missingRefs,
  };
}

export function applyMcpDiscoveryToSpec(
  spec: string,
  settings: McpConnectionSettings,
  tools: McpDiscoveredToolRow[]
): string {
  const parsed = JSON.parse(spec);
  const serverUrl = buildMcpServerUrl(settings.bridgeId, settings.runtimeExecution);

  parsed.servers = [
    {
      url: serverUrl,
      description:
        settings.runtimeExecution === 'sandbox_subprocess'
          ? 'MCP sandbox subprocess'
          : 'MCP API execution',
    },
  ];
  parsed['x-guideants-tool-source'] = {
    kind: 'mcp',
    runtimeExecution: settings.runtimeExecution,
    discoveryTransport: settings.discoveryTransport,
    bridgeId: settings.bridgeId,
    toolNamePrefix: settings.toolNamePrefix || undefined,
    ...(settings.runtimeExecution === 'api' && settings.url ? { url: settings.url } : {}),
    ...(settings.package ? { package: settings.package } : {}),
    ...(settings.environmentVariables?.length
      ? { environmentVariables: settings.environmentVariables }
      : {}),
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
  if (!settings.bridgeId.trim()) {
    return 'MCP bridge id is required.';
  }

  const expectedTransport = defaultDiscoveryTransport(settings.runtimeExecution);
  if (settings.discoveryTransport !== expectedTransport) {
    return `discoveryTransport must be ${expectedTransport} for runtimeExecution ${settings.runtimeExecution}.`;
  }

  if (settings.runtimeExecution === 'api') {
    if (!settings.url.trim()) {
      return 'MCP server URL is required for api runtime execution.';
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

  if (settings.runtimeExecution === 'sandbox_subprocess' && !settings.package) {
    return 'Package metadata is required for sandbox_subprocess runtime execution.';
  }

  return null;
}

export function isLegacyClientBridgeMcpSource(spec: string): boolean {
  try {
    const parsed = JSON.parse(spec);
    const serverUrl = parsed.servers?.[0]?.url as string | undefined;
    if (typeof serverUrl === 'string') {
      const url = new URL(serverUrl);
      if (url.protocol.replace(':', '') === 'client' && url.host.startsWith(LEGACY_CLIENT_MCP_HOST_PREFIX)) {
        return true;
      }
    }

    const meta = parsed['x-guideants-tool-source'];
    const legacyTransport = ['client', 'bridge'].join('_');
    return meta?.transport === legacyTransport;
  } catch {
    return false;
  }
}

export function validateMcpToolNamePrefixCollision(
  customTools: Array<{ name: string; openApiSpec: string }>,
  currentIndex: number,
  prefix: string,
): string | null {
  const normalized = prefix.trim() || 'mcp';
  for (let index = 0; index < customTools.length; index += 1) {
    if (index === currentIndex) {
      continue;
    }

    const meta = parseMcpToolSourceMetadata(customTools[index].openApiSpec);
    if (!meta) {
      continue;
    }

    const otherPrefix = meta.toolNamePrefix?.trim() || 'mcp';
    if (otherPrefix === normalized) {
      return `toolNamePrefix '${normalized}' is already used by source ${customTools[index].name}.`;
    }
  }

  return null;
}
