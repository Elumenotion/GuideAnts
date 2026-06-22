import React from 'react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import '@testing-library/jest-dom';

vi.unmock('../GuideAntsGuideButton');

import { GuideAntsGuideButton } from '../GuideAntsGuideButton';
import { useAuth } from '../../../contexts/AuthContext';
import { useGuideAntsGuide } from '../GuideAntsGuideProvider';

vi.mock('../../../contexts/AuthContext', () => ({
  useAuth: vi.fn(),
}));

vi.mock('../GuideAntsGuideProvider', () => ({
  useGuideAntsGuide: vi.fn(),
}));

const mockedUseAuth = vi.mocked(useAuth);
const mockedUseGuideAntsGuide = vi.mocked(useGuideAntsGuide);

describe('GuideAntsGuideButton', () => {
  const toggle = vi.fn();

  beforeEach(() => {
    vi.clearAllMocks();
    mockedUseGuideAntsGuide.mockReturnValue({
      isOpen: false,
      open: vi.fn(),
      close: vi.fn(),
      toggle,
      session: null,
      sessionLoading: false,
      buildAppContext: vi.fn(),
    });
  });

  it('renders nothing for anonymous users', () => {
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

    const { container } = render(<GuideAntsGuideButton />);
    expect(container).toBeEmptyDOMElement();
  });

  it('renders nothing for Pending users', () => {
    mockedUseAuth.mockReturnValue({
      user: {
        id: 'u1',
        name: 'Pending User',
        email: 'pending@example.com',
        role: 'Pending',
        mustChangePassword: false,
        lastLoginAt: null,
      },
      role: 'Pending',
      status: 'authenticated',
      isAuthenticated: true,
      login: vi.fn(),
      register: vi.fn(),
      changePassword: vi.fn(),
      refresh: vi.fn(),
      logout: vi.fn(),
    });

    const { container } = render(<GuideAntsGuideButton />);
    expect(container).toBeEmptyDOMElement();
  });

  it('renders for approved Contributor users and toggles the flyout', async () => {
    const user = userEvent.setup();
    mockedUseAuth.mockReturnValue({
      user: {
        id: 'u2',
        name: 'Contributor',
        email: 'contrib@example.com',
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

    render(<GuideAntsGuideButton />);
    await user.click(screen.getByRole('button', { name: 'GuideAnts Guide' }));
    expect(toggle).toHaveBeenCalledTimes(1);
  });

  it('renders for Admin users', () => {
    mockedUseAuth.mockReturnValue({
      user: {
        id: 'u3',
        name: 'Admin',
        email: 'admin@example.com',
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

    render(<GuideAntsGuideButton />);
    expect(screen.getByRole('button', { name: 'GuideAnts Guide' })).toBeInTheDocument();
  });
});
