import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { renderHook, waitFor } from '@testing-library/react';
import {
  createLocalModelOnboardingPoller,
  useLocalModelOnboardingOperation,
} from '../useOperationPolling';
import { api } from '../../../services/api';

vi.mock('../../../services/api', () => ({
  api: {
    settings: {
      getDownloadStatus: vi.fn(),
      getLlamaOperationStatus: vi.fn(),
    },
  },
}));

describe('useOperationPolling', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.clearAllTimers();
    vi.useRealTimers();
  });

  describe('useCuratedOperationPolling', () => {
    it('polls canonical operations route and never downloads', async () => {
      vi.useRealTimers();
      (api.settings.getLlamaOperationStatus as ReturnType<typeof vi.fn>).mockResolvedValue({
        operationId: 'op-curated',
        status: 'queued',
        stage: 'queued',
        routerModelId: 'Qwen3.6-35B-A3B-MTP-GGUF',
      });
      const onUpdate = vi.fn();

      const { useCuratedOperationPolling } = await import('../useOperationPolling');
      const { unmount } = renderHook(() =>
        useCuratedOperationPolling({
          operationId: 'op-curated',
          enabled: true,
          onUpdate,
          intervalMs: 10,
        })
      );

      await waitFor(() => {
        expect(onUpdate).toHaveBeenCalled();
      }, { timeout: 3000 });
      expect(api.settings.getLlamaOperationStatus).toHaveBeenCalledWith('op-curated');
      expect(api.settings.getDownloadStatus).not.toHaveBeenCalled();
      unmount();
      vi.useFakeTimers();
    });
  });

  describe('createLocalModelOnboardingPoller', () => {
    it('polls download status and forwards updates', async () => {
      (api.settings.getDownloadStatus as ReturnType<typeof vi.fn>).mockResolvedValue({
        operationId: 'op-1',
        status: 'downloading',
      });
      const onUpdate = vi.fn();

      const timerId = createLocalModelOnboardingPoller({
        operationId: 'op-1',
        onUpdate,
        intervalMs: 1000,
      });

      await vi.advanceTimersByTimeAsync(1000);

      expect(api.settings.getDownloadStatus).toHaveBeenCalledWith('op-1');
      expect(onUpdate).toHaveBeenCalledWith({ operationId: 'op-1', status: 'downloading' });
      window.clearInterval(timerId);
    });

    it('calls onTerminal when status is completed', async () => {
      (api.settings.getDownloadStatus as ReturnType<typeof vi.fn>).mockResolvedValue({
        operationId: 'op-2',
        status: 'completed',
      });
      const onTerminal = vi.fn();

      const timerId = createLocalModelOnboardingPoller({
        operationId: 'op-2',
        onUpdate: vi.fn(),
        onTerminal,
        intervalMs: 500,
      });

      await vi.advanceTimersByTimeAsync(500);

      expect(onTerminal).toHaveBeenCalled();
      window.clearInterval(timerId);
    });

    it('stops polling after a terminal status so onTerminal fires once', async () => {
      (api.settings.getDownloadStatus as ReturnType<typeof vi.fn>).mockResolvedValue({
        operationId: 'op-term',
        status: 'completed',
      });
      const onUpdate = vi.fn();
      const onTerminal = vi.fn();

      const timerId = createLocalModelOnboardingPoller({
        operationId: 'op-term',
        onUpdate,
        onTerminal,
        intervalMs: 100,
      });

      await vi.advanceTimersByTimeAsync(500);

      expect(onTerminal).toHaveBeenCalledTimes(1);
      expect(onUpdate).toHaveBeenCalledTimes(1);
      window.clearInterval(timerId);
    });

    it('calls onPollFailureThreshold after repeated failures', async () => {
      (api.settings.getDownloadStatus as ReturnType<typeof vi.fn>).mockRejectedValue(new Error('offline'));
      const onPollFailureThreshold = vi.fn();

      const timerId = createLocalModelOnboardingPoller({
        operationId: 'op-3',
        onUpdate: vi.fn(),
        onPollFailureThreshold,
        intervalMs: 100,
        failureThreshold: 2,
      });

      await vi.advanceTimersByTimeAsync(200);

      expect(onPollFailureThreshold).toHaveBeenCalledTimes(1);
      window.clearInterval(timerId);
    });
  });

  describe('useLocalModelOnboardingOperation', () => {
    it('does not poll when disabled or operationId is null', () => {
      renderHook(() =>
        useLocalModelOnboardingOperation({
          operationId: null,
          onUpdate: vi.fn(),
        })
      );

      vi.advanceTimersByTime(5000);
      expect(api.settings.getDownloadStatus).not.toHaveBeenCalled();
    });

    it('starts polling when enabled with an operation id', async () => {
      vi.useRealTimers();
      (api.settings.getDownloadStatus as ReturnType<typeof vi.fn>).mockResolvedValue({
        operationId: 'op-hook',
        status: 'queued',
      });
      const onUpdate = vi.fn();

      const { unmount } = renderHook(() =>
        useLocalModelOnboardingOperation({
          operationId: 'op-hook',
          enabled: true,
          onUpdate,
          intervalMs: 50,
        })
      );

      await waitFor(() => {
        expect(onUpdate).toHaveBeenCalledWith({ operationId: 'op-hook', status: 'queued' });
      });

      unmount();
      expect(api.settings.getDownloadStatus).toHaveBeenCalled();
      vi.useFakeTimers();
    });
  });
});
