import { useCallback, useEffect, useRef, useState } from 'react';
import { useToast } from '../components/common/Toast';
import { api } from '../services/api';
import type { NotebookChatReadinessDto } from '../types/notebookToolbar';

const POLL_ACTIVE_MS = 2_000;

export interface UseNotebookChatReadinessResult {
  data: NotebookChatReadinessDto | null;
  isLoading: boolean;
  error: string | null;
  refresh: () => Promise<void>;
}

function hasActiveOperation(readiness: NotebookChatReadinessDto | null): boolean {
  if (!readiness?.inProgressOperationId || !readiness.inProgressState) {
    return false;
  }
  return readiness.inProgressState !== 'ready' && readiness.inProgressState !== 'failed';
}

export function useNotebookChatReadiness(
  notebookId: string | undefined,
  conversationId: string | null,
  enabled = true
): UseNotebookChatReadinessResult {
  const { showToast } = useToast();
  const [data, setData] = useState<NotebookChatReadinessDto | null>(null);
  const [isLoading, setIsLoading] = useState(Boolean(enabled));
  const [error, setError] = useState<string | null>(null);
  const pollTimer = useRef<ReturnType<typeof setInterval> | null>(null);
  const visible = useRef(true);

  const refresh = useCallback(async () => {
    if (!enabled || !notebookId) {
      return;
    }

    setError(null);
    setIsLoading(true);
    try {
      const readiness = await api.notebooks.chatReadiness(notebookId, conversationId || undefined);
      setData(readiness);
    } catch (readinessError: any) {
      setError(readinessError?.message || 'Failed to load chat readiness');
      showToast({
        type: 'error',
        title: 'Chat readiness',
        message: readinessError?.message || 'Load failed',
      });
    } finally {
      setIsLoading(false);
    }
  }, [enabled, notebookId, conversationId, showToast]);

  useEffect(() => {
    if (!enabled) {
      setData(null);
      setError(null);
      setIsLoading(false);
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
    if (pollTimer.current) {
      clearInterval(pollTimer.current);
      pollTimer.current = null;
    }

    if (!enabled || !notebookId || !hasActiveOperation(data)) {
      return undefined;
    }

    pollTimer.current = setInterval(() => {
      if (!visible.current) {
        return;
      }
      void (async () => {
        try {
          const readiness = await api.notebooks.chatReadiness(notebookId, conversationId || undefined);
          setData(readiness);
        } catch {
          // Keep last good readiness snapshot.
        }
      })();
    }, POLL_ACTIVE_MS);

    return () => {
      if (pollTimer.current) {
        clearInterval(pollTimer.current);
      }
    };
  }, [enabled, notebookId, conversationId, data]);

  return {
    data,
    isLoading,
    error,
    refresh,
  };
}
