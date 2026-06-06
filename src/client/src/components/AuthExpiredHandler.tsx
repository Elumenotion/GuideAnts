import { useEffect } from 'react';
import { useLocation, useNavigate } from 'react-router-dom';
import { useToast } from './common/Toast';
import { useAuth } from '../contexts/AuthContext';
import { AUTH_EXPIRED_EVENT, type AuthExpiredDetail } from '../services/authEvents';

function isPublicPath(pathname: string): boolean {
  return pathname === '/login'
    || pathname === '/register'
    || pathname === '/terms'
    || pathname === '/privacy'
    || pathname === '/oauth/callback'
    || pathname === '/redirect'
    || pathname.startsWith('/public/');
}

export default function AuthExpiredHandler() {
  const navigate = useNavigate();
  const location = useLocation();
  const { showToast } = useToast();
  const { logout } = useAuth();

  useEffect(() => {
    const listener = (event: Event) => {
      if (isPublicPath(location.pathname)) {
        return;
      }

      logout();
      const detail = (event as CustomEvent<AuthExpiredDetail>).detail;
      const message = detail?.reason || 'Your session expired. Please sign in again.';
      showToast({
        type: 'error',
        title: 'Authentication required',
        message,
      });

      const returnUrl = `${location.pathname}${location.search}`;
      navigate(`/login?returnUrl=${encodeURIComponent(returnUrl)}`, { replace: true });
    };

    window.addEventListener(AUTH_EXPIRED_EVENT, listener);
    return () => {
      window.removeEventListener(AUTH_EXPIRED_EVENT, listener);
    };
  }, [location.pathname, location.search, logout, navigate, showToast]);

  return null;
}
