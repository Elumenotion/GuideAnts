import { describe, expect, it, vi, beforeEach } from 'vitest';
import userEvent from '@testing-library/user-event';
import { render, screen, waitFor } from '@testing-library/react';
import '@testing-library/jest-dom';
import { ToastProvider } from '../../../../components/common/Toast';
import { LocalLlamaRuntimeTab } from '../LocalLlamaRuntimeTab';
import { api } from '../../../../services/api';

vi.mock('../../../../services/api', () => ({
  api: {
    settings: {
      getLlamaRuntimeStatus: vi.fn(),
    },
  },
}));

const inventoryRow = {
  routerModelId: 'alias-1',
  catalogModelIds: ['llama/local'],
  runtimeState: 'unloaded',
  notebookReferenceCount: 0,
  artifactPath: '/models/llama',
  hasModelFile: true,
  hasMmprojFile: false,
};

describe('LocalLlamaRuntimeTab', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(api.settings.getLlamaRuntimeStatus).mockResolvedValue([
      { routerModelId: 'alias-1', loadState: 'idle', isLocked: false },
    ] as never);
  });

  it('renders inventory rows and triggers load actions', async () => {
    const user = userEvent.setup();
    const onLoad = vi.fn().mockResolvedValue(undefined);
    const onRefresh = vi.fn();

    render(
      <ToastProvider>
        <LocalLlamaRuntimeTab
          inventory={[inventoryRow] as never}
          inventoryLoading={false}
          inventoryRefreshing={false}
          inventoryError={null}
          onRefresh={onRefresh}
          onLoad={onLoad}
          onRequestUnload={vi.fn()}
          onRequestDelete={vi.fn()}
          onOpenAddModelWizard={vi.fn()}
          focusedAlias="alias-1"
        />
      </ToastProvider>,
    );

    expect(screen.getByText('alias-1')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Load' }));
    await waitFor(() => expect(onLoad).toHaveBeenCalledWith('alias-1'));
  });

  it('offers attach to catalog for unbound aliases with artifacts', async () => {
    const user = userEvent.setup();
    const onOpenAddModelWizard = vi.fn();

    render(
      <ToastProvider>
        <LocalLlamaRuntimeTab
          inventory={[
            {
              ...inventoryRow,
              routerModelId: 'Qwen3.5-9B-GGUF',
              catalogModelIds: [],
            },
          ] as never}
          inventoryLoading={false}
          inventoryRefreshing={false}
          inventoryError={null}
          onRefresh={vi.fn()}
          onLoad={vi.fn()}
          onRequestUnload={vi.fn()}
          onRequestDelete={vi.fn()}
          onOpenAddModelWizard={onOpenAddModelWizard}
        />
      </ToastProvider>,
    );

    expect(screen.getByText('Not in catalog')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Attach to catalog' }));
    expect(onOpenAddModelWizard).toHaveBeenCalledWith('llama-cpp', 'Qwen3.5-9B-GGUF');
  });

  it('shows unavailable guidance when inventory cannot reach the runtime', () => {
    render(
      <ToastProvider>
        <LocalLlamaRuntimeTab
          inventory={[]}
          inventoryLoading={false}
          inventoryRefreshing={false}
          inventoryError="No local llama server is configured"
          onRefresh={vi.fn()}
          onLoad={vi.fn()}
          onRequestUnload={vi.fn()}
          onRequestDelete={vi.fn()}
          onOpenAddModelWizard={vi.fn()}
        />
      </ToastProvider>,
    );

    expect(screen.getByText(/No local llama server is configured/i)).toBeInTheDocument();
  });
});
