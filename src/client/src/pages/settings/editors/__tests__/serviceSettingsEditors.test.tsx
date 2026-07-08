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
    expect(screen.getByRole('button', { name: 'trigger-model-auto-loaded' })).toBeInTheDocument();
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
    expect(screen.getAllByText('SpeechSynthesis:TimeoutSeconds').length).toBeGreaterThan(0);
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

  it('EmbeddingsEditor saves and queues rebuild after confirmation', async () => {
    const user = userEvent.setup();
    const save = vi.fn(async () => true);
    vi.mocked(api.settings.rebuildEmbeddings).mockResolvedValue({
      jobId: 'job-2',
      status: 'queued',
    } as never);
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
    await user.click(screen.getByRole('button', { name: 'Save and rebuild' }));

    expect(save).toHaveBeenCalled();
    expect(api.settings.rebuildEmbeddings).toHaveBeenCalled();
    expect(await screen.findByText(/job-2/i)).toBeInTheDocument();
  });

  it('EmbeddingsEditor saves directly when rebuild is not required', async () => {
    const user = userEvent.setup();
    const save = vi.fn(async () => true);
    vi.mocked(useServiceEditorController).mockReturnValue({
      ...makeController('Embeddings'),
      save,
    } as ReturnType<typeof useServiceEditorController>);

    render(<EmbeddingsEditor />);
    await user.click(screen.getByRole('button', { name: 'Save' }));
    expect(save).toHaveBeenCalled();
    expect(screen.queryByText(/Reindex Required/i)).not.toBeInTheDocument();
  });

  it('EmbeddingsEditor surfaces generic rebuild failures', async () => {
    const user = userEvent.setup();
    vi.mocked(api.settings.rebuildEmbeddings).mockRejectedValue(new Error('Queue failed'));
    vi.mocked(useServiceEditorController).mockReturnValue(
      makeController('Embeddings') as ReturnType<typeof useServiceEditorController>,
    );

    render(<EmbeddingsEditor />);
    await user.click(screen.getByRole('button', { name: 'Rebuild vectors' }));
    await user.click(screen.getByRole('button', { name: 'Queue rebuild' }));

    expect(await screen.findByText('Queue failed')).toBeInTheDocument();
  });

  it('ImageGenerationEditor renders azure profile guidance from deployment draft', () => {
    vi.mocked(useServiceEditorController).mockReturnValue({
      ...makeController('ImageGeneration', {
        providerId: 'ImageGeneration.AzureOpenAI.Images',
        providerKind: 'Cloud',
        fields: {
          Deployment: { name: 'Deployment', value: 'dalle3', isSecret: false, hasValue: true },
        },
      }),
      draft: {
        activeProviderId: 'ImageGeneration.AzureOpenAI.Images',
        activeDraft: { Deployment: 'dall-e-3-prod' },
        switchProvider: vi.fn(),
        patchActiveDraft: vi.fn(),
      },
    } as ReturnType<typeof useServiceEditorController>);

    render(<ImageGenerationEditor />);
    expect(screen.getByText(/Inferred profile/i)).toBeInTheDocument();
    expect(screen.getByText(/Allowed sizes/i)).toBeInTheDocument();
  });

  it.each([
    ['ImageGeneration.Google.Imagen', /Gemini image generation/i],
    ['ImageGeneration.HuggingFace.Inference', /Hugging Face image generation/i],
    ['ImageGeneration.OpenRouter.Image', /OpenRouter image generation/i],
    ['ImageGeneration.OpenAI.Images', /\/images\/generations/i],
    ['ImageGeneration.Unknown.Cloud', /Cloud image generation uses the selected provider/i],
  ] as const)('ImageGenerationEditor renders cloud guidance for %s', (providerId, matcher) => {
    vi.mocked(useServiceEditorController).mockReturnValue(
      makeController('ImageGeneration', { providerId, providerKind: 'Cloud' }) as ReturnType<
        typeof useServiceEditorController
      >,
    );

    render(<ImageGenerationEditor />);
    expect(screen.getByText(matcher)).toBeInTheDocument();
  });

  it('ImageGenerationEditor renders local SD runtime guidance', () => {
    vi.mocked(useServiceEditorController).mockReturnValue(
      makeController('ImageGeneration', { providerId: 'ImageGeneration.Local.Sd' }) as ReturnType<
        typeof useServiceEditorController
      >,
    );

    render(<ImageGenerationEditor />);
    expect(screen.getByText(/flux-style sizes/i)).toBeInTheDocument();
    expect(screen.getByText(/GA_SD_/i)).toBeInTheDocument();
  });

  it('Speech editors show loading and missing-state placeholders', () => {
    vi.mocked(useServiceEditorController).mockReturnValue({
      ...makeController('SpeechSynthesis'),
      loading: true,
      state: null,
      selectedProvider: null,
    } as ReturnType<typeof useServiceEditorController>);
    const { rerender } = render(<SpeechSynthesisEditor />);
    expect(screen.getByText(/Loading Speech Synthesis settings/i)).toBeInTheDocument();

    vi.mocked(useServiceEditorController).mockReturnValue({
      ...makeController('SpeechTranscription'),
      loading: false,
      state: null,
      selectedProvider: null,
      error: 'Speech transcription unavailable',
    } as ReturnType<typeof useServiceEditorController>);
    rerender(<SpeechTranscriptionEditor />);
    expect(screen.getByText('Speech transcription unavailable')).toBeInTheDocument();
  });

  it('ImageGenerationEditor shows loading and missing-state placeholders', () => {
    vi.mocked(useServiceEditorController).mockReturnValue({
      ...makeController('ImageGeneration'),
      loading: true,
      state: null,
      selectedProvider: null,
    } as ReturnType<typeof useServiceEditorController>);

    const { rerender } = render(<ImageGenerationEditor />);
    expect(screen.getByText(/Loading Image Generation settings/i)).toBeInTheDocument();

    vi.mocked(useServiceEditorController).mockReturnValue({
      ...makeController('ImageGeneration'),
      loading: false,
      state: null,
      selectedProvider: null,
      error: 'Image generation unavailable',
    } as ReturnType<typeof useServiceEditorController>);
    rerender(<ImageGenerationEditor />);
    expect(screen.getByText('Image generation unavailable')).toBeInTheDocument();
  });

  it('ImageGenerationEditor switches providers and surfaces operational dependencies', async () => {
    const user = userEvent.setup();
    const switchProvider = vi.fn();
    vi.mocked(useServiceEditorController).mockReturnValue({
      ...makeController('ImageGeneration', {
        providerId: 'ImageGeneration.Local.Sd',
        runtimeDependencies: [{ key: 'ImageGeneration:Endpoint', hasValue: false, currentValue: null }],
      }),
      providerOptions: [
        {
          providerId: 'ImageGeneration.Local.Sd',
          displayName: 'Local SD',
          kind: 'Local',
          hasExplicitMode: true,
          connectionConfigured: true,
          canActivate: true,
        },
        {
          providerId: 'ImageGeneration.OpenAI.Images',
          displayName: 'OpenAI',
          kind: 'Cloud',
          hasExplicitMode: true,
          connectionConfigured: true,
          canActivate: true,
        },
      ],
      draft: {
        activeProviderId: 'ImageGeneration.Local.Sd',
        activeDraft: {},
        switchProvider,
        patchActiveDraft: vi.fn(),
      },
    } as ReturnType<typeof useServiceEditorController>);

    render(<ImageGenerationEditor />);
    expect(screen.getByText('Operational Dependencies')).toBeInTheDocument();
    expect(screen.getAllByText('ImageGeneration:Endpoint').length).toBeGreaterThan(0);

    await user.click(screen.getByRole('button', { name: /OpenAI Cloud/i }));
    expect(switchProvider).toHaveBeenCalledWith('ImageGeneration.OpenAI.Images');
  });

  it('ImageGenerationEditor disables save when connection is not configured', () => {
    vi.mocked(useServiceEditorController).mockReturnValue({
      ...makeController('ImageGeneration', {
        providerId: 'ImageGeneration.Local.Sd',
        connectionConfigured: false,
      }),
    } as ReturnType<typeof useServiceEditorController>);

    render(<ImageGenerationEditor />);
    const saveButton = screen.getByRole('button', { name: 'Save' });
    expect(saveButton).toBeDisabled();
    expect(saveButton).toHaveAttribute('title', 'Configure the provider connection first.');
  });

  it('ImageGenerationEditor saves and surfaces controller errors', async () => {
    const user = userEvent.setup();
    const save = vi.fn(async () => true);
    vi.mocked(useServiceEditorController).mockReturnValue({
      ...makeController('ImageGeneration', { providerId: 'ImageGeneration.Local.Sd', hasExplicitMode: false }),
      save,
      error: 'Save failed',
    } as ReturnType<typeof useServiceEditorController>);

    render(<ImageGenerationEditor />);
    const saveButton = screen.getByRole('button', { name: 'Save' });
    expect(saveButton).toHaveAttribute(
      'title',
      'Save will create an explicit service mode and activate provider.',
    );
    await user.click(saveButton);
    expect(save).toHaveBeenCalled();
    expect(screen.getByText('Save failed')).toBeInTheDocument();
  });

  it('SpeechSynthesisEditor switches providers and saves with implicit mode guidance', async () => {
    const user = userEvent.setup();
    const switchProvider = vi.fn();
    const save = vi.fn(async () => true);
    vi.mocked(useServiceEditorController).mockReturnValue({
      ...makeController('SpeechSynthesis', {
        providerId: 'SpeechSynthesis.Local.Tts',
        hasExplicitMode: false,
      }),
      providerOptions: [
        {
          providerId: 'SpeechSynthesis.Local.Tts',
          displayName: 'Local TTS',
          kind: 'Local',
          hasExplicitMode: false,
          connectionConfigured: true,
          canActivate: true,
        },
        {
          providerId: 'SpeechSynthesis.OpenAI.Tts',
          displayName: 'OpenAI',
          kind: 'Cloud',
          hasExplicitMode: true,
          connectionConfigured: true,
          canActivate: true,
        },
      ],
      draft: {
        activeProviderId: 'SpeechSynthesis.Local.Tts',
        activeDraft: {},
        switchProvider,
        patchActiveDraft: vi.fn(),
      },
      save,
    } as ReturnType<typeof useServiceEditorController>);

    render(<SpeechSynthesisEditor />);
    await user.click(screen.getByRole('button', { name: /OpenAI Cloud/i }));
    expect(switchProvider).toHaveBeenCalledWith('SpeechSynthesis.OpenAI.Tts');

    const saveButton = screen.getByRole('button', { name: 'Save' });
    expect(saveButton).toHaveAttribute(
      'title',
      'Save will create an explicit service mode and activate provider.',
    );
    await user.click(saveButton);
    expect(save).toHaveBeenCalled();
  });

  it('SpeechSynthesisEditor shows missing-state error', () => {
    vi.mocked(useServiceEditorController).mockReturnValue({
      ...makeController('SpeechSynthesis'),
      loading: false,
      state: null,
      selectedProvider: null,
      error: 'Speech synthesis unavailable',
    } as ReturnType<typeof useServiceEditorController>);

    render(<SpeechSynthesisEditor />);
    expect(screen.getByText('Speech synthesis unavailable')).toBeInTheDocument();
  });

  it('SpeechTranscriptionEditor shows loading, switches providers, and renders dependencies', async () => {
    const user = userEvent.setup();
    const switchProvider = vi.fn();
    vi.mocked(useServiceEditorController).mockReturnValue({
      ...makeController('SpeechTranscription', {
        providerId: 'SpeechTranscription.Local.Stt',
        runtimeDependencies: [{ key: 'SpeechTranscription:TimeoutSeconds', hasValue: true, currentValue: '120' }],
      }),
      providerOptions: [
        {
          providerId: 'SpeechTranscription.Local.Stt',
          displayName: 'Local ASR',
          kind: 'Local',
          hasExplicitMode: true,
          connectionConfigured: true,
          canActivate: true,
        },
        {
          providerId: 'SpeechTranscription.OpenAI.Audio',
          displayName: 'OpenAI',
          kind: 'Cloud',
          hasExplicitMode: true,
          connectionConfigured: true,
          canActivate: true,
        },
      ],
      draft: {
        activeProviderId: 'SpeechTranscription.Local.Stt',
        activeDraft: {},
        switchProvider,
        patchActiveDraft: vi.fn(),
      },
    } as ReturnType<typeof useServiceEditorController>);

    const { rerender } = render(<SpeechTranscriptionEditor />);
    expect(screen.getByText('Operational Dependencies')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: /OpenAI Cloud/i }));
    expect(switchProvider).toHaveBeenCalledWith('SpeechTranscription.OpenAI.Audio');

    vi.mocked(useServiceEditorController).mockReturnValue({
      ...makeController('SpeechTranscription'),
      loading: true,
      state: null,
      selectedProvider: null,
    } as ReturnType<typeof useServiceEditorController>);
    rerender(<SpeechTranscriptionEditor />);
    expect(screen.getByText(/Loading Speech Transcription settings/i)).toBeInTheDocument();
  });
});
