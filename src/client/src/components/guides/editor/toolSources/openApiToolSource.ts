import { HTTP_METHODS } from './openApiToolSourceConstants';

export interface OpenApiTool {
  operationId: string;
  method: string;
  path: string;
  summary?: string;
  description?: string;
}

export function extractServerUrl(spec: string): string | null {
  try {
    const parsed = JSON.parse(spec);

    if (parsed.servers && Array.isArray(parsed.servers) && parsed.servers.length > 0) {
      return parsed.servers[0].url;
    }

    if (parsed.host) {
      const scheme = parsed.schemes?.[0] || 'https';
      return `${scheme}://${parsed.host}`;
    }

    return null;
  } catch {
    return null;
  }
}

/** @deprecated Use extractConnectorKeyFromServerUrl from toolSourceClassification */
export function extractHostFromUrl(url: string): string | null {
  try {
    const urlObj = new URL(url);
    return urlObj.host;
  } catch {
    return null;
  }
}

export function extractTools(spec: string): OpenApiTool[] {
  try {
    const parsed = JSON.parse(spec);
    const tools: OpenApiTool[] = [];

    if (parsed.paths && typeof parsed.paths === 'object') {
      for (const [path, pathItem] of Object.entries(parsed.paths)) {
        if (typeof pathItem === 'object' && pathItem !== null) {
          for (const method of HTTP_METHODS) {
            const operation = (pathItem as Record<string, unknown>)[method];
            if (operation && typeof operation === 'object') {
              const op = operation as Record<string, unknown>;
              tools.push({
                operationId:
                  (typeof op.operationId === 'string' && op.operationId) ||
                  `${method}_${path.replace(/\//g, '_')}`,
                method: method.toUpperCase(),
                path,
                summary: typeof op.summary === 'string' ? op.summary : undefined,
                description: typeof op.description === 'string' ? op.description : undefined,
              });
            }
          }
        }
      }
    }

    return tools;
  } catch {
    return [];
  }
}

export function isConnectorKeyUnique(
  connectorKey: string,
  tools: { apiHost?: string }[],
  currentIndex: number
): boolean {
  return !tools.some((tool, idx) => idx !== currentIndex && tool.apiHost === connectorKey);
}

export function updateServerUrlInSpec(spec: string, serverUrl: string): string {
  const parsed = JSON.parse(spec);

  if (parsed.openapi) {
    if (parsed.servers && Array.isArray(parsed.servers) && parsed.servers.length > 0) {
      parsed.servers[0].url = serverUrl;
    } else {
      parsed.servers = [{ url: serverUrl }];
    }
  } else if (parsed.swagger) {
    const urlObj = new URL(serverUrl);
    parsed.host = urlObj.host;
    parsed.schemes = [urlObj.protocol.replace(':', '')];
    if (urlObj.pathname && urlObj.pathname !== '/') {
      parsed.basePath = urlObj.pathname;
    }
  }

  return JSON.stringify(parsed, null, 2);
}
