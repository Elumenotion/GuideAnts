import { describe, expect, it, vi } from 'vitest';
import userEvent from '@testing-library/user-event';
import { render, screen } from '@testing-library/react';
import '@testing-library/jest-dom';
import { ModelsRuntimeWorkspace } from '../ModelsRuntimeWorkspace';
import { createEmptyProfileForm } from '../../utils';

vi.mock('../ModelsTab', () => ({
  ModelsTab: () => <div>models-tab-panel</div>,
}));
vi.mock('../ProfilesTab', () => ({
  ProfilesTab: () => <div>profiles-tab-panel</div>,
}));
vi.mock('../LocalLlamaRuntimeTab', () => ({
  LocalLlamaRuntimeTab: () => <div>local-llama-tab-panel</div>,
}));

const baseProps = {
  llamaInventory: [],
  llamaInventoryLoading: false,
  llamaInventoryRefreshing: false,
  llamaInventoryError: null,
  onRefreshLlamaInventory: vi.fn(),
  onLoadLlamaModel: vi.fn(),
  onRequestUnloadLlamaRouter: vi.fn(),
  onRequestDeleteLlamaRouter: vi.fn(),
  profilesLoading: false,
  profiles: [],
  modelsLoading: false,
  modelsError: null,
  orderedModels: [],
  deletingModelId: null,
  onRetryLoadModels: vi.fn(),
  onRequestDeleteModel: vi.fn(),
  onCatalogEdited: vi.fn(),
  onOpenAddModelWizard: vi.fn(),
  activeAddOperation: null,
  profileDialogOpen: false,
  editingProfileId: null,
  profileForm: createEmptyProfileForm(),
  profileSaving: false,
  profilesError: null,
  deletingProfileId: null,
  onProfileFormChange: vi.fn(),
  onOpenCreateProfile: vi.fn(),
  onImportProfile: vi.fn(),
  onResetProfileForm: vi.fn(),
  onSaveProfile: vi.fn(),
  onRetryLoadProfiles: vi.fn(),
  onEditProfile: vi.fn(),
  onRequestDeleteProfile: vi.fn(),
  onInsertRuntimeProfileTemplate: vi.fn(),
};

describe('ModelsRuntimeWorkspace', () => {
  it('switches catalog, profiles, and local llama sub-tabs', async () => {
    const user = userEvent.setup();

    render(<ModelsRuntimeWorkspace {...baseProps} initialSubTab="catalog" />);
    expect(screen.getByText('models-tab-panel')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Runtime Profiles' }));
    expect(screen.getByText('profiles-tab-panel')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Local Llama Runtime' }));
    expect(screen.getByText('local-llama-tab-panel')).toBeInTheDocument();
  });

  it('opens local llama when focused alias is provided', () => {
    render(<ModelsRuntimeWorkspace {...baseProps} focusedAlias="alias-1" />);
    expect(screen.getByText('local-llama-tab-panel')).toBeInTheDocument();
  });
});
