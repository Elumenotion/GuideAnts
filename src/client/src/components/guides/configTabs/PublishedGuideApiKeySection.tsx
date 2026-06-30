import { useState } from 'react';
import { api } from '../../../services/api';

export type PublishedGuideApiKeyContext = 'auth' | 'mcp';

const CONTEXT_COPY: Record<
  PublishedGuideApiKeyContext,
  { title: string; body: string; otherTab: string }
> = {
  auth: {
    title: 'One API key for this published guide',
    otherTab: 'MCP and Skills',
    body:
      'Use this tab when configuring Wire API clients, embedded published access, or other HTTP integrations. ' +
      'The same key authenticates MCP clients configured on the MCP and Skills tab. ' +
      'Regenerating or removing the key here invalidates every client still using the previous key — update Cursor, Claude Code, SDK configs, and other integrations.',
  },
  mcp: {
    title: 'One API key for this published guide',
    otherTab: 'Auth',
    body:
      'Use this tab when wiring MCP clients such as Cursor or Claude Code. ' +
      'This is the same key as on the Auth tab — there is only one key per published guide. ' +
      'Regenerating or removing the key here also breaks Wire API and other clients that rely on the key from the Auth tab.',
  },
};

interface PublishedGuideApiKeySectionProps {
  context: PublishedGuideApiKeyContext;
  hasApiKey: boolean;
  sessionApiKey: string | null;
  guideId: string;
  publishedGuideId?: string;
  authWebhookUrl: string;
  onApiKeyChange: (hasKey: boolean) => void;
  onSessionApiKeyChange: (apiKey: string | null) => void;
}

export function PublishedGuideApiKeySection({
  context,
  hasApiKey,
  sessionApiKey,
  guideId,
  publishedGuideId,
  authWebhookUrl,
  onApiKeyChange,
  onSessionApiKeyChange,
}: PublishedGuideApiKeySectionProps) {
  const [isGenerating, setIsGenerating] = useState(false);
  const [showRegenerateConfirm, setShowRegenerateConfirm] = useState(false);
  const [showRemoveConfirm, setShowRemoveConfirm] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [copied, setCopied] = useState(false);

  const copy = CONTEXT_COPY[context];
  const canUseApiKey = !authWebhookUrl.trim();
  const isEditMode = !!publishedGuideId;

  const handleGenerateApiKey = async () => {
    if (!publishedGuideId) return;

    setIsGenerating(true);
    setError(null);
    try {
      const response = await api.guides.guides.generateApiKey(guideId, publishedGuideId);
      onSessionApiKeyChange(response.apiKey);
      onApiKeyChange(true);
      setShowRegenerateConfirm(false);
    } catch (err: unknown) {
      const errorMessage = err instanceof Error ? err.message : 'Failed to generate API key';
      setError(errorMessage);
    } finally {
      setIsGenerating(false);
    }
  };

  const handleRemoveApiKey = async () => {
    if (!publishedGuideId) return;

    setIsGenerating(true);
    setError(null);
    try {
      await api.guides.guides.removeApiKey(guideId, publishedGuideId);
      onSessionApiKeyChange(null);
      onApiKeyChange(false);
      setShowRemoveConfirm(false);
    } catch (err: unknown) {
      const errorMessage = err instanceof Error ? err.message : 'Failed to remove API key';
      setError(errorMessage);
    } finally {
      setIsGenerating(false);
    }
  };

  const copyToClipboard = async () => {
    if (sessionApiKey) {
      await navigator.clipboard.writeText(sessionApiKey);
      setCopied(true);
      setTimeout(() => setCopied(false), 2000);
    }
  };

  return (
    <div className="border border-gray-200 rounded-lg p-4 space-y-4">
      <div className="p-4 bg-blue-50 border border-blue-100 rounded-md text-sm text-blue-900">
        <p className="font-medium">{copy.title}</p>
        <p className="mt-1">{copy.body}</p>
        <p className="mt-2 text-blue-800">
          Changes on this tab and the <strong>{copy.otherTab}</strong> tab affect the same key.
        </p>
      </div>

      <div className="flex items-center justify-between">
        <div>
          <h4 className="text-sm font-medium text-gray-900">API Key</h4>
          <p className="text-xs text-gray-500 mt-0.5">
            Authenticate with the <code className="bg-gray-100 px-1 rounded">x-guideants-apikey</code> header
          </p>
        </div>
        {hasApiKey && !sessionApiKey && (
          <span className="inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-green-100 text-green-800">
            Configured
          </span>
        )}
      </div>

      {!canUseApiKey && (
        <div className="p-3 bg-amber-50 border border-amber-100 rounded-md text-sm text-amber-800">
          <p>Remove the webhook URL on the Auth tab to enable API key authentication.</p>
        </div>
      )}

      {canUseApiKey && (
        <div className="space-y-3">
          {sessionApiKey && (
            <div className="p-4 bg-green-50 border border-green-200 rounded-md">
              <div className="flex items-center gap-2 mb-2">
                <svg className="w-5 h-5 text-green-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path
                    strokeLinecap="round"
                    strokeLinejoin="round"
                    strokeWidth={2}
                    d="M9 12l2 2 4-4m6 2a9 9 0 11-18 0 9 9 0 0118 0z"
                  />
                </svg>
                <span className="text-sm font-medium text-green-800">API Key Generated</span>
              </div>
              <div className="flex items-center gap-2 mb-3">
                <code className="flex-1 px-3 py-2 bg-white border border-green-300 rounded font-mono text-sm text-gray-900 select-all break-all">
                  {sessionApiKey}
                </code>
                <button
                  type="button"
                  onClick={copyToClipboard}
                  className="px-3 py-2 text-sm font-medium text-green-700 bg-white border border-green-300 rounded hover:bg-green-50 flex-shrink-0"
                >
                  {copied ? 'Copied!' : 'Copy'}
                </button>
              </div>
              <div className="p-3 bg-amber-50 border border-amber-200 rounded text-xs text-amber-800">
                <p className="font-medium">Save this key now</p>
                <p className="mt-1">
                  The plaintext key is shown only once after generate or regenerate. Store it securely before
                  closing this dialog.
                </p>
              </div>
            </div>
          )}

          {isEditMode && (
            <div className="flex flex-wrap gap-2">
              {!hasApiKey && !sessionApiKey && (
                <button
                  type="button"
                  onClick={handleGenerateApiKey}
                  disabled={isGenerating}
                  className="px-4 py-2 text-sm font-medium text-white bg-blue-600 rounded hover:bg-blue-700 disabled:opacity-50"
                >
                  {isGenerating ? 'Generating...' : 'Generate API Key'}
                </button>
              )}

              {(hasApiKey || sessionApiKey) && !showRegenerateConfirm && !showRemoveConfirm && (
                <>
                  <button
                    type="button"
                    onClick={() => setShowRegenerateConfirm(true)}
                    className="px-4 py-2 text-sm font-medium text-amber-700 bg-amber-50 border border-amber-200 rounded hover:bg-amber-100"
                  >
                    Regenerate Key
                  </button>
                  <button
                    type="button"
                    onClick={() => setShowRemoveConfirm(true)}
                    className="px-4 py-2 text-sm font-medium text-red-700 bg-red-50 border border-red-200 rounded hover:bg-red-100"
                  >
                    Remove Key
                  </button>
                </>
              )}

              {showRegenerateConfirm && (
                <div className="w-full p-3 bg-amber-50 border border-amber-200 rounded-md">
                  <p className="text-sm text-amber-800 mb-2">
                    <strong>Warning:</strong> Regenerating invalidates the current key. MCP clients, Wire API
                    integrations, and any other setup using the old key will stop working until updated.
                  </p>
                  <div className="flex gap-2">
                    <button
                      type="button"
                      onClick={handleGenerateApiKey}
                      disabled={isGenerating}
                      className="px-3 py-1.5 text-sm font-medium text-white bg-amber-600 rounded hover:bg-amber-700 disabled:opacity-50"
                    >
                      {isGenerating ? 'Regenerating...' : 'Confirm Regenerate'}
                    </button>
                    <button
                      type="button"
                      onClick={() => setShowRegenerateConfirm(false)}
                      className="px-3 py-1.5 text-sm font-medium text-gray-700 bg-white border border-gray-300 rounded hover:bg-gray-50"
                    >
                      Cancel
                    </button>
                  </div>
                </div>
              )}

              {showRemoveConfirm && (
                <div className="w-full p-3 bg-red-50 border border-red-200 rounded-md">
                  <p className="text-sm text-red-800 mb-2">
                    <strong>Warning:</strong> Removing the key disables MCP (if enabled) and turns off API key
                    authentication for Wire API and other clients. Existing integrations will fail immediately.
                  </p>
                  <div className="flex gap-2">
                    <button
                      type="button"
                      onClick={handleRemoveApiKey}
                      disabled={isGenerating}
                      className="px-3 py-1.5 text-sm font-medium text-white bg-red-600 rounded hover:bg-red-700 disabled:opacity-50"
                    >
                      {isGenerating ? 'Removing...' : 'Confirm Remove'}
                    </button>
                    <button
                      type="button"
                      onClick={() => setShowRemoveConfirm(false)}
                      className="px-3 py-1.5 text-sm font-medium text-gray-700 bg-white border border-gray-300 rounded hover:bg-gray-50"
                    >
                      Cancel
                    </button>
                  </div>
                </div>
              )}
            </div>
          )}

          {!isEditMode && (
            <p className="text-xs text-gray-500 italic">API key generation is available after publishing the guide.</p>
          )}

          {error && (
            <div className="p-3 bg-red-50 border border-red-200 rounded-md text-sm text-red-800">{error}</div>
          )}
        </div>
      )}
    </div>
  );
}
