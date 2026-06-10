import React from 'react';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import { useNotebookHeaderToolbar } from '../useNotebookHeaderToolbar';
import { api } from '../../services/api';
import { ToastProvider } from '../../components/common/Toast';
import type { NotebookHeaderToolbarDto } from '../../types/notebookToolbar';

vi.mock('../../services/api', () => ({
  api: {
    notebooks: {
      headerToolbar: vi.fn(),
    },
  },
}));

const baseToolbar: NotebookHeaderToolbarDto = {
  generatedUtc: '2024-01-01T00:00:00Z',
  chat: {
    status: 'ready',
    summary: 'Chat ready',
    conversationId: 'convo-1',
    selectedAssistantName: null,
    effectiveModelId: 'm1',
    effectiveModelDisplayName: 'Model',
    effectiveProvider: 'openai',
    overrideAllChatModels: false,
    supportsLocalRuntimePower: false,
    localRuntimeOn: false,
    modelOptions: [],
    blockers: [],
    inProgressOperationId: null,
    inProgressState: 'ready',
  },
  services: [],
};

const activeToolbar: NotebookHeaderToolbarDto = {
  ...baseToolbar,
  chat: {
    ...baseToolbar.chat,
    inProgressOperationId: 'op-1',
    inProgressState: 'loading',
  },
};

const wrapper = ({ children }: { children: React.ReactNode }) => (
  <ToastProvider>{children}</ToastProvider>
);

describe('useNotebookHeaderToolbar', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    (api.notebooks.headerToolbar as ReturnType<typeof vi.fn>).mockResolvedValue(baseToolbar);
    Object.defineProperty(document, 'visibilityState', {
      configurable: true,
      value: 'visible',
    });
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('loads toolbar data on mount', async () => {
    const { result } = renderHook(
      () => useNotebookHeaderToolbar('nb-1', 'convo-1'),
      { wrapper }
    );

    await act(async () => {
      await Promise.resolve();
    });

    expect(result.current.data).toEqual(baseToolbar);
    expect(api.notebooks.headerToolbar).toHaveBeenCalledWith('nb-1', 'convo-1');
    expect(result.current.isLoading).toBe(false);
  });

  it('clears state when disabled', async () => {
    const { result, rerender } = renderHook(
      ({ enabled }) => useNotebookHeaderToolbar('nb-1', null, enabled),
      { wrapper, initialProps: { enabled: true } }
    );

    await act(async () => {
      await Promise.resolve();
    });

    rerender({ enabled: false });

    expect(result.current.data).toBeNull();
    expect(result.current.isLoading).toBe(false);
  });

  it('polls when inFlight is true', async () => {
    vi.useFakeTimers();

    const { result } = renderHook(
      () => useNotebookHeaderToolbar('nb-1', null),
      { wrapper }
    );

    await act(async () => {
      await Promise.resolve();
    });

    act(() => {
      result.current.setInFlight(true);
    });

    const callsBefore = (api.notebooks.headerToolbar as ReturnType<typeof vi.fn>).mock.calls.length;

    await act(async () => {
      vi.advanceTimersByTime(2000);
      await Promise.resolve();
    });

    expect(
      (api.notebooks.headerToolbar as ReturnType<typeof vi.fn>).mock.calls.length
    ).toBeGreaterThan(callsBefore);
  });

  it('polls when toolbar reports active operation', async () => {
    vi.useFakeTimers();
    (api.notebooks.headerToolbar as ReturnType<typeof vi.fn>).mockResolvedValue(activeToolbar);

    renderHook(() => useNotebookHeaderToolbar('nb-1', null), { wrapper });

    await act(async () => {
      await Promise.resolve();
    });

    const callsBefore = (api.notebooks.headerToolbar as ReturnType<typeof vi.fn>).mock.calls.length;

    await act(async () => {
      vi.advanceTimersByTime(2000);
      await Promise.resolve();
    });

    expect(
      (api.notebooks.headerToolbar as ReturnType<typeof vi.fn>).mock.calls.length
    ).toBeGreaterThan(callsBefore);
  });

  it('continues polling briefly after inFlight clears', async () => {
    vi.useFakeTimers();

    const { result } = renderHook(
      () => useNotebookHeaderToolbar('nb-1', null),
      { wrapper }
    );

    await act(async () => {
      await Promise.resolve();
    });

    act(() => {
      result.current.setInFlight(true);
    });

    act(() => {
      result.current.setInFlight(false);
    });

    const callsBefore = (api.notebooks.headerToolbar as ReturnType<typeof vi.fn>).mock.calls.length;

    await act(async () => {
      vi.advanceTimersByTime(2000);
      await Promise.resolve();
    });

    expect(
      (api.notebooks.headerToolbar as ReturnType<typeof vi.fn>).mock.calls.length
    ).toBeGreaterThan(callsBefore);
  });

  it('refreshes on toolbar window event', async () => {
    renderHook(() => useNotebookHeaderToolbar('nb-1', null), { wrapper });

    await act(async () => {
      await Promise.resolve();
    });

    const callsBefore = (api.notebooks.headerToolbar as ReturnType<typeof vi.fn>).mock.calls.length;

    await act(async () => {
      window.dispatchEvent(new Event('refresh-notebook-toolbar'));
      await Promise.resolve();
    });

    expect(
      (api.notebooks.headerToolbar as ReturnType<typeof vi.fn>).mock.calls.length
    ).toBeGreaterThan(callsBefore);
  });

  it('handles load errors', async () => {
    (api.notebooks.headerToolbar as ReturnType<typeof vi.fn>).mockRejectedValue(
      new Error('Toolbar load failed')
    );

    const { result } = renderHook(
      () => useNotebookHeaderToolbar('nb-1', null),
      { wrapper }
    );

    await act(async () => {
      await Promise.resolve();
    });

    expect(result.current.error).toBe('Toolbar load failed');
  });
});
