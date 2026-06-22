import { useMemo, useState } from 'react';
import { API_BASE_URL } from '../../../config/apiConfig';

export type WireApiEndpointFlagKey =
  | 'models'
  | 'chatCompletions'
  | 'responses'
  | 'embeddings'
  | 'imageGenerations'
  | 'audioTranscriptions'
  | 'audioSpeech';

export type WireApiAliasKey = 'guide' | 'embeddings' | 'image' | 'transcription' | 'speech';

export type WireApiMaxRequestSizeKey =
  | 'chatCompletionsBytes'
  | 'responsesBytes'
  | 'embeddingsBytes'
  | 'imageGenerationsBytes'
  | 'audioTranscriptionsBytes'
  | 'audioSpeechBytes';

export type WireApiEndpointFlagsState = Record<WireApiEndpointFlagKey, boolean>;
export type WireApiAliasState = Record<WireApiAliasKey, string>;
export type WireApiMaxRequestSizesState = Partial<Record<WireApiMaxRequestSizeKey, number>>;

interface ApisTabProps {
  publishedGuideId?: string;
  wireApiEnabled: boolean;
  setWireApiEnabled: (enabled: boolean) => void;
  wireApiProfile: string;
  setWireApiProfile: (profile: string) => void;
  endpointFlags: WireApiEndpointFlagsState;
  setEndpointFlag: (key: WireApiEndpointFlagKey, value: boolean) => void;
  aliases: WireApiAliasState;
  setAlias: (key: WireApiAliasKey, value: string) => void;
  maxRequestSizes: WireApiMaxRequestSizesState;
  setMaxRequestSize: (key: WireApiMaxRequestSizeKey, value: number | undefined) => void;
  hasApiKey: boolean;
  authWebhookUrl: string;
}

const endpointRows: Array<{ key: WireApiEndpointFlagKey; label: string; route: string }> = [
  { key: 'models', label: 'Models', route: 'GET /models' },
  { key: 'chatCompletions', label: 'Chat Completions', route: 'POST /chat/completions' },
  { key: 'responses', label: 'Responses', route: 'POST /responses' },
  { key: 'embeddings', label: 'Embeddings', route: 'POST /embeddings' },
  { key: 'imageGenerations', label: 'Image Generations', route: 'POST /images/generations' },
  { key: 'audioTranscriptions', label: 'Audio Transcriptions', route: 'POST /audio/transcriptions' },
  { key: 'audioSpeech', label: 'Audio Speech', route: 'POST /audio/speech' },
];

const maxRequestRows: Array<{ key: WireApiMaxRequestSizeKey; label: string }> = [
  { key: 'chatCompletionsBytes', label: 'Chat Completions Max Request Bytes' },
  { key: 'responsesBytes', label: 'Responses Max Request Bytes' },
  { key: 'embeddingsBytes', label: 'Embeddings Max Request Bytes' },
  { key: 'imageGenerationsBytes', label: 'Image Generations Max Request Bytes' },
  { key: 'audioTranscriptionsBytes', label: 'Audio Transcriptions Max Request Bytes' },
  { key: 'audioSpeechBytes', label: 'Audio Speech Max Request Bytes' },
];

export function ApisTab({
  publishedGuideId,
  wireApiEnabled,
  setWireApiEnabled,
  wireApiProfile,
  setWireApiProfile,
  endpointFlags,
  setEndpointFlag,
  aliases,
  setAlias,
  maxRequestSizes,
  setMaxRequestSize,
  hasApiKey,
  authWebhookUrl,
}: ApisTabProps) {
  const [copied, setCopied] = useState(false);

  const baseUrl = useMemo(() => {
    if (publishedGuideId) {
      return `${API_BASE_URL}/published/openai/${publishedGuideId}/v1`;
    }

    return `${API_BASE_URL}/published/openai/{pubId}/v1`;
  }, [publishedGuideId]);

  const nonChatEnabled =
    endpointFlags.embeddings ||
    endpointFlags.imageGenerations ||
    endpointFlags.audioTranscriptions ||
    endpointFlags.audioSpeech;
  const chatEnabled = endpointFlags.chatCompletions || endpointFlags.responses;

  const missingProviderServiceMode = wireApiEnabled && nonChatEnabled && !wireApiProfile.trim();
  const missingChatAlias = wireApiEnabled && chatEnabled && !aliases.guide.trim();
  const authMode = hasApiKey ? 'api_key' : authWebhookUrl.trim() ? 'webhook' : 'anonymous';
  const sdkAuthWarning = wireApiEnabled && authMode !== 'api_key';

  const readiness = !wireApiEnabled
    ? { label: 'Disabled', classes: 'bg-gray-100 text-gray-700' }
    : missingProviderServiceMode
      ? { label: 'Missing provider/service mode', classes: 'bg-amber-100 text-amber-800' }
      : missingChatAlias
        ? { label: 'Missing chat model alias', classes: 'bg-amber-100 text-amber-800' }
        : { label: 'Enabled', classes: 'bg-green-100 text-green-800' };

  const copyBaseUrl = async () => {
    await navigator.clipboard.writeText(baseUrl);
    setCopied(true);
    setTimeout(() => setCopied(false), 2000);
  };

  const jsExample = `import OpenAI from "openai";

const client = new OpenAI({
  apiKey: process.env.GUIDEANTS_API_KEY,
  baseURL: "${baseUrl}",
});

const response = await client.chat.completions.create({
  model: "${aliases.guide || 'guide'}",
  messages: [{ role: "user", content: "Hello from SDK" }],
});`;

  const pyExample = `from openai import OpenAI
import os

client = OpenAI(
    api_key=os.environ["GUIDEANTS_API_KEY"],
    base_url="${baseUrl}",
)

response = client.chat.completions.create(
    model="${aliases.guide || 'guide'}",
    messages=[{"role": "user", "content": "Hello from SDK"}],
)`;

  const curlExample = `curl -X POST "${baseUrl}/chat/completions" \\
  -H "Content-Type: application/json" \\
  -H "Authorization: Bearer <api-key>" \\
  -d '{
    "model": "${aliases.guide || 'guide'}",
    "messages": [{"role":"user","content":"Hello from curl"}]
  }'`;

  return (
    <div className="space-y-6">
      <div className="flex items-center justify-between">
        <h3 className="text-lg font-medium text-gray-900">OpenAI-Compatible APIs</h3>
        <span className={`inline-flex items-center px-2.5 py-1 rounded text-xs font-medium ${readiness.classes}`}>
          {readiness.label}
        </span>
      </div>

      <div className="border border-gray-200 rounded-lg p-4 space-y-4">
        <label className="flex items-center justify-between">
          <div className="pr-4">
            <p className="text-sm font-medium text-gray-900">Enable OpenAI-compatible APIs</p>
            <p className="text-xs text-gray-500 mt-1">
              Enables <code>/api/published/openai/{'{pubId}'}/v1</code> for this published guide.
            </p>
          </div>
          <input
            id="wireApiEnabled"
            aria-label="Enable OpenAI-compatible APIs"
            type="checkbox"
            checked={wireApiEnabled}
            onChange={(e) => setWireApiEnabled(e.target.checked)}
            className="h-4 w-4 text-blue-600 rounded border-gray-300"
          />
        </label>

        <div>
          <label htmlFor="wireApiProfile" className="block text-sm font-medium text-gray-700 mb-1">
            Provider / Service Mode Profile
          </label>
          <input
            id="wireApiProfile"
            value={wireApiProfile}
            onChange={(e) => setWireApiProfile(e.target.value)}
            className="w-full px-3 py-2 border border-gray-300 rounded-md font-mono text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
            placeholder="example: openai_default"
          />
          <p className="text-xs text-gray-500 mt-1">
            Required for non-chat endpoints when those endpoints are enabled.
          </p>
        </div>

        {sdkAuthWarning && (
          <div className="p-3 bg-amber-50 border border-amber-200 rounded-md text-sm text-amber-800">
            OpenAI SDKs work best with API key authentication. Current auth mode: <strong>{authMode}</strong>.
          </div>
        )}
      </div>

      <div className="border border-gray-200 rounded-lg p-4">
        <h4 className="text-sm font-medium text-gray-900 mb-3">Endpoint Toggles</h4>
        <div className="space-y-2">
          {endpointRows.map((row) => (
            <label key={row.key} className="flex items-center justify-between py-1">
              <div>
                <span className="text-sm text-gray-800">{row.label}</span>
                <span className="ml-2 text-xs text-gray-500 font-mono">{row.route}</span>
              </div>
              <input
                aria-label={`${row.label} enabled`}
                type="checkbox"
                checked={endpointFlags[row.key]}
                onChange={(e) => setEndpointFlag(row.key, e.target.checked)}
                className="h-4 w-4 text-blue-600 rounded border-gray-300"
              />
            </label>
          ))}
        </div>
      </div>

      <div className="border border-gray-200 rounded-lg p-4 space-y-4">
        <h4 className="text-sm font-medium text-gray-900">Model Alias Mapping</h4>
        <p className="text-xs text-gray-500">
          Client requests must use aliases, not provider-native model IDs.
        </p>
        <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
          <div>
            <label htmlFor="aliasGuide" className="block text-sm font-medium text-gray-700 mb-1">Guide model alias</label>
            <input
              id="aliasGuide"
              value={aliases.guide}
              onChange={(e) => setAlias('guide', e.target.value)}
              className="w-full px-3 py-2 border border-gray-300 rounded-md font-mono text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
              placeholder="guide"
            />
          </div>
          <div>
            <label htmlFor="aliasEmbeddings" className="block text-sm font-medium text-gray-700 mb-1">Embeddings alias</label>
            <input
              id="aliasEmbeddings"
              value={aliases.embeddings}
              onChange={(e) => setAlias('embeddings', e.target.value)}
              className="w-full px-3 py-2 border border-gray-300 rounded-md font-mono text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
              placeholder="embeddings"
            />
          </div>
          <div>
            <label htmlFor="aliasImage" className="block text-sm font-medium text-gray-700 mb-1">Image alias</label>
            <input
              id="aliasImage"
              value={aliases.image}
              onChange={(e) => setAlias('image', e.target.value)}
              className="w-full px-3 py-2 border border-gray-300 rounded-md font-mono text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
              placeholder="image"
            />
          </div>
          <div>
            <label htmlFor="aliasTranscription" className="block text-sm font-medium text-gray-700 mb-1">Transcription alias</label>
            <input
              id="aliasTranscription"
              value={aliases.transcription}
              onChange={(e) => setAlias('transcription', e.target.value)}
              className="w-full px-3 py-2 border border-gray-300 rounded-md font-mono text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
              placeholder="transcription"
            />
          </div>
          <div>
            <label htmlFor="aliasSpeech" className="block text-sm font-medium text-gray-700 mb-1">Speech alias</label>
            <input
              id="aliasSpeech"
              value={aliases.speech}
              onChange={(e) => setAlias('speech', e.target.value)}
              className="w-full px-3 py-2 border border-gray-300 rounded-md font-mono text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
              placeholder="speech"
            />
          </div>
        </div>
      </div>

      <div className="border border-gray-200 rounded-lg p-4 space-y-4">
        <h4 className="text-sm font-medium text-gray-900">Max Request Size (Bytes)</h4>
        <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
          {maxRequestRows.map((row) => (
            <div key={row.key}>
              <label htmlFor={row.key} className="block text-sm font-medium text-gray-700 mb-1">
                {row.label}
              </label>
              <input
                id={row.key}
                type="number"
                min="1"
                value={maxRequestSizes[row.key] ?? ''}
                onChange={(e) =>
                  setMaxRequestSize(
                    row.key,
                    e.target.value ? Math.max(1, parseInt(e.target.value, 10)) : undefined
                  )
                }
                className="w-full px-3 py-2 border border-gray-300 rounded-md font-mono text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
                placeholder="Use server default"
              />
            </div>
          ))}
        </div>
      </div>

      <div className="border border-gray-200 rounded-lg p-4 space-y-4">
        <h4 className="text-sm font-medium text-gray-900">Base URL and Headers</h4>
        <div className="flex items-center gap-2">
          <code className="flex-1 px-3 py-2 bg-gray-50 border border-gray-200 rounded text-xs font-mono text-gray-900 break-all">
            {baseUrl}
          </code>
          <button
            type="button"
            onClick={copyBaseUrl}
            className="px-3 py-2 text-xs font-medium text-blue-700 bg-blue-50 border border-blue-200 rounded hover:bg-blue-100"
          >
            {copied ? 'Copied!' : 'Copy'}
          </button>
        </div>
        {!publishedGuideId && (
          <p className="text-xs text-gray-500">
            Publish first to get a concrete <code>{'{pubId}'}</code> in this URL.
          </p>
        )}
        <div className="text-xs text-gray-700 space-y-1">
          {hasApiKey && (
            <>
              <p><strong>Authorization:</strong> Bearer {'<api-key>'}</p>
              <p><strong>x-guideants-apikey:</strong> {'<api-key>'}</p>
            </>
          )}
          {!hasApiKey && authWebhookUrl.trim() && (
            <>
              <p><strong>Authorization:</strong> Bearer {'<token>'}</p>
              <p><strong>X-Published-Auth:</strong> {'<token>'}</p>
            </>
          )}
          {!hasApiKey && !authWebhookUrl.trim() && (
            <p>No auth header required (anonymous mode).</p>
          )}
        </div>
      </div>

      <div className="border border-gray-200 rounded-lg p-4 space-y-4">
        <h4 className="text-sm font-medium text-gray-900">SDK Examples</h4>
        <div>
          <p className="text-xs font-medium text-gray-700 mb-1">curl</p>
          <pre className="p-3 bg-gray-50 border border-gray-200 rounded text-xs font-mono overflow-x-auto">{curlExample}</pre>
        </div>
        <div>
          <p className="text-xs font-medium text-gray-700 mb-1">OpenAI JavaScript SDK</p>
          <pre className="p-3 bg-gray-50 border border-gray-200 rounded text-xs font-mono overflow-x-auto">{jsExample}</pre>
        </div>
        <div>
          <p className="text-xs font-medium text-gray-700 mb-1">OpenAI Python SDK</p>
          <pre className="p-3 bg-gray-50 border border-gray-200 rounded text-xs font-mono overflow-x-auto">{pyExample}</pre>
        </div>
      </div>
    </div>
  );
}

