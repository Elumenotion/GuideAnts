import { useEffect, useMemo, useState } from 'react';
import { Link, useLocation, useNavigate } from 'react-router-dom';
import { FaSignInAlt, FaSpinner } from 'react-icons/fa';
import { TextActionButton } from './settings/components/shared/ActionButtons';
import { getErrorMessage } from './settings/utils';
import { useAuth, type AuthUser } from '../contexts/AuthContext';

function resolveRequestedPath(search: string): string | null {
  const params = new URLSearchParams(search);
  const returnUrl = params.get('returnUrl');
  if (!returnUrl) {
    return null;
  }

  try {
    const decoded = decodeURIComponent(returnUrl);
    if (decoded.startsWith('/')) {
      return decoded;
    }
  } catch {
    // Ignore malformed return urls.
  }

  return null;
}

function getPostAuthPath(user: AuthUser, requestedPath: string | null): string {
  if (user.mustChangePassword) {
    return '/change-password';
  }
  if (user.role === 'Pending') {
    return '/pending';
  }
  if (requestedPath && requestedPath !== '/login' && requestedPath !== '/register') {
    return requestedPath;
  }
  return '/';
}

export default function Login() {
  const navigate = useNavigate();
  const location = useLocation();
  const { login, isAuthenticated, user } = useAuth();

  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);

  const requestedPath = useMemo(() => resolveRequestedPath(location.search), [location.search]);

  useEffect(() => {
    if (isAuthenticated && user) {
      navigate(getPostAuthPath(user, requestedPath), { replace: true });
    }
  }, [isAuthenticated, navigate, requestedPath, user]);

  const handleSubmit = async () => {
    setError(null);
    setSubmitting(true);
    try {
      const authenticated = await login({ email, password });
      navigate(getPostAuthPath(authenticated, requestedPath), { replace: true });
    } catch (submitError) {
      setError(getErrorMessage(submitError, 'Sign in failed.'));
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <div className="min-h-screen bg-gray-50 flex items-center justify-center px-4">
      <div className="w-full max-w-md">
        <div className="rounded border border-gray-200 bg-white p-6 shadow-sm">
          <div className="text-center">
            <img src="/guide.png" alt="GuideAnts logo" className="mx-auto h-12 w-12" />
            <h1 className="mt-3 text-2xl font-bold text-gray-900">GuideAnts Notebooks</h1>
            <p className="mt-1 text-sm text-gray-600">Sign in with your GuideAnts account.</p>
          </div>

          <div className="mt-6 space-y-4">
            {error ? (
              <div className="rounded border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700" role="alert">
                {error}
              </div>
            ) : null}

            <label className="block">
              <span className="mb-1 block text-sm font-medium text-gray-700">Email</span>
              <input
                type="email"
                autoComplete="email"
                value={email}
                onChange={(event) => setEmail(event.target.value)}
                className="w-full rounded border border-gray-300 px-3 py-2 text-sm text-gray-900 shadow-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
                required
              />
            </label>

            <label className="block">
              <span className="mb-1 block text-sm font-medium text-gray-700">Password</span>
              <input
                type="password"
                autoComplete="current-password"
                value={password}
                onChange={(event) => setPassword(event.target.value)}
                className="w-full rounded border border-gray-300 px-3 py-2 text-sm text-gray-900 shadow-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
                required
              />
            </label>

            <div className="pt-1">
              <TextActionButton
                tone="primary"
                icon={submitting ? <FaSpinner className="animate-spin" /> : <FaSignInAlt />}
                disabled={submitting || email.trim().length === 0 || password.trim().length === 0}
                onClick={() => void handleSubmit()}
              >
                {submitting ? 'Signing in...' : 'Sign In'}
              </TextActionButton>
            </div>
          </div>

          <div className="mt-5 space-y-2 text-sm text-gray-700">
            <p>
              First time here?{' '}
              <Link to="/register" className="text-blue-700 hover:underline">
                Create an account
              </Link>
              . The first account becomes the administrator.
            </p>
            <p>
              By continuing, you agree to the{' '}
              <Link to="/terms" className="text-blue-700 hover:underline">
                Terms of Service
              </Link>{' '}
              and{' '}
              <Link to="/privacy" className="text-blue-700 hover:underline">
                Privacy Policy
              </Link>
              .
            </p>
          </div>
        </div>
      </div>
    </div>
  );
}
