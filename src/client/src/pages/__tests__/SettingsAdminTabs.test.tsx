import React from 'react';
import { describe, expect, it, vi, beforeEach } from 'vitest';
import userEvent from '@testing-library/user-event';
import { render, screen, waitFor } from '@testing-library/react';
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
      getRuntimeProfiles: vi.fn(),
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
  HomeButton: () => <button type="button">Home</button>,
}));
vi.mock('../../components/common/SettingsButton', () => ({
  SettingsButton: () => <button type="button">Settings</button>,
}));
vi.mock('../../components/common/HeaderUserMenu', () => ({
  HeaderUserMenu: () => <div>user-menu</div>,
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
vi.mock('../settings/components/ModelsRuntimeWorkspace', () => ({
  ModelsRuntimeWorkspace: () => <div>models-runtime-tab-panel</div>,
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

describe('Settings admin tabs', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(api.settings.getSections).mockResolvedValue([]);
    vi.mocked(api.settings.getModels).mockResolvedValue([]);
    vi.mocked(api.settings.getRuntimeProfiles).mockResolvedValue([]);
    vi.mocked(api.settings.getLlamaInventory).mockResolvedValue([]);
  });

  it('renders overview by default and switches major admin tabs', async () => {
    const user = userEvent.setup();
    renderSettingsAsAdmin();

    expect(await screen.findByText('overview-tab-panel')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: /Connections/i }));
    expect(await screen.findByText('connections-tab-panel')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: /Models & Runtime/i }));
    expect(await screen.findByText('models-runtime-tab-panel')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: /Services/i }));
    expect(await screen.findByText('services-tab-panel')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: /Infrastructure/i }));
    expect(await screen.findByText('infrastructure-tab-panel')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: /Users/i }));
    expect(await screen.findByText('users-tab-panel')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: /Telemetry/i }));
    expect(await screen.findByText('telemetry-tab-panel')).toBeInTheDocument();
  });

  it('loads admin settings data on mount', async () => {
    renderSettingsAsAdmin();

    await waitFor(() => {
      expect(api.settings.getSections).toHaveBeenCalled();
      expect(api.settings.getModels).toHaveBeenCalled();
      expect(api.settings.getRuntimeProfiles).toHaveBeenCalled();
      expect(api.settings.getLlamaInventory).toHaveBeenCalled();
    });
  });
});
