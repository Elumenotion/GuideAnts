import React from 'react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen } from '../../../test/test-utils';
import userEvent from '@testing-library/user-event';
import { GuidesButton } from '../GuidesButton';
import { useAuth } from '../../../contexts/AuthContext';

const mockNavigate = vi.fn();

vi.mock('react-router', async () => {
  const actual = await vi.importActual('react-router');
  return {
    ...actual,
    useNavigate: () => mockNavigate,
  };
});

vi.mock('../../../contexts/AuthContext', () => ({
  useAuth: vi.fn(),
  AuthProvider: ({ children }: { children: React.ReactNode }) => <>{children}</>,
}));

const mockedUseAuth = vi.mocked(useAuth);

const authBase = {
  user: null as any,
  role: null as any,
  status: 'anonymous' as any,
  isAuthenticated: false,
  login: vi.fn(),
  register: vi.fn(),
  changePassword: vi.fn(),
  refresh: vi.fn(),
  logout: vi.fn(),
};

const authStatus = (status: string, role: string) => ({
  ...authBase,
  status,
  role,
  isAuthenticated: status === 'authenticated',
  user: status === 'authenticated' ? { id: 'u1', mustChangePassword: false } : null,
});

describe('GuidesButton', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('navigates to /guides when clicked', async () => {
    mockedUseAuth.mockReturnValue(authStatus('authenticated', 'Admin'));
    render(<GuidesButton />);
    await userEvent.click(screen.getByRole('button', { name: 'Open Guides' }));
    expect(mockNavigate).toHaveBeenCalledWith('/guides');
  });

  it('renders nothing for anonymous users', () => {
    mockedUseAuth.mockReturnValue(authStatus('anonymous', 'Admin'));
    const { container } = render(<GuidesButton />);
    expect(container).toBeEmptyDOMElement();
  });

  it('renders nothing for non-admin (Editor) users', () => {
    mockedUseAuth.mockReturnValue(authStatus('authenticated', 'Editor'));
    const { container } = render(<GuidesButton />);
    expect(container).toBeEmptyDOMElement();
  });

  it('renders nothing for non-admin (Reader) users', () => {
    mockedUseAuth.mockReturnValue(authStatus('authenticated', 'Reader'));
    const { container } = render(<GuidesButton />);
    expect(container).toBeEmptyDOMElement();
  });

  it('applies optional className', () => {
    mockedUseAuth.mockReturnValue(authStatus('authenticated', 'Admin'));
    render(<GuidesButton className="extra-class" />);
    expect(screen.getByRole('button', { name: 'Open Guides' })).toHaveClass('extra-class');
  });
});
