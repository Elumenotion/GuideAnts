import { useEffect, useMemo, useState } from 'react';
import { Link, useLocation, useNavigate } from 'react-router-dom';
import { FaSpinner, FaUserPlus } from 'react-icons/fa';
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
  if (requestedPath && requestedPath !== '/register' && requestedPath !== '/login') {
    return requestedPath;
  }
  return '/';
}

export default function Register() {
  const navigate = useNavigate();
  const location = useLocation();
  const { register, isAuthenticated, user } = useAuth();

  const [name, setName] = useState('');
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [confirmPassword, setConfirmPassword] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);

  const requestedPath = useMemo(() => resolveRequestedPath(location.search), [location.search]);

  useEffect(() => {
    if (isAuthenticated && user) {
      navigate(getPostAuthPath(user, requestedPath), { replace: true });
    }
  }, [isAuthenticated, navigate, requestedPath, user]);

  const validationError = useMemo(() => {
    if (name.trim().length === 0) {
      return 'Name is required.';
    }
    if (email.trim().length === 0) {
      return 'Email is required.';
    }
    if (password.trim().length < 8) {
      return 'Password must be at least 8 characters.';
    }
    if (password !== confirmPassword) {
      return 'Passwords do not match.';
    }
    return null;
  }, [confirmPassword, email, name, password]);

  const handleSubmit = async () => {
    if (validationError) {
      setError(validationError);
      return;
    }

    setError(null);
    setSubmitting(true);
    try {
      const authenticated = await register({
        name,
        email,
        password,
      });
      navigate(getPostAuthPath(authenticated, requestedPath), { replace: true });
    } catch (submitError) {
      setError(getErrorMessage(submitError, 'Registration failed.'));
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
            <h1 className="mt-3 text-2xl font-bold text-gray-900">Create Account</h1>
            <p className="mt-1 text-sm text-gray-600">Start using GuideAnts Notebooks.</p>
          </div>

          <div className="mt-6 space-y-4">
            {error ? (
              <div className="rounded border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700" role="alert">
                {error}
              </div>
            ) : null}

            <label className="block">
              <span className="mb-1 block text-sm font-medium text-gray-700">Name</span>
              <input
                type="text"
                autoComplete="name"
                value={name}
                onChange={(event) => setName(event.target.value)}
                className="w-full rounded border border-gray-300 px-3 py-2 text-sm text-gray-900 shadow-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
                required
              />
            </label>

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
                autoComplete="new-password"
                value={password}
                onChange={(event) => setPassword(event.target.value)}
                className="w-full rounded border border-gray-300 px-3 py-2 text-sm text-gray-900 shadow-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
                required
              />
            </label>

            <label className="block">
              <span className="mb-1 block text-sm font-medium text-gray-700">Confirm password</span>
              <input
                type="password"
                autoComplete="new-password"
                value={confirmPassword}
                onChange={(event) => setConfirmPassword(event.target.value)}
                className="w-full rounded border border-gray-300 px-3 py-2 text-sm text-gray-900 shadow-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
                required
              />
            </label>

            <div className="pt-1">
              <TextActionButton
                tone="primary"
                icon={submitting ? <FaSpinner className="animate-spin" /> : <FaUserPlus />}
                disabled={submitting || Boolean(validationError)}
                onClick={() => void handleSubmit()}
              >
                {submitting ? 'Creating account...' : 'Create Account'}
              </TextActionButton>
            </div>
          </div>

          <div className="mt-5 text-sm text-gray-700">
            <p>
              Already have an account?{' '}
              <Link to="/login" className="text-blue-700 hover:underline">
                Sign in
              </Link>
              .
            </p>
            <p className="mt-2">
              The first account registered on a new install becomes the administrator.
            </p>
          </div>
        </div>
      </div>
    </div>
  );
}
