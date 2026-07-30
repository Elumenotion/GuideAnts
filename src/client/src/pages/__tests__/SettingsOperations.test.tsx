import React from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import userEvent from '@testing-library/user-event';
import { act, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import '@testing-library/jest-dom';
import Settings from '../Settings';
import { useAuth } from '../../contexts/AuthContext';
import { api } from '../../services/api';
import { ToastProvider } from '../../components/common/Toast';
import type { ModelDownloadOperationDto } from '../../types/settings';

let onboardingHandlers: {
  onUpdate?: (op: ModelDownloadOperationDto) => void;
  onTerminal?: (op: ModelDownloadOperationDto) => void;
  onPollFailureThreshold?: () => void;
} = {};

vi.mock('../../contexts/AuthContext', () => ({
  useAuth: vi.fn(),
}));

vi.mock('../../services/api', () => ({
  api: {
    settings: {
      getSections: vi.fn(),
      getModels: vi.fn(),
      getRuntimeProfiles: vi.fn(),
      getLlamaInventory: vi.fn(),
      deleteModel: vi.fn(),
      deleteRuntimeProfile: vi.fn(),
      unloadLlamaModel: vi.fn(),
      deleteLlamaRouterEntry: vi.fn(),
      loadLlamaModel: vi.fn(),
      createRuntimeProfile: vi.fn(),
      updateRuntimeProfile: vi.fn(),
    },
  },
}));

vi.mock('../../features/guideantsGuide/GuideAntsGuideButton', () => ({
  GuideAntsGuideButton: () => null,
}));
vi.mock('../../features/guideantsGuide/viewContext', () => ({
  usePublishGuideViewContext: vi.fn(),
}));
vi.mock('../../components/common/HomeButton', () => ({
  HomeButton: () => null,
}));
vi.mock('../../components/common/SettingsButton', () => ({
  SettingsButton: () => null,
}));
vi.mock('../../components/common/HeaderUserMenu', () => ({
  HeaderUserMenu: () => null,
}));
vi.mock('../../tour/TourStartButton', () => ({
  TourStartButton: () => null,
}));
vi.mock('../../features/localModelOnboarding/useOperationPolling', () => ({
  useLocalModelOnboardingOperation: (opts: typeof onboardingHandlers) => {
    onboardingHandlers = opts;
    return { statusLabel: 'downloading' };
  },
}));
vi.mock('../settings/components/OverviewTab', () => ({
  OverviewTab: () => <div>overview-tab-panel</div>,
}));
vi.mock('../settings/components/PersonalizationTab', () => ({
  PersonalizationTab: () => <div>personalization-tab-panel</div>,
}));
vi.mock('../settings/components/UsersTab', () => ({
  UsersTab: () => <div>users-tab-panel</div>,
}));
vi.mock('../settings/components/ConnectionsTab', () => ({
  ConnectionsTab: () => <div>connections-tab-panel</div>,
}));
vi.mock('../settings/components/ServicesTab', () => ({
  ServicesTab: () => <div>services-tab-panel</div>,
}));
vi.mock('../settings/components/InfrastructureTab', () => ({
  InfrastructureTab: () => <div>infrastructure-tab-panel</div>,
}));
vi.mock('../settings/components/TelemetryTab', () => ({
  TelemetryTab: () => <div>telemetry-tab-panel</div>,
}));
vi.mock('../settings/components/catalog/AddModelWizard', () => ({
  AddModelWizard: () => null,
}));
vi.mock('../settings/components/ModelsRuntimeWorkspace', () => ({
  ModelsRuntimeWorkspace: ({
    onLoadLlamaModel,
    onSaveProfile,
    onOpenCreateProfile,
  }: {
    onLoadLlamaModel: (routerModelId: string) => void;
    onSaveProfile: () => void;
    onOpenCreateProfile: () => void;
  }) => (
    <div>
      <button type="button" onClick={() => onLoadLlamaModel('alias-1')}>
        trigger-load-llama
      </button>
      <button type="button" onClick={onOpenCreateProfile}>
        open-create-profile
      </button>
      <button type="button" onClick={onSaveProfile}>
        save-empty-profile
      </button>
    </div>
  ),
}));

const mockedUseAuth = vi.mocked(useAuth);

function renderSettingsAsAdmin() {
  mockedUseAuth.mockReturnValue({
    user: {
      id: 'admin-1',
      name: 'Admin',
      email: 'admin@example.com',
      role: 'Admin',
      mustChangePassword: false,
      lastLoginAt: null,
    },
    role: 'Admin',
    status: 'authenticated',
    isAuthenticated: true,
    login: vi.fn(),
    register: vi.fn(),
    changePassword: vi.fn(),
    refresh: vi.fn(),
    logout: vi.fn(),
  });

  return render(
    <MemoryRouter>
      <ToastProvider>
        <Settings />
      </ToastProvider>
    </MemoryRouter>,
  );
}

describe('Settings operational flows', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    onboardingHandlers = {};
    window.sessionStorage.clear();
    vi.mocked(api.settings.getSections).mockResolvedValue([]);
    vi.mocked(api.settings.getModels).mockResolvedValue([]);
    vi.mocked(api.settings.getRuntimeProfiles).mockResolvedValue([]);
    vi.mocked(api.settings.getLlamaInventory).mockResolvedValue([]);
    vi.mocked(api.settings.loadLlamaModel).mockResolvedValue(undefined as never);
    vi.mocked(api.settings.createRuntimeProfile).mockResolvedValue({
      profileId: 'new_profile',
      displayName: 'New Profile',
      description: '',
      providers: [],
      combineSystemAndDeveloperMessages: false,
      thoughtBlockPattern: '',
      samplingParametersJson: '{}',
      thinkingControlJson: '{}',
      created: '2026-01-01T00:00:00Z',
      updated: '2026-01-02T00:00:00Z',
    });
  });

  async function openModelsRuntime(user: ReturnType<typeof userEvent.setup>) {
    await user.click(screen.getByRole('button', { name: /Models & Runtime/i }));
    expect(await screen.findByText('trigger-load-llama')).toBeInTheDocument();
  }

  it('loads a llama router model and refreshes inventory', async () => {
    const user = userEvent.setup();
    renderSettingsAsAdmin();
    await openModelsRuntime(user);

    await user.click(screen.getByRole('button', { name: 'trigger-load-llama' }));

    await waitFor(() => {
      expect(api.settings.loadLlamaModel).toHaveBeenCalledWith('alias-1');
      expect(api.settings.getLlamaInventory).toHaveBeenCalledTimes(2);
    });
  });

  it('clears the add-model banner after a completed onboarding operation', async () => {
    window.sessionStorage.setItem(
      'guideants.settings.activeModelOperation',
      JSON.stringify({
        operationId: 'op-1',
        catalogModelId: 'llama/local',
        routerModelId: 'alias-1',
        kind: 'add',
        pollRoute: 'downloads',
      }),
    );

    renderSettingsAsAdmin();
    expect(await screen.findByText(/llama\/local/i)).toBeInTheDocument();

    await act(async () => {
      onboardingHandlers.onTerminal?.({
        operationId: 'op-1',
        status: 'completed',
        catalogModelId: 'llama/local',
      } as ModelDownloadOperationDto);
    });

    await waitFor(() => {
      expect(window.sessionStorage.getItem('guideants.settings.activeModelOperation')).toBeNull();
      expect(api.settings.getModels).toHaveBeenCalledTimes(2);
    });
  });

  it('clears the add-model banner when onboarding polling fails', async () => {
    window.sessionStorage.setItem(
      'guideants.settings.activeModelOperation',
      JSON.stringify({
        operationId: 'op-2',
        catalogModelId: 'llama/remote',
        routerModelId: 'alias-2',
        kind: 'add',
        pollRoute: 'downloads',
      }),
    );

    renderSettingsAsAdmin();
    expect(await screen.findByText(/llama\/remote/i)).toBeInTheDocument();

    await act(async () => {
      onboardingHandlers.onPollFailureThreshold?.();
    });

    await waitFor(() => {
      expect(window.sessionStorage.getItem('guideants.settings.activeModelOperation')).toBeNull();
    });
  });

  it('clears the add-model banner when onboarding fails terminally', async () => {
    window.sessionStorage.setItem(
      'guideants.settings.activeModelOperation',
      JSON.stringify({
        operationId: 'op-3',
        catalogModelId: 'llama/failed',
        routerModelId: 'alias-3',
        kind: 'add',
        pollRoute: 'downloads',
      }),
    );

    renderSettingsAsAdmin();
    expect(await screen.findByText(/llama\/failed/i)).toBeInTheDocument();

    await act(async () => {
      onboardingHandlers.onTerminal?.({
        operationId: 'op-3',
        status: 'failed',
        catalogModelId: 'llama/failed',
        errorMessage: 'Download failed',
      } as ModelDownloadOperationDto);
    });

    await waitFor(() => {
      expect(window.sessionStorage.getItem('guideants.settings.activeModelOperation')).toBeNull();
    });
  });

  it('surfaces profile validation failures without calling the API', async () => {
    const user = userEvent.setup();
    renderSettingsAsAdmin();
    await openModelsRuntime(user);

    await user.click(screen.getByRole('button', { name: 'open-create-profile' }));
    await user.click(screen.getByRole('button', { name: 'save-empty-profile' }));

    await waitFor(() => {
      expect(api.settings.createRuntimeProfile).not.toHaveBeenCalled();
    });
  });
});
