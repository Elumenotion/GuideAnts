import { useState } from 'react';
import { useNavigate } from 'react-router';
import { FaSignOutAlt, FaSpinner, FaSyncAlt } from 'react-icons/fa';
import { TextActionButton } from './settings/components/shared/ActionButtons';
import { useAuth } from '../contexts/AuthContext';
import { getErrorMessage } from './settings/utils';

export default function Pending() {
  const navigate = useNavigate();
  const { user, refresh, logout } = useAuth();
  const [checking, setChecking] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const handleRefresh = async () => {
    setChecking(true);
    setError(null);
    try {
      const refreshed = await refresh();
      if (!refreshed) {
        navigate('/login', { replace: true });
        return;
      }
      if (refreshed.mustChangePassword) {
        navigate('/change-password', { replace: true });
        return;
      }
      if (refreshed.role !== 'Pending') {
        navigate('/', { replace: true });
      }
    } catch (refreshError) {
      setError(getErrorMessage(refreshError, 'Unable to refresh your account status.'));
    } finally {
      setChecking(false);
    }
  };

  const handleSignOut = () => {
    logout();
    navigate('/login', { replace: true });
  };

  return (
    <div className="min-h-screen bg-gray-50 flex items-center justify-center px-4">
      <div className="w-full max-w-md">
        <div className="rounded border border-gray-200 bg-white p-6 shadow-sm text-center">
          <img src="/guide.png" alt="GuideAnts logo" className="mx-auto h-12 w-12" />
          <h1 className="mt-3 text-2xl font-bold text-gray-900">Awaiting Approval</h1>
          <p className="mt-2 text-sm text-gray-600">
            {user?.name ? `${user.name}, ` : ''}your account is pending administrator approval.
          </p>

          {error ? (
            <div className="mt-4 rounded border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700" role="alert">
              {error}
            </div>
          ) : null}

          <div className="mt-6 flex items-center justify-center gap-2">
            <TextActionButton
              tone="primary"
              icon={checking ? <FaSpinner className="animate-spin" /> : <FaSyncAlt />}
              disabled={checking}
              onClick={() => void handleRefresh()}
            >
              {checking ? 'Checking...' : 'Refresh Status'}
            </TextActionButton>
            <TextActionButton tone="neutral" icon={<FaSignOutAlt />} onClick={handleSignOut}>
              Sign Out
            </TextActionButton>
          </div>
        </div>
      </div>
    </div>
  );
}
