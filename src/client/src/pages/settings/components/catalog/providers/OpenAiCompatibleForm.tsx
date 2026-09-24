import { useState } from 'react';
import type { ProviderAddForm, ProviderEditForm } from './types';

/** Client-side sentinel for "remove the stored API key" (see server OpenAiCompatibleRuntimeConfigSecrets). */
export const OPENAI_COMPATIBLE_REMOVE_KEY_SENTINEL = '__REMOVE__';

/**
 * Builds the row-owned RuntimeConfigJson for the openai-compatible provider:
 * `{ baseUrl, apiKey }`. BaseUrl is the v1 base (absolute http(s), no trailing
 * slash). For edit, when the key input is empty and the key is not being
 * removed, the existing value (possibly an enc::v2 ciphertext) is carried
 * through so the server preserves it byte-for-byte.
 */
export function buildOpenAiCompatibleRuntimeConfigJson(
  baseUrl: string,
  apiKey: string,
  removeKey = false,
  existingJson?: string,
): string {
  const trimmedBaseUrl = baseUrl.trim();
  if (trimmedBaseUrl.length === 0) {
    throw new Error('Base URL is required.');
  }
  if (trimmedBaseUrl.endsWith('/')) {
    throw new Error('Base URL must not include a trailing slash (configure the v1 base, e.g. http://localhost:8000/v1).');
  }
  if (!/^https?:\/\//i.test(trimmedBaseUrl)) {
    throw new Error('Base URL must be an absolute http(s) URL.');
  }
  const payload: Record<string, unknown> = { baseUrl: trimmedBaseUrl };
  if (removeKey) {
    payload.apiKey = OPENAI_COMPATIBLE_REMOVE_KEY_SENTINEL;
  } else if (apiKey.trim().length > 0) {
    payload.apiKey = apiKey.trim();
  } else if (existingJson) {
    try {
      const parsed = JSON.parse(existingJson) as { apiKey?: unknown };
      if (typeof parsed.apiKey === 'string' && parsed.apiKey.length > 0) {
        payload.apiKey = parsed.apiKey;
      }
    } catch {
      // Ignore malformed existing JSON; the server re-validates on save.
    }
  } else {
    payload.apiKey = '';
  }
  return JSON.stringify(payload);
}

interface ParsedOpenAiCompatibleRuntimeConfig {
  baseUrl: string;
  /** True when the stored row carries any non-empty apiKey (plaintext or enc::v2 ciphertext). */
  keySet: boolean;
}

function parseOpenAiCompatibleRuntimeConfig(runtimeConfigJson?: string): ParsedOpenAiCompatibleRuntimeConfig {
  if (!runtimeConfigJson) {
    return { baseUrl: '', keySet: false };
  }
  try {
    const parsed = JSON.parse(runtimeConfigJson) as { baseUrl?: unknown; apiKey?: unknown };
    return {
      baseUrl: typeof parsed.baseUrl === 'string' ? parsed.baseUrl : '',
      keySet: typeof parsed.apiKey === 'string' && parsed.apiKey.length > 0,
    };
  } catch {
    return { baseUrl: '', keySet: false };
  }
}

function ProviderInfo() {
  return (
    <p className="text-sm text-gray-700">
      Connects this model to an OpenAI-compatible endpoint you run (vLLM, Ollama, LM Studio,
      …). The base URL is the v1 base — the server posts to{' '}
      <span className="font-mono">{'{baseUrl}/chat/completions'}</span>. The API key is optional;
      local servers typically run keyless.
    </p>
  );
}

export function OpenAiCompatibleAddForm({ value, onChange }: ProviderAddForm) {
  return (
    <div className="space-y-3">
      <ProviderInfo />
      <label className="block text-sm text-gray-700">
        Base URL
        <input
          type="text"
          value={value.openAiCompatibleBaseUrl}
          onChange={(event) => onChange({ openAiCompatibleBaseUrl: event.target.value })}
          placeholder="http://localhost:8000/v1"
          className="mt-1 w-full rounded border border-gray-300 px-3 py-2 text-sm text-gray-900 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
        />
      </label>
      <label className="block text-sm text-gray-700">
        API key (optional)
        <input
          type="password"
          value={value.openAiCompatibleApiKey}
          onChange={(event) => onChange({ openAiCompatibleApiKey: event.target.value })}
          placeholder="Optional (local servers run keyless)"
          className="mt-1 w-full rounded border border-gray-300 px-3 py-2 text-sm text-gray-900 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
        />
      </label>
    </div>
  );
}

export function OpenAiCompatibleEditForm({ value, onChange }: ProviderEditForm) {
  const existing = parseOpenAiCompatibleRuntimeConfig(value.runtimeConfigJson);
  const [baseUrl, setBaseUrl] = useState(existing.baseUrl);
  const [apiKey, setApiKey] = useState('');
  const [removeKey, setRemoveKey] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const commit = (nextBaseUrl: string, nextApiKey: string, nextRemoveKey: boolean) => {
    setBaseUrl(nextBaseUrl);
    setApiKey(nextApiKey);
    setRemoveKey(nextRemoveKey);
    try {
      onChange({
        runtimeConfigJson: buildOpenAiCompatibleRuntimeConfigJson(
          nextBaseUrl,
          nextApiKey,
          nextRemoveKey,
          value.runtimeConfigJson,
        ),
      });
      setError(null);
    } catch (buildError) {
      setError(buildError instanceof Error ? buildError.message : 'Invalid base URL.');
    }
  };

  return (
    <div className="space-y-3">
      <ProviderInfo />
      {error ? (
        <div className="rounded border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">{error}</div>
      ) : null}
      <label className="block text-sm text-gray-700">
        Base URL
        <input
          type="text"
          value={baseUrl}
          onChange={(event) => commit(event.target.value, apiKey, removeKey)}
          placeholder="http://localhost:8000/v1"
          className="mt-1 w-full rounded border border-gray-300 px-3 py-2 text-sm text-gray-900 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
        />
      </label>
      <label className="block text-sm text-gray-700">
        API key {existing.keySet ? '(set)' : '(optional)'}
        <input
          type="password"
          value={apiKey}
          onChange={(event) => commit(baseUrl, event.target.value, removeKey)}
          placeholder={existing.keySet ? '••• key set — enter a new value to replace' : 'Optional (local servers run keyless)'}
          disabled={removeKey}
          className="mt-1 w-full rounded border border-gray-300 px-3 py-2 text-sm text-gray-900 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500 disabled:bg-gray-100"
        />
      </label>
      {existing.keySet ? (
        <label className="inline-flex items-center gap-2 text-sm text-gray-700">
          <input
            type="checkbox"
            checked={removeKey}
            onChange={(event) => commit(baseUrl, apiKey, event.target.checked)}
            className="h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500"
          />
          Remove API key
        </label>
      ) : null}
    </div>
  );
}
