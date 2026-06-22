import React from 'react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import '@testing-library/jest-dom';
import Settings from '../Settings';
import SystemGuidesWorkspace from '../SystemGuidesWorkspace';
import { ProtectedRoute } from '../../components/ProtectedRoute';
import { useAuth } from '../../contexts/AuthContext';
import type { AppRole } from '../../types/user';
import { ToastProvider } from '../../components/common/Toast';

vi.mock('../../contexts/AuthContext', () => ({
  useAuth: vi.fn(),
}));

vi.mock('../../services/api', () => ({
  api: {
    settings: {
      getSections: vi.fn().mockResolvedValue([]),
      getModels: vi.fn().mockResolvedValue([]),
      getRuntimeProfiles: vi.fn().mockResolvedValue([]),
      getLlamaInventory: vi.fn().mockResolvedValue([]),
    },
    systemGuide: {
      getWorkspace: vi.fn(),
    },
  },
}));

vi.mock('../settings/components/OverviewTab', () => ({
  OverviewTab: () => <div>overview-tab</div>,
}));
vi.mock('../settings/components/PersonalizationTab', () => ({
  PersonalizationTab: () => <div>personalization-tab</div>,
}));
vi.mock('../settings/components/SettingsTabNavigation', () => ({
  SettingsTabNavigation: () => <div>settings-tabs</div>,
}));
vi.mock('../../components/common/HomeButton', () => ({
  HomeButton: () => <button type="button">Home</button>,
}));
vi.mock('../../components/common/SettingsButton', () => ({
  SettingsButton: () => <button type="button">Settings</button>,
}));
vi.mock('../../components/common/HeaderUserMenu', () => ({
  HeaderUserMenu: () => <div>user-menu</div>,
}));
vi.mock('../../tour/TourStartButton', () => ({
  TourStartButton: () => null,
}));
vi.mock('../../features/guideantsGuide/GuideAntsGuideButton', () => ({
  GuideAntsGuideButton: () => null,
}));
vi.mock('../ProjectDetails', () => ({
  default: () => <div>project-details-workspace</div>,
}));
vi.mock('../../contexts/ProjectContext', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../contexts/ProjectContext')>();
  return {
    ...actual,
    ProjectProvider: ({ children }: { children: React.ReactNode }) => <div>{children}</div>,
  };
});
vi.mock('../../components/LoadingSpinner', () => ({
  default: ({ message }: { message?: string }) => <div>{message}</div>,
}));
vi.mock('../../components/ErrorScreen', () => ({
  default: ({ title }: { title?: string }) => <div>{title}</div>,
}));

const mockedUseAuth = vi.mocked(useAuth);

function authState(role: AppRole | null) {
  return {
    user: role
      ? {
          id: 'user-1',
          name: 'Test User',
          email: 'test@example.com',
          role,
          mustChangePassword: false,
          lastLoginAt: null,
        }
      : null,
    role,
    status: role ? ('authenticated' as const) : ('anonymous' as const),
    isAuthenticated: Boolean(role),
    login: vi.fn(),
    register: vi.fn(),
    changePassword: vi.fn(),
    refresh: vi.fn(),
    logout: vi.fn(),
  };
}

function renderSettings() {
  return render(
    <MemoryRouter initialEntries={['/settings']}>
      <ToastProvider>
        <Routes>
          <Route path="/settings" element={<Settings />} />
        </Routes>
      </ToastProvider>
    </MemoryRouter>,
  );
}

function renderSystemGuidesRoute(path = '/settings/system-guides', role: AppRole = 'Contributor') {
  mockedUseAuth.mockReturnValue(authState(role));

  return render(
    <MemoryRouter initialEntries={[path]}>
      <Routes>
        <Route path="/settings" element={<div>settings-page</div>} />
        <Route
          path="/settings/system-guides"
          element={
            <ProtectedRoute requireAdmin adminFallbackPath="/settings">
              <SystemGuidesWorkspace />
            </ProtectedRoute>
          }
        />
      </Routes>
    </MemoryRouter>,
  );
}

describe('System guides settings access', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('shows the System Guides header link for admins only', () => {
    mockedUseAuth.mockReturnValue(authState('Admin'));
    renderSettings();

    expect(screen.getByRole('link', { name: 'System Guides' })).toHaveAttribute(
      'href',
      '/settings/system-guides',
    );
  });

  it('hides the System Guides header link for non-admins', () => {
    mockedUseAuth.mockReturnValue(authState('Contributor'));
    renderSettings();

    expect(screen.queryByRole('link', { name: 'System Guides' })).not.toBeInTheDocument();
  });

  it('redirects non-admins away from the system guides route', () => {
    renderSystemGuidesRoute('/settings/system-guides', 'Contributor');

    expect(screen.getByText('settings-page')).toBeInTheDocument();
    expect(screen.queryByText('Loading system guides workspace...')).not.toBeInTheDocument();
  });
});
