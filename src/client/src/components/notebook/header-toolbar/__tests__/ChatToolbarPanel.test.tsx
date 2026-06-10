import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ChatToolbarPanel } from '../ChatToolbarPanel';
import { api } from '../../../../services/api';

vi.mock('../../../../services/api', () => ({
  api: {
    settings: {
      chatDefaults: {
        get: vi.fn(async () => ({
          rowVersion: 'rv-1',
          defaultModelId: 'gpt-5-mini',
          overrideAllChatModels: true,
          temperature: null,
          topP: null,
          reasoningEffort: 'enabled',
          samplingParametersJson: null,
        })),
        update: vi.fn(async () => ({
          rowVersion: 'rv-2',
          defaultModelId: 'gpt-5-mini',
          overrideAllChatModels: true,
          temperature: null,
          topP: null,
          reasoningEffort: null,
          samplingParametersJson: null,
        })),
      },
    },
    guides: {
      catalogs: {
        models: vi.fn(async () => ([
          {
            modelId: 'gpt-5-mini',
            displayName: 'GPT-5 mini',
            provider: 'azure-openai',
            isActive: true,
            reasoningChoices: ['minimal', 'low', 'medium', 'high'],
          },
          {
            modelId: 'gemini-2.5-flash',
            displayName: 'Gemini 2.5 Flash',
            provider: 'google-gemini-chat',
            isActive: true,
          },
        ])),
      },
    },
    projects: {
      notebooks: {
        conversations: {
          loadLlamaRuntime: vi.fn(async () => ({ operationId: 'op1', state: 'ready' })),
          pollLlamaRuntimeOperation: vi.fn(async () => ({ operationId: 'op1', state: 'ready' })),
        },
      },
    },
  },
}));

describe('ChatToolbarPanel', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.useRealTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('changes global chat default model through settings API when override is enabled', async () => {
    const user = userEvent.setup();
    const onRefresh = vi.fn(async () => {});
    render(
      <ChatToolbarPanel
        chat={{
          status: 'ready',
          summary: 'Chat ready',
          conversationId: 'c1',
          selectedAssistantName: 'assistant',
          effectiveModelId: 'gpt-5-mini',
          effectiveModelDisplayName: 'GPT-5 mini',
          effectiveProvider: 'azure-openai',
          overrideAllChatModels: true,
          supportsLocalRuntimePower: false,
          localRuntimeOn: false,
          modelOptions: [
            { modelId: 'gpt-5-mini', displayName: 'GPT-5 mini', provider: 'azure-openai', isActive: true },
            { modelId: 'gemini-2.5-flash', displayName: 'Gemini 2.5 Flash', provider: 'google-gemini-chat', isActive: true },
          ],
          blockers: [],
          inProgressOperationId: null,
          inProgressState: null,
        }}
        projectId="p1"
        notebookId="n1"
        conversationId="c1"
        inFlight={false}
        setInFlight={vi.fn()}
        onRefresh={onRefresh}
        onOpenSettings={vi.fn()}
        onRequestUnloadConfirm={vi.fn()}
      />
    );

    await user.click(screen.getByRole('option', { name: /Gemini 2.5 Flash/i }));
    expect(api.settings.chatDefaults.update).toHaveBeenCalled();
    expect(api.settings.chatDefaults.update).toHaveBeenCalledWith(
      expect.objectContaining({
        defaultModelId: 'gemini-2.5-flash',
        reasoningEffort: null,
      })
    );
    expect(onRefresh).toHaveBeenCalled();
  });

  it('normalizes the full default parameter payload when changing models', async () => {
    vi.mocked(api.settings.chatDefaults.get).mockResolvedValueOnce({
      rowVersion: 'rv-stale',
      defaultModelId: 'qwen3.6-27b',
      overrideAllChatModels: true,
      temperature: 0.7,
      topP: 0.8,
      reasoningEffort: null,
      samplingParametersJson: '{"temperature":0.7,"top_p":0.8,"mirostat":2}',
    });
    vi.mocked(api.guides.catalogs.models).mockResolvedValue([
      {
        modelId: 'qwen3.6-27b',
        displayName: 'Qwen 3.6 27B',
        provider: 'llama-cpp',
        isActive: true,
        samplingParameterPolicy: [
          {
            key: 'temperature',
            label: 'Temperature',
            description: 'Controls randomness',
            min: 0,
            max: 2,
            step: 0.1,
            recommendedDefault: 0.7,
            displayOrder: 0,
          },
          {
            key: 'top_p',
            label: 'Top P',
            description: 'Controls nucleus sampling',
            min: 0,
            max: 1,
            step: 0.05,
            recommendedDefault: 0.8,
            displayOrder: 1,
          },
          {
            key: 'mirostat',
            label: 'Mirostat',
            description: 'Controls adaptive sampling',
            min: 0,
            max: 2,
            step: 1,
            recommendedDefault: 0,
            displayOrder: 2,
          },
        ],
      },
      {
        modelId: 'gpt-5.2-codex',
        displayName: 'gpt-5.2-codex',
        provider: 'azure-openai-responses',
        isActive: true,
        reasoningChoices: ['minimal', 'low', 'medium', 'high'],
        defaultReasoningChoice: 'medium',
        samplingParameterPolicy: [],
      },
    ]);
    vi.mocked(api.settings.chatDefaults.update).mockImplementationOnce(async (request: any) => ({
      rowVersion: 'rv-normalized',
      defaultModelId: request.defaultModelId,
      overrideAllChatModels: request.overrideAllChatModels,
      temperature: request.temperature,
      topP: request.topP,
      reasoningEffort: request.reasoningEffort,
      samplingParametersJson: request.samplingParametersJson,
    }));

    const user = userEvent.setup();
    const onRefresh = vi.fn(async () => {});
    render(
      <ChatToolbarPanel
        chat={{
          status: 'ready',
          summary: 'Chat ready',
          conversationId: 'c1',
          selectedAssistantName: 'assistant',
          effectiveModelId: 'qwen3.6-27b',
          effectiveModelDisplayName: 'Qwen 3.6 27B',
          effectiveProvider: 'llama-cpp',
          overrideAllChatModels: true,
          supportsLocalRuntimePower: false,
          localRuntimeOn: false,
          modelOptions: [
            { modelId: 'qwen3.6-27b', displayName: 'Qwen 3.6 27B', provider: 'llama-cpp', isActive: true },
            { modelId: 'gpt-5.2-codex', displayName: 'gpt-5.2-codex', provider: 'azure-openai-responses', isActive: true },
          ],
          blockers: [],
          inProgressOperationId: null,
          inProgressState: null,
        }}
        projectId="p1"
        notebookId="n1"
        conversationId="c1"
        inFlight={false}
        setInFlight={vi.fn()}
        onRefresh={onRefresh}
        onOpenSettings={vi.fn()}
        onRequestUnloadConfirm={vi.fn()}
      />
    );

    await user.click(screen.getByRole('option', { name: /gpt-5\.2-codex/i }));

    expect(api.settings.chatDefaults.update).toHaveBeenCalledWith({
      rowVersion: 'rv-stale',
      defaultModelId: 'gpt-5.2-codex',
      overrideAllChatModels: true,
      temperature: null,
      topP: null,
      reasoningEffort: 'medium',
      samplingParametersJson: null,
    });
    expect(onRefresh).toHaveBeenCalled();
  });

  it('shows a load action without unload when selected local model is not loaded', async () => {
    render(
      <ChatToolbarPanel
        chat={{
          status: 'requiresLoad',
          summary: 'Qwen selected. Mistral is currently loaded. Load Qwen to switch.',
          conversationId: 'c1',
          selectedAssistantName: 'assistant',
          effectiveModelId: 'qwen-local',
          effectiveModelDisplayName: 'Qwen',
          effectiveProvider: 'llama-cpp',
          overrideAllChatModels: true,
          supportsLocalRuntimePower: true,
          localRuntimeOn: false,
          modelOptions: [
            { modelId: 'qwen-local', displayName: 'Qwen', provider: 'llama-cpp', isActive: true },
          ],
          blockers: [],
          inProgressOperationId: null,
          inProgressState: null,
        }}
        projectId="p1"
        notebookId="n1"
        conversationId="c1"
        inFlight={false}
        setInFlight={vi.fn()}
        onRefresh={vi.fn(async () => {})}
        onOpenSettings={vi.fn()}
        onRequestUnloadConfirm={vi.fn()}
      />
    );

    await waitFor(() => expect(api.guides.catalogs.models).toHaveBeenCalled());
    expect(screen.getByRole('button', { name: /load selected local chat model/i })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /load selected local chat model/i })).toHaveTextContent('Load model');
    expect(screen.queryByRole('button', { name: /unload selected local chat model/i })).not.toBeInTheDocument();
  });

  it('shows error when chat defaults fail to load', async () => {
    vi.mocked(api.settings.chatDefaults.get).mockRejectedValueOnce(new Error('Defaults unavailable'));

    render(
      <ChatToolbarPanel
        chat={{
          status: 'ready',
          summary: 'Chat ready',
          conversationId: 'c1',
          selectedAssistantName: 'assistant',
          effectiveModelId: 'gpt-5-mini',
          effectiveModelDisplayName: 'GPT-5 mini',
          effectiveProvider: 'azure-openai',
          overrideAllChatModels: true,
          supportsLocalRuntimePower: false,
          localRuntimeOn: false,
          modelOptions: [
            { modelId: 'gpt-5-mini', displayName: 'GPT-5 mini', provider: 'azure-openai', isActive: true },
          ],
          blockers: [],
          inProgressOperationId: null,
          inProgressState: null,
        }}
        projectId="p1"
        notebookId="n1"
        conversationId="c1"
        inFlight={false}
        setInFlight={vi.fn()}
        onRefresh={vi.fn(async () => {})}
        onOpenSettings={vi.fn()}
        onRequestUnloadConfirm={vi.fn()}
      />
    );

    await waitFor(() => expect(screen.getByText('Defaults unavailable')).toBeInTheDocument());
  });

  it('shows error when catalog models fail to load', async () => {
    vi.mocked(api.guides.catalogs.models).mockRejectedValueOnce(new Error('Catalog offline'));

    render(
      <ChatToolbarPanel
        chat={{
          status: 'ready',
          summary: 'Chat ready',
          conversationId: 'c1',
          selectedAssistantName: 'assistant',
          effectiveModelId: 'gpt-5-mini',
          effectiveModelDisplayName: 'GPT-5 mini',
          effectiveProvider: 'azure-openai',
          overrideAllChatModels: true,
          supportsLocalRuntimePower: false,
          localRuntimeOn: false,
          modelOptions: [
            { modelId: 'gpt-5-mini', displayName: 'GPT-5 mini', provider: 'azure-openai', isActive: true },
          ],
          blockers: [],
          inProgressOperationId: null,
          inProgressState: null,
        }}
        projectId="p1"
        notebookId="n1"
        conversationId="c1"
        inFlight={false}
        setInFlight={vi.fn()}
        onRefresh={vi.fn(async () => {})}
        onOpenSettings={vi.fn()}
        onRequestUnloadConfirm={vi.fn()}
      />
    );

    await waitFor(() => expect(screen.getByText('Catalog offline')).toBeInTheDocument());
  });

  it('toggles override-all-chat-models checkbox', async () => {
    const user = userEvent.setup();
    const setInFlight = vi.fn();
    vi.mocked(api.settings.chatDefaults.update).mockResolvedValueOnce({
      rowVersion: 'rv-3',
      defaultModelId: 'gpt-5-mini',
      overrideAllChatModels: false,
      temperature: null,
      topP: null,
      reasoningEffort: null,
      samplingParametersJson: null,
    });

    render(
      <ChatToolbarPanel
        chat={{
          status: 'ready',
          summary: 'Chat ready',
          conversationId: 'c1',
          selectedAssistantName: 'assistant',
          effectiveModelId: 'gpt-5-mini',
          effectiveModelDisplayName: 'GPT-5 mini',
          effectiveProvider: 'azure-openai',
          overrideAllChatModels: true,
          supportsLocalRuntimePower: false,
          localRuntimeOn: false,
          modelOptions: [
            { modelId: 'gpt-5-mini', displayName: 'GPT-5 mini', provider: 'azure-openai', isActive: true },
          ],
          blockers: [],
          inProgressOperationId: null,
          inProgressState: null,
        }}
        projectId="p1"
        notebookId="n1"
        conversationId="c1"
        inFlight={false}
        setInFlight={setInFlight}
        onRefresh={vi.fn(async () => {})}
        onOpenSettings={vi.fn()}
        onRequestUnloadConfirm={vi.fn()}
      />
    );

    await waitFor(() => expect(api.settings.chatDefaults.get).toHaveBeenCalled());
    const checkbox = screen.getByRole('checkbox');
    expect(checkbox).toBeChecked();
    await user.click(checkbox);
    expect(api.settings.chatDefaults.update).toHaveBeenCalledWith(
      expect.objectContaining({ overrideAllChatModels: false })
    );
    expect(setInFlight).toHaveBeenCalledWith(true);
    expect(setInFlight).toHaveBeenCalledWith(false);
  });

  it('does not change model when override is disabled', async () => {
    const user = userEvent.setup();
    vi.mocked(api.settings.chatDefaults.get).mockResolvedValueOnce({
      rowVersion: 'rv-off',
      defaultModelId: 'gpt-5-mini',
      overrideAllChatModels: false,
      temperature: null,
      topP: null,
      reasoningEffort: null,
      samplingParametersJson: null,
    });

    render(
      <ChatToolbarPanel
        chat={{
          status: 'ready',
          summary: 'Chat ready',
          conversationId: 'c1',
          selectedAssistantName: 'assistant',
          effectiveModelId: 'gpt-5-mini',
          effectiveModelDisplayName: 'GPT-5 mini',
          effectiveProvider: 'azure-openai',
          overrideAllChatModels: false,
          supportsLocalRuntimePower: false,
          localRuntimeOn: false,
          modelOptions: [
            { modelId: 'gpt-5-mini', displayName: 'GPT-5 mini', provider: 'azure-openai', isActive: true },
            { modelId: 'gemini-2.5-flash', displayName: 'Gemini 2.5 Flash', provider: 'google-gemini-chat', isActive: true },
          ],
          blockers: [],
          inProgressOperationId: null,
          inProgressState: null,
        }}
        projectId="p1"
        notebookId="n1"
        conversationId="c1"
        inFlight={false}
        setInFlight={vi.fn()}
        onRefresh={vi.fn(async () => {})}
        onOpenSettings={vi.fn()}
        onRequestUnloadConfirm={vi.fn()}
      />
    );

    await waitFor(() => expect(api.settings.chatDefaults.get).toHaveBeenCalled());
    const option = screen.getByRole('option', { name: /Gemini 2.5 Flash/i });
    expect(option).toBeDisabled();
    await user.click(option);
    expect(api.settings.chatDefaults.update).not.toHaveBeenCalled();
  });

  it('polls local runtime load until ready', async () => {
    const user = userEvent.setup();
    const onRefresh = vi.fn(async () => {});
    const setInFlight = vi.fn();
    vi.mocked(api.projects.notebooks.conversations.loadLlamaRuntime).mockResolvedValueOnce({
      operationId: 'op-pending',
      state: 'running',
    });
    vi.mocked(api.projects.notebooks.conversations.pollLlamaRuntimeOperation).mockResolvedValueOnce({
      operationId: 'op-pending',
      state: 'ready',
    });

    render(
      <ChatToolbarPanel
        chat={{
          status: 'requiresLoad',
          summary: 'Load model',
          conversationId: 'c1',
          selectedAssistantName: 'assistant',
          effectiveModelId: 'qwen-local',
          effectiveModelDisplayName: 'Qwen',
          effectiveProvider: 'llama-cpp',
          overrideAllChatModels: true,
          supportsLocalRuntimePower: true,
          localRuntimeOn: false,
          modelOptions: [
            { modelId: 'qwen-local', displayName: 'Qwen', provider: 'llama-cpp', isActive: true },
          ],
          blockers: [],
          inProgressOperationId: null,
          inProgressState: null,
        }}
        projectId="p1"
        notebookId="n1"
        conversationId="c1"
        assistantIdForLlama="asst-1"
        inFlight={false}
        setInFlight={setInFlight}
        onRefresh={onRefresh}
        onOpenSettings={vi.fn()}
        onRequestUnloadConfirm={vi.fn()}
      />
    );

    await waitFor(() => expect(api.guides.catalogs.models).toHaveBeenCalled());
    await user.click(screen.getByRole('button', { name: /load selected local chat model/i }));

    await waitFor(
      () => {
        expect(api.projects.notebooks.conversations.loadLlamaRuntime).toHaveBeenCalledWith('p1', 'n1', 'asst-1');
        expect(api.projects.notebooks.conversations.pollLlamaRuntimeOperation).toHaveBeenCalled();
        expect(onRefresh).toHaveBeenCalled();
      },
      { timeout: 5000 }
    );
    expect(setInFlight).toHaveBeenCalledWith(true);
    expect(setInFlight).toHaveBeenCalledWith(false);
  }, 10000);

  it('shows switching label when runtime operation is in progress', async () => {
    render(
      <ChatToolbarPanel
        chat={{
          status: 'loading',
          summary: 'Switching model',
          conversationId: 'c1',
          selectedAssistantName: 'assistant',
          effectiveModelId: 'qwen-local',
          effectiveModelDisplayName: 'Qwen',
          effectiveProvider: 'llama-cpp',
          overrideAllChatModels: true,
          supportsLocalRuntimePower: true,
          localRuntimeOn: false,
          modelOptions: [
            { modelId: 'qwen-local', displayName: 'Qwen', provider: 'llama-cpp', isActive: true },
          ],
          blockers: [],
          inProgressOperationId: 'op-1',
          inProgressState: 'running',
        }}
        projectId="p1"
        notebookId="n1"
        conversationId="c1"
        inFlight={false}
        setInFlight={vi.fn()}
        onRefresh={vi.fn(async () => {})}
        onOpenSettings={vi.fn()}
        onRequestUnloadConfirm={vi.fn()}
      />
    );

    await waitFor(() => expect(api.guides.catalogs.models).toHaveBeenCalled());
    expect(screen.getByText('Switching...')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /load selected local chat model/i })).toBeDisabled();
  });

  it('calls onOpenSettings when settings link clicked', async () => {
    const user = userEvent.setup();
    const onOpenSettings = vi.fn();
    render(
      <ChatToolbarPanel
        chat={{
          status: 'ready',
          summary: 'Chat ready',
          conversationId: 'c1',
          selectedAssistantName: 'assistant',
          effectiveModelId: 'gpt-5-mini',
          effectiveModelDisplayName: 'GPT-5 mini',
          effectiveProvider: 'azure-openai',
          overrideAllChatModels: true,
          supportsLocalRuntimePower: false,
          localRuntimeOn: false,
          modelOptions: [
            { modelId: 'gpt-5-mini', displayName: 'GPT-5 mini', provider: 'azure-openai', isActive: true },
          ],
          blockers: [],
          inProgressOperationId: null,
          inProgressState: null,
        }}
        projectId="p1"
        notebookId="n1"
        conversationId="c1"
        inFlight={false}
        setInFlight={vi.fn()}
        onRefresh={vi.fn(async () => {})}
        onOpenSettings={onOpenSettings}
        onRequestUnloadConfirm={vi.fn()}
        showWorkspaceCopy={false}
      />
    );

    await user.click(screen.getByRole('button', { name: /open in settings/i }));
    expect(onOpenSettings).toHaveBeenCalled();
    expect(screen.queryByText(/workspace controls/i)).not.toBeInTheDocument();
  });

  it('shows update error when model change fails', async () => {
    const user = userEvent.setup();
    vi.mocked(api.settings.chatDefaults.update).mockRejectedValueOnce(new Error('Update rejected'));

    render(
      <ChatToolbarPanel
        chat={{
          status: 'ready',
          summary: 'Chat ready',
          conversationId: 'c1',
          selectedAssistantName: 'assistant',
          effectiveModelId: 'gpt-5-mini',
          effectiveModelDisplayName: 'GPT-5 mini',
          effectiveProvider: 'azure-openai',
          overrideAllChatModels: true,
          supportsLocalRuntimePower: false,
          localRuntimeOn: false,
          modelOptions: [
            { modelId: 'gpt-5-mini', displayName: 'GPT-5 mini', provider: 'azure-openai', isActive: true },
            { modelId: 'gemini-2.5-flash', displayName: 'Gemini 2.5 Flash', provider: 'google-gemini-chat', isActive: true },
          ],
          blockers: [],
          inProgressOperationId: null,
          inProgressState: null,
        }}
        projectId="p1"
        notebookId="n1"
        conversationId="c1"
        inFlight={false}
        setInFlight={vi.fn()}
        onRefresh={vi.fn(async () => {})}
        onOpenSettings={vi.fn()}
        onRequestUnloadConfirm={vi.fn()}
      />
    );

    await user.click(screen.getByRole('option', { name: /Gemini 2.5 Flash/i }));
    await waitFor(() => expect(screen.getByText('Update rejected')).toBeInTheDocument());
  });

  it('shows loaded state and unload action when selected local model is loaded', async () => {
    render(
      <ChatToolbarPanel
        chat={{
          status: 'ready',
          summary: 'Qwen selected. Local model loaded.',
          conversationId: 'c1',
          selectedAssistantName: 'assistant',
          effectiveModelId: 'qwen-local',
          effectiveModelDisplayName: 'Qwen',
          effectiveProvider: 'llama-cpp',
          overrideAllChatModels: true,
          supportsLocalRuntimePower: true,
          localRuntimeOn: true,
          modelOptions: [
            { modelId: 'qwen-local', displayName: 'Qwen', provider: 'llama-cpp', isActive: true },
          ],
          blockers: [],
          inProgressOperationId: null,
          inProgressState: null,
        }}
        projectId="p1"
        notebookId="n1"
        conversationId="c1"
        inFlight={false}
        setInFlight={vi.fn()}
        onRefresh={vi.fn(async () => {})}
        onOpenSettings={vi.fn()}
        onRequestUnloadConfirm={vi.fn()}
      />
    );

    await waitFor(() => expect(api.guides.catalogs.models).toHaveBeenCalled());
    expect(screen.getByRole('button', { name: /selected local chat model is loaded/i })).toHaveTextContent('Loaded');
    expect(screen.getByRole('button', { name: /unload selected local chat model/i })).toHaveTextContent('Unload');
  });
});
