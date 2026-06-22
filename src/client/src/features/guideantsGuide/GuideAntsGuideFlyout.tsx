import {
  useCallback,
  useEffect,
  useRef,
  useState,
} from 'react';
import type { GuideantsChatElement } from 'guideants';
import { useLocation } from 'react-router-dom';
import { API_BASE_URL, getApiOrigin } from '../../config/apiConfig';
import { registerGuideAntsAppBridge } from './guideantsAppBridge';
import { useGuideAntsGuide } from './GuideAntsGuideProvider';
import { loadGuideants } from './loadGuideants';

function resolveGuideAntsApiBaseUrl(): string {
  const trimmed = API_BASE_URL.replace(/\/api$/, '');
  return trimmed || window.location.origin;
}

export function GuideAntsGuideFlyout() {
  const { isOpen, close, session, sessionLoading, buildAppContext } = useGuideAntsGuide();
  const location = useLocation();
  const panelRef = useRef<HTMLDivElement>(null);
  const chatHostRef = useRef<HTMLDivElement>(null);
  const chatRef = useRef<GuideantsChatElement | null>(null);
  const [guideantsReady, setGuideantsReady] = useState(false);
  const [bridgeRegistered, setBridgeRegistered] = useState(false);

  const publishedGuideId = session?.publishedGuideId ?? null;
  const canRenderChat = Boolean(publishedGuideId) && guideantsReady && !sessionLoading;

  useEffect(() => {
    if (!isOpen) {
      return;
    }
    let cancelled = false;
    void loadGuideants().then(() => {
      if (!cancelled) {
        setGuideantsReady(true);
      }
    });
    return () => {
      cancelled = true;
    };
  }, [isOpen]);

  useEffect(() => {
    if (!isOpen) {
      setGuideantsReady(false);
      setBridgeRegistered(false);
      chatRef.current = null;
      if (chatHostRef.current) {
        chatHostRef.current.replaceChildren();
      }
    }
  }, [isOpen]);

  const wireChatElement = useCallback(
    (chat: GuideantsChatElement) => {
      if (bridgeRegistered) {
        return;
      }
      chat.setContextProvider(() => JSON.stringify(buildAppContext()));
      registerGuideAntsAppBridge(chat, buildAppContext, session?.isAdminGuide ?? false);
      setBridgeRegistered(true);
    },
    [bridgeRegistered, buildAppContext, session?.isAdminGuide],
  );

  useEffect(() => {
    if (!canRenderChat || !chatHostRef.current || bridgeRegistered) {
      return;
    }

    const element = document.createElement('guideants-chat') as GuideantsChatElement;
    element.className = 'theme-guideants h-full min-h-0';
    element.setAttribute('pub-id', publishedGuideId!);
    element.setAttribute('api-base-url', resolveGuideAntsApiBaseUrl());
    element.setAttribute('command-mode', session?.commandMode ? 'true' : 'false');
    element.setAttribute('speech-to-text-enabled', 'true');

    chatRef.current = element;
    chatHostRef.current.replaceChildren(element);

    if ('registerTool' in element && typeof element.registerTool === 'function') {
      wireChatElement(element);
      return;
    }

    const onReady = () => {
      wireChatElement(element);
    };
    element.addEventListener('wf-stream-start', onReady, { once: true });
    const readyTimer = window.setTimeout(onReady, 0);
    return () => {
      window.clearTimeout(readyTimer);
      element.removeEventListener('wf-stream-start', onReady);
    };
  }, [bridgeRegistered, canRenderChat, publishedGuideId, session?.commandMode, wireChatElement]);

  useEffect(() => {
    if (!chatRef.current) {
      return;
    }

    chatRef.current.setAttribute('command-mode', session?.commandMode ? 'true' : 'false');
  }, [session?.commandMode]);

  useEffect(() => {
    if (!isOpen || !bridgeRegistered || !chatRef.current) {
      return;
    }
    chatRef.current.setContextProvider(() => JSON.stringify(buildAppContext()));
  }, [bridgeRegistered, buildAppContext, isOpen, location.pathname]);

  useEffect(() => {
    if (!isOpen) {
      return;
    }
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        close();
      }
    };
    document.addEventListener('keydown', onKeyDown);
    return () => document.removeEventListener('keydown', onKeyDown);
  }, [close, isOpen]);

  useEffect(() => {
    if (!isOpen) {
      return;
    }
    const onPointerDown = (event: MouseEvent) => {
      const target = event.target as Node;
      if (panelRef.current?.contains(target)) {
        return;
      }
      if ((target as HTMLElement | null)?.closest?.('[data-guideants-guide-trigger="true"]')) {
        return;
      }
      close();
    };
    document.addEventListener('mousedown', onPointerDown);
    return () => document.removeEventListener('mousedown', onPointerDown);
  }, [close, isOpen]);

  if (!isOpen) {
    return null;
  }

  return (
    <>
      <div className="fixed inset-0 z-40 bg-black/20" aria-hidden="true" />
      <div
        ref={panelRef}
        role="dialog"
        aria-modal="true"
        aria-label="GuideAnts Guide"
        className="fixed inset-y-0 right-0 z-50 flex w-full max-w-[400px] flex-col border-l border-gray-200 bg-white shadow-xl"
        data-testid="guideants-guide-flyout"
      >
        <div className="flex items-center justify-between border-b border-gray-200 px-4 py-3">
          <div className="flex min-w-0 items-center gap-2">
            <h2 className="truncate text-base font-semibold text-gray-900">
              {session?.guideName ?? 'GuideAnts Guide'}
            </h2>
            {session?.isAdminGuide ? (
              <span className="rounded bg-amber-100 px-1.5 py-0.5 text-xs font-medium text-amber-800">Admin</span>
            ) : null}
          </div>
          <button
            type="button"
            onClick={close}
            aria-label="Close GuideAnts Guide"
            className="h-8 w-8 rounded-md border border-gray-300 text-gray-600 hover:bg-gray-50"
          >
            ×
          </button>
        </div>

        <div className="flex min-h-0 flex-1 flex-col">
          {sessionLoading ? (
            <div className="flex flex-1 items-center justify-center p-6">
              <div
                className="h-8 w-8 animate-spin rounded-full border-2 border-blue-600 border-t-transparent"
                role="status"
                aria-label="Loading guide session"
              />
            </div>
          ) : null}

          {!sessionLoading && !publishedGuideId ? (
            <div className="p-4 text-sm text-gray-600">Guide session is unavailable.</div>
          ) : null}

          {canRenderChat ? (
            <div ref={chatHostRef} className="min-h-0 flex-1 overflow-hidden p-2" data-api-origin={getApiOrigin()} />
          ) : null}
        </div>
      </div>
    </>
  );
}
