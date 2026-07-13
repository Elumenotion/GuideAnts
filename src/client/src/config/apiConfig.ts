// API Configuration - Runtime API URL from window object

import { isElectron } from '../utils/environment';

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

function getApiProxyTarget(): string | undefined {
  return normalizeConfiguredApiUrl(import.meta.env.VITE_API_PROXY_TARGET);
}

/**
 * Resolve a user-facing API URL for copy/paste (SDK examples, curl, MCP endpoint).
 * Browser same-origin deployments use the page host; Electron uses the real API host.
 */
export function resolveExternalApiUrl(
  pathFromApiBase: string,
  apiBaseUrl: string = API_BASE_URL,
): string {
  const suffix = pathFromApiBase.startsWith('/') ? pathFromApiBase : `/${pathFromApiBase}`;
  const combined = `${apiBaseUrl.replace(/\/+$/, '')}${suffix}`;

  if (/^https?:\/\//i.test(combined)) {
    return new URL(combined).href;
  }

  if (isElectron()) {
    const proxyTarget = getApiProxyTarget();
    if (proxyTarget) {
      return new URL(combined, `${proxyTarget}/`).href;
    }
  }

  return resolveUrlAgainstOrigin(combined).href;
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
