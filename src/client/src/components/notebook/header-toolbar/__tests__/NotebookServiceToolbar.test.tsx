import React from 'react';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import { beforeEach, describe, expect, it, vi } from 'vitest';
import { NotebookServiceToolbar } from '../NotebookServiceToolbar';
import type { NotebookHeaderToolbarDto } from '../../../../types/notebookToolbar';
import { api } from '../../../../services/api';

const mockNavigate = vi.fn();

vi.mock('react-router-dom', () => ({
  useNavigate: () => mockNavigate,
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

function renderToolbar(overrides: Partial<React.ComponentProps<typeof NotebookServiceToolbar>> = {}) {
  return render(
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
      {...overrides}
    />
  );
}

describe('NotebookServiceToolbar', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

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

  it('shows loading placeholder when data is not yet available', () => {
    renderToolbar({ data: null, isLoading: true });
    expect(screen.getByText('Loading...')).toBeInTheDocument();
  });

  it('returns null when not loading and data is missing', () => {
    const { container } = renderToolbar({ data: null, isLoading: false });
    expect(container).toBeEmptyDOMElement();
  });

  it('opens and closes desktop popover panels', async () => {
    const user = userEvent.setup();
    renderToolbar();

    await user.click(screen.getByRole('button', { name: /^chat$/i }));
    expect(screen.getByText(/Workspace controls apply/i)).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: /^chat$/i }));
    expect(screen.queryByText(/Workspace controls apply/i)).not.toBeInTheDocument();
  });

  it('calls refresh when refresh button is clicked', async () => {
    const onRefresh = vi.fn(async () => {});
    renderToolbar({ onRefresh });

    fireEvent.click(screen.getByRole('button', { name: /refresh toolbar/i }));
    await waitFor(() => expect(onRefresh).toHaveBeenCalled());
  });

  it('renders mobile sheet layout and invokes onMobileOpen', async () => {
    const user = userEvent.setup();
    const onMobileOpen = vi.fn();
    renderToolbar({ isMobile: true, onMobileOpen });

    expect(screen.getByTestId('notebook-service-toolbar-mobile')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: /image generation/i }));
    expect(onMobileOpen).toHaveBeenCalled();
    expect(screen.getByRole('heading', { name: /image generation/i })).toBeInTheDocument();
  });

  it('closes open panel on outside click and escape key', async () => {
    const user = userEvent.setup();
    renderToolbar();

    await user.click(screen.getByRole('button', { name: /^chat$/i }));
    expect(screen.getByText(/Workspace controls apply/i)).toBeInTheDocument();

    fireEvent.mouseDown(document.body);
    expect(screen.queryByText(/Workspace controls apply/i)).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: /speech synthesis/i }));
    fireEvent.keyDown(document, { key: 'Escape' });
    expect(screen.queryByText(/Workspace controls apply/i)).not.toBeInTheDocument();
  });

  it('confirms chat runtime unload and refreshes toolbar', async () => {
    const user = userEvent.setup();
    const onRefresh = vi.fn(async () => {});
    const setInFlight = vi.fn();
    vi.mocked(api.projects.notebooks.conversations.unloadLlamaRuntime).mockResolvedValue({} as never);

    renderToolbar({ onRefresh, setInFlight });

    await user.click(screen.getByRole('button', { name: /^chat$/i }));
    await user.click(screen.getByRole('button', { name: /unload selected local chat model/i }));
    await user.click(screen.getByRole('button', { name: /^turn off$/i }));

    await waitFor(() => {
      expect(api.projects.notebooks.conversations.unloadLlamaRuntime).toHaveBeenCalledWith(
        'p1',
        'n1',
        'a1'
      );
      expect(onRefresh).toHaveBeenCalled();
    });
  });
});

