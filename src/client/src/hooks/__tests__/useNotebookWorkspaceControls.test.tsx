import React from 'react';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import { useNotebookWorkspaceControls } from '../useNotebookWorkspaceControls';
import { api } from '../../services/api';
import { ToastProvider } from '../../components/common/Toast';
import type {
  NotebookChatReadinessDto,
  NotebookHeaderToolbarDto,
} from '../../types/notebookToolbar';

vi.mock('../../services/api', () => ({
  api: {
    notebooks: {
      chatReadiness: vi.fn(),
      headerToolbar: vi.fn(),
    },
  },
}));

const readyChat: NotebookChatReadinessDto = {
  effectiveModelId: 'model-1',
  effectiveModelDisplayName: 'Test Model',
  effectiveProvider: 'openai',
  blockers: [],
  supportsLocalRuntimePower: false,
  localRuntimeOn: false,
  inProgressOperationId: null,
  inProgressState: 'ready',
};

const activeChat: NotebookChatReadinessDto = {
  ...readyChat,
  inProgressOperationId: 'op-1',
  inProgressState: 'loading',
};

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
  services: [
    {
      serviceId: 'image',
      displayName: 'Image',
      kind: 'image',
      status: 'inProgress',
      summary: 'Loading',
      activeProviderId: 'p1',
      activeProviderLabel: 'Provider',
      supportsLocalRuntimePower: false,
      localRuntimeOn: false,
      providerOptions: [],
      selection: null,
      blockers: [],
      localModelOptions: [],
      inProgressOperationId: 'svc-op-1',
      inProgressState: 'loading',
    },
  ],
};

const wrapper = ({ children }: { children: React.ReactNode }) => (
  <ToastProvider>{children}</ToastProvider>
);

describe('useNotebookWorkspaceControls', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    (api.notebooks.chatReadiness as ReturnType<typeof vi.fn>).mockResolvedValue(readyChat);
    (api.notebooks.headerToolbar as ReturnType<typeof vi.fn>).mockResolvedValue(baseToolbar);
    Object.defineProperty(document, 'visibilityState', {
      configurable: true,
      value: 'visible',
    });
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('loads chat readiness for all users', async () => {
    const { result } = renderHook(
      () =>
        useNotebookWorkspaceControls({
          notebookId: 'nb-1',
          conversationId: 'convo-1',
        }),
      { wrapper }
    );

    await act(async () => {
      await Promise.resolve();
    });

    expect(result.current.chat).toEqual(readyChat);
    expect(result.current.toolbar).toBeNull();
    expect(api.notebooks.chatReadiness).toHaveBeenCalledWith('nb-1', 'convo-1');
    expect(api.notebooks.headerToolbar).not.toHaveBeenCalled();
    expect(result.current.chatIsLoading).toBe(false);
  });

  it('loads toolbar when includeToolbar is true', async () => {
    const { result } = renderHook(
      () =>
        useNotebookWorkspaceControls({
          notebookId: 'nb-1',
          conversationId: 'convo-1',
          includeToolbar: true,
        }),
      { wrapper }
    );

    await act(async () => {
      await Promise.resolve();
    });

    expect(result.current.chat).toEqual(readyChat);
    expect(result.current.toolbar).toEqual(baseToolbar);
    expect(api.notebooks.headerToolbar).toHaveBeenCalledWith('nb-1', 'convo-1');
  });

  it('clears state when disabled', async () => {
    const { result, rerender } = renderHook(
      ({ enabled }) =>
        useNotebookWorkspaceControls({
          notebookId: 'nb-1',
          conversationId: null,
          includeToolbar: true,
          enabled,
        }),
      { wrapper, initialProps: { enabled: true } }
    );

    await act(async () => {
      await Promise.resolve();
    });

    rerender({ enabled: false });

    expect(result.current.chat).toBeNull();
    expect(result.current.toolbar).toBeNull();
    expect(result.current.chatIsLoading).toBe(false);
    expect(result.current.toolbarIsLoading).toBe(false);
  });

  it('refresh updates chat and toolbar together for admins', async () => {
    const { result } = renderHook(
      () =>
        useNotebookWorkspaceControls({
          notebookId: 'nb-1',
          conversationId: 'convo-2',
          includeToolbar: true,
        }),
      { wrapper }
    );

    await act(async () => {
      await Promise.resolve();
    });

    vi.clearAllMocks();

    await act(async () => {
      await result.current.refresh();
    });

    expect(api.notebooks.chatReadiness).toHaveBeenCalledWith('nb-1', 'convo-2');
    expect(api.notebooks.headerToolbar).toHaveBeenCalledWith('nb-1', 'convo-2');
  });

  it('refreshes on toolbar window event', async () => {
    renderHook(
      () =>
        useNotebookWorkspaceControls({
          notebookId: 'nb-1',
          conversationId: null,
          includeToolbar: true,
        }),
      { wrapper }
    );

    await act(async () => {
      await Promise.resolve();
    });

    const chatCallsBefore = (api.notebooks.chatReadiness as ReturnType<typeof vi.fn>).mock.calls.length;
    const toolbarCallsBefore = (api.notebooks.headerToolbar as ReturnType<typeof vi.fn>).mock.calls.length;

    await act(async () => {
      window.dispatchEvent(new Event('refresh-notebook-toolbar'));
      await Promise.resolve();
    });

    expect(
      (api.notebooks.chatReadiness as ReturnType<typeof vi.fn>).mock.calls.length
    ).toBeGreaterThan(chatCallsBefore);
    expect(
      (api.notebooks.headerToolbar as ReturnType<typeof vi.fn>).mock.calls.length
    ).toBeGreaterThan(toolbarCallsBefore);
  });

  it('polls chat while a chat operation is in progress', async () => {
    vi.useFakeTimers();
    (api.notebooks.chatReadiness as ReturnType<typeof vi.fn>).mockResolvedValue(activeChat);

    renderHook(
      () =>
        useNotebookWorkspaceControls({
          notebookId: 'nb-1',
          conversationId: null,
        }),
      { wrapper }
    );

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
    expect(api.notebooks.headerToolbar).not.toHaveBeenCalled();
  });

  it('polls toolbar while a service operation is in progress', async () => {
    vi.useFakeTimers();
    (api.notebooks.headerToolbar as ReturnType<typeof vi.fn>).mockResolvedValue(activeToolbar);

    renderHook(
      () =>
        useNotebookWorkspaceControls({
          notebookId: 'nb-1',
          conversationId: null,
          includeToolbar: true,
        }),
      { wrapper }
    );

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

  it('polls toolbar while inFlight is true', async () => {
    vi.useFakeTimers();

    const { result } = renderHook(
      () =>
        useNotebookWorkspaceControls({
          notebookId: 'nb-1',
          conversationId: null,
          includeToolbar: true,
        }),
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

  it('handles chat load errors', async () => {
    (api.notebooks.chatReadiness as ReturnType<typeof vi.fn>).mockRejectedValue(
      new Error('Readiness failed')
    );

    const { result } = renderHook(
      () =>
        useNotebookWorkspaceControls({
          notebookId: 'nb-1',
          conversationId: null,
        }),
      { wrapper }
    );

    await act(async () => {
      await Promise.resolve();
    });

    expect(result.current.chatError).toBe('Readiness failed');
    expect(result.current.chatIsLoading).toBe(false);
  });

  it('handles toolbar load errors', async () => {
    (api.notebooks.headerToolbar as ReturnType<typeof vi.fn>).mockRejectedValue(
      new Error('Toolbar load failed')
    );

    const { result } = renderHook(
      () =>
        useNotebookWorkspaceControls({
          notebookId: 'nb-1',
          conversationId: null,
          includeToolbar: true,
        }),
      { wrapper }
    );

    await act(async () => {
      await Promise.resolve();
    });

    expect(result.current.toolbarError).toBe('Toolbar load failed');
    expect(result.current.toolbarIsLoading).toBe(false);
  });
});
