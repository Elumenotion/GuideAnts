import { describe, expect, it, vi, beforeEach } from 'vitest';
import userEvent from '@testing-library/user-event';
import { render, screen } from '@testing-library/react';
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
      updateModel: vi.fn(),
    },
  },
}));

vi.mock('../catalog/providers/OpenAiChatForm', () => ({
  OpenAiChatEditForm: () => <div>openai-chat-form</div>,
}));
vi.mock('../catalog/providers/OpenAiResponsesForm', () => ({
  OpenAiResponsesEditForm: () => <div>openai-responses-form</div>,
}));
vi.mock('../catalog/providers/AzureOpenAiChatForm', () => ({
  AzureOpenAiChatEditForm: () => <div>azure-openai-chat-form</div>,
}));
vi.mock('../catalog/providers/AzureOpenAiResponsesForm', () => ({
  AzureOpenAiResponsesEditForm: () => <div>azure-openai-responses-form</div>,
}));
vi.mock('../catalog/providers/AnthropicForm', () => ({
  AnthropicEditForm: () => <div>anthropic-form</div>,
}));
vi.mock('../catalog/providers/LlamaCppForm', () => ({
  LlamaCppEditForm: () => <div>llama-cpp-form</div>,
}));
vi.mock('../catalog/providers/GoogleGeminiForm', () => ({
  GoogleGeminiEditForm: () => <div>google-gemini-form</div>,
}));
vi.mock('../catalog/providers/HuggingFaceInferenceForm', () => ({
  HuggingFaceInferenceEditForm: () => <div>hf-inference-form</div>,
}));
vi.mock('../catalog/providers/OpenRouterForm', () => ({
  OpenRouterEditForm: () => <div>openrouter-form</div>,
}));

const cloudModel: SettingsModelDto = {
  modelId: 'gpt-test',
  displayName: 'GPT Test',
  provider: 'openai-chat',
  isActive: true,
  created: '2026-01-01T00:00:00Z',
  updated: '2026-01-02T00:00:00Z',
};

const profile = {
  profileId: 'openai_default',
  displayName: 'OpenAI Default',
  description: '',
  providers: ['openai-chat'],
  combineSystemAndDeveloperMessages: false,
  thoughtBlockPattern: '',
  samplingParametersJson: '{}',
  thinkingControlJson: '{}',
  created: '2026-01-01T00:00:00Z',
  updated: '2026-01-02T00:00:00Z',
};

describe('ModelsTab catalog edit modal', () => {
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
    ]);
    vi.mocked(api.settings.updateModel).mockResolvedValue(undefined as never);
  });

  it('opens the catalog row editor from a model row', async () => {
    const user = userEvent.setup();
    const onCatalogEdited = vi.fn().mockResolvedValue(undefined);

    render(
      <ToastProvider>
        <ModelsTab
          modelsLoading={false}
          modelsError={null}
          orderedModels={[cloudModel]}
          deletingModelId={null}
          onRetryLoadModels={vi.fn()}
          onRequestDeleteModel={vi.fn()}
          profiles={[profile]}
          profilesLoading={false}
          onCatalogEdited={onCatalogEdited}
          onOpenAddModel={vi.fn()}
          activeAddOperation={null}
        />
      </ToastProvider>,
    );

    await user.click(screen.getByTitle('Edit model gpt-test'));
    expect(await screen.findByText('openai-chat-form')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Save' }));
    expect(api.settings.updateModel).toHaveBeenCalledWith('gpt-test', expect.any(Object));
  });
});
