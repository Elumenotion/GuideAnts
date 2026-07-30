import { render, screen } from '@testing-library/react';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router';
import { describe, expect, it, vi } from 'vitest';
import { ProtectedRoute } from '../ProtectedRoute';
import { useAuth } from '../../contexts/AuthContext';
import type { AppRole } from '../../types/user';

vi.mock('../../contexts/AuthContext', () => ({
  useAuth: vi.fn(),
}));

const mockedUseAuth = vi.mocked(useAuth);

function LoginProbe() {
  const location = useLocation();
  return <div>{`login${location.search}`}</div>;
}

function renderRoutes(path: string, requireAdmin = false) {
  render(
    <MemoryRouter initialEntries={[path]}>
      <Routes>
        <Route path="/login" element={<LoginProbe />} />
        <Route path="/pending" element={<ProtectedRoute><div>pending-page</div></ProtectedRoute>} />
        <Route path="/change-password" element={<ProtectedRoute><div>change-password-page</div></ProtectedRoute>} />
        <Route path="/admin" element={<ProtectedRoute requireAdmin={requireAdmin}><div>admin-page</div></ProtectedRoute>} />
        <Route path="/writer" element={<ProtectedRoute requireEditor><div>writer-page</div></ProtectedRoute>} />
        <Route path="/projects/:id" element={<ProtectedRoute><div>project-page</div></ProtectedRoute>} />
        <Route path="/" element={<ProtectedRoute><div>home-page</div></ProtectedRoute>} />
      </Routes>
    </MemoryRouter>
  );
}

function authState(overrides?: {
  status?: 'loading' | 'anonymous' | 'authenticated';
  isAuthenticated?: boolean;
  role?: AppRole | null;
  mustChangePassword?: boolean;
}) {
  const role = overrides?.role ?? null;
  const status = overrides?.status ?? (role ? 'authenticated' : 'anonymous');
  const isAuthenticated = overrides?.isAuthenticated ?? Boolean(role);
  return {
    user: role ? {
      id: 'user-1',
      name: 'Test User',
      email: 'test@example.com',
      role,
      mustChangePassword: overrides?.mustChangePassword ?? false,
      lastLoginAt: null,
    } : null,
    role,
    status,
    isAuthenticated,
    login: vi.fn(),
    register: vi.fn(),
    changePassword: vi.fn(),
    refresh: vi.fn(),
    logout: vi.fn(),
  };
}

describe('ProtectedRoute', () => {
  it('redirects unauthenticated users to login with returnUrl', () => {
    mockedUseAuth.mockReturnValue(authState());

    renderRoutes('/projects/abc?tab=details');

    expect(screen.getByText('login?returnUrl=%2Fprojects%2Fabc%3Ftab%3Ddetails')).toBeInTheDocument();
  });

  it('redirects pending users to pending route', () => {
    mockedUseAuth.mockReturnValue(authState({ role: 'Pending' }));

    renderRoutes('/');

    expect(screen.getByText('pending-page')).toBeInTheDocument();
  });

  it('redirects must-change-password users to change-password route', () => {
    mockedUseAuth.mockReturnValue(authState({ role: 'Contributor', mustChangePassword: true }));

    renderRoutes('/');

    expect(screen.getByText('change-password-page')).toBeInTheDocument();
  });

  it('blocks non-pending users from pending route', () => {
    mockedUseAuth.mockReturnValue(authState({ role: 'Contributor' }));

    renderRoutes('/pending');

    expect(screen.getByText('home-page')).toBeInTheDocument();
  });

  it('blocks non-must-change users from change-password route', () => {
    mockedUseAuth.mockReturnValue(authState({ role: 'Contributor' }));

    renderRoutes('/change-password');

    expect(screen.getByText('home-page')).toBeInTheDocument();
  });

  it('requires admin role for admin-protected route', () => {
    mockedUseAuth.mockReturnValue(authState({ role: 'Contributor' }));

    renderRoutes('/admin', true);

    expect(screen.getByText('home-page')).toBeInTheDocument();
  });

  it('requires editor role for writer-protected route', () => {
    mockedUseAuth.mockReturnValue(authState({ role: 'Reader' }));

    renderRoutes('/writer');

    expect(screen.getByText('home-page')).toBeInTheDocument();
  });

  it('allows contributor role for writer-protected route', () => {
    mockedUseAuth.mockReturnValue(authState({ role: 'Contributor' }));

    renderRoutes('/writer');

    expect(screen.getByText('writer-page')).toBeInTheDocument();
  });
});
