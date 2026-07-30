import React from 'react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import '@testing-library/jest-dom';
import { HeaderUserMenu } from '../HeaderUserMenu';
import { useAuth } from '../../../contexts/AuthContext';

const navigateMock = vi.fn();

vi.mock('react-router', async (importOriginal) => {
  const actual = await importOriginal<typeof import('react-router')>();
  return {
    ...actual,
    useNavigate: () => navigateMock,
  };
});

vi.mock('../../../contexts/AuthContext', () => ({
  useAuth: vi.fn(),
}));

const mockedUseAuth = vi.mocked(useAuth);

function renderMenu() {
  return render(
    <MemoryRouter>
      <HeaderUserMenu />
    </MemoryRouter>,
  );
}

describe('HeaderUserMenu', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('renders nothing when there is no authenticated user', () => {
    mockedUseAuth.mockReturnValue({
      user: null,
      role: null,
      status: 'anonymous',
      isAuthenticated: false,
      login: vi.fn(),
      register: vi.fn(),
      changePassword: vi.fn(),
      refresh: vi.fn(),
      logout: vi.fn(),
    });

    const { container } = renderMenu();
    expect(container).toBeEmptyDOMElement();
  });

  it('opens the menu and shows user details', async () => {
    const user = userEvent.setup();
    mockedUseAuth.mockReturnValue({
      user: {
        id: 'u1',
        name: 'Ada Lovelace',
        email: 'ada@example.com',
        role: 'Admin',
        mustChangePassword: false,
        lastLoginAt: null,
      },
      role: 'Admin',
      status: 'authenticated',
      isAuthenticated: true,
      login: vi.fn(),
      register: vi.fn(),
      changePassword: vi.fn(),
      refresh: vi.fn(),
      logout: vi.fn(),
    });

    renderMenu();

    await user.click(screen.getByRole('button', { name: 'User menu' }));

    expect(screen.getByText('Ada Lovelace')).toBeInTheDocument();
    expect(screen.getByText('ada@example.com')).toBeInTheDocument();
    expect(screen.getByText('Role: Admin')).toBeInTheDocument();
  });

  it('signs out and navigates to login', async () => {
    const user = userEvent.setup();
    const logout = vi.fn();
    mockedUseAuth.mockReturnValue({
      user: {
        id: 'u1',
        name: 'Test User',
        email: 'test@example.com',
        role: 'Contributor',
        mustChangePassword: false,
        lastLoginAt: null,
      },
      role: 'Contributor',
      status: 'authenticated',
      isAuthenticated: true,
      login: vi.fn(),
      register: vi.fn(),
      changePassword: vi.fn(),
      refresh: vi.fn(),
      logout,
    });

    renderMenu();
    await user.click(screen.getByRole('button', { name: 'User menu' }));
    await user.click(screen.getByRole('button', { name: /sign out/i }));

    expect(logout).toHaveBeenCalledTimes(1);
    expect(navigateMock).toHaveBeenCalledWith('/login', { replace: true });
  });

  it('closes the menu when clicking outside', async () => {
    const user = userEvent.setup();
    mockedUseAuth.mockReturnValue({
      user: {
        id: 'u1',
        name: 'Test User',
        email: 'test@example.com',
        role: 'Contributor',
        mustChangePassword: false,
        lastLoginAt: null,
      },
      role: 'Contributor',
      status: 'authenticated',
      isAuthenticated: true,
      login: vi.fn(),
      register: vi.fn(),
      changePassword: vi.fn(),
      refresh: vi.fn(),
      logout: vi.fn(),
    });

    render(
      <MemoryRouter>
        <div>
          <HeaderUserMenu />
          <button type="button">Outside</button>
        </div>
      </MemoryRouter>,
    );

    await user.click(screen.getByRole('button', { name: 'User menu' }));
    expect(screen.getByText('test@example.com')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Outside' }));
    expect(screen.queryByText('test@example.com')).not.toBeInTheDocument();
  });
});
