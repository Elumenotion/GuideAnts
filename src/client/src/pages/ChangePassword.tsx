import { useMemo, useState } from 'react';
import { useNavigate } from 'react-router';
import { FaKey, FaSignOutAlt, FaSpinner } from 'react-icons/fa';
import { TextActionButton } from './settings/components/shared/ActionButtons';
import { useAuth } from '../contexts/AuthContext';
import { getErrorMessage } from './settings/utils';

export default function ChangePassword() {
  const navigate = useNavigate();
  const { changePassword, logout, refresh } = useAuth();
  const [currentPassword, setCurrentPassword] = useState('');
  const [newPassword, setNewPassword] = useState('');
  const [confirmPassword, setConfirmPassword] = useState('');
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const validationError = useMemo(() => {
    if (currentPassword.trim().length === 0) {
      return 'Current password is required.';
    }
    if (newPassword.trim().length < 8) {
      return 'New password must be at least 8 characters.';
    }
    if (confirmPassword !== newPassword) {
      return 'New passwords do not match.';
    }
    return null;
  }, [confirmPassword, currentPassword, newPassword]);

  const handleSignOut = () => {
    logout();
    navigate('/login', { replace: true });
  };

  const handleSubmit = async () => {
    if (validationError) {
      setError(validationError);
      return;
    }

    setSaving(true);
    setError(null);
    try {
      await changePassword({
        currentPassword,
        newPassword,
      });
      const refreshed = await refresh();
      if (refreshed?.role === 'Pending') {
        navigate('/pending', { replace: true });
        return;
      }
      navigate('/', { replace: true });
    } catch (submitError) {
      setError(getErrorMessage(submitError, 'Unable to update password.'));
    } finally {
      setSaving(false);
    }
  };

  return (
    <div className="min-h-screen bg-gray-50 flex items-center justify-center px-4">
      <div className="w-full max-w-md">
        <div className="rounded border border-gray-200 bg-white p-6 shadow-sm">
          <div className="text-center">
            <img src="/guide.png" alt="GuideAnts logo" className="mx-auto h-12 w-12" />
            <h1 className="mt-3 text-2xl font-bold text-gray-900">Change Password</h1>
            <p className="mt-1 text-sm text-gray-600">
              You must set a new password before continuing.
            </p>
          </div>

          <div className="mt-6 space-y-4">
            {error ? (
              <div className="rounded border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700" role="alert">
                {error}
              </div>
            ) : null}

            <label className="block">
              <span className="mb-1 block text-sm font-medium text-gray-700">Current password</span>
              <input
                type="password"
                autoComplete="current-password"
                value={currentPassword}
                onChange={(event) => setCurrentPassword(event.target.value)}
                className="w-full rounded border border-gray-300 px-3 py-2 text-sm text-gray-900 shadow-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
              />
            </label>

            <label className="block">
              <span className="mb-1 block text-sm font-medium text-gray-700">New password</span>
              <input
                type="password"
                autoComplete="new-password"
                value={newPassword}
                onChange={(event) => setNewPassword(event.target.value)}
                className="w-full rounded border border-gray-300 px-3 py-2 text-sm text-gray-900 shadow-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
              />
            </label>

            <label className="block">
              <span className="mb-1 block text-sm font-medium text-gray-700">Confirm new password</span>
              <input
                type="password"
                autoComplete="new-password"
                value={confirmPassword}
                onChange={(event) => setConfirmPassword(event.target.value)}
                className="w-full rounded border border-gray-300 px-3 py-2 text-sm text-gray-900 shadow-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
              />
            </label>

            <div className="pt-1 flex items-center gap-2">
              <TextActionButton
                tone="primary"
                icon={saving ? <FaSpinner className="animate-spin" /> : <FaKey />}
                disabled={saving || Boolean(validationError)}
                onClick={() => void handleSubmit()}
              >
                {saving ? 'Saving...' : 'Update Password'}
              </TextActionButton>
              <TextActionButton tone="neutral" icon={<FaSignOutAlt />} disabled={saving} onClick={handleSignOut}>
                Sign Out
              </TextActionButton>
            </div>
          </div>
        </div>
      </div>
    </div>
  );
}
