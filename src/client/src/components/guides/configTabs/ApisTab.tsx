import { useMemo, useState } from 'react';
import { API_BASE_URL } from '../../../config/apiConfig';

export type WireApiEndpointFlagKey =
  | 'models'
  | 'chatCompletions'
  | 'responses'
  | 'messages'
  | 'embeddings'
  | 'imageGenerations'
  | 'audioTranscriptions'
  | 'audioSpeech';

export type WireApiAliasKey = 'guide' | 'embeddings' | 'image' | 'transcription' | 'speech';

export type WireApiMaxRequestSizeKey =
  | 'chatCompletionsBytes'
  | 'responsesBytes'
  | 'messagesBytes'
  | 'embeddingsBytes'
  | 'imageGenerationsBytes'
  | 'audioTranscriptionsBytes'
  | 'audioSpeechBytes';

export type WireApiEndpointFlagsState = Record<WireApiEndpointFlagKey, boolean>;
export type WireApiAliasState = Record<WireApiAliasKey, string>;
export type WireApiMaxRequestSizesState = Partial<Record<WireApiMaxRequestSizeKey, number>>;
type WireApiExampleTabId =
  | 'models'
  | 'chat'
  | 'responses'
  | 'messages'
  | 'embeddings'
  | 'image'
  | 'transcription'
  | 'speech';

interface ApisTabProps {
  publishedGuideId?: string;
  wireApiEnabled: boolean;
  setWireApiEnabled: (enabled: boolean) => void;
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
  { key: 'messages', label: 'Messages (Anthropic)', route: 'POST /messages' },
  { key: 'embeddings', label: 'Embeddings', route: 'POST /embeddings' },
  { key: 'imageGenerations', label: 'Image Generations', route: 'POST /images/generations' },
  { key: 'audioTranscriptions', label: 'Audio Transcriptions', route: 'POST /audio/transcriptions' },
  { key: 'audioSpeech', label: 'Audio Speech', route: 'POST /audio/speech' },
];

const maxRequestRows: Array<{ key: WireApiMaxRequestSizeKey; label: string }> = [
  { key: 'chatCompletionsBytes', label: 'Chat Completions Max Request Bytes' },
  { key: 'responsesBytes', label: 'Responses Max Request Bytes' },
  { key: 'messagesBytes', label: 'Messages Max Request Bytes' },
  { key: 'embeddingsBytes', label: 'Embeddings Max Request Bytes' },
  { key: 'imageGenerationsBytes', label: 'Image Generations Max Request Bytes' },
  { key: 'audioTranscriptionsBytes', label: 'Audio Transcriptions Max Request Bytes' },
  { key: 'audioSpeechBytes', label: 'Audio Speech Max Request Bytes' },
];

export function ApisTab({
  publishedGuideId,
  wireApiEnabled,
  setWireApiEnabled,
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
  const [activeExampleTab, setActiveExampleTab] = useState<WireApiExampleTabId>('chat');

  const openAiBaseUrl = useMemo(() => {
    const resolvedApiBase = new URL(API_BASE_URL, window.location.origin);
    const apiBasePath = resolvedApiBase.pathname === '/'
      ? ''
      : resolvedApiBase.pathname.replace(/\/+$/, '');
    const pubPath = publishedGuideId
      ? `/published/openai/${publishedGuideId}/v1`
      : '/published/openai/{pubId}/v1';
    return `${window.location.origin}${apiBasePath}${pubPath}`;
  }, [publishedGuideId]);

  const anthropicBaseUrl = useMemo(() => {
    const resolvedApiBase = new URL(API_BASE_URL, window.location.origin);
    const apiBasePath = resolvedApiBase.pathname === '/'
      ? ''
      : resolvedApiBase.pathname.replace(/\/+$/, '');
    const pubPath = publishedGuideId
      ? `/published/anthropic/${publishedGuideId}/v1`
      : '/published/anthropic/{pubId}/v1';
    return `${window.location.origin}${apiBasePath}${pubPath}`;
  }, [publishedGuideId]);

  const authMode = hasApiKey ? 'api_key' : authWebhookUrl.trim() ? 'webhook' : 'anonymous';
  const sdkAuthWarning = wireApiEnabled && authMode !== 'api_key';

  const aliasValues = useMemo(
    () => ({
      guide: aliases.guide || 'guide',
      embeddings: aliases.embeddings || 'embeddings',
      image: aliases.image || 'image',
      transcription: aliases.transcription || 'transcription',
      speech: aliases.speech || 'speech',
    }),
    [aliases]
  );

  const exampleTabs = useMemo(
    () => [
      {
        id: 'models' as const,
        label: 'Models',
        route: 'GET /models',
        path: '/models',
        modelAlias: aliasValues.guide,
        enabled: endpointFlags.models,
        baseKind: 'openai' as const,
      },
      {
        id: 'chat' as const,
        label: 'Chat',
        route: 'POST /chat/completions',
        path: '/chat/completions',
        modelAlias: aliasValues.guide,
        enabled: endpointFlags.chatCompletions,
        baseKind: 'openai' as const,
      },
      {
        id: 'responses' as const,
        label: 'Responses',
        route: 'POST /responses',
        path: '/responses',
        modelAlias: aliasValues.guide,
        enabled: endpointFlags.responses,
        baseKind: 'openai' as const,
      },
      {
        id: 'messages' as const,
        label: 'Messages',
        route: 'POST /messages',
        path: '/messages',
        modelAlias: aliasValues.guide,
        enabled: endpointFlags.messages,
        baseKind: 'anthropic' as const,
      },
      {
        id: 'embeddings' as const,
        label: 'Embeddings',
        route: 'POST /embeddings',
        path: '/embeddings',
        modelAlias: aliasValues.embeddings,
        enabled: endpointFlags.embeddings,
        baseKind: 'openai' as const,
      },
      {
        id: 'image' as const,
        label: 'Image',
        route: 'POST /images/generations',
        path: '/images/generations',
        modelAlias: aliasValues.image,
        enabled: endpointFlags.imageGenerations,
        baseKind: 'openai' as const,
      },
      {
        id: 'transcription' as const,
        label: 'Transcription',
        route: 'POST /audio/transcriptions',
        path: '/audio/transcriptions',
        modelAlias: aliasValues.transcription,
        enabled: endpointFlags.audioTranscriptions,
        baseKind: 'openai' as const,
      },
      {
        id: 'speech' as const,
        label: 'Speech',
        route: 'POST /audio/speech',
        path: '/audio/speech',
        modelAlias: aliasValues.speech,
        enabled: endpointFlags.audioSpeech,
        baseKind: 'openai' as const,
      },
    ],
    [aliasValues, endpointFlags]
  );

  const activeExample = useMemo(
    () => exampleTabs.find((tab) => tab.id === activeExampleTab) ?? exampleTabs[0],
    [activeExampleTab, exampleTabs]
  );
  const jsSdkLabel = activeExample.id === 'messages' ? 'Anthropic JavaScript SDK' : 'OpenAI JavaScript SDK';
  const pySdkLabel = activeExample.id === 'messages' ? 'Anthropic Python SDK' : 'OpenAI Python SDK';

  const copyBaseUrl = async () => {
    await navigator.clipboard.writeText(openAiBaseUrl);
    setCopied(true);
    setTimeout(() => setCopied(false), 2000);
  };

  const { curlExample, jsExample, pyExample } = useMemo(() => {
    const selectedBaseUrl = activeExample.baseKind === 'anthropic' ? anthropicBaseUrl : openAiBaseUrl;
    const requestUrl = `${selectedBaseUrl}${activeExample.path}`;
    const modelAlias = activeExample.modelAlias;

    if (activeExample.id === 'models') {
      return {
        curlExample: `curl "${requestUrl}" \\
  -H "Authorization: Bearer <api-key>"`,
        jsExample: `import OpenAI from "openai";

const client = new OpenAI({
  apiKey: process.env.GUIDEANTS_API_KEY,
  baseURL: "${openAiBaseUrl}",
});

const models = await client.models.list();`,
        pyExample: `from openai import OpenAI
import os

client = OpenAI(
    api_key=os.environ["GUIDEANTS_API_KEY"],
    base_url="${openAiBaseUrl}",
)

models = client.models.list()`,
      };
    }

    if (activeExample.id === 'responses') {
      return {
        curlExample: `curl -X POST "${requestUrl}" \\
  -H "Content-Type: application/json" \\
  -H "Authorization: Bearer <api-key>" \\
  -d '{
    "model": "${modelAlias}",
    "input": "Hello from responses"
  }'`,
        jsExample: `import OpenAI from "openai";

const client = new OpenAI({
  apiKey: process.env.GUIDEANTS_API_KEY,
  baseURL: "${openAiBaseUrl}",
});

const response = await client.responses.create({
  model: "${modelAlias}",
  input: "Hello from responses",
});`,
        pyExample: `from openai import OpenAI
import os

client = OpenAI(
    api_key=os.environ["GUIDEANTS_API_KEY"],
    base_url="${openAiBaseUrl}",
)

response = client.responses.create(
    model="${modelAlias}",
    input="Hello from responses",
)`,
      };
    }

    if (activeExample.id === 'messages') {
      return {
        curlExample: `curl -X POST "${requestUrl}" \\
  -H "Content-Type: application/json" \\
  -H "x-api-key: <api-key>" \\
  -d '{
    "model": "${modelAlias}",
    "max_tokens": 1024,
    "messages": [{"role":"user","content":"Hello from messages"}]
  }'`,
        jsExample: `import Anthropic from "@anthropic-ai/sdk";

const client = new Anthropic({
  apiKey: process.env.GUIDEANTS_API_KEY,
  baseURL: "${anthropicBaseUrl}",
});

const response = await client.messages.create({
  model: "${modelAlias}",
  max_tokens: 1024,
  messages: [{ role: "user", content: "Hello from messages" }],
});`,
        pyExample: `from anthropic import Anthropic
import os

client = Anthropic(
    api_key=os.environ["GUIDEANTS_API_KEY"],
    base_url="${anthropicBaseUrl}",
)

response = client.messages.create(
    model="${modelAlias}",
    max_tokens=1024,
    messages=[{"role": "user", "content": "Hello from messages"}],
)`,
      };
    }

    if (activeExample.id === 'embeddings') {
      return {
        curlExample: `curl -X POST "${requestUrl}" \\
  -H "Content-Type: application/json" \\
  -H "Authorization: Bearer <api-key>" \\
  -d '{
    "model": "${modelAlias}",
    "input": "Hello from embeddings"
  }'`,
        jsExample: `import OpenAI from "openai";

const client = new OpenAI({
  apiKey: process.env.GUIDEANTS_API_KEY,
  baseURL: "${openAiBaseUrl}",
});

const response = await client.embeddings.create({
  model: "${modelAlias}",
  input: "Hello from embeddings",
});`,
        pyExample: `from openai import OpenAI
import os

client = OpenAI(
    api_key=os.environ["GUIDEANTS_API_KEY"],
    base_url="${openAiBaseUrl}",
)

response = client.embeddings.create(
    model="${modelAlias}",
    input="Hello from embeddings",
)`,
      };
    }

    if (activeExample.id === 'image') {
      return {
        curlExample: `curl -X POST "${requestUrl}" \\
  -H "Content-Type: application/json" \\
  -H "Authorization: Bearer <api-key>" \\
  -d '{
    "model": "${modelAlias}",
    "prompt": "A lighthouse on a cliff at sunrise",
    "size": "1024x1024"
  }'`,
        jsExample: `import OpenAI from "openai";

const client = new OpenAI({
  apiKey: process.env.GUIDEANTS_API_KEY,
  baseURL: "${openAiBaseUrl}",
});

const response = await client.images.generate({
  model: "${modelAlias}",
  prompt: "A lighthouse on a cliff at sunrise",
  size: "1024x1024",
});`,
        pyExample: `from openai import OpenAI
import os

client = OpenAI(
    api_key=os.environ["GUIDEANTS_API_KEY"],
    base_url="${openAiBaseUrl}",
)

response = client.images.generate(
    model="${modelAlias}",
    prompt="A lighthouse on a cliff at sunrise",
    size="1024x1024",
)`,
      };
    }

    if (activeExample.id === 'transcription') {
      return {
        curlExample: `curl -X POST "${requestUrl}" \\
  -H "Authorization: Bearer <api-key>" \\
  -F "model=${modelAlias}" \\
  -F "file=@sample.wav"`,
        jsExample: `import OpenAI from "openai";
import fs from "fs";

const client = new OpenAI({
  apiKey: process.env.GUIDEANTS_API_KEY,
  baseURL: "${openAiBaseUrl}",
});

const response = await client.audio.transcriptions.create({
  model: "${modelAlias}",
  file: fs.createReadStream("sample.wav"),
});`,
        pyExample: `from openai import OpenAI
import os

client = OpenAI(
    api_key=os.environ["GUIDEANTS_API_KEY"],
    base_url="${openAiBaseUrl}",
)

with open("sample.wav", "rb") as audio_file:
    response = client.audio.transcriptions.create(
        model="${modelAlias}",
        file=audio_file,
    )`,
      };
    }

    if (activeExample.id === 'speech') {
      return {
        curlExample: `curl -X POST "${requestUrl}" \\
  -H "Content-Type: application/json" \\
  -H "Authorization: Bearer <api-key>" \\
  -d '{
    "model": "${modelAlias}",
    "voice": "alloy",
    "input": "Hello from speech",
    "response_format": "wav"
  }' \\
  --output speech.wav`,
        jsExample: `import OpenAI from "openai";
import fs from "fs";

const client = new OpenAI({
  apiKey: process.env.GUIDEANTS_API_KEY,
  baseURL: "${openAiBaseUrl}",
});

const response = await client.audio.speech.create({
  model: "${modelAlias}",
  voice: "alloy",
  input: "Hello from speech",
  response_format: "wav",
});

const buffer = Buffer.from(await response.arrayBuffer());
fs.writeFileSync("speech.wav", buffer);`,
        pyExample: `from openai import OpenAI
import os

client = OpenAI(
    api_key=os.environ["GUIDEANTS_API_KEY"],
    base_url="${openAiBaseUrl}",
)

response = client.audio.speech.create(
    model="${modelAlias}",
    voice="alloy",
    input="Hello from speech",
    response_format="wav",
)

with open("speech.wav", "wb") as output_file:
    output_file.write(response.read())`,
      };
    }

    return {
      curlExample: `curl -X POST "${requestUrl}" \\
  -H "Content-Type: application/json" \\
  -H "Authorization: Bearer <api-key>" \\
  -d '{
    "model": "${modelAlias}",
    "messages": [{"role":"user","content":"Hello from chat"}]
  }'`,
      jsExample: `import OpenAI from "openai";

const client = new OpenAI({
  apiKey: process.env.GUIDEANTS_API_KEY,
  baseURL: "${openAiBaseUrl}",
});

const response = await client.chat.completions.create({
  model: "${modelAlias}",
  messages: [{ role: "user", content: "Hello from chat" }],
});`,
      pyExample: `from openai import OpenAI
import os

client = OpenAI(
    api_key=os.environ["GUIDEANTS_API_KEY"],
    base_url="${openAiBaseUrl}",
)

response = client.chat.completions.create(
    model="${modelAlias}",
    messages=[{"role": "user", "content": "Hello from chat"}],
)`,
    };
  }, [activeExample, anthropicBaseUrl, openAiBaseUrl]);

  return (
    <div className="space-y-6">
      <h3 className="text-lg font-medium text-gray-900">Published API Endpoints</h3>

      <div className="border border-gray-200 rounded-lg p-4 space-y-4">
        <label className="flex items-center justify-between">
          <div className="pr-4">
            <p className="text-sm font-medium text-gray-900">Enable Published API Endpoints</p>
            <p className="text-xs text-gray-500 mt-1">Enables OpenAI-compatible and Anthropic-compatible API surfaces for this published guide.</p>
          </div>
          <input
            id="wireApiEnabled"
            aria-label="Enable Published API Endpoints"
            type="checkbox"
            checked={wireApiEnabled}
            onChange={(e) => setWireApiEnabled(e.target.checked)}
            className="h-4 w-4 text-blue-600 rounded border-gray-300"
          />
        </label>

        {sdkAuthWarning && (
          <div className="p-3 bg-amber-50 border border-amber-200 rounded-md text-sm text-amber-800">
            SDK clients work best with API key authentication. Current auth mode: <strong>{authMode}</strong>.
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
        <div className="space-y-2">
          <div className="flex items-center gap-2">
            <span className="text-xs text-gray-600 w-28">OpenAI Base URL</span>
            <code className="flex-1 px-3 py-2 bg-gray-50 border border-gray-200 rounded text-xs font-mono text-gray-900 break-all">
              {openAiBaseUrl}
            </code>
            <button
              type="button"
              onClick={copyBaseUrl}
              className="px-3 py-2 text-xs font-medium text-blue-700 bg-blue-50 border border-blue-200 rounded hover:bg-blue-100"
            >
              {copied ? 'Copied!' : 'Copy'}
            </button>
          </div>
          <div className="flex items-center gap-2">
            <span className="text-xs text-gray-600 w-28">Anthropic Base URL</span>
            <code className="flex-1 px-3 py-2 bg-gray-50 border border-gray-200 rounded text-xs font-mono text-gray-900 break-all">
              {anthropicBaseUrl}
            </code>
          </div>
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
              <p><strong>x-api-key:</strong> {'<api-key>'}</p>
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
        <div className="flex flex-wrap gap-2">
          {exampleTabs.map((tab) => (
            <button
              key={tab.id}
              type="button"
              onClick={() => setActiveExampleTab(tab.id)}
              className={`px-3 py-1.5 rounded border text-xs font-medium ${
                activeExample.id === tab.id
                  ? 'bg-blue-600 text-white border-blue-600'
                  : 'bg-white text-gray-700 border-gray-300 hover:bg-gray-50'
              }`}
            >
              {tab.label}
            </button>
          ))}
        </div>
        <div className="space-y-1">
          <p className="text-xs text-gray-700 font-medium">{activeExample.route}</p>
          <code className="block px-3 py-2 bg-gray-50 border border-gray-200 rounded text-xs font-mono text-gray-900 break-all">
            {`${activeExample.baseKind === 'anthropic' ? anthropicBaseUrl : openAiBaseUrl}${activeExample.path}`}
          </code>
          {!activeExample.enabled && (
            <p className="text-xs text-amber-700">This endpoint is currently disabled in Endpoint Toggles.</p>
          )}
        </div>
        <div>
          <p className="text-xs font-medium text-gray-700 mb-1">curl</p>
          <pre className="p-3 bg-gray-50 border border-gray-200 rounded text-xs font-mono overflow-x-auto">{curlExample}</pre>
        </div>
        <div>
          <p className="text-xs font-medium text-gray-700 mb-1">{jsSdkLabel}</p>
          <pre className="p-3 bg-gray-50 border border-gray-200 rounded text-xs font-mono overflow-x-auto">{jsExample}</pre>
        </div>
        <div>
          <p className="text-xs font-medium text-gray-700 mb-1">{pySdkLabel}</p>
          <pre className="p-3 bg-gray-50 border border-gray-200 rounded text-xs font-mono overflow-x-auto">{pyExample}</pre>
        </div>
      </div>
    </div>
  );
}


