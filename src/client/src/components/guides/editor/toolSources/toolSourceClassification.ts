export type ToolSourceKind =
  | 'web-api'
  | 'client-actions'
  | 'sandbox-module'
  | 'local-function'
  | 'mcp-connection'
  | 'unknown';

export type ToolSourceStatus =
  | 'valid'
  | 'needs-attention'
  | 'custom'
  | 'invalid-json';

export const SOURCE_KIND_LABELS: Record<ToolSourceKind, string> = {
  'web-api': 'Web API',
  'client-actions': 'Client Actions',
  'sandbox-module': 'Sandbox Module',
  'local-function': 'Local Function',
  'mcp-connection': 'MCP Connection',
  unknown: 'Unknown',
};

export const CONNECTOR_KEY_LABELS: Record<ToolSourceKind, string> = {
  'web-api': 'API host',
  'client-actions': 'Client bridge',
  'sandbox-module': 'Init module',
  'local-function': 'Local tool host',
  'mcp-connection': 'MCP server',
  unknown: 'Connector key',
};

export function classifySchemeFromServerUrl(serverUrl: string | null): ToolSourceKind {
  if (!serverUrl) {
    return 'unknown';
  }

  try {
    const url = new URL(serverUrl);
    switch (url.protocol.replace(':', '')) {
      case 'http':
      case 'https':
        return 'web-api';
      case 'client':
        if (url.host.startsWith('mcp-bridge-')) {
          return 'mcp-connection';
        }
        return 'client-actions';
      case 'sandbox':
        return 'sandbox-module';
      case 'tool':
        return 'local-function';
      case 'mcp':
        return 'mcp-connection';
      default:
        return 'unknown';
    }
  } catch {
    return 'unknown';
  }
}

export function classifyToolSourceFromSpec(spec: string, serverUrl: string | null): ToolSourceKind {
  try {
    const parsed = JSON.parse(spec);
    const meta = parsed['x-guideants-tool-source'];
    if (meta?.kind === 'mcp') {
      return 'mcp-connection';
    }
  } catch {
    // fall through to URL classification
  }

  return classifySchemeFromServerUrl(serverUrl);
}

export function extractConnectorKeyFromServerUrl(serverUrl: string): string | null {
  try {
    const url = new URL(serverUrl);
    const kind = classifySchemeFromServerUrl(serverUrl);

    if (kind === 'sandbox-module') {
      const host = url.host;
      const path = url.pathname && url.pathname !== '/' ? url.pathname.replace(/^\//, '') : '';
      return path ? `${host}${path.startsWith('/') ? '' : '/'}${path}` : host || null;
    }

    if (kind === 'mcp-connection' && url.host.startsWith('mcp-bridge-')) {
      return url.host.slice('mcp-bridge-'.length);
    }

    return url.host || null;
  } catch {
    return null;
  }
}
