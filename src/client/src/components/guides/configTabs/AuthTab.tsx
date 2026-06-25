import type { PublishedGuideAuthMode } from '../../../types/guides';
import { PublishedGuideApiKeySection } from './PublishedGuideApiKeySection';

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

  const canUseWebhook = !hasApiKey && !sessionApiKey;

  return (
    <div className="space-y-6">
      <h3 className="text-lg font-medium text-gray-900">Authentication</h3>

      {friendlyName.trim() && (
        <div className="p-4 bg-blue-50 border border-blue-100 rounded-md mb-4 text-sm text-blue-800">
          <p>
            <strong>Note:</strong> You have a Public URL configured (&quot;{friendlyName}&quot;).
          </p>
          <p className="mt-1">
            Public URL mode is anonymous. Remove the friendly name before saving if you want API key or webhook
            auth.
          </p>
        </div>
      )}

      <PublishedGuideApiKeySection
        context="auth"
        hasApiKey={hasApiKey}
        sessionApiKey={sessionApiKey}
        guideId={guideId}
        publishedGuideId={publishedGuideId}
        authWebhookUrl={authWebhookUrl}
        onApiKeyChange={onApiKeyChange}
        onSessionApiKeyChange={onSessionApiKeyChange}
      />

      <div className={`border border-gray-200 rounded-lg p-4 ${!canUseWebhook ? 'opacity-50' : ''}`}>
        <div className="mb-3">
          <h4 className="text-sm font-medium text-gray-900">Webhook Authentication (Advanced)</h4>
          <p className="text-xs text-gray-500 mt-0.5">Validate tokens via your own authentication service</p>
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
