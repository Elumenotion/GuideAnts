import { useEffect } from 'react';
import { HashRouter, BrowserRouter } from 'react-router-dom';
import AppContent from './components/AppContent';
import ZoomControl from './components/ZoomControl';
import ErrorBoundary from './components/ErrorBoundary';
import StartupGate from './components/StartupGate';
import { ToastProvider } from './components/common/Toast';
import { useMermaidCleanup } from './hooks/useMermaidCleanup';
import { useAppPort } from './hooks/useAppPort';
import { DndProvider } from 'react-dnd';
import { HTML5Backend } from 'react-dnd-html5-backend';
import { getRouterType, isElectron, debugEnvironment } from './utils/environment';
import OAuthCallback from './pages/OAuthCallback';
import { UrlCorrector } from './components/common/UrlCorrector';
import { AuthProvider } from './contexts/AuthContext';
import AuthExpiredHandler from './components/AuthExpiredHandler';
import { GuideAntsGuideProvider } from './features/guideantsGuide/GuideAntsGuideProvider';

function App() {
  useAppPort();
  const routerType = getRouterType();

  useMermaidCleanup();

  useEffect(() => {
    if (import.meta.env.DEV) {
      debugEnvironment();
    }
  }, []);

  // Handle OAuth callback before router for HashRouter compatibility
  if (routerType === 'hash' && (window.location.pathname === '/oauth/callback' || window.location.pathname === '/redirect')) {
    return (
      <div className="h-full">
        <ErrorBoundary>
          <ToastProvider>
            <OAuthCallback />
          </ToastProvider>
        </ErrorBoundary>
      </div>
    );
  }

  const Router = routerType === 'hash' ? HashRouter : BrowserRouter;

  return (
    <div className="h-full">
      <ErrorBoundary>
        <ToastProvider>
          <DndProvider backend={HTML5Backend}>
            <StartupGate enabled={routerType === 'browser'}>
              <Router>
                <AuthProvider>
                  <GuideAntsGuideProvider>
                    <AuthExpiredHandler />
                    <UrlCorrector />
                    {isElectron() && <ZoomControl />}
                    <AppContent />
                  </GuideAntsGuideProvider>
                </AuthProvider>
              </Router>
            </StartupGate>
          </DndProvider>
        </ToastProvider>
      </ErrorBoundary>
    </div>
  );
}

export default App;
