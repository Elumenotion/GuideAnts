import { CustomToolDto } from '../../../../types/guides';
import type { ToolSourceKind } from './toolSourceClassification';
import { extractConnectorKeyFromServerUrl } from './toolSourceClassification';
import { buildMcpBridgeServerUrl, generateMcpBridgeId } from './mcpToolSource';

export type DraftSourceKind =
  | 'web-api'
  | 'client-actions'
  | 'sandbox-module'
  | 'mcp-connection'
  | 'local-function'
  | 'raw-openapi';

const DRAFT_DEFAULTS: Record<
  DraftSourceKind,
  { serverUrl: string; title: string; connectorKey: string }
> = {
  'web-api': {
    serverUrl: 'https://api.example.com',
    title: 'Web API',
    connectorKey: 'api.example.com',
  },
  'client-actions': {
    serverUrl: 'client://my-client-bridge',
    title: 'Client Actions',
    connectorKey: 'my-client-bridge',
  },
  'sandbox-module': {
    serverUrl: 'sandbox://__init__.py',
    title: 'Sandbox Module',
    connectorKey: '__init__.py',
  },
  'mcp-connection': {
    serverUrl: 'client://mcp-bridge-new',
    title: 'MCP Connection',
    connectorKey: 'new',
  },
  'local-function': {
    serverUrl: 'tool://localhost',
    title: 'Local Function',
    connectorKey: 'localhost',
  },
  'raw-openapi': {
    serverUrl: 'https://api.example.com',
    title: 'Custom OpenAPI',
    connectorKey: 'api.example.com',
  },
};

export function buildEmptyOpenApiDescriptor(
  kind: DraftSourceKind,
  options?: { customDescriptor?: boolean }
): string {
  const defaults = DRAFT_DEFAULTS[kind];
  const spec: Record<string, unknown> = {
    openapi: '3.0.1',
    info: {
      title: defaults.title,
      version: '1.0.0',
    },
    servers: [
      kind === 'sandbox-module'
        ? { url: defaults.serverUrl, description: 'Sandbox execution environment' }
        : { url: defaults.serverUrl },
    ],
    paths: {},
  };

  if (options?.customDescriptor || kind === 'raw-openapi') {
    spec['x-guideants-custom-descriptor'] = true;
  }

  if (kind === 'raw-openapi') {
    spec['x-guideants-tool-source'] = { kind: 'raw' };
  } else if (kind === 'mcp-connection') {
    const bridgeId = generateMcpBridgeId();
    spec.servers = [{ url: buildMcpBridgeServerUrl(bridgeId), description: 'MCP client bridge' }];
    spec['x-guideants-tool-source'] = {
      kind: 'mcp',
      transport: 'streamable_http',
      bridgeId,
      toolNamePrefix: 'mcp',
    };
  } else {
    const sourceKind = kind === 'local-function' ? 'local-function' : kind;
    spec['x-guideants-tool-source'] = { kind: sourceKind };
  }

  return JSON.stringify(spec, null, 2);
}

function uniqueName(base: string, existing: Set<string>): string {
  let name = base;
  let counter = 2;
  while (existing.has(name)) {
    name = `${base} ${counter++}`;
  }
  return name;
}

export function createDraftCustomTool(
  kind: DraftSourceKind,
  existingTools: CustomToolDto[]
): { tool: CustomToolDto; focusFieldId: string; sourceKind: ToolSourceKind } {
  const existingNames = new Set(existingTools.map((t) => t.name));
  const defaults = DRAFT_DEFAULTS[kind];
  const isCustom = kind === 'raw-openapi';
  const openApiSpec = buildEmptyOpenApiDescriptor(kind, { customDescriptor: isCustom });

  let apiHost = defaults.connectorKey;
  if (kind === 'mcp-connection') {
    try {
      const parsed = JSON.parse(openApiSpec);
      const bridgeId = parsed['x-guideants-tool-source']?.bridgeId;
      if (typeof bridgeId === 'string') {
        apiHost = bridgeId;
      }
    } catch {
      // keep default
    }
  }

  const tool: CustomToolDto = {
    name: uniqueName(apiHost, existingNames),
    openApiSpec,
    apiHost,
  };

  const focusFieldId =
    kind === 'web-api' || kind === 'raw-openapi'
      ? 'server-url'
      : kind === 'mcp-connection'
        ? 'mcp-bridge-id'
        : kind === 'client-actions'
          ? 'client-bridge-id'
          : kind === 'sandbox-module'
            ? 'init-module'
            : 'local-target';

  const sourceKind: ToolSourceKind =
    kind === 'raw-openapi'
      ? 'web-api'
      : kind === 'local-function'
        ? 'local-function'
        : kind === 'mcp-connection'
          ? 'mcp-connection'
          : kind;

  return { tool, focusFieldId, sourceKind };
}

export function buildServerUrlForConnectorKey(
  sourceKind: ToolSourceKind,
  connectorKey: string
): string {
  switch (sourceKind) {
    case 'client-actions':
      return `client://${connectorKey}`;
    case 'mcp-connection':
      return buildMcpBridgeServerUrl(connectorKey);
    case 'sandbox-module':
      return connectorKey.startsWith('sandbox://')
        ? connectorKey
        : `sandbox://${connectorKey}`;
    case 'local-function':
      return 'tool://localhost';
    case 'web-api':
    default:
      if (connectorKey.startsWith('http://') || connectorKey.startsWith('https://')) {
        return connectorKey;
      }
      return `https://${connectorKey}`;
  }
}

export function updateConnectorKeyInTool(
  tool: CustomToolDto,
  sourceKind: ToolSourceKind,
  connectorKey: string
): CustomToolDto {
  const serverUrl = buildServerUrlForConnectorKey(sourceKind, connectorKey);
  let parsed: Record<string, unknown>;
  try {
    parsed = JSON.parse(tool.openApiSpec);
  } catch {
    return tool;
  }

  if (parsed.servers && Array.isArray(parsed.servers) && parsed.servers.length > 0) {
    (parsed.servers[0] as Record<string, unknown>).url = serverUrl;
  } else {
    parsed.servers = [{ url: serverUrl }];
  }

  const derivedKey = extractConnectorKeyFromServerUrl(serverUrl) ?? connectorKey;

  return {
    ...tool,
    openApiSpec: JSON.stringify(parsed, null, 2),
    apiHost: derivedKey,
    name: derivedKey,
  };
}
