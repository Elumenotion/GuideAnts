import React from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import userEvent from '@testing-library/user-event';
import { render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import '@testing-library/jest-dom';
import Settings from '../Settings';
import { useAuth } from '../../contexts/AuthContext';
import { api } from '../../services/api';
import { ToastProvider } from '../../components/common/Toast';
import type { CreateRuntimeProfileRequest, SettingsRuntimeProfileDto } from '../../types/settings';

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
      createRuntimeProfile: vi.fn(),
      updateRuntimeProfile: vi.fn(),
      deleteRuntimeProfile: vi.fn(),
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
  useLocalModelOnboardingOperation: () => ({ statusLabel: 'downloading' }),
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

const profile: SettingsRuntimeProfileDto = {
  profileId: 'local_llama',
  displayName: 'Local Llama',
  description: 'Dev profile',
  providers: ['llama-cpp'],
  created: '2026-01-01T00:00:00Z',
  updated: '2026-01-02T00:00:00Z',
  combineSystemAndDeveloperMessages: false,
  thoughtBlockPattern: '',
  samplingParametersJson: '{}',
  thinkingControlJson: '{}',
};

const customProfileRequest: CreateRuntimeProfileRequest = {
  profileId: 'new_profile',
  displayName: 'New Profile',
  description: '',
  combineSystemAndDeveloperMessages: false,
  thoughtBlockPattern: '',
  samplingParametersJson: '{}',
  thinkingControlJson: '{}',
};

vi.mock('../settings/components/ModelsRuntimeWorkspace', () => ({
  ModelsRuntimeWorkspace: ({
    onOpenAddModelWizard,
  }: {
    onOpenAddModelWizard: (provider?: string) => void;
  }) => (
    <button type="button" onClick={() => onOpenAddModelWizard('llama-cpp')}>
      open-add-model-wizard
    </button>
  ),
}));

vi.mock('../settings/components/catalog/AddModelWizard', () => ({
  AddModelWizard: ({
    isOpen,
    onCreateRuntimeProfileTemplate,
    onCreateCustomRuntimeProfile,
  }: {
    isOpen: boolean;
    onCreateRuntimeProfileTemplate: (template: 'qwen3_5' | 'qwen3_6' | 'gemma4') => Promise<void>;
    onCreateCustomRuntimeProfile: (request: CreateRuntimeProfileRequest) => Promise<SettingsRuntimeProfileDto>;
  }) =>
    isOpen ? (
      <div>
        <button
          type="button"
          onClick={() => {
            void onCreateCustomRuntimeProfile(customProfileRequest).catch(() => undefined);
          }}
        >
          save-profile
        </button>
        <button type="button" onClick={() => onCreateRuntimeProfileTemplate('qwen3_5')}>
          insert-template
        </button>
        <button type="button" onClick={() => onCreateRuntimeProfileTemplate('qwen3_6')}>
          insert-template-qwen36
        </button>
        <button type="button" onClick={() => onCreateRuntimeProfileTemplate('gemma4')}>
          insert-template-gemma4
        </button>
      </div>
    ) : null,
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

describe('Settings profile flows', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(api.settings.getSections).mockResolvedValue([]);
    vi.mocked(api.settings.getModels).mockResolvedValue([]);
    vi.mocked(api.settings.getRuntimeProfiles).mockResolvedValue([profile]);
    vi.mocked(api.settings.getLlamaInventory).mockResolvedValue([]);
    vi.mocked(api.settings.createRuntimeProfile).mockResolvedValue(profile);
    vi.mocked(api.settings.updateRuntimeProfile).mockResolvedValue(profile);
  });

  async function openAddModelWizard(user: ReturnType<typeof userEvent.setup>) {
    await user.click(screen.getByRole('button', { name: /Models & Runtime/i }));
    await user.click(await screen.findByRole('button', { name: 'open-add-model-wizard' }));
    expect(await screen.findByText('save-profile')).toBeInTheDocument();
  }

  it('creates a custom runtime profile from the add model wizard', async () => {
    const user = userEvent.setup();
    renderSettingsAsAdmin();
    await openAddModelWizard(user);

    await user.click(screen.getByRole('button', { name: 'save-profile' }));

    await waitFor(() => {
      expect(api.settings.createRuntimeProfile).toHaveBeenCalledWith(
        expect.objectContaining({ profileId: 'new_profile' }),
      );
      expect(api.settings.getRuntimeProfiles).toHaveBeenCalledTimes(2);
    });
  });

  it('inserts a runtime profile template and reuses existing profiles gracefully', async () => {
    const user = userEvent.setup();
    vi.mocked(api.settings.createRuntimeProfile)
      .mockRejectedValueOnce(new Error('Profile already exists'))
      .mockResolvedValueOnce(profile);

    renderSettingsAsAdmin();
    await openAddModelWizard(user);

    await user.click(screen.getByRole('button', { name: 'insert-template' }));

    await waitFor(() => {
      expect(api.settings.createRuntimeProfile).toHaveBeenCalledWith(
        expect.objectContaining({ profileId: 'qwen3_5' }),
      );
      expect(api.settings.getRuntimeProfiles).toHaveBeenCalledTimes(2);
    });
  });

  it('creates gemma4 and qwen3_6 runtime profile templates', async () => {
    const user = userEvent.setup();
    renderSettingsAsAdmin();
    await openAddModelWizard(user);

    await user.click(screen.getByRole('button', { name: 'insert-template-qwen36' }));
    await waitFor(() => {
      expect(api.settings.createRuntimeProfile).toHaveBeenCalledWith(
        expect.objectContaining({ profileId: 'qwen3_6' }),
      );
    });

    await user.click(screen.getByRole('button', { name: 'insert-template-gemma4' }));
    await waitFor(() => {
      expect(api.settings.createRuntimeProfile).toHaveBeenCalledWith(
        expect.objectContaining({ profileId: 'gemma4' }),
      );
    });
  });

  it('surfaces template creation failures that are not duplicate-profile cases', async () => {
    const user = userEvent.setup();
    vi.mocked(api.settings.createRuntimeProfile).mockRejectedValue(new Error('Template write failed'));

    renderSettingsAsAdmin();
    await openAddModelWizard(user);

    await user.click(screen.getByRole('button', { name: 'insert-template' }));

    await waitFor(() => {
      expect(api.settings.createRuntimeProfile).toHaveBeenCalled();
    });
  });

  it('surfaces custom profile save failures from the API', async () => {
    const user = userEvent.setup();
    vi.mocked(api.settings.createRuntimeProfile).mockRejectedValue(new Error('Create failed'));

    renderSettingsAsAdmin();
    await openAddModelWizard(user);

    await user.click(screen.getByRole('button', { name: 'save-profile' }));

    await waitFor(() => {
      expect(api.settings.createRuntimeProfile).toHaveBeenCalled();
    });
  });
});
