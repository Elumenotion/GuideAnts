import React from 'react';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, waitFor, cleanup } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import '@testing-library/jest-dom';
import { MemoryRouter } from 'react-router-dom';
import { ToastProvider } from '../../../components/common/Toast';

vi.unmock('../GuideAntsGuideButton');

import { GuideAntsGuideProvider } from '../GuideAntsGuideProvider';
import { GuideAntsGuideButton } from '../GuideAntsGuideButton';
import { useAuth } from '../../../contexts/AuthContext';
import { api } from '../../../services/api';
import { resetGuideantsLoadStateForTests } from '../loadGuideants';

vi.mock('../../../contexts/AuthContext', () => ({
  useAuth: vi.fn(),
}));

vi.mock('../../../services/api', () => ({
  api: {
    systemGuide: {
      getSession: vi.fn(),
    },
  },
}));

vi.mock('guideants', () => ({}));

const mockedUseAuth = vi.mocked(useAuth);
const mockedGetSession = vi.mocked(api.systemGuide.getSession);

class MockGuideantsChat extends HTMLElement {
  setAuthToken = vi.fn();

  setContextProvider = vi.fn();

  registerTool = vi.fn();
}

function renderGuideShell(initialRoute = '/projects/p1') {
  return render(
    <ToastProvider>
      <MemoryRouter initialEntries={[initialRoute]}>
        <GuideAntsGuideProvider>
          <GuideAntsGuideButton />
        </GuideAntsGuideProvider>
      </MemoryRouter>
    </ToastProvider>,
  );
}

describe('GuideAntsGuideFlyout', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    resetGuideantsLoadStateForTests();
    if (!customElements.get('guideants-chat')) {
      customElements.define('guideants-chat', MockGuideantsChat);
    }

    mockedUseAuth.mockReturnValue({
      user: {
        id: 'u1',
        name: 'Ada Lovelace',
        email: 'ada@example.com',
        role: 'Contributor',
        mustChangePassword: false,
        lastLoginAt: null,
      },
      role: 'Contributor',
      status: 'authenticated',
      isAuthenticated: true,
      login: vi.fn(),
      register: vi.fn(),
      changePassword: vi.fn(),
      refresh: vi.fn(),
      logout: vi.fn(),
    });

    mockedGetSession.mockResolvedValue({
      publishedGuideId: '11111111-1111-1111-1111-111111111111',
      projectId: '22222222-2222-2222-2222-222222222222',
      notebookId: '33333333-3333-3333-3333-333333333333',
      guideId: '44444444-4444-4444-4444-444444444444',
      guideName: 'GuideAnts Guide',
      clientBridgeId: 'guideants-app',
      isAdminGuide: false,
      commandMode: true,
    });
  });

  afterEach(() => {
    cleanup();
  });

  it('opens and closes the flyout panel', async () => {
    const user = userEvent.setup();
    renderGuideShell();

    await user.click(screen.getByRole('button', { name: 'GuideAnts Guide' }));
    expect(await screen.findByTestId('guideants-guide-flyout')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Close GuideAnts Guide' }));
    await waitFor(() => {
      expect(screen.queryByTestId('guideants-guide-flyout')).not.toBeInTheDocument();
    });
  });

  it('mounts guideants-chat with pub-id and does not call setAuthToken', async () => {
    const user = userEvent.setup();
    renderGuideShell();

    await user.click(screen.getByRole('button', { name: 'GuideAnts Guide' }));
    await waitFor(() => {
      expect(mockedGetSession).toHaveBeenCalledTimes(1);
    });

    const chat = await waitFor(() => {
      const element = document.querySelector('guideants-chat') as MockGuideantsChat | null;
      expect(element).not.toBeNull();
      return element as MockGuideantsChat;
    });

    expect(chat.getAttribute('pub-id')).toBe('11111111-1111-1111-1111-111111111111');
    expect(chat.getAttribute('speech-to-text-enabled')).toBe('true');
    expect(chat.getAttribute('command-mode')).toBe('true');
    expect(chat.setAuthToken).not.toHaveBeenCalled();
    expect(chat.registerTool).toHaveBeenCalledWith('AppEcho', expect.any(Function));
    expect(chat.setContextProvider).toHaveBeenCalled();
  });

  it('injects notebook route context into the chat context provider', async () => {
    const user = userEvent.setup();
    renderGuideShell('/projects/project-1/notebooks/notebook-2');

    await user.click(screen.getByRole('button', { name: 'GuideAnts Guide' }));
    const chat = await waitFor(() => {
      const element = document.querySelector('guideants-chat') as MockGuideantsChat | null;
      expect(element).not.toBeNull();
      return element as MockGuideantsChat;
    });

    const contextProvider = chat.setContextProvider.mock.calls.at(-1)?.[0] as (() => string) | undefined;
    expect(contextProvider).toBeDefined();
    const context = JSON.parse(contextProvider!()) as Record<string, unknown>;

    expect(context.projectId).toBe('project-1');
    expect(context.notebookId).toBe('notebook-2');
    expect(context.guideId).toBeUndefined();
  });

  it('shows the Admin badge for admin sessions', async () => {
    mockedGetSession.mockResolvedValueOnce({
      publishedGuideId: 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa',
      projectId: 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb',
      notebookId: 'cccccccc-cccc-cccc-cccc-cccccccccccc',
      guideId: 'dddddddd-dddd-dddd-dddd-dddddddddddd',
      guideName: 'GuideAnts Guide Admin',
      clientBridgeId: 'guideants-app',
      isAdminGuide: true,
      commandMode: true,
    });

    const user = userEvent.setup();
    renderGuideShell('/settings');

    await user.click(screen.getByRole('button', { name: 'GuideAnts Guide' }));
    expect(await screen.findByText('Admin')).toBeInTheDocument();
  });

  it('respects commandMode from guide session configuration', async () => {
    mockedGetSession.mockResolvedValueOnce({
      publishedGuideId: 'eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee',
      projectId: 'ffffffff-ffff-ffff-ffff-ffffffffffff',
      notebookId: '11111111-2222-3333-4444-555555555555',
      guideId: '66666666-7777-8888-9999-000000000000',
      guideName: 'GuideAnts Guide',
      clientBridgeId: 'guideants-app',
      isAdminGuide: false,
      commandMode: false,
    });

    const user = userEvent.setup();
    renderGuideShell('/projects/p1');

    await user.click(screen.getByRole('button', { name: 'GuideAnts Guide' }));
    const chat = await waitFor(() => {
      const element = document.querySelector('guideants-chat') as MockGuideantsChat | null;
      expect(element).not.toBeNull();
      return element as MockGuideantsChat;
    });

    expect(chat.getAttribute('command-mode')).toBe('false');
  });
});
