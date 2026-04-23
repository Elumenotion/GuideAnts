import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
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
          reasoningEffort: null,
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
          modelOptions: [{ modelId: 'gpt-5-mini', displayName: 'GPT-5 mini', provider: 'azure-openai', isActive: true }],
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

    await user.click(screen.getByRole('option', { name: /GPT-5 mini/i }));
    expect(api.settings.chatDefaults.update).toHaveBeenCalled();
    expect(onRefresh).toHaveBeenCalled();
  });
});
