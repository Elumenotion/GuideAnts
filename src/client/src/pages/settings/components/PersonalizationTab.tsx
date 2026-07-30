import { useCallback, useEffect, useMemo, useState } from 'react';
import { FaKey, FaRedo, FaSave, FaSignOutAlt, FaSpinner } from 'react-icons/fa';
import { useNavigate } from 'react-router';
import LoadingSpinner from '../../../components/LoadingSpinner';
import { useToast } from '../../../components/common/Toast';
import { api } from '../../../services/api';
import { userService } from '../../../services/userService';
import type { UserDto } from '../../../types/user';
import { getErrorMessage } from '../utils';
import { TextActionButton } from './shared/ActionButtons';
import { useAuth } from '../../../contexts/AuthContext';

interface PersonalizationFormState {
  name: string;
  email: string;
}

function createFormState(user: UserDto): PersonalizationFormState {
  return {
    name: user.name ?? '',
    email: user.email ?? '',
  };
}

export function PersonalizationTab() {
  const navigate = useNavigate();
  const { showToast } = useToast();
  const { user: authUser, changePassword, logout } = useAuth();
  const [user, setUser] = useState<UserDto | null>(null);
  const [draft, setDraft] = useState<PersonalizationFormState>({ name: '', email: '' });
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [currentPassword, setCurrentPassword] = useState('');
  const [newPassword, setNewPassword] = useState('');
  const [confirmPassword, setConfirmPassword] = useState('');
  const [passwordSaving, setPasswordSaving] = useState(false);
  const [passwordError, setPasswordError] = useState<string | null>(null);

  const loadUser = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const nextUser = await api.users.getCurrent();
      setUser(nextUser);
      setDraft(createFormState(nextUser));
      userService.setCurrentUser(nextUser);
    } catch (loadError) {
      setError(getErrorMessage(loadError, 'Failed to load personalization settings.'));
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void loadUser();
  }, [loadUser]);

  const trimmedDraft = useMemo(
    () => ({
      name: draft.name.trim(),
      email: draft.email.trim(),
    }),
    [draft]
  );

  const isDirty = useMemo(() => {
    if (!user) {
      return false;
    }

    return trimmedDraft.name !== user.name || trimmedDraft.email !== user.email;
  }, [trimmedDraft, user]);

  const validationError = useMemo(() => {
    if (!trimmedDraft.name) {
      return 'Display name is required.';
    }
    if (!trimmedDraft.email) {
      return 'Email is required.';
    }
    return null;
  }, [trimmedDraft]);

  const handleSave = useCallback(async () => {
    if (validationError) {
      setError(validationError);
      return;
    }

    setSaving(true);
    setError(null);
    try {
      const updated = await api.users.updatePersonalization(trimmedDraft);
      setUser(updated);
      setDraft(createFormState(updated));
      userService.setCurrentUser(updated);
      showToast({ type: 'success', title: 'Personalization saved' });
    } catch (saveError) {
      setError(getErrorMessage(saveError, 'Failed to save personalization settings.'));
      showToast({
        type: 'error',
        title: 'Save failed',
        message: getErrorMessage(saveError, 'Request failed.'),
      });
    } finally {
      setSaving(false);
    }
  }, [showToast, trimmedDraft, validationError]);

  const handleReset = useCallback(() => {
    if (user) {
      setDraft(createFormState(user));
      setError(null);
    }
  }, [user]);

  const passwordValidationError = useMemo(() => {
    if (!currentPassword.trim()) {
      return 'Current password is required.';
    }
    if (newPassword.trim().length < 8) {
      return 'New password must be at least 8 characters.';
    }
    if (newPassword !== confirmPassword) {
      return 'Passwords do not match.';
    }
    return null;
  }, [confirmPassword, currentPassword, newPassword]);

  const handleChangePassword = useCallback(async () => {
    if (passwordValidationError) {
      setPasswordError(passwordValidationError);
      return;
    }

    setPasswordSaving(true);
    setPasswordError(null);
    try {
      await changePassword({
        currentPassword,
        newPassword,
      });
      setCurrentPassword('');
      setNewPassword('');
      setConfirmPassword('');
      showToast({ type: 'success', title: 'Password updated' });
    } catch (changeError) {
      setPasswordError(getErrorMessage(changeError, 'Unable to update password.'));
      showToast({
        type: 'error',
        title: 'Password update failed',
        message: getErrorMessage(changeError, 'Request failed.'),
      });
    } finally {
      setPasswordSaving(false);
    }
  }, [changePassword, currentPassword, newPassword, passwordValidationError, showToast, confirmPassword]);

  const handleSignOut = useCallback(() => {
    logout();
    navigate('/login', { replace: true });
  }, [logout, navigate]);

  if (loading) {
    return <LoadingSpinner message="Loading personalization..." />;
  }

  if (!user) {
    return (
      <div className="rounded border border-red-200 bg-red-50 p-4 text-sm text-red-700">
        <div className="font-medium">Personalization unavailable</div>
        <div className="mt-1">{error ?? 'No user row was found.'}</div>
        <div className="mt-3">
          <TextActionButton tone="neutral" icon={<FaRedo />} onClick={() => void loadUser()}>
            Retry
          </TextActionButton>
        </div>
      </div>
    );
  }

  return (
    <section className="space-y-4">
      <div className="rounded border border-gray-200 bg-white p-5 shadow-sm">
        <div className="mb-5 flex flex-wrap items-center justify-between gap-3">
          <div>
            <h2 className="text-lg font-semibold text-gray-900">Personalization</h2>
            <p className="mt-1 text-sm text-gray-600">Profile details</p>
          </div>
          <div className="flex items-center gap-2">
            <TextActionButton tone="neutral" icon={<FaRedo />} disabled={!isDirty || saving} onClick={handleReset}>
              Reset
            </TextActionButton>
            <TextActionButton
              tone="primary"
              icon={saving ? <FaSpinner className="animate-spin" /> : <FaSave />}
              disabled={!isDirty || saving || Boolean(validationError)}
              onClick={() => void handleSave()}
            >
              Save
            </TextActionButton>
          </div>
        </div>

        {error ? (
          <div className="mb-4 rounded border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700" role="alert">
            {error}
          </div>
        ) : null}

        <div className="grid gap-4 md:grid-cols-2">
          <label className="block">
            <span className="mb-1 block text-sm font-medium text-gray-700">Display name</span>
            <input
              type="text"
              value={draft.name}
              maxLength={255}
              onChange={(event) => setDraft((previous) => ({ ...previous, name: event.target.value }))}
              className="w-full rounded border border-gray-300 px-3 py-2 text-sm text-gray-900 shadow-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
            />
          </label>

          <label className="block">
            <span className="mb-1 block text-sm font-medium text-gray-700">Email</span>
            <input
              type="email"
              value={draft.email}
              onChange={(event) => setDraft((previous) => ({ ...previous, email: event.target.value }))}
              className="w-full rounded border border-gray-300 px-3 py-2 text-sm text-gray-900 shadow-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
            />
          </label>
        </div>
      </div>

      <div className="rounded border border-gray-200 bg-white p-5 shadow-sm space-y-4">
        <div>
          <h3 className="text-lg font-semibold text-gray-900">Account Security</h3>
          <p className="mt-1 text-sm text-gray-600">Manage your password and session.</p>
        </div>

        <label className="block">
          <span className="mb-1 block text-sm font-medium text-gray-700">Current role</span>
          <input
            type="text"
            value={authUser?.role ?? 'Unknown'}
            disabled
            className="w-full rounded border border-gray-300 px-3 py-2 text-sm text-gray-900 shadow-sm bg-gray-50"
          />
        </label>

        {passwordError ? (
          <div className="rounded border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700" role="alert">
            {passwordError}
          </div>
        ) : null}

        <div className="grid gap-4 md:grid-cols-3">
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
        </div>

        <div className="flex items-center gap-2">
          <TextActionButton
            tone="primary"
            icon={passwordSaving ? <FaSpinner className="animate-spin" /> : <FaKey />}
            disabled={passwordSaving || Boolean(passwordValidationError)}
            onClick={() => void handleChangePassword()}
          >
            {passwordSaving ? 'Updating...' : 'Change Password'}
          </TextActionButton>
          <TextActionButton tone="neutral" icon={<FaSignOutAlt />} onClick={handleSignOut}>
            Sign Out
          </TextActionButton>
        </div>
      </div>
    </section>
  );
}
