import { useState } from 'react';
import { api } from '../../../services/api';
import type { PublishedGuideAuthMode } from '../../../types/guides';

interface AuthTabProps {
  authWebhookUrl: string;
  setAuthWebhookUrl: (url: string) => void;
  authWebhookTimeout: number;
  setAuthWebhookTimeout: (timeout: number) => void;
  friendlyName: string;
  hasApiKey: boolean;
  sessionApiKey: string | null;
  guideId: string;
  publishedGuideId?: string;
  authMode?: PublishedGuideAuthMode;
  onApiKeyChange: (hasKey: boolean) => void;
  onSessionApiKeyChange: (apiKey: string | null) => void;
}

export function AuthTab({
  authWebhookUrl,
  setAuthWebhookUrl,
  authWebhookTimeout,
  setAuthWebhookTimeout,
  friendlyName,
  hasApiKey,
  sessionApiKey,
  guideId,
  publishedGuideId,
  authMode,
  onApiKeyChange,
  onSessionApiKeyChange,
}: AuthTabProps) {
  const [isGenerating, setIsGenerating] = useState(false);
  const [showRegenerateConfirm, setShowRegenerateConfirm] = useState(false);
  const [showRemoveConfirm, setShowRemoveConfirm] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [copied, setCopied] = useState(false);

  const isAppIdentity = authMode === 'AppIdentity';

  if (isAppIdentity) {
    return (
      <div className="space-y-6">
        <h3 className="text-lg font-medium text-gray-900">Authentication</h3>
        <div className="p-4 bg-blue-50 border border-blue-100 rounded-md text-sm text-blue-800">
          <p>
            <strong>Authentication:</strong> GuideAnts app identity — callers must present a signed-in
            GuideAnts user token. Managed by the system; cannot be changed here.
          </p>
        </div>
      </div>
    );
  }

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

  const canUseApiKey = !authWebhookUrl.trim();
  const canUseWebhook = !hasApiKey && !sessionApiKey;
  const isEditMode = !!publishedGuideId;

  return (
    <div className="space-y-6">
      <h3 className="text-lg font-medium text-gray-900">Authentication</h3>
      
      {friendlyName.trim() && (
         <div className="p-4 bg-blue-50 border border-blue-100 rounded-md mb-4 text-sm text-blue-800">
           <p><strong>Note:</strong> You have a Public URL configured ("{friendlyName}").</p>
           <p className="mt-1">Public URL mode is anonymous. Remove the friendly name before saving if you want API key or webhook auth.</p>
         </div>
      )}

      {/* API Key Section */}
      <div className="border border-gray-200 rounded-lg p-4">
        <div className="flex items-center justify-between mb-3">
          <div>
            <h4 className="text-sm font-medium text-gray-900">API Key Authentication</h4>
            <p className="text-xs text-gray-500 mt-0.5">
              Simple key-based authentication via <code className="bg-gray-100 px-1 rounded">x-guideants-apikey</code> header
            </p>
          </div>
          {hasApiKey && !sessionApiKey && (
            <span className="inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-green-100 text-green-800">
              Enabled
            </span>
          )}
        </div>

        {!canUseApiKey && (
          <div className="p-3 bg-amber-50 border border-amber-100 rounded-md text-sm text-amber-800">
            <p>Remove the webhook URL below to enable API key authentication.</p>
          </div>
        )}

        {canUseApiKey && (
          <div className="space-y-3">
            {/* Show generated key */}
            {sessionApiKey && (
              <div className="p-4 bg-green-50 border border-green-200 rounded-md">
                <div className="flex items-center gap-2 mb-2">
                  <svg className="w-5 h-5 text-green-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 12l2 2 4-4m6 2a9 9 0 11-18 0 9 9 0 0118 0z" />
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
                    className="px-3 py-2 text-sm font-medium text-green-700 bg-white border border-green-300 rounded hover:bg-green-50"
                  >
                    {copied ? 'Copied!' : 'Copy'}
                  </button>
                </div>
                <div className="p-3 bg-amber-50 border border-amber-200 rounded text-xs text-amber-800">
                  <p className="font-medium">⚠️ Important: Save this key now!</p>
                  <p className="mt-1">This API key will only be displayed once. Store it securely — you will not be able to retrieve it again.</p>
                </div>
              </div>
            )}

            {/* Actions */}
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
                      <strong>Warning:</strong> Regenerating the API key will invalidate the current key. Any clients using it will stop working.
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
                      <strong>Warning:</strong> Removing the API key will disable authentication. The guide will become accessible anonymously.
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
              <p className="text-xs text-gray-500 italic">
                API key generation is available after publishing the guide.
              </p>
            )}

            {error && (
              <div className="p-3 bg-red-50 border border-red-200 rounded-md text-sm text-red-800">
                {error}
              </div>
            )}
          </div>
        )}
      </div>

      {/* Webhook Section */}
      <div className={`border border-gray-200 rounded-lg p-4 ${!canUseWebhook ? 'opacity-50' : ''}`}>
        <div className="mb-3">
          <h4 className="text-sm font-medium text-gray-900">Webhook Authentication (Advanced)</h4>
          <p className="text-xs text-gray-500 mt-0.5">
            Validate tokens via your own authentication service
          </p>
        </div>

        {!canUseWebhook && (
          <div className="p-3 bg-amber-50 border border-amber-100 rounded-md text-sm text-amber-800 mb-3">
            <p>Remove the API key to enable webhook authentication.</p>
          </div>
        )}

        <fieldset disabled={!canUseWebhook} className="space-y-4">
          <div>
            <label htmlFor="authWebhookUrl" className="block text-sm font-medium text-gray-700 mb-1">
              Authentication Webhook URL
            </label>
            <input
              type="url"
              id="authWebhookUrl"
              value={authWebhookUrl}
              onChange={(e) => setAuthWebhookUrl(e.target.value)}
              className="w-full px-3 py-2 border border-gray-300 rounded-md font-mono text-xs focus:outline-none focus:ring-2 focus:ring-blue-500 disabled:bg-gray-100"
              placeholder="https://your-api.com/validate-token"
            />
            <p className="text-xs text-gray-500 mt-1">
              If provided, users must authenticate via your own implementation.
              <br />
              Leave empty for anonymous access.
            </p>
          </div>

          {authWebhookUrl && (
            <div>
              <label htmlFor="authWebhookTimeout" className="block text-sm font-medium text-gray-700 mb-1">
                Webhook Timeout (seconds)
              </label>
              <input
                type="number"
                id="authWebhookTimeout"
                min="1"
                max="30"
                value={authWebhookTimeout}
                onChange={(e) => setAuthWebhookTimeout(parseInt(e.target.value))}
                className="w-full px-3 py-2 border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500 disabled:bg-gray-100"
              />
            </div>
          )}
        </fieldset>
      </div>
    </div>
  );
}




