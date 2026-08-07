import { useCallback, useEffect, useState } from 'react';
import { useLocation, useNavigate } from 'react-router';
import ErrorScreen from '../components/ErrorScreen';

const REDIRECT_MS = 30_000;

export default function NotFoundPage() {
  const navigate = useNavigate();
  const location = useLocation();
  const [secondsLeft, setSecondsLeft] = useState(Math.ceil(REDIRECT_MS / 1000));

  const goHome = useCallback(() => {
    navigate('/');
  }, [navigate]);

  useEffect(() => {
    const startedAt = Date.now();

    const tick = () => {
      const remainingMs = REDIRECT_MS - (Date.now() - startedAt);
      setSecondsLeft(Math.max(0, Math.ceil(remainingMs / 1000)));
    };

    tick();
    const intervalId = window.setInterval(tick, 250);
    const timeoutId = window.setTimeout(() => {
      navigate('/');
    }, REDIRECT_MS);

    return () => {
      window.clearInterval(intervalId);
      window.clearTimeout(timeoutId);
    };
  }, [navigate]);

  const pathDetails = `${location.pathname}${location.search}`;

  return (
    <div data-testid="not-found-page">
      <ErrorScreen
        title="Page not found"
        message="This address is not a valid GuideAnts page. Check the link or go home to continue."
        error={pathDetails}
        showRetryButton={false}
        showBackButton={false}
        customActions={
          <>
            <button
              type="button"
              onClick={goHome}
              className="w-full px-6 py-3 text-white font-medium rounded-lg transition-colors bg-blue-600 hover:bg-blue-700 focus:ring-2 focus:ring-blue-500 focus:ring-offset-2"
            >
              Go to Home
            </button>
            <p className="mt-3 text-sm text-gray-500">
              Redirecting to home in {secondsLeft}s…
            </p>
          </>
        }
      />
    </div>
  );
}
