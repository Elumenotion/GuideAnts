import { afterEach, describe, expect, test, vi } from 'vitest';

async function loadApiConfig() {
  vi.resetModules();
  return import('./apiConfig');
}

describe('apiConfig', () => {
  const env = import.meta.env as unknown as Record<string, string | undefined>;
  const originalApiUrl = env.VITE_API_URL;
  const originalElectron = (window as any).electron;

  afterEach(() => {
    env.VITE_API_URL = originalApiUrl;
    (window as any).electron = originalElectron;
    delete window.__RUNTIME_CONFIG__;
  });

  test('uses runtime config first', async () => {
    env.VITE_API_URL = 'http://env.example/api';
    window.__RUNTIME_CONFIG__ = { apiUrl: 'http://runtime.example/api' };
    (window as any).electron = undefined;
    const { getApiBaseUrl } = await loadApiConfig();

    expect(getApiBaseUrl()).toBe('http://runtime.example/api');
  });

  test('uses VITE_API_URL when runtime config is absent', async () => {
    delete window.__RUNTIME_CONFIG__;
    env.VITE_API_URL = 'http://env.example/api';
    (window as any).electron = undefined;
    const { getApiBaseUrl } = await loadApiConfig();

    expect(getApiBaseUrl()).toBe('http://env.example/api');
  });

  test('requires explicit API URL when no runtime override is set', async () => {
    delete window.__RUNTIME_CONFIG__;
    env.VITE_API_URL = undefined;
    (window as any).electron = undefined;
    await expect(loadApiConfig()).rejects.toThrow('API URL not configured');
  });

  test('resolveAgainstApiBase handles relative API base safely', async () => {
    env.VITE_API_URL = 'http://env.example/api';
    const { resolveAgainstApiBase } = await loadApiConfig();
    const resolved = resolveAgainstApiBase('/api/projects', '/api');
    expect(resolved.origin).toBe(window.location.origin);
    expect(resolved.pathname).toBe('/api/projects');
  });

  test('resolveAgainstApiBase handles absolute API base safely', async () => {
    env.VITE_API_URL = 'http://env.example/api';
    const { resolveAgainstApiBase } = await loadApiConfig();
    const resolved = resolveAgainstApiBase('/api/projects', 'https://api.example.com/api');
    expect(resolved.origin).toBe('https://api.example.com');
    expect(resolved.pathname).toBe('/api/projects');
  });
});
