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
import { createEmptyProfileForm } from '../settings/utils';
import type { SettingsRuntimeProfileDto } from '../../types/settings';

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
vi.mock('../settings/components/catalog/AddModelWizard', () => ({
  AddModelWizard: () => null,
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

vi.mock('../settings/components/ModelsRuntimeWorkspace', () => ({
  ModelsRuntimeWorkspace: ({
    onOpenCreateProfile,
    onSaveProfile,
    onEditProfile,
    onInsertRuntimeProfileTemplate,
    onProfileFormChange,
    onImportProfile,
  }: {
    onOpenCreateProfile: () => void;
    onSaveProfile: () => void;
    onEditProfile: (profile: SettingsRuntimeProfileDto) => void;
    onInsertRuntimeProfileTemplate: (template: 'qwen3_5' | 'qwen3_6' | 'gemma4') => void;
    onProfileFormChange: <K extends keyof ReturnType<typeof createEmptyProfileForm>>(
      key: K,
      value: ReturnType<typeof createEmptyProfileForm>[K],
    ) => void;
    onImportProfile: (form: ReturnType<typeof createEmptyProfileForm>) => void;
  }) => (
    <div>
      <button type="button" onClick={onOpenCreateProfile}>
        open-create-profile
      </button>
      <button type="button" onClick={() => onEditProfile(profile)}>
        edit-profile
      </button>
      <button
        type="button"
        onClick={() => {
          onProfileFormChange('profileId', 'new_profile');
          onProfileFormChange('displayName', 'New Profile');
        }}
      >
        patch-profile-form
      </button>
      <button type="button" onClick={onSaveProfile}>
        save-profile
      </button>
      <button type="button" onClick={() => onInsertRuntimeProfileTemplate('qwen3_5')}>
        insert-template
      </button>
      <button type="button" onClick={() => onInsertRuntimeProfileTemplate('qwen3_6')}>
        insert-template-qwen36
      </button>
      <button type="button" onClick={() => onInsertRuntimeProfileTemplate('gemma4')}>
        insert-template-gemma4
      </button>
      <button
        type="button"
        onClick={() =>
          onImportProfile({
            ...createEmptyProfileForm(),
            profileId: 'imported_profile',
            displayName: 'Imported Profile',
          })
        }
      >
        import-profile
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

  async function openModelsRuntime(user: ReturnType<typeof userEvent.setup>) {
    await user.click(screen.getByRole('button', { name: /Models & Runtime/i }));
    expect(await screen.findByText('save-profile')).toBeInTheDocument();
  }

  it('creates a runtime profile from the models workspace', async () => {
    const user = userEvent.setup();
    renderSettingsAsAdmin();
    await openModelsRuntime(user);

    await user.click(screen.getByRole('button', { name: 'open-create-profile' }));
    await user.click(screen.getByRole('button', { name: 'patch-profile-form' }));
    await user.click(screen.getByRole('button', { name: 'save-profile' }));

    await waitFor(() => {
      expect(api.settings.createRuntimeProfile).toHaveBeenCalled();
      expect(api.settings.getRuntimeProfiles).toHaveBeenCalledTimes(2);
    });
  });

  it('updates an existing runtime profile', async () => {
    const user = userEvent.setup();
    renderSettingsAsAdmin();
    await openModelsRuntime(user);

    await user.click(screen.getByRole('button', { name: 'edit-profile' }));
    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'save-profile' })).toBeEnabled();
    });
    await user.click(screen.getByRole('button', { name: 'save-profile' }));

    await waitFor(() => {
      expect(api.settings.updateRuntimeProfile).toHaveBeenCalledWith('local_llama', expect.any(Object));
    });
  });

  it('inserts a runtime profile template and reuses existing profiles gracefully', async () => {
    const user = userEvent.setup();
    vi.mocked(api.settings.createRuntimeProfile)
      .mockRejectedValueOnce(new Error('Profile already exists'))
      .mockResolvedValueOnce(profile);

    renderSettingsAsAdmin();
    await openModelsRuntime(user);

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
    await openModelsRuntime(user);

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
    await openModelsRuntime(user);

    await user.click(screen.getByRole('button', { name: 'insert-template' }));

    await waitFor(() => {
      expect(api.settings.createRuntimeProfile).toHaveBeenCalled();
    });
  });

  it('surfaces profile save failures from the API', async () => {
    const user = userEvent.setup();
    vi.mocked(api.settings.createRuntimeProfile).mockRejectedValue(new Error('Create failed'));

    renderSettingsAsAdmin();
    await openModelsRuntime(user);

    await user.click(screen.getByRole('button', { name: 'open-create-profile' }));
    await user.click(screen.getByRole('button', { name: 'patch-profile-form' }));
    await user.click(screen.getByRole('button', { name: 'save-profile' }));

    await waitFor(() => {
      expect(api.settings.createRuntimeProfile).toHaveBeenCalled();
    });
  });

  it('opens the profile dialog with imported profile data', async () => {
    const user = userEvent.setup();
    renderSettingsAsAdmin();
    await openModelsRuntime(user);

    await user.click(screen.getByRole('button', { name: 'import-profile' }));
    await user.click(screen.getByRole('button', { name: 'save-profile' }));

    await waitFor(() => {
      expect(api.settings.createRuntimeProfile).toHaveBeenCalledWith(
        expect.objectContaining({ profileId: 'imported_profile' }),
      );
    });
  });
});
