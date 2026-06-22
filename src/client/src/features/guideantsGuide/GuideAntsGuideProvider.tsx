import {
  createContext,
  useCallback,
  useContext,
  useMemo,
  useState,
  type ReactNode,
} from 'react';
import { useLocation } from 'react-router-dom';
import { useToast } from '../../components/common/Toast';
import { useAuth } from '../../contexts/AuthContext';
import { api } from '../../services/api';
import type { SystemGuideSessionDto } from '../../types/systemGuide';
import type { AppGuideContext } from './types';
import { GuideAntsGuideFlyout } from './GuideAntsGuideFlyout';

interface GuideAntsGuideContextValue {
  isOpen: boolean;
  open: () => void;
  close: () => void;
  toggle: () => void;
  session: SystemGuideSessionDto | null;
  sessionLoading: boolean;
  buildAppContext: () => AppGuideContext;
}

const GuideAntsGuideContext = createContext<GuideAntsGuideContextValue | undefined>(undefined);

export function useGuideAntsGuide(): GuideAntsGuideContextValue {
  const context = useContext(GuideAntsGuideContext);
  if (!context) {
    throw new Error('useGuideAntsGuide must be used within GuideAntsGuideProvider');
  }
  return context;
}

export function GuideAntsGuideProvider({ children }: { children: ReactNode }) {
  const { user, role, status } = useAuth();
  const location = useLocation();
  const { showToast } = useToast();
  const [isOpen, setIsOpen] = useState(false);
  const [session, setSession] = useState<SystemGuideSessionDto | null>(null);
  const [sessionLoading, setSessionLoading] = useState(false);

  const buildAppContext = useCallback((): AppGuideContext => {
    if (!user || !role) {
      throw new Error('Guide context requires an authenticated user');
    }
    return {
      route: location.pathname,
      role,
      userId: user.id,
      displayName: user.name,
    };
  }, [location.pathname, role, user]);

  const close = useCallback(() => {
    setIsOpen(false);
  }, []);

  const fetchSession = useCallback(async (): Promise<SystemGuideSessionDto | null> => {
    setSessionLoading(true);
    try {
      const nextSession = await api.systemGuide.getSession();
      setSession(nextSession);
      return nextSession;
    } catch (error) {
      setSession(null);
      const message = error instanceof Error ? error.message : 'Failed to load GuideAnts Guide session';
      showToast({ type: 'error', title: 'Guide unavailable', message });
      return null;
    } finally {
      setSessionLoading(false);
    }
  }, [showToast]);

  const open = useCallback(async () => {
    if (status !== 'authenticated' || role === 'Pending' || !user) {
      return;
    }
    setIsOpen(true);
    await fetchSession();
  }, [fetchSession, role, status, user]);

  const toggle = useCallback(async () => {
    if (isOpen) {
      close();
      return;
    }
    await open();
  }, [close, isOpen, open]);

  const value = useMemo(
    () => ({
      isOpen,
      open,
      close,
      toggle,
      session,
      sessionLoading,
      buildAppContext,
    }),
    [buildAppContext, close, isOpen, open, session, sessionLoading, toggle],
  );

  return (
    <GuideAntsGuideContext.Provider value={value}>
      {children}
      <GuideAntsGuideFlyout />
    </GuideAntsGuideContext.Provider>
  );
}
