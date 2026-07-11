import React from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import userEvent from '@testing-library/user-event';
import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
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
vi.mock('../settings/components/PersonalizationTab', () => ({
  PersonalizationTab: () => <div>personalization-tab-panel</div>,
}));
vi.mock('../settings/components/UsersTab', () => ({
  UsersTab: () => <div>users-tab-panel</div>,
}));
vi.mock('../settings/components/ConnectionsTab', () => ({
  ConnectionsTab: ({ focusedSection }: { focusedSection: string | null }) => (
    <div>connections-tab-panel:{focusedSection ?? 'none'}</div>
  ),
}));
vi.mock('../settings/components/ServicesTab', () => ({
  ServicesTab: ({ focusedService }: { focusedService: string | null }) => (
    <div>services-tab-panel:{focusedService ?? 'none'}</div>
  ),
}));
vi.mock('../settings/components/ModelsRuntimeWorkspace', () => ({
  ModelsRuntimeWorkspace: ({ initialSubTab }: { initialSubTab?: string }) => (
    <div>models-runtime-tab-panel:{initialSubTab ?? 'default'}</div>
  ),
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
vi.mock('../settings/components/OverviewTab', () => ({
  OverviewTab: ({
    onOpenConnections,
    onOpenServices,
    onOpenModelsRuntime,
  }: {
    onOpenConnections: (section?: string) => void;
    onOpenServices: (serviceId: string) => void;
    onOpenModelsRuntime: (subTab: 'catalog' | 'local-llama') => void;
  }) => (
    <div>
      <button type="button" onClick={() => onOpenConnections('openai-chat')}>
        open-connections
      </button>
      <button type="button" onClick={() => onOpenServices('Embeddings')}>
        open-embeddings-service
      </button>
      <button type="button" onClick={() => onOpenModelsRuntime('local-llama')}>
        open-loaded-models-runtime
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

describe('Settings navigation deep links', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(api.settings.getSections).mockResolvedValue([]);
    vi.mocked(api.settings.getModels).mockResolvedValue([]);
    vi.mocked(api.settings.getRuntimeProfiles).mockResolvedValue([]);
    vi.mocked(api.settings.getLlamaInventory).mockResolvedValue([]);
    window.sessionStorage.clear();
  });

  it('routes overview shortcuts into focused settings tabs', async () => {
    const user = userEvent.setup();
    renderSettingsAsAdmin();

    await user.click(screen.getByRole('button', { name: 'open-connections' }));
    expect(await screen.findByText('connections-tab-panel:openai-chat')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: /Overview/i }));
    await user.click(screen.getByRole('button', { name: 'open-embeddings-service' }));
    expect(await screen.findByText('services-tab-panel:Embeddings')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: /Overview/i }));
    await user.click(screen.getByRole('button', { name: 'open-loaded-models-runtime' }));
    expect(await screen.findByText('models-runtime-tab-panel:local-llama')).toBeInTheDocument();
  });

  it('shows the active add-model progress banner and open-progress action', async () => {
    const user = userEvent.setup();
    window.sessionStorage.setItem(
      'guideants.settings.activeAddOperation',
      JSON.stringify({
        operationId: 'op-1',
        catalogModelId: 'llama/local',
      }),
    );

    renderSettingsAsAdmin();

    expect(await screen.findByText(/llama\/local/i)).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Open progress' }));
    expect(await screen.findByText('models-runtime-tab-panel:catalog')).toBeInTheDocument();
  });
});
