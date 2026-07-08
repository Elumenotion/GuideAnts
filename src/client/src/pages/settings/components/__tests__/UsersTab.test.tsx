import { describe, expect, it, vi, beforeEach } from 'vitest';
import userEvent from '@testing-library/user-event';
import { render, screen, waitFor } from '@testing-library/react';
import '@testing-library/jest-dom';
import { ToastProvider } from '../../../../components/common/Toast';
import { UsersTab } from '../UsersTab';
import { api } from '../../../../services/api';

vi.mock('../../../../services/api', () => ({
  api: {
    adminUsers: {
      list: vi.fn(),
      approve: vi.fn(),
      changeRole: vi.fn(),
      deactivate: vi.fn(),
      reactivate: vi.fn(),
      setPassword: vi.fn(),
    },
  },
}));

const pendingUser = {
  userId: 'user-pending',
  name: 'Pending User',
  email: 'pending@example.com',
  role: 'Pending' as const,
  isActive: true,
  createdUtc: '2026-01-01T00:00:00Z',
  lastLoginUtc: null,
};

const contributor = {
  userId: 'user-contrib',
  name: 'Contributor User',
  email: 'contrib@example.com',
  role: 'Contributor' as const,
  isActive: true,
  createdUtc: '2026-01-01T00:00:00Z',
  lastLoginUtc: '2026-01-02T00:00:00Z',
};

function renderUsersTab() {
  return render(
    <ToastProvider>
      <UsersTab />
    </ToastProvider>,
  );
}

describe('UsersTab', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(api.adminUsers.list).mockResolvedValue([pendingUser, contributor]);
    vi.mocked(api.adminUsers.approve).mockResolvedValue(undefined as never);
    vi.mocked(api.adminUsers.changeRole).mockResolvedValue(undefined as never);
  });

  it('loads users and approves pending accounts', async () => {
    const user = userEvent.setup();
    renderUsersTab();

    expect(await screen.findByText('Pending User')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Approve user' }));

    await waitFor(() => {
      expect(api.adminUsers.approve).toHaveBeenCalledWith('user-pending', 'Contributor');
    });
  });

  it('changes roles for active users', async () => {
    const user = userEvent.setup();
    renderUsersTab();

    await screen.findByText('Contributor User');
    await user.selectOptions(screen.getAllByRole('combobox')[2], 'Admin');
    await user.click(screen.getByRole('button', { name: 'Update role' }));

    await waitFor(() => {
      expect(api.adminUsers.changeRole).toHaveBeenCalledWith('user-contrib', 'Admin');
    });
  });

  it('shows load failures with retry', async () => {
    const user = userEvent.setup();
    vi.mocked(api.adminUsers.list)
      .mockRejectedValueOnce(new Error('Users unavailable'))
      .mockResolvedValueOnce([contributor]);

    renderUsersTab();

    expect(await screen.findByText(/Users unavailable/i)).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Refresh' }));
    expect(await screen.findByText('Contributor User')).toBeInTheDocument();
  });

  it('deactivates an active user after confirmation', async () => {
    const user = userEvent.setup();
    vi.mocked(api.adminUsers.list).mockResolvedValue([contributor]);
    vi.mocked(api.adminUsers.deactivate).mockResolvedValue(undefined as never);
    renderUsersTab();

    await screen.findByText('Contributor User');
    await user.click(screen.getByRole('button', { name: 'Deactivate user' }));
    await user.click(screen.getByRole('button', { name: 'Deactivate' }));

    await waitFor(() => {
      expect(api.adminUsers.deactivate).toHaveBeenCalledWith('user-contrib');
    });
  });

  it('sets a temporary password from the modal', async () => {
    const user = userEvent.setup();
    vi.mocked(api.adminUsers.list).mockResolvedValue([contributor]);
    vi.mocked(api.adminUsers.setPassword).mockResolvedValue(undefined as never);
    renderUsersTab();

    await screen.findByText('Contributor User');
    await user.click(screen.getByRole('button', { name: 'Set password' }));
    await user.type(screen.getByLabelText('Temporary password'), 'password123');
    await user.type(screen.getByLabelText('Confirm password'), 'password123');
    await user.click(screen.getByRole('button', { name: 'Set Password' }));

    await waitFor(() => {
      expect(api.adminUsers.setPassword).toHaveBeenCalledWith('user-contrib', 'password123');
    });
  });
});
