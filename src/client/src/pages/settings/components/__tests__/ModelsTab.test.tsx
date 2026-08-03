import { describe, expect, it, vi, beforeEach } from 'vitest';
import userEvent from '@testing-library/user-event';
import { render, screen, waitFor } from '@testing-library/react';
import '@testing-library/jest-dom';
import { ToastProvider } from '../../../../components/common/Toast';
import { ModelsTab } from '../ModelsTab';
import { api } from '../../../../services/api';
import type { SettingsModelDto } from '../../../../types/settings';

vi.mock('../../../../services/api', () => ({
  api: {
    settings: {
      routing: {
        getChatTargetsPreflight: vi.fn(),
      },
    },
  },
}));

vi.mock('../catalog/CatalogRowEditModal', () => ({
  CatalogRowEditModal: () => null,
}));

const llamaModel: SettingsModelDto = {
  modelId: 'llama/local',
  displayName: 'Local Llama',
  provider: 'llama-cpp',
  runtimeConfigJson: JSON.stringify({
    routerModelId: 'alias-1',
  }),
  isActive: true,
  created: '2026-01-01T00:00:00Z',
  updated: '2026-01-02T00:00:00Z',
};

const cloudModel: SettingsModelDto = {
  modelId: 'gpt-test',
  displayName: 'GPT Test',
  provider: 'openai-chat',
  isActive: true,
  created: '2026-01-01T00:00:00Z',
  updated: '2026-01-02T00:00:00Z',
};

function renderModelsTab(overrides: Partial<React.ComponentProps<typeof ModelsTab>> = {}) {
  return render(
    <ToastProvider>
      <ModelsTab
        modelsLoading={false}
        modelsError={null}
        orderedModels={[cloudModel, llamaModel]}
        deletingModelId={null}
        onRetryLoadModels={vi.fn()}
        onRequestDeleteModel={vi.fn()}
        onCatalogEdited={vi.fn().mockResolvedValue(undefined)}
        onOpenAddModel={vi.fn()}
        activeModelOperation={null}
        onModelOperationStarted={vi.fn()}
        {...overrides}
      />
    </ToastProvider>,
  );
}

describe('ModelsTab', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(api.settings.routing.getChatTargetsPreflight).mockResolvedValue([
      {
        modelId: 'gpt-test',
        provider: 'openai-chat',
        status: 'ready',
        blockers: [],
        assistantUsageCount: 0,
        referenceKind: 'catalog',
      },
      {
        modelId: 'llama/local',
        provider: 'llama-cpp',
        status: 'blocked',
        blockers: ["RUNTIME_STATE 'unloaded'"],
        assistantUsageCount: 0,
        referenceKind: 'catalog',
      },
    ]);
  });

  it('renders catalog rows and readiness badges', async () => {
    renderModelsTab();

    expect(screen.getByText('GPT Test')).toBeInTheDocument();
    expect(screen.getByText('Local Llama')).toBeInTheDocument();
    expect(await screen.findByText('Ready')).toBeInTheDocument();
    expect(await screen.findByText('Not loaded')).toBeInTheDocument();
  });

  it('opens add-model flow and retries on error', async () => {
    const user = userEvent.setup();
    const onOpenAddModel = vi.fn();
    const onRetryLoadModels = vi.fn();

    const { rerender } = renderModelsTab({ onOpenAddModel, onRetryLoadModels });

    await user.click(screen.getByRole('button', { name: /Add Model/i }));
    expect(onOpenAddModel).toHaveBeenCalled();

    rerender(
      <ToastProvider>
        <ModelsTab
          modelsLoading={false}
          modelsError="Failed to load models"
          orderedModels={[]}
          deletingModelId={null}
          onRetryLoadModels={onRetryLoadModels}
          onRequestDeleteModel={vi.fn()}
          onCatalogEdited={vi.fn()}
          onOpenAddModel={vi.fn()}
          activeModelOperation={null}
          onModelOperationStarted={vi.fn()}
        />
      </ToastProvider>,
    );

    await user.click(screen.getByRole('button', { name: /Retry/i }));
    expect(onRetryLoadModels).toHaveBeenCalled();
  });

  it('requests delete for a model row', async () => {
    const user = userEvent.setup();
    const onRequestDeleteModel = vi.fn();
    renderModelsTab({ onRequestDeleteModel });

    await waitFor(() => expect(screen.getByText('GPT Test')).toBeInTheDocument());
    await user.click(screen.getByTitle('Delete model gpt-test'));
    expect(onRequestDeleteModel).toHaveBeenCalledWith('gpt-test');
  });

  it('shows active add-operation banner and llama runtime badges', async () => {
    const loadedLlama: SettingsModelDto = {
      ...llamaModel,
      modelId: 'llama/loaded',
      displayName: 'Loaded Llama',
      runtimeConfigJson: JSON.stringify({
        routerModelId: 'alias-loaded',
      }),
    };
    const invalidLlama: SettingsModelDto = {
      ...llamaModel,
      modelId: 'llama/broken',
      displayName: 'Broken Llama',
      runtimeConfigJson: '{not-json',
    };

    renderModelsTab({
      orderedModels: [cloudModel, llamaModel, loadedLlama, invalidLlama],
      activeModelOperation: {
        operationId: 'op-1',
        routerModelId: 'alias-1',
        catalogModelId: 'llama/local',
        kind: 'add',
        pollRoute: 'downloads',
      },
      llamaInventory: [
        {
          routerModelId: 'alias-1',
          runtimeState: 'loaded',
          modelPath: '/models/llama.gguf',
          hasModelFile: true,
          hasMmprojFile: false,
          catalogModelIds: ['llama/local'],
          notebookReferenceCount: 0,
        },
        {
          routerModelId: 'alias-loaded',
          runtimeState: 'loaded',
          modelPath: '/models/loaded.gguf',
          hasModelFile: true,
          hasMmprojFile: false,
          catalogModelIds: ['llama/loaded'],
          notebookReferenceCount: 0,
        },
      ],
      llamaInventoryLoading: false,
    });

    expect(
      screen.getByText(/Add operation in progress for/i).textContent,
    ).toContain('llama/local');
    expect(await screen.findByText('Installing…')).toBeInTheDocument();
    expect(await screen.findByText('Loaded')).toBeInTheDocument();
    expect(await screen.findByText('Invalid local JSON')).toBeInTheDocument();
  });

  it('highlights a focused catalog row when deep-linked', async () => {
    const scrollIntoView = vi.fn();
    HTMLElement.prototype.scrollIntoView = scrollIntoView;

    renderModelsTab({ focusedModelId: 'gpt-test' });

    await waitFor(() => {
      expect(scrollIntoView).toHaveBeenCalled();
    });
  });
});
