// API Configuration - Runtime API URL from window object

declare global {
  interface Window {
    __RUNTIME_CONFIG__?: {
      apiUrl: string;
    };
  }
}

export function resolveUrlAgainstOrigin(value: string): URL {
  return new URL(value, window.location.origin);
}

export function resolveAgainstApiBase(value: string, apiBaseUrl: string = API_BASE_URL): URL {
  return new URL(value, resolveUrlAgainstOrigin(apiBaseUrl));
}

function normalizeConfiguredApiUrl(value?: string): string | undefined {
  const trimmed = value?.trim();
  if (!trimmed || trimmed === 'undefined' || trimmed === 'null') {
    return undefined;
  }

  if (trimmed.length > 1) {
    return trimmed.replace(/\/+$/, '');
  }

  return trimmed;
}

/**
 * Get the API base URL from runtime configuration
 */
export function getApiBaseUrl(): string {
  // Runtime config injected into window
  const runtimeApiUrl = normalizeConfiguredApiUrl(window.__RUNTIME_CONFIG__?.apiUrl);
  if (runtimeApiUrl) {
    return runtimeApiUrl;
  }

  // Build-time environment variable
  const configuredApiUrl = normalizeConfiguredApiUrl(import.meta.env.VITE_API_URL);
  if (configuredApiUrl) {
    return configuredApiUrl;
  }

  throw new Error(
    'API URL not configured. Set window.__RUNTIME_CONFIG__.apiUrl or VITE_API_URL.'
  );
}

export const API_BASE_URL = getApiBaseUrl();

export function getApiOrigin(apiBaseUrl: string = API_BASE_URL): string {
  return resolveUrlAgainstOrigin(apiBaseUrl).origin;
}

export function getApiHost(apiBaseUrl: string = API_BASE_URL): string {
  return resolveUrlAgainstOrigin(apiBaseUrl).host;
}
