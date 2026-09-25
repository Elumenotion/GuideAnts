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
      probeModelContextWindow: vi.fn(),
    },
  },
}));

vi.mock('../NonLocalModelParameterSurfaceEditor', () => ({
  NonLocalModelParameterSurfaceEditor: () => <div>parameter-surface-editor</div>,
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
  combineSystemAndDeveloperMessages: true,
  samplingParametersJson: '{}',
  thinkingControlJson: '{}',
  requestFieldsWhenToolsPresentJson: '{}',
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
  runtimeConfigJson: JSON.stringify({ routerModelId: 'qwen' }),
  combineSystemAndDeveloperMessages: true,
  samplingParametersJson: '{}',
  thinkingControlJson: '{"defaultChoice":"none","choiceActions":{"none":[]}}',
  requestFieldsWhenToolsPresentJson: '{}',
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

  it('renders parameter surface editor for non-local models and saves row-owned fields', async () => {
    const user = userEvent.setup();
    const model: SettingsModelDto = {
      ...openAiModel,
      samplingParametersJson: '{"temperature":{"key":"temperature","label":"Temperature","description":"","min":0,"max":2,"step":0.1,"default":1,"displayOrder":0,"exposedInGuideBuilder":true}}',
      reasoningChoicesJson: '["low","high"]',
    };

    render(
      <CatalogRowEditModal
        model={model}
        orderedModels={[model]}
        isOpen
        onClose={vi.fn()}
        onSaved={vi.fn()}
      />,
    );

    expect(screen.getByText('parameter-surface-editor')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(() => {
      expect(api.settings.updateModel).toHaveBeenCalledWith(
        'gpt-test',
        expect.objectContaining({
          samplingParametersJson: expect.stringContaining('temperature'),
          reasoningChoicesJson: '["low","high"]',
          runtimeConfigJson: undefined,
        }),
      );
    });
  });

  it('renders provider form and saves catalog edits', async () => {
    const user = userEvent.setup();
    const onClose = vi.fn();
    const onSaved = vi.fn().mockResolvedValue(undefined);

    render(
      <CatalogRowEditModal
        model={openAiModel}
        orderedModels={[openAiModel]}
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
        isOpen
        onClose={vi.fn()}
        onSaved={vi.fn()}
      />,
    );

    await user.click(screen.getByRole('button', { name: 'Save' }));
    expect(await screen.findByText(/Save failed/i)).toBeInTheDocument();
  });

  it('renders AI stack fields for llama-cpp rows and round-trips them into RuntimeConfigJson', async () => {
    const user = userEvent.setup();
    const stackedModel: SettingsModelDto = {
      ...llamaModel,
      modelId: 'llama/qwen-max',
      runtimeConfigJson: JSON.stringify({
        routerModelId: 'qwen-max-alias',
        stackBaseUrl: 'http://192.0.2.1:8112',
        stackApiKey: 'existing-key',
      }),
    };

    render(
      <CatalogRowEditModal
        model={stackedModel}
        orderedModels={[stackedModel]}
        isOpen
        onClose={vi.fn()}
        onSaved={vi.fn()}
      />,
    );

    // Pre-populated from the row's RuntimeConfigJson.
    expect(screen.getByDisplayValue('http://192.0.2.1:8112')).toBeInTheDocument();
    expect(screen.getByDisplayValue('existing-key')).toBeInTheDocument();

    // Clear and re-enter the stack URL; leave the key as-is.
    const urlInput = screen.getByDisplayValue('http://192.0.2.1:8112');
    await user.clear(urlInput);
    await user.type(urlInput, 'http://192.168.0.222:8112');
    await user.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(() => {
      expect(api.settings.updateModel).toHaveBeenCalledWith(
        'llama/qwen-max',
        expect.objectContaining({
          runtimeConfigJson: JSON.stringify({
            routerModelId: 'qwen-max-alias',
            stackBaseUrl: 'http://192.168.0.222:8112',
            stackApiKey: 'existing-key',
          }),
        }),
      );
    });
  });

  it('omits AI stack fields for non-llama providers', () => {
    render(
      <CatalogRowEditModal
        model={openAiModel}
        orderedModels={[openAiModel]}
        isOpen
        onClose={vi.fn()}
        onSaved={vi.fn()}
      />,
    );

    expect(screen.queryByText('Stack base URL')).not.toBeInTheDocument();
    expect(screen.queryByText('Stack API key')).not.toBeInTheDocument();
  });

  describe('context window fields', () => {
    const windowModel: SettingsModelDto = {
      ...openAiModel,
      contextWindowTokens: 271828,
      maxOutputTokens: 31415,
    };

    const renderModal = (model: SettingsModelDto) =>
      render(
        <CatalogRowEditModal
          model={model}
          orderedModels={[model]}
          isOpen
          onClose={vi.fn()}
          onSaved={vi.fn()}
        />,
      );

    const savedRequest = () => vi.mocked(api.settings.updateModel).mock.calls[0]![1];

    it('shows the row existing values in both inputs', () => {
      renderModal(windowModel);
      expect(screen.getByLabelText('Context window (tokens)')).toHaveValue('271828');
      expect(screen.getByLabelText('Max output (tokens)')).toHaveValue('31415');
    });

    it('shows empty inputs for a row without values', () => {
      renderModal(openAiModel);
      expect(screen.getByLabelText('Context window (tokens)')).toHaveValue('');
      expect(screen.getByLabelText('Max output (tokens)')).toHaveValue('');
    });

    it('sends an edited context window on save', async () => {
      const user = userEvent.setup();
      renderModal(windowModel);
      const input = screen.getByLabelText('Context window (tokens)');
      await user.clear(input);
      await user.type(input, '314159');
      await user.click(screen.getByRole('button', { name: 'Save' }));

      await waitFor(() => expect(api.settings.updateModel).toHaveBeenCalled());
      expect(savedRequest()).toEqual(
        expect.objectContaining({ contextWindowTokens: 314159, maxOutputTokens: 31415 }),
      );
    });

    it('sends null when the fields are cleared', async () => {
      const user = userEvent.setup();
      renderModal(windowModel);
      await user.clear(screen.getByLabelText('Context window (tokens)'));
      await user.clear(screen.getByLabelText('Max output (tokens)'));
      await user.click(screen.getByRole('button', { name: 'Save' }));

      await waitFor(() => expect(api.settings.updateModel).toHaveBeenCalled());
      expect(savedRequest()).toEqual(
        expect.objectContaining({ contextWindowTokens: null, maxOutputTokens: null }),
      );
    });

    it('does not wipe existing values when saving an unrelated edit', async () => {
      const user = userEvent.setup();
      renderModal(windowModel);
      await user.click(screen.getByRole('checkbox', { name: /active/i }));
      const order = screen.getByDisplayValue('1');
      await user.clear(order);
      await user.type(order, '7');
      await user.click(screen.getByRole('button', { name: 'Save' }));

      await waitFor(() => expect(api.settings.updateModel).toHaveBeenCalled());
      expect(savedRequest()).toEqual(
        expect.objectContaining({
          isActive: false,
          displayOrder: 7,
          contextWindowTokens: 271828,
          maxOutputTokens: 31415,
        }),
      );
    });

    it('always includes both keys, as null, for a row with no values', async () => {
      const user = userEvent.setup();
      renderModal(openAiModel);
      await user.click(screen.getByRole('button', { name: 'Save' }));

      await waitFor(() => expect(api.settings.updateModel).toHaveBeenCalled());
      const request = savedRequest();
      expect(request).toHaveProperty('contextWindowTokens', null);
      expect(request).toHaveProperty('maxOutputTokens', null);
    });

    it.each([['0'], ['-5'], ['12.5'], ['abc']])('sends null for the invalid entry %s', async (entry) => {
      const user = userEvent.setup();
      renderModal(windowModel);
      const context = screen.getByLabelText('Context window (tokens)');
      const output = screen.getByLabelText('Max output (tokens)');
      await user.clear(context);
      await user.type(context, entry);
      await user.clear(output);
      await user.type(output, entry);
      await user.click(screen.getByRole('button', { name: 'Save' }));

      await waitFor(() => expect(api.settings.updateModel).toHaveBeenCalled());
      expect(savedRequest()).toEqual(
        expect.objectContaining({ contextWindowTokens: null, maxOutputTokens: null }),
      );
    });
  });

  describe('fetch from provider', () => {
    const anthropicModel: SettingsModelDto = {
      ...openAiModel,
      modelId: 'claude-haiku-4-5',
      provider: 'anthropic',
      contextWindowTokens: 1000,
      maxOutputTokens: 500,
    };

    const renderModal = (model: SettingsModelDto) =>
      render(
        <CatalogRowEditModal
          model={model}
          orderedModels={[model]}
          isOpen
          onClose={vi.fn()}
          onSaved={vi.fn()}
        />,
      );

    it('fills both inputs from the probe result without saving', async () => {
      const user = userEvent.setup();
      vi.mocked(api.settings.probeModelContextWindow).mockResolvedValue({
        supported: true,
        contextWindowTokens: 200000,
        maxOutputTokens: 64000,
        message: null,
      });
      renderModal(anthropicModel);

      await user.click(screen.getByRole('button', { name: /fetch from provider/i }));

      await waitFor(() =>
        expect(screen.getByLabelText('Context window (tokens)')).toHaveValue('200000'),
      );
      expect(screen.getByLabelText('Max output (tokens)')).toHaveValue('64000');
      expect(api.settings.probeModelContextWindow).toHaveBeenCalledWith('claude-haiku-4-5', 'anthropic');
      expect(api.settings.updateModel).not.toHaveBeenCalled();
    });

    it('shows the message and leaves the inputs alone when no values come back', async () => {
      const user = userEvent.setup();
      vi.mocked(api.settings.probeModelContextWindow).mockResolvedValue({
        supported: true,
        contextWindowTokens: null,
        maxOutputTokens: null,
        message: 'No Anthropic API key is configured.',
      });
      renderModal(anthropicModel);

      await user.click(screen.getByRole('button', { name: /fetch from provider/i }));

      expect(await screen.findByText('No Anthropic API key is configured.')).toBeInTheDocument();
      expect(screen.getByLabelText('Context window (tokens)')).toHaveValue('1000');
      expect(screen.getByLabelText('Max output (tokens)')).toHaveValue('500');
      expect(api.settings.updateModel).not.toHaveBeenCalled();
    });

    it('is absent for a provider that publishes no context window', () => {
      renderModal(openAiModel);
      expect(screen.queryByRole('button', { name: /fetch from provider/i })).not.toBeInTheDocument();
    });
  });
});
