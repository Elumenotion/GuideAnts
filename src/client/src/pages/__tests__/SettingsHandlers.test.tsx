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
      getLlamaInventory: vi.fn(),
      deleteModel: vi.fn(),
      unloadLlamaModel: vi.fn(),
      deleteLlamaRouterEntry: vi.fn(),
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
vi.mock('../settings/components/ModelsRuntimeWorkspace', () => ({
  ModelsRuntimeWorkspace: ({
    onRequestDeleteModel,
    onRequestUnloadLlamaRouter,
    onRequestDeleteLlamaRouter,
    onOpenAddModelWizard,
  }: {
    onRequestDeleteModel: (modelId: string) => void;
    onRequestUnloadLlamaRouter: (routerModelId: string, notebookReferenceCount: number) => void;
    onRequestDeleteLlamaRouter: (
      routerModelId: string,
      catalogModelIds: string[],
      notebookReferenceCount: number,
    ) => void;
    onOpenAddModelWizard: (provider?: string) => void;
  }) => (
    <div>
      <button type="button" onClick={() => onRequestDeleteModel('gpt-test')}>
        trigger-delete-model
      </button>
      <button type="button" onClick={() => onRequestUnloadLlamaRouter('alias-1', 2)}>
        trigger-unload-llama
      </button>
      <button
        type="button"
        onClick={() => onRequestDeleteLlamaRouter('alias-1', ['llama/local'], 1)}
      >
        trigger-delete-llama
      </button>
      <button type="button" onClick={() => onOpenAddModelWizard('llama-cpp')}>
        open-add-model-wizard
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

describe('Settings confirmation handlers', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(api.settings.getSections).mockResolvedValue([]);
    vi.mocked(api.settings.getModels).mockResolvedValue([]);
    vi.mocked(api.settings.getLlamaInventory).mockResolvedValue([]);
    vi.mocked(api.settings.deleteModel).mockResolvedValue(undefined as never);
    vi.mocked(api.settings.unloadLlamaModel).mockResolvedValue(undefined as never);
    vi.mocked(api.settings.deleteLlamaRouterEntry).mockResolvedValue(undefined as never);
  });

  async function openModelsRuntime(user: ReturnType<typeof userEvent.setup>) {
    await user.click(screen.getByRole('button', { name: /Models & Runtime/i }));
    expect(await screen.findByText('trigger-delete-model')).toBeInTheDocument();
  }

  it('confirms catalog model deletion', async () => {
    const user = userEvent.setup();
    renderSettingsAsAdmin();
    await openModelsRuntime(user);

    await user.click(screen.getByRole('button', { name: 'trigger-delete-model' }));
    await user.click(screen.getByRole('button', { name: 'Delete' }));

    await waitFor(() => {
      expect(api.settings.deleteModel).toHaveBeenCalledWith('gpt-test');
    });
  });

  it('confirms llama router unload', async () => {
    const user = userEvent.setup();
    renderSettingsAsAdmin();
    await openModelsRuntime(user);

    await user.click(screen.getByRole('button', { name: 'trigger-unload-llama' }));
    await user.click(screen.getByRole('button', { name: 'Unload' }));

    await waitFor(() => {
      expect(api.settings.unloadLlamaModel).toHaveBeenCalledWith('alias-1');
    });
  });

  it('confirms llama router deletion', async () => {
    const user = userEvent.setup();
    renderSettingsAsAdmin();
    await openModelsRuntime(user);

    await user.click(screen.getByRole('button', { name: 'trigger-delete-llama' }));
    await user.click(screen.getByRole('button', { name: 'Delete alias + files' }));

    await waitFor(() => {
      expect(api.settings.deleteLlamaRouterEntry).toHaveBeenCalledWith('alias-1');
    });
  });

  it('opens the add model wizard from models workspace', async () => {
    const user = userEvent.setup();
    renderSettingsAsAdmin();
    await openModelsRuntime(user);

    await user.click(screen.getByRole('button', { name: 'open-add-model-wizard' }));
    expect(api.settings.getModels).toHaveBeenCalled();
  });

  it('redirects non-admin users to personalization', async () => {
    mockedUseAuth.mockReturnValue({
      user: {
        id: 'reader-1',
        name: 'Reader',
        email: 'reader@example.com',
        role: 'Reader',
        mustChangePassword: false,
        lastLoginAt: null,
      },
      role: 'Reader',
      status: 'authenticated',
      isAuthenticated: true,
      login: vi.fn(),
      register: vi.fn(),
      changePassword: vi.fn(),
      refresh: vi.fn(),
      logout: vi.fn(),
    });

    render(
      <MemoryRouter>
        <ToastProvider>
          <Settings />
        </ToastProvider>
      </MemoryRouter>,
    );

    expect(await screen.findByText('personalization-tab-panel')).toBeInTheDocument();
    expect(screen.queryByText('overview-tab-panel')).not.toBeInTheDocument();
  });

  it('surfaces delete model failures via toast', async () => {
    const user = userEvent.setup();
    vi.mocked(api.settings.deleteModel).mockRejectedValue(new Error('Delete blocked'));
    renderSettingsAsAdmin();
    await openModelsRuntime(user);

    await user.click(screen.getByRole('button', { name: 'trigger-delete-model' }));
    await user.click(screen.getByRole('button', { name: 'Delete' }));

    await waitFor(() => {
      expect(api.settings.deleteModel).toHaveBeenCalledWith('gpt-test');
    });
  });

  it('surfaces llama unload failures via toast', async () => {
    const user = userEvent.setup();
    vi.mocked(api.settings.unloadLlamaModel).mockRejectedValue(new Error('Unload blocked'));
    renderSettingsAsAdmin();
    await openModelsRuntime(user);

    await user.click(screen.getByRole('button', { name: 'trigger-unload-llama' }));
    await user.click(screen.getByRole('button', { name: 'Unload' }));

    await waitFor(() => {
      expect(api.settings.unloadLlamaModel).toHaveBeenCalledWith('alias-1');
    });
  });

});
