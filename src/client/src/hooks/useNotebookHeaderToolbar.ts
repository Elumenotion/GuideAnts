import { useCallback, useEffect, useRef, useState } from 'react';
import { api } from '../services/api';
import type { NotebookHeaderToolbarDto } from '../types/notebookToolbar';
import { useToast } from '../components/common/Toast';

const POLL_ACTIVE_MS = 2_000;
const POLL_ACTIVE_COOLDOWN_MS = 15_000;

export interface UseNotebookHeaderToolbarResult {
  data: NotebookHeaderToolbarDto | null;
  isLoading: boolean;
  error: string | null;
  refresh: () => Promise<void>;
  inFlight: boolean;
  setInFlight: (v: boolean) => void;
}

export function useNotebookHeaderToolbar(
  notebookId: string | undefined,
  conversationId: string | null
): UseNotebookHeaderToolbarResult {
  const { showToast } = useToast();
  const [data, setData] = useState<NotebookHeaderToolbarDto | null>(null);
  const [isLoading, setIsLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [inFlight, setInFlight] = useState(false);
  const pollTimer = useRef<ReturnType<typeof setInterval> | null>(null);
  const visible = useRef(true);
  const inFlightCooldownUntilMs = useRef(0);
  const previousInFlight = useRef(false);

  const hasActiveOperation = (dto: NotebookHeaderToolbarDto | null): boolean => {
    if (!dto) return false;

    if (dto.chat.inProgressOperationId && dto.chat.inProgressState && dto.chat.inProgressState !== 'ready' && dto.chat.inProgressState !== 'failed') {
      return true;
    }

    return dto.services.some(service =>
      !!service.inProgressOperationId &&
      !!service.inProgressState &&
      service.inProgressState !== 'ready' &&
      service.inProgressState !== 'failed'
    );
  };

  const refresh = useCallback(async () => {
    if (!notebookId) return;
    setError(null);
    setIsLoading(true);
    try {
      const dto = await api.notebooks.headerToolbar(
        notebookId,
        conversationId || undefined
      );
      setData(dto);
    } catch (e: any) {
      setError(e?.message || 'Failed to load toolbar');
      showToast({ type: 'error', title: 'Toolbar', message: e?.message || 'Load failed' });
    } finally {
      setIsLoading(false);
    }
  }, [notebookId, conversationId, showToast]);

  useEffect(() => {
    void refresh();
  }, [refresh]);

  useEffect(() => {
    const onToolbarRefresh = () => {
      void refresh();
    };
    window.addEventListener('refresh-notebook-toolbar', onToolbarRefresh);
    return () => window.removeEventListener('refresh-notebook-toolbar', onToolbarRefresh);
  }, [refresh]);

  useEffect(() => {
    const onVis = () => {
      visible.current = document.visibilityState === 'visible';
    };
    document.addEventListener('visibilitychange', onVis);
    return () => document.removeEventListener('visibilitychange', onVis);
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
    if (!notebookId) return undefined;

    const inCooldown = Date.now() < inFlightCooldownUntilMs.current;
    const shouldPoll = inFlight || inCooldown || hasActiveOperation(data);
    if (!shouldPoll) {
      return undefined;
    }

    pollTimer.current = setInterval(() => {
      if (!visible.current) return;
      void (async () => {
        try {
          const dto = await api.notebooks.headerToolbar(
            notebookId,
            conversationId || undefined
          );
          setData(dto);
        } catch {
          // keep last good snapshot
        }
      })();
    }, POLL_ACTIVE_MS);
    return () => {
      if (pollTimer.current) clearInterval(pollTimer.current);
    };
  }, [notebookId, conversationId, inFlight, data]);

  return {
    data,
    isLoading,
    error,
    refresh,
    inFlight,
    setInFlight,
  };
}
