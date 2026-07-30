import React from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import userEvent from '@testing-library/user-event';
import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import '@testing-library/jest-dom';
import Settings from '../Settings';
import { useAuth } from '../../contexts/AuthContext';
import { api } from '../../services/api';
import { ToastProvider } from '../../components/common/Toast';

vi.mock('../../contexts/AuthContext', () => ({
  useAuth: vi.fn(),
}));

vi.mock('../../services/api', () => ({
  api: {
    settings: {
      getSections: vi.fn(),
      getModels: vi.fn(),
      getLlamaInventory: vi.fn(),
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
vi.mock('../settings/components/ModelsRuntimeWorkspace', () => ({
  ModelsRuntimeWorkspace: ({ onOpenAddModelWizard }: { onOpenAddModelWizard: (provider?: string) => void }) => (
    <button type="button" onClick={() => onOpenAddModelWizard('llama-cpp')}>
      open-wizard
    </button>
  ),
}));
vi.mock('../settings/components/catalog/AddModelWizard', () => ({
  AddModelWizard: ({
    isOpen,
    onClose,
  }: {
    isOpen: boolean;
    onClose: () => void;
  }) =>
    isOpen ? (
      <div>
        <div>add-model-wizard</div>
        <button type="button" onClick={onClose}>
          close-wizard
        </button>
      </div>
    ) : null,
}));

const mockedUseAuth = vi.mocked(useAuth);

describe('Settings add-model wizard integration', () => {
  beforeEach(() => {
    vi.clearAllMocks();
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
    vi.mocked(api.settings.getSections).mockResolvedValue([]);
    vi.mocked(api.settings.getModels).mockResolvedValue([]);
    vi.mocked(api.settings.getLlamaInventory).mockResolvedValue([]);
  });

  it('opens and closes the add-model wizard from Models & Runtime', async () => {
    const user = userEvent.setup();

    render(
      <MemoryRouter>
        <ToastProvider>
          <Settings />
        </ToastProvider>
      </MemoryRouter>,
    );

    await user.click(screen.getByRole('button', { name: /Models & Runtime/i }));
    await user.click(screen.getByRole('button', { name: 'open-wizard' }));

    expect(screen.getByText('add-model-wizard')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'close-wizard' }));
    expect(screen.queryByText('add-model-wizard')).not.toBeInTheDocument();
  });
});
