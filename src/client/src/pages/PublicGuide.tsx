import { useState, useEffect, useRef, createElement } from 'react';
import { useParams, Link } from 'react-router-dom';
import { api } from '../services/api';
import { API_BASE_URL } from '../config/apiConfig';

// Lazy load guideants web component only when this page is used
let guideantLoaded = false;
const loadGuideants = async () => {
  if (!guideantLoaded) {
    await import('guideants');
    guideantLoaded = true;
  }
};

interface PublicGuideData {
  id: string;
  friendlyName: string;
  projectId: string;
  notebookId: string;
  guideId: string;
  guideName: string;
  guideDescription?: string;
  avatarUrl?: string;
  maxUserMessageLength?: number;
  maxTurns?: number;
  requiresAuth: boolean;
  commandMode?: boolean;
}

export default function PublicGuide() {
  const { friendlyName } = useParams<{ friendlyName: string }>();
  const [guideData, setGuideData] = useState<PublicGuideData | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const chatRef = useRef<HTMLElement | null>(null);

  // Lazy load the guideants web component
  useEffect(() => {
    loadGuideants();
  }, []);

  // Fetch guide data on mount
  useEffect(() => {
    if (!friendlyName) return;

    const fetchGuideData = async () => {
      try {
        setLoading(true);
        setError(null);

        const data = await api.public.getPublishedGuideByFriendlyName(friendlyName);
        
        // Convert relative avatar URL to absolute URL
        if (data.avatarUrl && data.avatarUrl.startsWith('/')) {
          data.avatarUrl = API_BASE_URL.replace(/\/api$/, '') + data.avatarUrl;
        }
        
        setGuideData(data);
      } catch (err: any) {
        console.error('Error fetching guide data:', err);
        if (err.status === 404) {
          setError('Guide not found');
        } else {
          setError('Failed to load guide');
        }
      } finally {
        setLoading(false);
      }
    };

    fetchGuideData();
  }, [friendlyName]);

  // Initialize guideants-chat component
  useEffect(() => {
    if (guideData && chatRef.current) {
      // Set auth token if required (auth flow to be implemented)
      if (guideData.requiresAuth) {
        console.log('This guide requires authentication');
      }
    }
  }, [guideData]);

  if (loading) {
    return (
      <div className="min-h-screen flex items-center justify-center bg-gray-50">
        <div className="text-center">
          <svg className="animate-spin h-12 w-12 text-blue-600 mx-auto mb-4" xmlns="http://www.w3.org/2000/svg" fill="none" viewBox="0 0 24 24">
            <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4"></circle>
            <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4zm2 5.291A7.962 7.962 0 014 12H0c0 3.042 1.135 5.824 3 7.938l3-2.647z"></path>
          </svg>
          <p className="text-gray-600">Loading guide...</p>
        </div>
      </div>
    );
  }

  if (error || !guideData) {
    return (
      <div className="min-h-screen flex items-center justify-center bg-gray-50">
        <div className="text-center max-w-md mx-auto px-6">
          <svg className="h-16 w-16 text-red-500 mx-auto mb-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-3L13.732 4c-.77-1.333-2.694-1.333-3.464 0L3.34 16c-.77 1.333.192 3 1.732 3z" />
          </svg>
          <h1 className="text-2xl font-bold text-gray-900 mb-2">{error || 'Guide not found'}</h1>
          <p className="text-gray-600 mb-6">
            The guide you're looking for doesn't exist or is no longer available.
          </p>
          <Link 
            to="/" 
            className="inline-block px-6 py-3 bg-blue-600 text-white rounded-md hover:bg-blue-700 transition-colors"
          >
            Go to Home
          </Link>
        </div>
      </div>
    );
  }

  return (
    <div className="h-full overflow-auto bg-gray-50">
      {/* Header */}
      <header className="bg-white border-b border-gray-200 shadow-sm">
        <div className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8 py-8">
          <div className="flex items-start gap-6">
            {guideData.avatarUrl && (
              <img 
                src={guideData.avatarUrl} 
                alt={guideData.guideName}
                className="w-20 h-20 rounded-lg object-cover flex-shrink-0 shadow-md"
              />
            )}
            <div className="flex-1 min-w-0">
              <h1 className="text-3xl font-bold text-gray-900 mb-2 break-words">
                {guideData.guideName}
              </h1>
              {guideData.guideDescription && (
                <p className="text-lg text-gray-600 break-words">
                  {guideData.guideDescription}
                </p>
              )}
            </div>
          </div>
        </div>
      </header>
      <Link 
        to="https://www.guideants.ai/get-started.html"
        className="flex items-center justify-center py-2 px-3 bg-blue-50 border-b border-blue-200 cursor-pointer hover:bg-blue-100 transition-colors"
      >
        <span className="text-blue-700 text-sm text-center">
          <strong>Want to create your own guide?</strong>{' '}
          <span className="text-blue-600 underline font-semibold">
            Get started with free credits now!
          </span>
        </span>
      </Link>

      {/* Chat Component */}
      <main className="max-w-7xl mx-auto w-full px-4 sm:px-6 lg:px-8 py-8">
        <div className="bg-white rounded-lg shadow-md p-6">
          {createElement('guideants-chat', { 
            ref: chatRef, 
            className: 'theme-guideants',
            'pub-id': guideData.id,
            'api-base-url': API_BASE_URL.replace(/\/api$/, '')
          })}
        </div>
      </main>

      {/* GuideAnts Theme */}
      <style>{`
        /* GuideAnts Brand Theme - Blue Accent Theme */
        guideants-chat.theme-guideants {
          --wf-primary-color: #3B82F6;
          --wf-primary-hover: #1D4ED8;
          --wf-danger-color: #EF4444;
          --wf-danger-hover: #DC2626;
          --wf-border-color: #3B82F6;
          --wf-bg-user: #DBEAFE;
          --wf-bg-assistant: #F3F4F6;
          --wf-bg-control-bar: #F9FAFB;
          --wf-bg-container: #FFFFFF;
          --wf-text-primary: #1F2937;
          --wf-text-secondary: #6B7280;
          --wf-text-error: #DC2626;
          --wf-border-radius: 0.5rem;
          --wf-border-radius-lg: 0.75rem;
        }
      `}</style>
    </div>
  );
}

