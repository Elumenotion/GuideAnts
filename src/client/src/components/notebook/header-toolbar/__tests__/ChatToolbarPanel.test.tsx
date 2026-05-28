import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
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
