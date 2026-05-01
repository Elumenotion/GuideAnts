import { render, screen } from '@testing-library/react';

import { describe, expect, it, vi } from 'vitest';
import { NotebookServiceToolbar } from '../NotebookServiceToolbar';
import type { NotebookHeaderToolbarDto } from '../../../../types/notebookToolbar';

vi.mock('react-router-dom', () => ({
  useNavigate: () => vi.fn(),
}));

vi.mock('../../../common/Toast', () => ({
  useToast: () => ({ showToast: vi.fn() }),
}));

vi.mock('../../../../services/api', () => ({
  api: {
    projects: {
      notebooks: {
        conversations: {
          unloadLlamaRuntime: vi.fn(),
        },
      },
    },
  },
}));

function makeToolbar(): NotebookHeaderToolbarDto {
  return {
    generatedUtc: new Date().toISOString(),
    chat: {
      status: 'ready',
      summary: 'Chat ready',
      conversationId: 'conv-1',
      selectedAssistantName: 'assistant',
      effectiveModelId: 'gpt-5-mini',
      effectiveModelDisplayName: 'GPT-5 mini',
      effectiveProvider: 'azure-openai',
      overrideAllChatModels: false,
      supportsLocalRuntimePower: true,
      localRuntimeOn: true,
      modelOptions: [{ modelId: 'gpt-5-mini', displayName: 'GPT-5 mini', provider: 'azure-openai', isActive: true }],
      blockers: [],
      inProgressOperationId: null,
      inProgressState: null,
    },
    services: [
      {
        serviceId: 'ImageGeneration',
        displayName: 'Image Generation',
        kind: 'image',
        status: 'ready',
        summary: 'ready',
        activeProviderId: 'ImageGeneration.LocalSd.Http',
        activeProviderLabel: 'Local',
        supportsLocalRuntimePower: true,
        localRuntimeOn: true,
        providerOptions: [],
        selection: null,
        blockers: [],
        localModelOptions: [],
        inProgressOperationId: null,
        inProgressState: null,
      },
      {
        serviceId: 'SpeechSynthesis',
        displayName: 'Speech Synthesis',
        kind: 'tts',
        status: 'ready',
        summary: 'ready',
        activeProviderId: 'SpeechSynthesis.LocalTts.Http',
        activeProviderLabel: 'Local',
        supportsLocalRuntimePower: true,
        localRuntimeOn: true,
        providerOptions: [],
        selection: null,
        blockers: [],
        localModelOptions: [],
        inProgressOperationId: null,
        inProgressState: null,
      },
      {
        serviceId: 'SpeechTranscription',
        displayName: 'Speech Transcription',
        kind: 'asr',
        status: 'ready',
        summary: 'ready',
        activeProviderId: 'SpeechTranscription.LocalAsr.Http',
        activeProviderLabel: 'Local',
        supportsLocalRuntimePower: true,
        localRuntimeOn: true,
        providerOptions: [],
        selection: null,
        blockers: [],
        localModelOptions: [],
        inProgressOperationId: null,
        inProgressState: null,
      },
    ],
  };
}

describe('NotebookServiceToolbar', () => {
  it('renders services in required order and excludes out-of-scope services', async () => {
    render(
      <NotebookServiceToolbar
        projectId="p1"
        notebookId="n1"
        conversationId="c1"
        data={makeToolbar()}
        isLoading={false}
        isMobile={false}
        onRefresh={vi.fn(async () => {})}
        inFlight={false}
        setInFlight={vi.fn()}
        assistantByName={{ assistant: { id: 'a1' } }}
      />
    );

    expect(screen.getByRole('button', { name: /^chat$/i })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /image generation/i })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /speech synthesis \(tts\)/i })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /speech transcription \(asr\)/i })).toBeInTheDocument();
    expect(screen.queryByText(/Embeddings/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/Document Intelligence/i)).not.toBeInTheDocument();
  });

});
