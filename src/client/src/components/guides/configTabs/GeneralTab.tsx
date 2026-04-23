import type { PublishedGuideDto } from '../../../types/guides';

interface GeneralTabProps {
  friendlyName: string;
  setFriendlyName: (name: string) => void;
  friendlyNameValidation: {
    validating: boolean;
    available?: boolean;
    reason?: string;
  };
  publishedGuide: PublishedGuideDto | null;
  authWebhookUrl: string;
  hasApiKey: boolean;
}

export function GeneralTab({
  friendlyName,
  setFriendlyName,
  friendlyNameValidation,
  publishedGuide,
  authWebhookUrl,
  hasApiKey
}: GeneralTabProps) {
  const hasAuth = authWebhookUrl.trim() || hasApiKey;
  return (
    <div className="space-y-6">
      <div>
        <h3 className="text-lg font-medium text-gray-900 mb-4">General Settings</h3>
        
        <div className="space-y-4">
          <label htmlFor="friendlyName" className="block text-sm font-medium text-gray-700">
            Public URL (Friendly Name)
          </label>
          <div className="relative">
            <input
              type="text"
              id="friendlyName"
              value={friendlyName}
              onChange={(e) => setFriendlyName(e.target.value)}
              className={`w-full px-3 py-2 pr-10 border rounded-md focus:outline-none focus:ring-2 ${
                friendlyName.trim() && friendlyName !== publishedGuide?.friendlyName && friendlyNameValidation.available === false
                  ? 'border-red-300 focus:ring-red-500'
                  : friendlyName.trim() && (friendlyNameValidation.available === true || friendlyName === publishedGuide?.friendlyName)
                  ? 'border-green-300 focus:ring-green-500'
                  : 'border-gray-300 focus:ring-blue-500'
              }`}
              placeholder="my-awesome-guide"
              maxLength={100}
            />
            <div className="absolute inset-y-0 right-0 flex items-center pr-3 pointer-events-none">
              {friendlyNameValidation.validating && (
                <svg className="animate-spin h-5 w-5 text-gray-400" xmlns="http://www.w3.org/2000/svg" fill="none" viewBox="0 0 24 24">
                  <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4"></circle>
                  <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4zm2 5.291A7.962 7.962 0 014 12H0c0 3.042 1.135 5.824 3 7.938l3-2.647z"></path>
                </svg>
              )}
              {!friendlyNameValidation.validating && friendlyName.trim() && (friendlyNameValidation.available === true || friendlyName === publishedGuide?.friendlyName) && (
                <svg className="h-5 w-5 text-green-500" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M5 13l4 4L19 7" />
                </svg>
              )}
              {!friendlyNameValidation.validating && friendlyName.trim() && friendlyName !== publishedGuide?.friendlyName && friendlyNameValidation.available === false && (
                <svg className="h-5 w-5 text-red-500" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
                </svg>
              )}
            </div>
          </div>
          <p className="text-xs text-gray-500">
            Create a custom URL path for your guide (letters, numbers, hyphens, and underscores only).
            <br />
            <strong>Note:</strong> Setting a public URL enables anonymous access. You cannot use both a public URL and authentication (API key or webhook) at the same time.
          </p>
          {friendlyNameValidation.reason && friendlyName !== publishedGuide?.friendlyName && (
            <p className="text-xs text-red-600">
              {friendlyNameValidation.reason}
            </p>
          )}
          {friendlyName.trim() && (friendlyNameValidation.available === true || friendlyName === publishedGuide?.friendlyName) && (
            <div className="p-2 bg-green-50 border border-green-100 rounded text-sm text-green-800">
              Public Link: <a href={`${window.location.origin}/public/${friendlyName}`} target="_blank" rel="noreferrer" className="underline font-mono">{window.location.origin}/public/{friendlyName}</a>
            </div>
          )}
          
          {hasAuth && (
            <div className="p-3 bg-amber-50 border border-amber-200 rounded text-sm text-amber-800 flex items-center gap-2">
              <svg className="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-3L13.732 4c-.77-1.333-2.694-1.333-3.464 0L3.34 16c-.77 1.333.192 3 1.732 3z" />
              </svg>
              <span>
                You currently have Authentication enabled ({hasApiKey ? 'API Key' : 'Webhook'}). 
                Setting a Public URL is not allowed while Authentication is active.
              </span>
            </div>
          )}
        </div>
      </div>
    </div>
  );
}

