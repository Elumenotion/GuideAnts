import {
  createContext,
  useCallback,
  useContext,
  useMemo,
  useRef,
  useState,
  type ReactNode,
} from 'react';
import { matchPath, useLocation, useNavigate } from 'react-router-dom';
import { useToast } from '../../components/common/Toast';
import { useAuth } from '../../contexts/AuthContext';
import { api } from '../../services/api';
import type { SystemGuideSessionDto } from '../../types/systemGuide';
import type { AppGuideContext, GuideAppActions, GuideViewContext } from './types';
import { getGuideViewContext } from './viewContext';
import { GuideAntsGuideFlyout } from './GuideAntsGuideFlyout';

interface GuideAntsGuideContextValue {
  isOpen: boolean;
  open: () => void;
  close: () => void;
  toggle: () => void;
  session: SystemGuideSessionDto | null;
  sessionLoading: boolean;
  buildAppContext: () => GuideViewContext;
  appActions: GuideAppActions;
}

const GuideAntsGuideContext = createContext<GuideAntsGuideContextValue | undefined>(undefined);

function buildRouteContext(pathname: string): Pick<AppGuideContext, 'projectId' | 'notebookId' | 'guideId'> {
  const notebookMatch = matchPath(
    { path: '/projects/:projectId/notebooks/:notebookId/*', end: false },
    pathname,
  );
  if (notebookMatch?.params.projectId && notebookMatch.params.notebookId) {
    return {
      projectId: notebookMatch.params.projectId,
      notebookId: notebookMatch.params.notebookId,
    };
  }

  const guideMatch = matchPath(
    { path: '/projects/:projectId/guides/guide/:guideId/*', end: false },
    pathname,
  );
  if (guideMatch?.params.projectId) {
    const guideId = guideMatch.params.guideId?.toLowerCase() === 'new'
      ? undefined
      : guideMatch.params.guideId;
    return {
      projectId: guideMatch.params.projectId,
      guideId,
    };
  }

  return {};
}

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
  const navigate = useNavigate();
  const { showToast } = useToast();
  const [isOpen, setIsOpen] = useState(false);
  const [session, setSession] = useState<SystemGuideSessionDto | null>(null);
  const [sessionLoading, setSessionLoading] = useState(false);

  // Keep a live ref to navigate so the stable appActions object (captured once
  // by the bridge at registration time) always calls the current navigator.
  const navigateRef = useRef(navigate);
  navigateRef.current = navigate;

  const appActions = useMemo<GuideAppActions>(
    () => ({
      navigate: (path: string) => navigateRef.current(path),
      goBack: () => navigateRef.current(-1),
    }),
    [],
  );

  const buildAppContext = useCallback((): GuideViewContext => {
    if (!user || !role) {
      throw new Error('Guide context requires an authenticated user');
    }
    const routeContext = buildRouteContext(location.pathname);
    const base: GuideViewContext = {
      route: location.pathname,
      role,
      userId: user.id,
      displayName: user.name,
      projectId: routeContext.projectId,
      notebookId: routeContext.notebookId,
      guideId: routeContext.guideId,
    };

    // Merge the page-published slice only while its publishing route is active.
    // Route-derived ids always win so a stale slice can never misreport scope.
    const patch = getGuideViewContext();
    if (!patch || patch.route !== location.pathname) {
      return base;
    }

    return {
      ...base,
      screen: patch.screen,
      projectTitle: patch.projectTitle,
      notebookTitle: patch.notebookTitle,
      guideName: patch.guideName,
      selectedItem: patch.selectedItem,
      activeConversationId: patch.activeConversationId,
      activeConversationTitle: patch.activeConversationTitle,
      settingsTab: patch.settingsTab,
      itemCounts: patch.itemCounts,
      projectId: base.projectId ?? patch.projectId,
      notebookId: base.notebookId ?? patch.notebookId,
      guideId: base.guideId ?? patch.guideId,
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
      appActions,
    }),
    [appActions, buildAppContext, close, isOpen, open, session, sessionLoading, toggle],
  );

  return (
    <GuideAntsGuideContext.Provider value={value}>
      {children}
      <GuideAntsGuideFlyout />
    </GuideAntsGuideContext.Provider>
  );
}
