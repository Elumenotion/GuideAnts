import { useCallback, useEffect, useRef, useState } from 'react';
import { useToast } from '../components/common/Toast';
import { api } from '../services/api';
import type {
  NotebookChatReadinessDto,
  NotebookHeaderToolbarDto,
} from '../types/notebookToolbar';

const POLL_ACTIVE_MS = 2_000;
const POLL_ACTIVE_COOLDOWN_MS = 15_000;

export interface UseNotebookWorkspaceControlsOptions {
  notebookId: string | undefined;
  conversationId: string | null;
  /** Loads the admin header-toolbar payload in addition to chat readiness. */
  includeToolbar?: boolean;
  enabled?: boolean;
}

export interface UseNotebookWorkspaceControlsResult {
  /** Canonical chat readiness for gating, warnings, and dialogs. */
  chat: NotebookChatReadinessDto | null;
  chatIsLoading: boolean;
  chatError: string | null;
  /** Admin toolbar payload; null when includeToolbar is false. */
  toolbar: NotebookHeaderToolbarDto | null;
  toolbarIsLoading: boolean;
  toolbarError: string | null;
  refresh: () => Promise<void>;
  inFlight: boolean;
  setInFlight: (v: boolean) => void;
}

function hasActiveChatOperation(chat: NotebookChatReadinessDto | null): boolean {
  if (!chat?.inProgressOperationId || !chat.inProgressState) {
    return false;
  }
  return chat.inProgressState !== 'ready' && chat.inProgressState !== 'failed';
}

function hasActiveToolbarOperation(toolbar: NotebookHeaderToolbarDto | null): boolean {
  if (!toolbar) {
    return false;
  }

  if (
    toolbar.chat.inProgressOperationId &&
    toolbar.chat.inProgressState &&
    toolbar.chat.inProgressState !== 'ready' &&
    toolbar.chat.inProgressState !== 'failed'
  ) {
    return true;
  }

  return toolbar.services.some(
    (service) =>
      !!service.inProgressOperationId &&
      !!service.inProgressState &&
      service.inProgressState !== 'ready' &&
      service.inProgressState !== 'failed'
  );
}

export function useNotebookWorkspaceControls({
  notebookId,
  conversationId,
  includeToolbar = false,
  enabled = true,
}: UseNotebookWorkspaceControlsOptions): UseNotebookWorkspaceControlsResult {
  const { showToast } = useToast();
  const [chat, setChat] = useState<NotebookChatReadinessDto | null>(null);
  const [chatIsLoading, setChatIsLoading] = useState(Boolean(enabled && notebookId));
  const [chatError, setChatError] = useState<string | null>(null);
  const [toolbar, setToolbar] = useState<NotebookHeaderToolbarDto | null>(null);
  const [toolbarIsLoading, setToolbarIsLoading] = useState(
    Boolean(enabled && notebookId && includeToolbar)
  );
  const [toolbarError, setToolbarError] = useState<string | null>(null);
  const [inFlight, setInFlight] = useState(false);
  const pollTimer = useRef<ReturnType<typeof setInterval> | null>(null);
  const visible = useRef(true);
  const inFlightCooldownUntilMs = useRef(0);
  const previousInFlight = useRef(false);

  const fetchChat = useCallback(
    async (options?: { showLoading?: boolean }) => {
      if (!enabled || !notebookId) {
        return;
      }

      if (options?.showLoading !== false) {
        setChatIsLoading(true);
      }
      setChatError(null);
      try {
        const readiness = await api.notebooks.chatReadiness(
          notebookId,
          conversationId || undefined
        );
        setChat(readiness);
      } catch (error: any) {
        const message = error?.message || 'Failed to load chat readiness';
        setChatError(message);
        if (options?.showLoading !== false) {
          showToast({
            type: 'error',
            title: 'Chat readiness',
            message,
          });
        }
      } finally {
        if (options?.showLoading !== false) {
          setChatIsLoading(false);
        }
      }
    },
    [enabled, notebookId, conversationId, showToast]
  );

  const fetchToolbar = useCallback(
    async (options?: { showLoading?: boolean }) => {
      if (!enabled || !notebookId || !includeToolbar) {
        return;
      }

      if (options?.showLoading !== false) {
        setToolbarIsLoading(true);
      }
      setToolbarError(null);
      try {
        const dto = await api.notebooks.headerToolbar(
          notebookId,
          conversationId || undefined
        );
        setToolbar(dto);
      } catch (error: any) {
        const message = error?.message || 'Failed to load toolbar';
        setToolbarError(message);
        if (options?.showLoading !== false) {
          showToast({
            type: 'error',
            title: 'Toolbar',
            message,
          });
        }
      } finally {
        if (options?.showLoading !== false) {
          setToolbarIsLoading(false);
        }
      }
    },
    [enabled, notebookId, conversationId, includeToolbar, showToast]
  );

  const refresh = useCallback(async () => {
    await Promise.all([fetchChat(), fetchToolbar()]);
  }, [fetchChat, fetchToolbar]);

  useEffect(() => {
    if (!enabled) {
      setChat(null);
      setChatError(null);
      setChatIsLoading(false);
      setToolbar(null);
      setToolbarError(null);
      setToolbarIsLoading(false);
      return;
    }
    void refresh();
  }, [enabled, refresh]);

  useEffect(() => {
    if (!enabled) {
      return;
    }
    const onToolbarRefresh = () => {
      void refresh();
    };
    window.addEventListener('refresh-notebook-toolbar', onToolbarRefresh);
    return () => window.removeEventListener('refresh-notebook-toolbar', onToolbarRefresh);
  }, [enabled, refresh]);

  useEffect(() => {
    const onVisibilityChange = () => {
      visible.current = document.visibilityState === 'visible';
    };
    document.addEventListener('visibilitychange', onVisibilityChange);
    return () => document.removeEventListener('visibilitychange', onVisibilityChange);
  }, []);

  useEffect(() => {
    if (previousInFlight.current && !inFlight) {
      inFlightCooldownUntilMs.current = Date.now() + POLL_ACTIVE_COOLDOWN_MS;
    }
    previousInFlight.current = inFlight;
  }, [inFlight]);

  useEffect(() => {
    if (pollTimer.current) {
      clearInterval(pollTimer.current);
      pollTimer.current = null;
    }

    if (!enabled || !notebookId) {
      return undefined;
    }

    const chatNeedsPoll = hasActiveChatOperation(chat);
    const inCooldown = Date.now() < inFlightCooldownUntilMs.current;
    const toolbarNeedsPoll =
      includeToolbar && (inFlight || inCooldown || hasActiveToolbarOperation(toolbar));

    if (!chatNeedsPoll && !toolbarNeedsPoll) {
      return undefined;
    }

    pollTimer.current = setInterval(() => {
      if (!visible.current) {
        return;
      }
      if (chatNeedsPoll) {
        void fetchChat({ showLoading: false });
      }
      if (toolbarNeedsPoll) {
        void fetchToolbar({ showLoading: false });
      }
    }, POLL_ACTIVE_MS);

    return () => {
      if (pollTimer.current) {
        clearInterval(pollTimer.current);
      }
    };
  }, [enabled, notebookId, includeToolbar, inFlight, chat, toolbar, fetchChat, fetchToolbar]);

  return {
    chat,
    chatIsLoading,
    chatError,
    toolbar,
    toolbarIsLoading,
    toolbarError,
    refresh,
    inFlight,
    setInFlight,
  };
}
