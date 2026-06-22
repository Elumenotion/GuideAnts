import type { ReactNode } from 'react';
import { Navigate, useLocation } from 'react-router-dom';
import { useAuth } from '../contexts/AuthContext';
import LoadingSpinner from './LoadingSpinner';

export function ProtectedRoute({
  children,
  requireAdmin = false,
  requireEditor = false,
  adminFallbackPath = '/',
}: {
  children: ReactNode;
  requireAdmin?: boolean;
  requireEditor?: boolean;
  adminFallbackPath?: string;
}) {
  const location = useLocation();
  const { status, isAuthenticated, user, role } = useAuth();

  if (status === 'loading') {
    return <LoadingSpinner message="Checking authentication..." />;
  }

  if (!isAuthenticated || !user) {
    const returnUrl = `${location.pathname}${location.search}`;
    return <Navigate to={`/login?returnUrl=${encodeURIComponent(returnUrl)}`} replace />;
  }

  if (role === 'Pending' && location.pathname !== '/pending') {
    return <Navigate to="/pending" replace />;
  }

  if (user.mustChangePassword && location.pathname !== '/change-password') {
    return <Navigate to="/change-password" replace />;
  }

  if (location.pathname === '/pending' && role !== 'Pending') {
    return <Navigate to="/" replace />;
  }

  if (location.pathname === '/change-password' && !user.mustChangePassword) {
    return <Navigate to="/" replace />;
  }

  if (requireAdmin && role !== 'Admin') {
    return <Navigate to={adminFallbackPath} replace />;
  }

  if (requireEditor && role === 'Reader') {
    return <Navigate to="/" replace />;
  }

  return <>{children}</>;
}
