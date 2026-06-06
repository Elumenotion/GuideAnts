import { useCallback, useEffect, useMemo, useState } from 'react';
import { FaCheck, FaKey, FaPowerOff, FaRedo, FaSpinner, FaUserEdit } from 'react-icons/fa';
import { api, type AdminUserSummaryDto, type AdminUserStatusFilter } from '../../../services/api';
import { useToast } from '../../../components/common/Toast';
import { ConfirmationDialog } from '../../../components/common/ConfirmationDialog';
import { getErrorMessage, formatDateTime } from '../utils';
import { IconActionButton, TextActionButton } from './shared/ActionButtons';
import type { AppRole } from '../../../types/user';

const ASSIGNABLE_ROLES: AppRole[] = ['Reader', 'Contributor', 'Admin'];

interface ConfirmAction {
  kind: 'deactivate' | 'reactivate';
  user: AdminUserSummaryDto;
}

export function UsersTab() {
  const { showToast } = useToast();
  const [users, setUsers] = useState<AdminUserSummaryDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [statusFilter, setStatusFilter] = useState<AdminUserStatusFilter>('all');
  const [roleFilter, setRoleFilter] = useState<string>('all');
  const [selectedRoleByUser, setSelectedRoleByUser] = useState<Record<string, AppRole>>({});
  const [pendingAction, setPendingAction] = useState<ConfirmAction | null>(null);
  const [actionInFlightUserId, setActionInFlightUserId] = useState<string | null>(null);

  const [passwordModalUser, setPasswordModalUser] = useState<AdminUserSummaryDto | null>(null);
  const [password, setPassword] = useState('');
  const [confirmPassword, setConfirmPassword] = useState('');
  const [passwordError, setPasswordError] = useState<string | null>(null);
  const [savingPassword, setSavingPassword] = useState(false);

  const loadUsers = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const nextUsers = await api.adminUsers.list({
        status: statusFilter,
        role: roleFilter === 'all' ? undefined : roleFilter,
      });
      setUsers(nextUsers);
      setSelectedRoleByUser((previous) => {
        const next = { ...previous };
        nextUsers.forEach((user) => {
          if (!next[user.userId]) {
            next[user.userId] = user.role === 'Pending' ? 'Contributor' : user.role;
          }
        });
        return next;
      });
    } catch (loadError) {
      setError(getErrorMessage(loadError, 'Failed to load users.'));
    } finally {
      setLoading(false);
    }
  }, [roleFilter, statusFilter]);

  useEffect(() => {
    void loadUsers();
  }, [loadUsers]);

  const handleApprove = async (user: AdminUserSummaryDto) => {
    const role = selectedRoleByUser[user.userId] ?? 'Contributor';
    setActionInFlightUserId(user.userId);
    try {
      await api.adminUsers.approve(user.userId, role);
      showToast({ type: 'success', title: `Approved ${user.name}` });
      await loadUsers();
    } catch (approveError) {
      showToast({
        type: 'error',
        title: 'Approve failed',
        message: getErrorMessage(approveError, 'Unable to approve user.'),
      });
    } finally {
      setActionInFlightUserId(null);
    }
  };

  const handleRoleChange = async (user: AdminUserSummaryDto) => {
    const role = selectedRoleByUser[user.userId] ?? user.role;
    setActionInFlightUserId(user.userId);
    try {
      await api.adminUsers.changeRole(user.userId, role);
      showToast({ type: 'success', title: `Updated role for ${user.name}` });
      await loadUsers();
    } catch (roleError) {
      showToast({
        type: 'error',
        title: 'Role update failed',
        message: getErrorMessage(roleError, 'Unable to change role.'),
      });
    } finally {
      setActionInFlightUserId(null);
    }
  };

  const confirmStatusChange = async () => {
    if (!pendingAction) {
      return;
    }

    setActionInFlightUserId(pendingAction.user.userId);
    try {
      if (pendingAction.kind === 'deactivate') {
        await api.adminUsers.deactivate(pendingAction.user.userId);
      } else {
        await api.adminUsers.reactivate(pendingAction.user.userId);
      }
      showToast({
        type: 'success',
        title: pendingAction.kind === 'deactivate' ? `Deactivated ${pendingAction.user.name}` : `Reactivated ${pendingAction.user.name}`,
      });
      setPendingAction(null);
      await loadUsers();
    } catch (statusError) {
      showToast({
        type: 'error',
        title: pendingAction.kind === 'deactivate' ? 'Deactivate failed' : 'Reactivate failed',
        message: getErrorMessage(statusError, 'Operation failed.'),
      });
    } finally {
      setActionInFlightUserId(null);
    }
  };

  const openSetPassword = (user: AdminUserSummaryDto) => {
    setPasswordModalUser(user);
    setPassword('');
    setConfirmPassword('');
    setPasswordError(null);
  };

  const submitPassword = async () => {
    if (!passwordModalUser) {
      return;
    }

    if (password.trim().length < 8) {
      setPasswordError('Password must be at least 8 characters.');
      return;
    }
    if (password !== confirmPassword) {
      setPasswordError('Passwords do not match.');
      return;
    }

    setSavingPassword(true);
    setPasswordError(null);
    try {
      await api.adminUsers.setPassword(passwordModalUser.userId, password);
      showToast({
        type: 'success',
        title: `Password set for ${passwordModalUser.name}`,
        message: 'The user must change this password at next sign-in.',
      });
      setPasswordModalUser(null);
      await loadUsers();
    } catch (passwordSetError) {
      setPasswordError(getErrorMessage(passwordSetError, 'Unable to set password.'));
    } finally {
      setSavingPassword(false);
    }
  };

  const sortedUsers = useMemo(
    () => [...users].sort((left, right) => left.name.localeCompare(right.name) || left.email.localeCompare(right.email)),
    [users]
  );
  const canSubmitPassword = password.trim().length >= 8 && password === confirmPassword;

  return (
    <section className="space-y-4">
      <div className="rounded border border-gray-200 bg-white p-5 shadow-sm">
        <div className="mb-5 flex flex-wrap items-center justify-between gap-3">
          <div>
            <h2 className="text-lg font-semibold text-gray-900">Users</h2>
            <p className="mt-1 text-sm text-gray-600">Approve, assign roles, manage account status, and set temporary passwords.</p>
          </div>
          <div className="flex items-center gap-2">
            <TextActionButton tone="neutral" icon={<FaRedo />} disabled={loading} onClick={() => void loadUsers()}>
              Refresh
            </TextActionButton>
          </div>
        </div>

        <div className="grid gap-4 md:grid-cols-2">
          <label className="block">
            <span className="mb-1 block text-sm font-medium text-gray-700">Status filter</span>
            <select
              value={statusFilter}
              onChange={(event) => setStatusFilter(event.target.value as AdminUserStatusFilter)}
              className="w-full rounded border border-gray-300 px-3 py-2 text-sm text-gray-900 shadow-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
            >
              <option value="all">All</option>
              <option value="pending">Pending</option>
              <option value="active">Active</option>
              <option value="inactive">Inactive</option>
            </select>
          </label>
          <label className="block">
            <span className="mb-1 block text-sm font-medium text-gray-700">Role filter</span>
            <select
              value={roleFilter}
              onChange={(event) => setRoleFilter(event.target.value)}
              className="w-full rounded border border-gray-300 px-3 py-2 text-sm text-gray-900 shadow-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
            >
              <option value="all">All roles</option>
              <option value="Pending">Pending</option>
              <option value="Reader">Reader</option>
              <option value="Contributor">Contributor</option>
              <option value="Admin">Admin</option>
            </select>
          </label>
        </div>
      </div>

      <div className="rounded border border-gray-200 bg-white p-5 shadow-sm">
        {loading ? (
          <div className="text-sm text-gray-600">Loading users...</div>
        ) : error ? (
          <div className="rounded border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700" role="alert">
            {error}
          </div>
        ) : (
          <div className="overflow-x-auto">
            <table className="min-w-full text-sm text-left text-gray-700">
              <thead className="text-xs uppercase text-gray-500 border-b border-gray-200">
                <tr>
                  <th className="px-3 py-2 font-semibold">Name</th>
                  <th className="px-3 py-2 font-semibold">Email</th>
                  <th className="px-3 py-2 font-semibold">Role</th>
                  <th className="px-3 py-2 font-semibold">Status</th>
                  <th className="px-3 py-2 font-semibold">Must change password</th>
                  <th className="px-3 py-2 font-semibold">Last login</th>
                  <th className="px-3 py-2 font-semibold text-right">Actions</th>
                </tr>
              </thead>
              <tbody>
                {sortedUsers.map((user) => {
                  const selectedRole = selectedRoleByUser[user.userId] ?? (user.role === 'Pending' ? 'Contributor' : user.role);
                  const isBusy = actionInFlightUserId === user.userId;
                  return (
                    <tr key={user.userId} className="border-b border-gray-100">
                      <td className="px-3 py-2">{user.name}</td>
                      <td className="px-3 py-2">{user.email}</td>
                      <td className="px-3 py-2">
                        <select
                          value={selectedRole}
                          onChange={(event) => {
                            setSelectedRoleByUser((previous) => ({
                              ...previous,
                              [user.userId]: event.target.value as AppRole,
                            }));
                          }}
                          className="rounded border border-gray-300 px-2 py-1 text-xs text-gray-900 shadow-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
                        >
                          {ASSIGNABLE_ROLES.map((role) => (
                            <option key={role} value={role}>{role}</option>
                          ))}
                        </select>
                      </td>
                      <td className="px-3 py-2">{user.isActive ? 'Active' : user.role === 'Pending' ? 'Pending' : 'Inactive'}</td>
                      <td className="px-3 py-2">{user.mustChangePassword ? 'Yes' : 'No'}</td>
                      <td className="px-3 py-2">{formatDateTime(user.lastLoginAt ?? undefined)}</td>
                      <td className="px-3 py-2">
                        <div className="flex items-center justify-end gap-1">
                          {user.role === 'Pending' ? (
                            <IconActionButton
                              label="Approve user"
                              tone="success"
                              icon={isBusy ? <FaSpinner className="animate-spin" /> : <FaCheck />}
                              disabled={isBusy}
                              onClick={() => void handleApprove(user)}
                            />
                          ) : (
                            <IconActionButton
                              label="Update role"
                              tone="info"
                              icon={isBusy ? <FaSpinner className="animate-spin" /> : <FaUserEdit />}
                              disabled={isBusy || selectedRole === user.role}
                              onClick={() => void handleRoleChange(user)}
                            />
                          )}
                          <IconActionButton
                            label="Set password"
                            tone="accent"
                            icon={<FaKey />}
                            disabled={isBusy}
                            onClick={() => openSetPassword(user)}
                          />
                          <IconActionButton
                            label={user.isActive ? 'Deactivate user' : 'Reactivate user'}
                            tone={user.isActive ? 'danger' : 'neutral'}
                            icon={<FaPowerOff />}
                            disabled={isBusy || user.role === 'Pending'}
                            onClick={() =>
                              setPendingAction({
                                kind: user.isActive ? 'deactivate' : 'reactivate',
                                user,
                              })
                            }
                          />
                        </div>
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        )}
      </div>

      <ConfirmationDialog
        isOpen={pendingAction !== null}
        onClose={() => setPendingAction(null)}
        onConfirm={() => void confirmStatusChange()}
        title={pendingAction?.kind === 'deactivate' ? 'Deactivate user?' : 'Reactivate user?'}
        message={pendingAction ? `${pendingAction.user.name} (${pendingAction.user.email})` : ''}
        confirmText={pendingAction?.kind === 'deactivate' ? 'Deactivate' : 'Reactivate'}
        confirmButtonClass={pendingAction?.kind === 'deactivate' ? 'bg-red-600 hover:bg-red-700 text-white' : undefined}
      />

      <ConfirmationDialog
        isOpen={passwordModalUser !== null}
        onClose={() => setPasswordModalUser(null)}
        onConfirm={() => void submitPassword()}
        title="Set Password"
        message={passwordModalUser ? `${passwordModalUser.name} (${passwordModalUser.email})` : ''}
        confirmText="Set Password"
        confirmButtonClass="bg-blue-600 hover:bg-blue-700 text-white"
        isLoading={savingPassword}
        confirmDisabled={!canSubmitPassword}
        body={passwordModalUser ? (
          <div className="space-y-4">
            {passwordError ? (
              <div className="rounded border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700" role="alert">
                {passwordError}
              </div>
            ) : null}
            <label className="block">
              <span className="mb-1 block text-sm font-medium text-gray-700">Temporary password</span>
              <input
                type="password"
                value={password}
                onChange={(event) => setPassword(event.target.value)}
                className="w-full rounded border border-gray-300 px-3 py-2 text-sm text-gray-900 shadow-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
              />
            </label>
            <label className="block">
              <span className="mb-1 block text-sm font-medium text-gray-700">Confirm password</span>
              <input
                type="password"
                value={confirmPassword}
                onChange={(event) => setConfirmPassword(event.target.value)}
                className="w-full rounded border border-gray-300 px-3 py-2 text-sm text-gray-900 shadow-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
              />
            </label>
          </div>
        ) : null}
      />
    </section>
  );
}
