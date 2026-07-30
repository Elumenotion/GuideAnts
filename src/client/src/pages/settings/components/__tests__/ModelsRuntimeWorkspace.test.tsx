import { describe, expect, it, vi } from 'vitest';
import userEvent from '@testing-library/user-event';
import { render, screen } from '@testing-library/react';
import '@testing-library/jest-dom';
import { ModelsRuntimeWorkspace } from '../ModelsRuntimeWorkspace';

vi.mock('../ModelsTab', () => ({
  ModelsTab: () => <div>models-tab-panel</div>,
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
  modelsLoading: false,
  modelsError: null,
  orderedModels: [],
  deletingModelId: null,
  onRetryLoadModels: vi.fn(),
  onRequestDeleteModel: vi.fn(),
  onCatalogEdited: vi.fn(),
  onOpenAddModelWizard: vi.fn(),
  activeAddOperation: null,
};

describe('ModelsRuntimeWorkspace', () => {
  it('renders catalog and loaded models sub-tabs only', async () => {
    const user = userEvent.setup();

    render(<ModelsRuntimeWorkspace {...baseProps} initialSubTab="catalog" />);
    expect(screen.getByText('models-tab-panel')).toBeInTheDocument();
    expect(screen.queryByText('profiles-tab-panel')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Runtime Profiles' })).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Loaded models' }));
    expect(screen.getByText('local-llama-tab-panel')).toBeInTheDocument();
  });

  it('opens loaded models when focused alias is provided', () => {
    render(<ModelsRuntimeWorkspace {...baseProps} focusedAlias="alias-1" />);
    expect(screen.getByText('local-llama-tab-panel')).toBeInTheDocument();
  });
});
