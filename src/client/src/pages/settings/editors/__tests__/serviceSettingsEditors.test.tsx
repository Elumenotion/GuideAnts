import { beforeEach, describe, expect, it, vi } from 'vitest';
import userEvent from '@testing-library/user-event';
import { render, screen } from '@testing-library/react';
import type { ProviderEditorStateDto, ServiceEditorStateDto } from '../../../../types/settings';
import { EmbeddingsEditor } from '../embeddings/EmbeddingsEditor';
import { ImageGenerationEditor } from '../image-generation/ImageGenerationEditor';
import { SpeechSynthesisEditor } from '../speech-synthesis/SpeechSynthesisEditor';
import { SpeechTranscriptionEditor } from '../speech-transcription/SpeechTranscriptionEditor';
import { DocumentIntelligenceEditor } from '../document-intelligence/DocumentIntelligenceEditor';

function makeProvider(overrides: Partial<ProviderEditorStateDto> = {}): ProviderEditorStateDto {
  return {
    providerId: 'Embeddings.Local',
    providerKind: 'Local',
    providerSection: 'LocalEmbeddings',
    hasExplicitMode: true,
    isDefaultMode: true,
    connectionConfigured: true,
    connectionMissingFields: [],
    canActivate: true,
    activationBlockers: [],
    fields: {
      Endpoint: { name: 'Endpoint', value: 'http://localhost:8111', isSecret: false, hasValue: true },
    },
    runtimeDependencies: [],
    operativeFields: ['Endpoint'],
    diagnosticFields: [],
    fieldMetadata: [],
    ...overrides,
  };
}

function makeController(serviceId: string, providerOverrides?: Partial<ProviderEditorStateDto>) {
  const provider = makeProvider(providerOverrides);
  const state: ServiceEditorStateDto = {
    serviceId,
    activeProviderId: provider.providerId,
    providers: [provider],
    readiness: { status: 'ready', blockers: [], warnings: [] },
  };

  return {
    state,
    loading: false,
    error: null,
    saving: false,
    fieldErrors: {},
    draft: {
      activeProviderId: provider.providerId,
      activeDraft: {},
      switchProvider: vi.fn(),
      patchActiveDraft: vi.fn(),
    },
    selectedProvider: provider,
    persistedActiveLabel: 'Local',
    editingProviderLabel: null,
    providerOptions: [
      {
        providerId: provider.providerId,
        displayName: 'Local',
        kind: 'Local',
        hasExplicitMode: true,
        connectionConfigured: true,
        canActivate: true,
      },
    ],
    save: vi.fn(async () => true),
    clearFieldError: vi.fn(),
  };
}

vi.mock('../../state/useServiceEditorController', () => ({
  useServiceEditorController: vi.fn(),
}));

vi.mock('../../../../services/api', () => ({
  api: {
    settings: {
      rebuildEmbeddings: vi.fn(),
    },
  },
}));

vi.mock('../embeddings/EmbRuntimeManager', () => ({
  EmbRuntimeManager: ({ onModelAutoLoaded }: { onModelAutoLoaded?: (modelRef: string) => void }) => (
    <button type="button" onClick={() => onModelAutoLoaded?.('local/model')}>
      trigger-model-auto-loaded
    </button>
  ),
}));
vi.mock('../image-generation/ImageBundleManager', () => ({
  ImageBundleManager: () => <div>image-bundle-manager</div>,
}));
vi.mock('../speech-synthesis/TtsModelManager', () => ({
  TtsModelManager: () => <div>tts-model-manager</div>,
}));
vi.mock('../speech-transcription/AsrModelManager', () => ({
  AsrModelManager: () => <div>asr-model-manager</div>,
}));

import { useServiceEditorController } from '../../state/useServiceEditorController';
import { api } from '../../../../services/api';

describe('service settings editors', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('EmbeddingsEditor renders ready state for local providers', () => {
    vi.mocked(useServiceEditorController).mockReturnValue(
      makeController('Embeddings') as ReturnType<typeof useServiceEditorController>,
    );

    render(<EmbeddingsEditor />);
    expect(screen.getByText('Embeddings')).toBeInTheDocument();
    expect(screen.getByText('emb-runtime-manager')).toBeInTheDocument();
  });

  it('ImageGenerationEditor renders ready state', () => {
    vi.mocked(useServiceEditorController).mockReturnValue(
      makeController('ImageGeneration', { providerId: 'ImageGeneration.Local.Sd' }) as ReturnType<
        typeof useServiceEditorController
      >,
    );

    render(<ImageGenerationEditor />);
    expect(screen.getByText('Image Generation')).toBeInTheDocument();
    expect(screen.getByText('image-bundle-manager')).toBeInTheDocument();
  });

  it('SpeechSynthesisEditor renders ready state', () => {
    vi.mocked(useServiceEditorController).mockReturnValue(
      makeController('SpeechSynthesis', { providerId: 'SpeechSynthesis.Local.Tts' }) as ReturnType<
        typeof useServiceEditorController
      >,
    );

    render(<SpeechSynthesisEditor />);
    expect(screen.getByText('Speech Synthesis')).toBeInTheDocument();
    expect(screen.getByText('tts-model-manager')).toBeInTheDocument();
  });

  it('SpeechTranscriptionEditor renders ready state', () => {
    vi.mocked(useServiceEditorController).mockReturnValue(
      makeController('SpeechTranscription', { providerId: 'SpeechTranscription.Local.Stt' }) as ReturnType<
        typeof useServiceEditorController
      >,
    );

    render(<SpeechTranscriptionEditor />);
    expect(screen.getByText('Speech Transcription')).toBeInTheDocument();
    expect(screen.getByText('asr-model-manager')).toBeInTheDocument();
  });

  it('DocumentIntelligenceEditor renders local guidance', () => {
    vi.mocked(useServiceEditorController).mockReturnValue(
      makeController('DocumentIntelligence', {
        providerId: 'DocumentIntelligence.Local.Docling',
        providerKind: 'Local',
      }) as ReturnType<typeof useServiceEditorController>,
    );

    render(<DocumentIntelligenceEditor />);
    expect(screen.getByText(/Docling engine controls/i)).toBeInTheDocument();
  });

  it('EmbeddingsEditor queues a manual vector rebuild', async () => {
    const user = userEvent.setup();
    vi.mocked(api.settings.rebuildEmbeddings).mockResolvedValue({
      jobId: 'job-1',
      status: 'queued',
    } as never);
    vi.mocked(useServiceEditorController).mockReturnValue(
      makeController('Embeddings') as ReturnType<typeof useServiceEditorController>,
    );

    render(<EmbeddingsEditor />);
    await user.click(screen.getByRole('button', { name: 'Rebuild vectors' }));
    await user.click(screen.getByRole('button', { name: 'Queue rebuild' }));

    expect(api.settings.rebuildEmbeddings).toHaveBeenCalled();
    expect(await screen.findByText(/job-1/i)).toBeInTheDocument();
  });

  it('shows loading placeholders when controller is loading', () => {
    vi.mocked(useServiceEditorController).mockReturnValue({
      ...makeController('Embeddings'),
      loading: true,
      state: null,
      selectedProvider: null,
    } as ReturnType<typeof useServiceEditorController>);

    render(<EmbeddingsEditor />);
    expect(screen.getByText(/Loading Embeddings settings/i)).toBeInTheDocument();
  });

  it('SpeechSynthesisEditor renders cloud synthesis guidance', () => {
    vi.mocked(useServiceEditorController).mockReturnValue(
      makeController('SpeechSynthesis', {
        providerId: 'SpeechSynthesis.AzureSpeech.Ssml',
        providerKind: 'Cloud',
      }) as ReturnType<typeof useServiceEditorController>,
    );

    render(<SpeechSynthesisEditor />);
    expect(screen.getByText(/SpeakSsmlAsync/i)).toBeInTheDocument();
  });

  it('SpeechTranscriptionEditor saves through the controller', async () => {
    const user = userEvent.setup();
    const save = vi.fn(async () => true);
    vi.mocked(useServiceEditorController).mockReturnValue({
      ...makeController('SpeechTranscription', { providerId: 'SpeechTranscription.Local.Stt' }),
      save,
    } as ReturnType<typeof useServiceEditorController>);

    render(<SpeechTranscriptionEditor />);
    await user.click(screen.getByRole('button', { name: 'Save' }));
    expect(save).toHaveBeenCalled();
  });

  it('EmbeddingsEditor shows error state when controller has no provider', () => {
    vi.mocked(useServiceEditorController).mockReturnValue({
      ...makeController('Embeddings'),
      state: null,
      selectedProvider: null,
      error: 'Embeddings unavailable',
    } as ReturnType<typeof useServiceEditorController>);

    render(<EmbeddingsEditor />);
    expect(screen.getByText('Embeddings unavailable')).toBeInTheDocument();
  });

  it('EmbeddingsEditor surfaces rebuild conflict responses', async () => {
    const user = userEvent.setup();
    vi.mocked(api.settings.rebuildEmbeddings).mockRejectedValue({
      status: 409,
      body: { jobId: 'job-running', status: 'running' },
    });
    vi.mocked(useServiceEditorController).mockReturnValue(
      makeController('Embeddings') as ReturnType<typeof useServiceEditorController>,
    );

    render(<EmbeddingsEditor />);
    await user.click(screen.getByRole('button', { name: 'Rebuild vectors' }));
    await user.click(screen.getByRole('button', { name: 'Queue rebuild' }));

    expect(await screen.findByText(/job-running/i)).toBeInTheDocument();
  });

  it('EmbeddingsEditor prompts to rebuild after model auto-load', async () => {
    const user = userEvent.setup();
    const save = vi.fn(async () => true);
    vi.mocked(useServiceEditorController).mockReturnValue({
      ...makeController('Embeddings'),
      save,
    } as ReturnType<typeof useServiceEditorController>);

    render(<EmbeddingsEditor />);
    await user.click(screen.getByRole('button', { name: 'trigger-model-auto-loaded' }));
    expect(save).toHaveBeenCalled();
    expect(await screen.findByText(/Reindex Recommended/i)).toBeInTheDocument();
  });

  it('EmbeddingsEditor prompts to rebuild when model id changes', async () => {
    const user = userEvent.setup();
    const save = vi.fn(async () => true);
    vi.mocked(useServiceEditorController).mockReturnValue({
      ...makeController('Embeddings', {
        fields: {
          Endpoint: { name: 'Endpoint', value: 'http://localhost:8111', isSecret: false, hasValue: true },
          ModelId: { name: 'ModelId', value: 'old-model', isSecret: false, hasValue: true },
        },
      }),
      draft: {
        activeProviderId: 'Embeddings.Local',
        activeDraft: { ModelId: 'new-model' },
        switchProvider: vi.fn(),
        patchActiveDraft: vi.fn(),
      },
      save,
    } as ReturnType<typeof useServiceEditorController>);

    render(<EmbeddingsEditor />);
    await user.click(screen.getByRole('button', { name: 'Save' }));
    expect(await screen.findByText(/Reindex Required/i)).toBeInTheDocument();
  });

  it.each([
    ['Embeddings.AzureOpenAI.Embedding', /Microsoft Foundry embedding requests/i],
    ['Embeddings.Google.Embedding', /Gemini API connection/i],
    ['Embeddings.HuggingFace.Inference', /Hugging Face embeddings/i],
    ['Embeddings.OpenRouter.Embeddings', /OpenRouter embeddings/i],
    ['Embeddings.OpenAI.Embedding', /text-embedding-3-small/i],
    ['Embeddings.Unknown.Cloud', /Cloud embeddings use the selected provider/i],
  ] as const)('EmbeddingsEditor renders cloud guidance for %s', (providerId, matcher) => {
    vi.mocked(useServiceEditorController).mockReturnValue(
      makeController('Embeddings', { providerId, providerKind: 'Cloud' }) as ReturnType<
        typeof useServiceEditorController
      >,
    );

    render(<EmbeddingsEditor />);
    expect(screen.getByText(matcher)).toBeInTheDocument();
    expect(screen.queryByText('trigger-model-auto-loaded')).not.toBeInTheDocument();
  });

  it('EmbeddingsEditor renders operational dependencies', () => {
    vi.mocked(useServiceEditorController).mockReturnValue(
      makeController('Embeddings', {
        runtimeDependencies: [{ key: 'Embeddings:Endpoint', hasValue: true, currentValue: 'http://localhost:8111' }],
      }) as ReturnType<typeof useServiceEditorController>,
    );

    render(<EmbeddingsEditor />);
    expect(screen.getByText('Operational Dependencies')).toBeInTheDocument();
    expect(screen.getByText('Embeddings:Endpoint')).toBeInTheDocument();
  });

  it.each([
    ['SpeechSynthesis.Google.TextToSpeech', /generateContent/i],
    ['SpeechSynthesis.HuggingFace.Inference', /Hugging Face TTS/i],
    ['SpeechSynthesis.OpenRouter.Tts', /OpenRouter TTS/i],
    ['SpeechSynthesis.OpenAI.Tts', /\/audio\/speech/i],
    ['SpeechSynthesis.Unknown.Cloud', /Cloud synthesis uses the selected provider/i],
  ] as const)('SpeechSynthesisEditor renders cloud guidance for %s', (providerId, matcher) => {
    vi.mocked(useServiceEditorController).mockReturnValue(
      makeController('SpeechSynthesis', { providerId, providerKind: 'Cloud' }) as ReturnType<
        typeof useServiceEditorController
      >,
    );

    render(<SpeechSynthesisEditor />);
    expect(screen.getByText(matcher)).toBeInTheDocument();
  });

  it('SpeechSynthesisEditor renders local runtime guidance and dependencies', () => {
    vi.mocked(useServiceEditorController).mockReturnValue(
      makeController('SpeechSynthesis', {
        providerId: 'SpeechSynthesis.Local.Tts',
        runtimeDependencies: [{ key: 'SpeechSynthesis:TimeoutSeconds', hasValue: false, currentValue: null }],
      }) as ReturnType<typeof useServiceEditorController>,
    );

    render(<SpeechSynthesisEditor />);
    expect(screen.getByText(/POST \/tts\/synthesize/i)).toBeInTheDocument();
    expect(screen.getByText('SpeechSynthesis:TimeoutSeconds')).toBeInTheDocument();
  });

  it.each([
    ['SpeechTranscription.AzureSpeech.Batch', /batch transcription/i],
    ['SpeechTranscription.Google.SpeechToText', /Transcription Model ID/i],
    ['SpeechTranscription.HuggingFace.Inference', /Hugging Face ASR/i],
    ['SpeechTranscription.OpenRouter.Audio', /Max Audio Bytes/i],
    ['SpeechTranscription.OpenAI.Audio', /whisper-1/i],
    ['SpeechTranscription.Unknown.Cloud', /Cloud transcription uses the selected provider/i],
  ] as const)('SpeechTranscriptionEditor renders cloud guidance for %s', (providerId, matcher) => {
    vi.mocked(useServiceEditorController).mockReturnValue(
      makeController('SpeechTranscription', { providerId, providerKind: 'Cloud' }) as ReturnType<
        typeof useServiceEditorController
      >,
    );

    render(<SpeechTranscriptionEditor />);
    expect(screen.getByText(matcher)).toBeInTheDocument();
  });

  it('SpeechTranscriptionEditor renders local runtime guidance', () => {
    vi.mocked(useServiceEditorController).mockReturnValue(
      makeController('SpeechTranscription', { providerId: 'SpeechTranscription.Local.Stt' }) as ReturnType<
        typeof useServiceEditorController
      >,
    );

    render(<SpeechTranscriptionEditor />);
    expect(screen.getByText(/GA_ASR_/i)).toBeInTheDocument();
  });
});
