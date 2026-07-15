import { forwardRef, useImperativeHandle } from 'react';
import { describe, expect, it, vi, beforeEach } from 'vitest';
import userEvent from '@testing-library/user-event';
import { render, screen, waitFor } from '@testing-library/react';
import '@testing-library/jest-dom';
import { CatalogRowEditModal } from '../CatalogRowEditModal';
import { api } from '../../../../../services/api';
import type { SettingsModelDto } from '../../../../../types/settings';

vi.mock('../../../../../services/api', () => ({
  api: {
    settings: {
      updateModel: vi.fn(),
    },
  },
}));

vi.mock('../providers/OpenAiChatForm', () => ({
  OpenAiChatEditForm: () => <div>openai-chat-form</div>,
}));
vi.mock('../providers/OpenAiResponsesForm', () => ({
  OpenAiResponsesEditForm: () => <div>openai-responses-form</div>,
}));
vi.mock('../providers/AzureOpenAiChatForm', () => ({
  AzureOpenAiChatEditForm: () => <div>azure-openai-chat-form</div>,
}));
vi.mock('../providers/AzureOpenAiResponsesForm', () => ({
  AzureOpenAiResponsesEditForm: () => <div>azure-openai-responses-form</div>,
}));
vi.mock('../providers/AnthropicForm', () => ({
  AnthropicEditForm: () => <div>anthropic-form</div>,
}));
const saveRouterPreset = vi.fn();

vi.mock('../providers/LlamaCppForm', () => ({
  LlamaCppEditForm: forwardRef(function MockLlamaCppEditForm(_props, ref) {
    useImperativeHandle(ref, () => ({
      saveRouterPreset,
    }));
    return <div>llama-cpp-form</div>;
  }),
}));
vi.mock('../providers/GoogleGeminiForm', () => ({
  GoogleGeminiEditForm: () => <div>google-gemini-form</div>,
}));
vi.mock('../providers/HuggingFaceInferenceForm', () => ({
  HuggingFaceInferenceEditForm: () => <div>hf-inference-form</div>,
}));
vi.mock('../providers/OpenRouterForm', () => ({
  OpenRouterEditForm: () => <div>openrouter-form</div>,
}));

const openAiModel: SettingsModelDto = {
  modelId: 'gpt-test',
  displayName: 'GPT Test',
  description: 'test model',
  provider: 'openai-chat',
  displayOrder: 1,
  isActive: true,
  created: '2026-01-01T00:00:00Z',
  updated: '2026-01-02T00:00:00Z',
};

const llamaModel: SettingsModelDto = {
  modelId: 'llama/qwen',
  displayName: 'Qwen Local',
  description: 'local model',
  provider: 'llama-cpp',
  displayOrder: 2,
  isActive: true,
  runtimeConfigJson: JSON.stringify({ runtimeProfileId: 'qwen3_6' }),
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

describe('CatalogRowEditModal', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(api.settings.updateModel).mockResolvedValue(undefined as never);
    saveRouterPreset.mockResolvedValue(undefined);
  });

  it('renders provider form and saves catalog edits', async () => {
    const user = userEvent.setup();
    const onClose = vi.fn();
    const onSaved = vi.fn().mockResolvedValue(undefined);

    render(
      <CatalogRowEditModal
        model={openAiModel}
        orderedModels={[openAiModel]}
        profiles={[profile]}
        profilesLoading={false}
        isOpen
        onClose={onClose}
        onSaved={onSaved}
      />,
    );

    expect(screen.getByText('openai-chat-form')).toBeInTheDocument();
    const displayNameInput = screen.getByDisplayValue('GPT Test');
    await user.clear(displayNameInput);
    await user.type(displayNameInput, 'Updated GPT');
    await user.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(() => {
      expect(api.settings.updateModel).toHaveBeenCalledWith(
        'gpt-test',
        expect.objectContaining({ displayName: 'Updated GPT' }),
      );
      expect(onSaved).toHaveBeenCalled();
      expect(onClose).toHaveBeenCalled();
    });
  });

  it('saves router preset before catalog metadata for llama-cpp rows', async () => {
    const user = userEvent.setup();
    const callOrder: string[] = [];
    saveRouterPreset.mockImplementation(async () => {
      callOrder.push('router-preset');
    });
    vi.mocked(api.settings.updateModel).mockImplementation(async () => {
      callOrder.push('catalog');
      return undefined as never;
    });

    render(
      <CatalogRowEditModal
        model={llamaModel}
        orderedModels={[llamaModel]}
        profiles={[]}
        profilesLoading={false}
        isOpen
        onClose={vi.fn()}
        onSaved={vi.fn()}
      />,
    );

    expect(screen.getByText('llama-cpp-form')).toBeInTheDocument();
    const displayNameInput = screen.getByDisplayValue('Qwen Local');
    await user.clear(displayNameInput);
    await user.type(displayNameInput, 'Updated Qwen');
    await user.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(() => {
      expect(saveRouterPreset).toHaveBeenCalled();
      expect(api.settings.updateModel).toHaveBeenCalledWith(
        'llama/qwen',
        expect.objectContaining({ displayName: 'Updated Qwen' }),
      );
      expect(callOrder).toEqual(['router-preset', 'catalog']);
    });
  });

  it('shows save failures', async () => {
    const user = userEvent.setup();
    vi.mocked(api.settings.updateModel).mockRejectedValue(new Error('Save failed'));

    render(
      <CatalogRowEditModal
        model={openAiModel}
        orderedModels={[openAiModel]}
        profiles={[profile]}
        profilesLoading={false}
        isOpen
        onClose={vi.fn()}
        onSaved={vi.fn()}
      />,
    );

    await user.click(screen.getByRole('button', { name: 'Save' }));
    expect(await screen.findByText(/Save failed/i)).toBeInTheDocument();
  });
});
