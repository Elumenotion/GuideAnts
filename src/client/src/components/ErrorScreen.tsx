import { useState } from 'react';
import { useNavigate } from 'react-router-dom';

interface ErrorScreenProps {
  error?: string | Error;
  title?: string;
  message?: string;
  showRetryButton?: boolean;
  showBackButton?: boolean;
  onRetry?: () => void;
  onBack?: () => void;
  customActions?: React.ReactNode;
}

const getErrorIcon = (errorMessage: string) => {
  if (errorMessage.toLowerCase().includes('network') || errorMessage.toLowerCase().includes('fetch')) {
    return '📡';
  }
  if (errorMessage.toLowerCase().includes('authentication') || errorMessage.toLowerCase().includes('unauthorized')) {
    return '🔒';
  }
  if (errorMessage.toLowerCase().includes('timeout')) {
    return '⏱️';
  }
  return '⚠️';
};

const getErrorMessage = (errorMessage: string) => {
  if (errorMessage.toLowerCase().includes('failed to fetch')) {
    return 'Unable to connect to the server. Please check your internet connection and try again.';
  }
  if (errorMessage.toLowerCase().includes('network')) {
    return 'A network error occurred. Please check your connection and try again.';
  }
  if (errorMessage.toLowerCase().includes('authentication') || errorMessage.toLowerCase().includes('no authentication token')) {
    return 'Authentication failed. Please sign in again to continue.';
  }
  if (errorMessage.toLowerCase().includes('unauthorized')) {
    return 'You are not authorized to access this resource. Please contact your administrator.';
  }
  if (errorMessage.toLowerCase().includes('timeout')) {
    return 'The request timed out. The server might be busy, please try again in a moment.';
  }
  return errorMessage;
};

export default function ErrorScreen({
  error,
  title = 'Connection Error',
  message,
  showRetryButton = true,
  showBackButton = true,
  onRetry,
  onBack,
  customActions
}: ErrorScreenProps) {
  const [isRetrying, setIsRetrying] = useState(false);
  
  // Safely use navigate hook - will be null if not in Router context
  let navigate = null;
  try {
    navigate = useNavigate();
  } catch (e) {
    // If useNavigate fails (not in Router context), navigate will remain null
    console.warn('ErrorScreen: useNavigate failed, navigation will use fallback methods');
  }

  const errorMessage = error instanceof Error ? error.message : error || 'An unexpected error occurred';
  const displayMessage = message || getErrorMessage(errorMessage);
  
  // Check if this is an authentication error
  const isAuthError = errorMessage.toLowerCase().includes('authentication') || 
                     errorMessage.toLowerCase().includes('no authentication token') ||
                     errorMessage.toLowerCase().includes('unauthorized');

  const handleRetry = async () => {
    // If it's an authentication error, redirect to login instead of retrying
    if (isAuthError) {
      if (navigate) {
        navigate('/login');
      } else {
        window.location.href = '/login';
      }
      return;
    }
    
    if (onRetry) {
      setIsRetrying(true);
      try {
        await onRetry();
      } finally {
        setIsRetrying(false);
      }
    } else {
      // Default retry behavior - reload the page
      window.location.reload();
    }
  };

  const handleBack = () => {
    if (onBack) {
      onBack();
    } else if (navigate) {
      // Default back behavior - go to projects page
      navigate('/');
    } else {
      // Fallback if no router context - reload to home
      window.location.href = '/';
    }
  };

  return (
    <div className="min-h-screen bg-gray-50 flex items-center justify-center p-8">
      <div className="max-w-md w-full">
        {/* Header */}
        <div className="text-center mb-8">
          <div className="flex items-center justify-center mb-4">
            <img src="/code-ants.png" alt="GuideAnts" className="w-12 h-12 mr-3" />
            <div>
              <h1 className="text-xl font-semibold text-gray-900">GuideAnts Notebooks</h1>
              <p className="text-sm text-gray-600">AI for people</p>
            </div>
          </div>
        </div>

        {/* Error Card */}
        <div className="bg-white rounded-lg shadow-lg p-8 text-center">
          {/* Error Icon */}
          <div className="text-6xl mb-4">
            {getErrorIcon(errorMessage)}
          </div>

          {/* Error Title */}
          <h2 className="text-2xl font-bold text-gray-900 mb-4">
            {title}
          </h2>

          {/* Error Message */}
          <p className="text-gray-600 mb-6 leading-relaxed">
            {displayMessage}
          </p>

          {/* Technical Details (collapsible) */}
          {error && (
            <details className="mb-6 text-left">
              <summary className="cursor-pointer text-sm text-gray-500 hover:text-gray-700 font-medium">
                Technical Details
              </summary>
              <div className="mt-2 p-3 bg-gray-50 rounded border text-sm text-gray-700 font-mono overflow-auto">
                {errorMessage}
              </div>
            </details>
          )}

          {/* Action Buttons */}
          <div className="space-y-3">
            {customActions || (
              <>
                {showRetryButton && (
                  <button
                    onClick={handleRetry}
                    disabled={isRetrying}
                    className={`w-full px-6 py-3 text-white font-medium rounded-lg transition-colors ${
                      isRetrying
                        ? 'bg-blue-400 cursor-not-allowed'
                        : 'bg-blue-600 hover:bg-blue-700 focus:ring-2 focus:ring-blue-500 focus:ring-offset-2'
                    }`}
                  >
                    {isRetrying ? (
                      <span className="flex items-center justify-center">
                        <svg className="animate-spin -ml-1 mr-3 h-5 w-5 text-white" xmlns="http://www.w3.org/2000/svg" fill="none" viewBox="0 0 24 24">
                          <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4"></circle>
                          <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4zm2 5.291A7.962 7.962 0 014 12H0c0 3.042 1.135 5.824 3 7.938l3-2.647z"></path>
                        </svg>
                        Retrying...
                      </span>
                    ) : (
                      isAuthError ? 'Sign In' : 'Try Again'
                    )}
                  </button>
                )}

                {showBackButton && (
                  <button
                    onClick={handleBack}
                    aria-label="Back to Home"
                    title="Home"
                    className="w-full px-6 py-3 text-gray-700 font-medium bg-gray-100 rounded-lg hover:bg-gray-200 focus:ring-2 focus:ring-gray-500 focus:ring-offset-2 transition-colors flex items-center justify-center gap-2"
                  >
                    <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="currentColor" className="w-5 h-5">
                      <path d="M11.47 3.84a.75.75 0 0 1 1.06 0l8 8a.75.75 0 1 1-1.06 1.06l-.72-.72V19.5A2.25 2.25 0 0 1 16.5 21.75h-2.25a.75.75 0 0 1-.75-.75v-3.75a1.5 1.5 0 0 0-1.5-1.5h-1.5a1.5 1.5 0 0 0-1.5 1.5V21a.75.75 0 0 1-.75.75H4.5A2.25 2.25 0 0 1 2.25 19.5v-7.32l-.72.72a.75.75 0 1 1-1.06-1.06l8-8Z" />
                    </svg>
                    <span className="sr-only">Home</span>
                  </button>
                )}
              </>
            )}
          </div>

          {/* Help Text */}
          <div className="mt-6 pt-6 border-t border-gray-200">
            <p className="text-sm text-gray-500">
              If the problem persists, please contact support or try refreshing the application.
            </p>
          </div>
        </div>
      </div>
    </div>
  );
}

// Utility function to create error screens for specific scenarios
export const NetworkErrorScreen = (props: Partial<ErrorScreenProps>) => (
  <ErrorScreen
    title="Connection Problem"
    message="Unable to connect to the server. Please check your internet connection and try again."
    {...props}
  />
);

export const AuthErrorScreen = (props: Partial<ErrorScreenProps>) => (
  <ErrorScreen
    title="Authentication Required"
    message="Please sign in to continue using the application."
    showRetryButton={false}
    customActions={
      <button
        onClick={() => window.location.href = '/login'}
        className="w-full px-6 py-3 text-white font-medium bg-blue-600 rounded-lg hover:bg-blue-700 focus:ring-2 focus:ring-blue-500 focus:ring-offset-2 transition-colors"
      >
        Sign In
      </button>
    }
    {...props}
  />
);

export const ServerErrorScreen = (props: Partial<ErrorScreenProps>) => (
  <ErrorScreen
    title="Server Error"
    message="The server is currently experiencing issues. Please try again in a few moments."
    {...props}
  />
); 