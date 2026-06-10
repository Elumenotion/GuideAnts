import React from 'react';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import { useNotebookChatReadiness } from '../useNotebookChatReadiness';
import { api } from '../../services/api';
import { ToastProvider } from '../../components/common/Toast';
import type { NotebookChatReadinessDto } from '../../types/notebookToolbar';

vi.mock('../../services/api', () => ({
  api: {
    notebooks: {
      chatReadiness: vi.fn(),
    },
  },
}));

const readyData: NotebookChatReadinessDto = {
  effectiveModelId: 'model-1',
  effectiveModelDisplayName: 'Test Model',
  effectiveProvider: 'openai',
  blockers: [],
  supportsLocalRuntimePower: false,
  localRuntimeOn: false,
  inProgressOperationId: null,
  inProgressState: 'ready',
};

const activeData: NotebookChatReadinessDto = {
  ...readyData,
  inProgressOperationId: 'op-1',
  inProgressState: 'loading',
};

const wrapper = ({ children }: { children: React.ReactNode }) => (
  <ToastProvider>{children}</ToastProvider>
);

describe('useNotebookChatReadiness', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    (api.notebooks.chatReadiness as ReturnType<typeof vi.fn>).mockResolvedValue(readyData);
    Object.defineProperty(document, 'visibilityState', {
      configurable: true,
      value: 'visible',
    });
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('loads readiness on mount', async () => {
    const { result } = renderHook(
      () => useNotebookChatReadiness('nb-1', 'convo-1'),
      { wrapper }
    );

    await act(async () => {
      await Promise.resolve();
    });

    expect(result.current.data).toEqual(readyData);
    expect(api.notebooks.chatReadiness).toHaveBeenCalledWith('nb-1', 'convo-1');
    expect(result.current.isLoading).toBe(false);
  });

  it('clears state when disabled', async () => {
    const { result, rerender } = renderHook(
      ({ enabled }) => useNotebookChatReadiness('nb-1', null, enabled),
      { wrapper, initialProps: { enabled: true } }
    );

    await act(async () => {
      await Promise.resolve();
    });

    rerender({ enabled: false });

    expect(result.current.data).toBeNull();
    expect(result.current.isLoading).toBe(false);
    expect(result.current.error).toBeNull();
  });

  it('refreshes on custom window event', async () => {
    renderHook(() => useNotebookChatReadiness('nb-1', null), { wrapper });

    await act(async () => {
      await Promise.resolve();
    });

    const callsBefore = (api.notebooks.chatReadiness as ReturnType<typeof vi.fn>).mock.calls.length;

    await act(async () => {
      window.dispatchEvent(new Event('refresh-notebook-toolbar'));
      await Promise.resolve();
    });

    expect(
      (api.notebooks.chatReadiness as ReturnType<typeof vi.fn>).mock.calls.length
    ).toBeGreaterThan(callsBefore);
  });

  it('polls while an operation is in progress', async () => {
    vi.useFakeTimers();
    (api.notebooks.chatReadiness as ReturnType<typeof vi.fn>).mockResolvedValue(activeData);

    renderHook(() => useNotebookChatReadiness('nb-1', null), { wrapper });

    await act(async () => {
      await Promise.resolve();
    });

    const callsBefore = (api.notebooks.chatReadiness as ReturnType<typeof vi.fn>).mock.calls.length;

    await act(async () => {
      vi.advanceTimersByTime(2000);
      await Promise.resolve();
    });

    expect(
      (api.notebooks.chatReadiness as ReturnType<typeof vi.fn>).mock.calls.length
    ).toBeGreaterThan(callsBefore);
  });

  it('handles refresh errors with toast', async () => {
    (api.notebooks.chatReadiness as ReturnType<typeof vi.fn>).mockRejectedValue(
      new Error('Readiness failed')
    );

    const { result } = renderHook(
      () => useNotebookChatReadiness('nb-1', null),
      { wrapper }
    );

    await act(async () => {
      await Promise.resolve();
    });

    expect(result.current.error).toBe('Readiness failed');
    expect(result.current.isLoading).toBe(false);
  });

  it('manual refresh reloads data', async () => {
    const { result } = renderHook(
      () => useNotebookChatReadiness('nb-1', 'convo-2'),
      { wrapper }
    );

    await act(async () => {
      await Promise.resolve();
    });

    await act(async () => {
      await result.current.refresh();
    });

    expect(api.notebooks.chatReadiness).toHaveBeenCalledWith('nb-1', 'convo-2');
  });
});
