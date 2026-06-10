import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import { SECRET_MASK } from '../../constants';
import { GeminiOptionalServicesStep } from '../GeminiOptionalServicesStep';
import { HuggingFaceOptionalServicesStep } from '../HuggingFaceOptionalServicesStep';
import { OpenAiOptionalServicesStep } from '../OpenAiOptionalServicesStep';
import { OpenRouterOptionalServicesStep } from '../OpenRouterOptionalServicesStep';
import { OptionalServicesStep } from '../OptionalServicesStep';
import {
  createFoundryOptionalServicesForm,
  createGeminiOptionalServicesForm,
  createHuggingFaceOptionalServicesForm,
  createOpenAiOptionalServicesForm,
  createOpenRouterOptionalServicesForm,
  getServiceCardCheckbox,
} from './stepTestHelpers';

describe('OptionalServicesStep (Foundry)', () => {
  const onChange = vi.fn();

  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('renders foundry optional services heading', () => {
    render(
      <OptionalServicesStep
        value={createFoundryOptionalServicesForm()}
        derivedCoreEndpoint="https://core.openai.azure.com/"
        errors={{}}
        onChange={onChange}
      />,
    );

    expect(screen.getByText('Optional Microsoft Foundry services')).toBeInTheDocument();
    expect(screen.getByText('Embeddings')).toBeInTheDocument();
    expect(screen.getByText('Image Generation')).toBeInTheDocument();
    expect(screen.getByText('Speech (Transcription and Synthesis)')).toBeInTheDocument();
    expect(screen.getByText('Document Intelligence')).toBeInTheDocument();
  });

  it('toggles configure-now for embeddings service', () => {
    render(
      <OptionalServicesStep
        value={createFoundryOptionalServicesForm()}
        derivedCoreEndpoint="https://core.openai.azure.com/"
        errors={{}}
        onChange={onChange}
      />,
    );

    fireEvent.click(getServiceCardCheckbox('Embeddings'));
    expect(onChange).toHaveBeenCalledWith({ enableEmbeddings: true });
  });

  it('edits linked endpoint fields when embeddings are enabled', () => {
    render(
      <OptionalServicesStep
        value={createFoundryOptionalServicesForm({ enableEmbeddings: true })}
        derivedCoreEndpoint="https://core.openai.azure.com/"
        errors={{}}
        onChange={onChange}
      />,
    );

    fireEvent.click(screen.getByText(/Use core-derived endpoint/));
    expect(onChange).toHaveBeenCalledWith({ linkEmbeddingsEndpointToCore: false });
  });

  it('shows embeddings fields when enabled and fires onChange', () => {
    render(
      <OptionalServicesStep
        value={createFoundryOptionalServicesForm({
          enableEmbeddings: true,
          linkEmbeddingsEndpointToCore: false,
          embeddingsApiKeyHasStoredValue: true,
        })}
        derivedCoreEndpoint="https://core.openai.azure.com/"
        errors={{ embeddingsDeployment: 'Required' }}
        onChange={onChange}
      />,
    );

    fireEvent.change(screen.getByLabelText('Endpoint'), { target: { value: 'https://embed.example/' } });
    fireEvent.change(screen.getByLabelText('API key'), { target: { value: 'embed-key' } });
    fireEvent.change(screen.getByLabelText('Deployment'), { target: { value: 'text-embedding-3-small' } });

    expect(onChange).toHaveBeenCalledWith({ embeddingsEndpoint: 'https://embed.example/' });
    expect(onChange).toHaveBeenCalledWith({ embeddingsApiKey: 'embed-key' });
    expect(onChange).toHaveBeenCalledWith({ embeddingsDeployment: 'text-embedding-3-small' });
    expect(screen.getByText(SECRET_MASK)).toBeInTheDocument();
    expect(screen.getByText('Required')).toBeInTheDocument();
  });

  it('enables speech service and edits speech fields', () => {
    render(
      <OptionalServicesStep
        value={createFoundryOptionalServicesForm({ enableSpeech: true })}
        derivedCoreEndpoint=""
        errors={{}}
        onChange={onChange}
      />,
    );

    fireEvent.change(screen.getByLabelText('Region'), { target: { value: 'eastus' } });
    expect(onChange).toHaveBeenCalledWith({ speechRegion: 'eastus' });
  });

  it('enables image generation and edits deployment fields', () => {
    render(
      <OptionalServicesStep
        value={createFoundryOptionalServicesForm({
          enableImages: true,
          linkImagesEndpointToCore: false,
          imagesApiKeyHasStoredValue: true,
        })}
        derivedCoreEndpoint="https://core.openai.azure.com/"
        errors={{ imagesDeployment: 'Required' }}
        onChange={onChange}
      />,
    );

    fireEvent.change(screen.getByLabelText('Generation deployment'), { target: { value: 'dalle-3' } });
    fireEvent.change(screen.getByLabelText('Edit deployment'), { target: { value: 'dalle-3-edit' } });
    fireEvent.change(screen.getByLabelText('API version'), { target: { value: '2024-10-01' } });

    expect(onChange).toHaveBeenCalledWith({ imagesDeployment: 'dalle-3' });
    expect(onChange).toHaveBeenCalledWith({ imagesEditDeployment: 'dalle-3-edit' });
    expect(onChange).toHaveBeenCalledWith({ imagesApiVersion: '2024-10-01' });
    expect(screen.getByText('Required')).toBeInTheDocument();
  });

  it('enables document intelligence and edits endpoint fields', () => {
    render(
      <OptionalServicesStep
        value={createFoundryOptionalServicesForm({ enableDocumentIntelligence: true })}
        derivedCoreEndpoint=""
        errors={{ documentIntelligenceEndpoint: 'Required' }}
        onChange={onChange}
      />,
    );

    fireEvent.change(screen.getByLabelText('Endpoint'), { target: { value: 'https://doc-int.example/' } });
    fireEvent.change(screen.getByLabelText('API key'), { target: { value: 'doc-key' } });

    expect(onChange).toHaveBeenCalledWith({ documentIntelligenceEndpoint: 'https://doc-int.example/' });
    expect(onChange).toHaveBeenCalledWith({ documentIntelligenceApiKey: 'doc-key' });
    expect(screen.getByText('Required')).toBeInTheDocument();
  });

  it('toggles all foundry optional services on', () => {
    render(
      <OptionalServicesStep
        value={createFoundryOptionalServicesForm()}
        derivedCoreEndpoint="https://core.openai.azure.com/"
        errors={{}}
        onChange={onChange}
      />,
    );

    fireEvent.click(getServiceCardCheckbox('Embeddings'));
    fireEvent.click(getServiceCardCheckbox('Image Generation'));
    fireEvent.click(getServiceCardCheckbox('Speech (Transcription and Synthesis)'));
    fireEvent.click(getServiceCardCheckbox('Document Intelligence'));

    expect(onChange).toHaveBeenCalledWith({ enableEmbeddings: true });
    expect(onChange).toHaveBeenCalledWith({ enableImages: true });
    expect(onChange).toHaveBeenCalledWith({ enableSpeech: true });
    expect(onChange).toHaveBeenCalledWith({ enableDocumentIntelligence: true });
  });

  it('edits all foundry optional fields when every service is enabled', () => {
    render(
      <OptionalServicesStep
        value={createFoundryOptionalServicesForm({
          enableEmbeddings: true,
          linkEmbeddingsEndpointToCore: false,
          enableImages: true,
          linkImagesEndpointToCore: false,
          enableSpeech: true,
          enableDocumentIntelligence: true,
        })}
        derivedCoreEndpoint="https://core.openai.azure.com/"
        errors={{}}
        onChange={onChange}
      />,
    );

    const endpoints = screen.getAllByLabelText('Endpoint');
    fireEvent.change(endpoints[0]!, { target: { value: 'https://embed.example/' } });
    fireEvent.change(endpoints[1]!, { target: { value: 'https://images.example/' } });
    fireEvent.change(endpoints[2]!, { target: { value: 'https://speech.example/' } });
    fireEvent.change(endpoints[3]!, { target: { value: 'https://doc.example/' } });
    fireEvent.change(screen.getByLabelText('Region'), { target: { value: 'westus2' } });

    expect(onChange).toHaveBeenCalledWith({ embeddingsEndpoint: 'https://embed.example/' });
    expect(onChange).toHaveBeenCalledWith({ imagesEndpoint: 'https://images.example/' });
    expect(onChange).toHaveBeenCalledWith({ speechEndpoint: 'https://speech.example/' });
    expect(onChange).toHaveBeenCalledWith({ documentIntelligenceEndpoint: 'https://doc.example/' });
    expect(onChange).toHaveBeenCalledWith({ speechRegion: 'westus2' });
  });
});

describe('OpenAiOptionalServicesStep', () => {
  const onChange = vi.fn();

  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('renders optional openai services', () => {
    render(
      <OpenAiOptionalServicesStep
        value={createOpenAiOptionalServicesForm()}
        errors={{}}
        onChange={onChange}
      />,
    );

    expect(screen.getByText('Optional OpenAI services')).toBeInTheDocument();
  });

  it('toggles embeddings and edits model fields', () => {
    render(
      <OpenAiOptionalServicesStep
        value={createOpenAiOptionalServicesForm({ enableEmbeddings: true })}
        errors={{ embeddingsModelId: 'Required' }}
        onChange={onChange}
      />,
    );

    fireEvent.change(screen.getByLabelText('Embedding Model ID'), {
      target: { value: 'text-embedding-3-large' },
    });
    fireEvent.change(screen.getByLabelText('Dimensions (optional)'), { target: { value: '1536' } });

    expect(onChange).toHaveBeenCalledWith({ embeddingsModelId: 'text-embedding-3-large' });
    expect(onChange).toHaveBeenCalledWith({ embeddingsDimensions: '1536' });
    expect(screen.getByText('Required')).toBeInTheDocument();
  });

  it('toggles speech synthesis and edits voice fields', () => {
    render(
      <OpenAiOptionalServicesStep
        value={createOpenAiOptionalServicesForm({ enableSpeechSynthesis: true })}
        errors={{ speechSynthesisVoiceName: 'Required' }}
        onChange={onChange}
      />,
    );

    fireEvent.change(screen.getByLabelText('Voice Name'), { target: { value: 'nova' } });
    fireEvent.change(screen.getByLabelText('TTS Model ID'), { target: { value: 'tts-1-hd' } });
    expect(onChange).toHaveBeenCalledWith({ speechSynthesisVoiceName: 'nova' });
    expect(onChange).toHaveBeenCalledWith({ speechSynthesisModelId: 'tts-1-hd' });
    expect(screen.getByText('Required')).toBeInTheDocument();
  });

  it('enables speech transcription and edits model fields', () => {
    render(
      <OpenAiOptionalServicesStep
        value={createOpenAiOptionalServicesForm({ enableSpeechTranscription: true })}
        errors={{}}
        onChange={onChange}
      />,
    );

    fireEvent.change(screen.getByLabelText('Transcription Model ID'), { target: { value: 'whisper-large-v3' } });
    expect(onChange).toHaveBeenCalledWith({ speechTranscriptionModelId: 'whisper-large-v3' });
  });

  it('enables image generation and edits timeout', () => {
    render(
      <OpenAiOptionalServicesStep
        value={createOpenAiOptionalServicesForm({ enableImages: true })}
        errors={{}}
        onChange={onChange}
      />,
    );

    fireEvent.change(screen.getByLabelText('Image Model ID'), { target: { value: 'dall-e-3' } });
    fireEvent.change(screen.getByLabelText('Timeout Seconds'), { target: { value: '240' } });
    expect(onChange).toHaveBeenCalledWith({ imagesModelId: 'dall-e-3' });
    expect(onChange).toHaveBeenCalledWith({ imagesTimeoutSeconds: '240' });
  });

  it('edits all openai optional fields when every service is enabled', () => {
    render(
      <OpenAiOptionalServicesStep
        value={createOpenAiOptionalServicesForm({
          enableEmbeddings: true,
          enableImages: true,
          enableSpeechTranscription: true,
          enableSpeechSynthesis: true,
        })}
        errors={{}}
        onChange={onChange}
      />,
    );

    fireEvent.change(screen.getByLabelText('Embedding Model ID'), { target: { value: 'custom-embed-model' } });
    fireEvent.change(screen.getByLabelText('Image Model ID'), { target: { value: 'custom-image-model' } });
    fireEvent.change(screen.getByLabelText('Transcription Model ID'), { target: { value: 'custom-asr-model' } });
    fireEvent.change(screen.getByLabelText('TTS Model ID'), { target: { value: 'custom-tts-model' } });

    expect(onChange).toHaveBeenCalledWith({ embeddingsModelId: 'custom-embed-model' });
    expect(onChange).toHaveBeenCalledWith({ imagesModelId: 'custom-image-model' });
    expect(onChange).toHaveBeenCalledWith({ speechTranscriptionModelId: 'custom-asr-model' });
    expect(onChange).toHaveBeenCalledWith({ speechSynthesisModelId: 'custom-tts-model' });
  });

  it('edits timeout fields and shows validation errors for every openai service', () => {
    render(
      <OpenAiOptionalServicesStep
        value={createOpenAiOptionalServicesForm({
          enableEmbeddings: true,
          enableImages: true,
          enableSpeechTranscription: true,
          enableSpeechSynthesis: true,
        })}
        errors={{
          embeddingsTimeoutSeconds: 'Invalid timeout',
          imagesTimeoutSeconds: 'Invalid timeout',
          speechTranscriptionTimeoutSeconds: 'Invalid timeout',
          speechSynthesisTimeoutSeconds: 'Invalid timeout',
          speechSynthesisVoiceName: 'Required',
          embeddingsDimensions: 'Bad dimensions',
        }}
        onChange={onChange}
      />,
    );

    expect(screen.getByText(/Output embedding dimensions/)).toBeInTheDocument();
    expect(screen.getAllByText('Invalid timeout')).toHaveLength(4);
    expect(screen.getByText('Required')).toBeInTheDocument();
    expect(screen.getByText('Bad dimensions')).toBeInTheDocument();

    fireEvent.change(document.getElementById('openai-embeddings-timeout')!, { target: { value: '120' } });
    fireEvent.change(document.getElementById('openai-image-timeout')!, { target: { value: '180' } });
    fireEvent.change(document.getElementById('openai-transcription-timeout')!, { target: { value: '240' } });
    fireEvent.change(document.getElementById('openai-tts-timeout')!, { target: { value: '301' } });

    expect(onChange).toHaveBeenCalledWith({ embeddingsTimeoutSeconds: '120' });
    expect(onChange).toHaveBeenCalledWith({ imagesTimeoutSeconds: '180' });
    expect(onChange).toHaveBeenCalledWith({ speechTranscriptionTimeoutSeconds: '240' });
    expect(onChange).toHaveBeenCalledWith({ speechSynthesisTimeoutSeconds: '301' });
  });

  it('toggles each openai optional service off', () => {
    render(
      <OpenAiOptionalServicesStep
        value={createOpenAiOptionalServicesForm({
          enableEmbeddings: true,
          enableImages: true,
          enableSpeechTranscription: true,
          enableSpeechSynthesis: true,
        })}
        errors={{}}
        onChange={onChange}
      />,
    );

    fireEvent.click(getServiceCardCheckbox('Embeddings'));
    fireEvent.click(getServiceCardCheckbox('Image Generation'));
    fireEvent.click(getServiceCardCheckbox('Speech Transcription'));
    fireEvent.click(getServiceCardCheckbox('Speech Synthesis'));

    expect(onChange).toHaveBeenCalledWith({ enableEmbeddings: false });
    expect(onChange).toHaveBeenCalledWith({ enableImages: false });
    expect(onChange).toHaveBeenCalledWith({ enableSpeechTranscription: false });
    expect(onChange).toHaveBeenCalledWith({ enableSpeechSynthesis: false });
  });
});

describe('HuggingFaceOptionalServicesStep', () => {
  const onChange = vi.fn();

  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('renders optional hugging face services', () => {
    render(
      <HuggingFaceOptionalServicesStep
        value={createHuggingFaceOptionalServicesForm()}
        errors={{}}
        onChange={onChange}
      />,
    );

    expect(screen.getByText('Optional Hugging Face services')).toBeInTheDocument();
  });

  it('enables image generation and edits image model ids', () => {
    render(
      <HuggingFaceOptionalServicesStep
        value={createHuggingFaceOptionalServicesForm({ enableImages: true })}
        errors={{ imagesTextToImageModelId: 'Required' }}
        onChange={onChange}
      />,
    );

    fireEvent.change(screen.getByLabelText('Text-to-Image Model ID'), {
      target: { value: 'stabilityai/stable-diffusion-xl' },
    });
    fireEvent.change(screen.getByLabelText('Image-to-Image Model ID'), {
      target: { value: 'black-forest-labs/FLUX.1-dev' },
    });

    expect(onChange).toHaveBeenCalledWith({ imagesTextToImageModelId: 'stabilityai/stable-diffusion-xl' });
    expect(onChange).toHaveBeenCalledWith({ imagesImageToImageModelId: 'black-forest-labs/FLUX.1-dev' });
    expect(screen.getByText('Required')).toBeInTheDocument();
  });

  it('toggles embeddings service', () => {
    render(
      <HuggingFaceOptionalServicesStep
        value={createHuggingFaceOptionalServicesForm({ enableEmbeddings: true })}
        errors={{}}
        onChange={onChange}
      />,
    );

    fireEvent.change(screen.getByLabelText('Embedding Model ID'), { target: { value: 'sentence-transformers/all-MiniLM-L6-v2' } });
    expect(onChange).toHaveBeenCalledWith({ embeddingsModelId: 'sentence-transformers/all-MiniLM-L6-v2' });
  });

  it('enables speech transcription and synthesis fields', () => {
    render(
      <HuggingFaceOptionalServicesStep
        value={createHuggingFaceOptionalServicesForm({
          enableSpeechTranscription: true,
          enableSpeechSynthesis: true,
        })}
        errors={{}}
        onChange={onChange}
      />,
    );

    fireEvent.change(screen.getByLabelText('Transcription Model ID'), { target: { value: 'openai/whisper-medium' } });
    fireEvent.change(screen.getByLabelText('TTS Model ID'), { target: { value: 'ResembleAI/chatterbox-v2' } });
    expect(onChange).toHaveBeenCalledWith({ speechTranscriptionModelId: 'openai/whisper-medium' });
    expect(onChange).toHaveBeenCalledWith({ speechSynthesisModelId: 'ResembleAI/chatterbox-v2' });
  });

  it('toggles all hugging face optional services on', () => {
    render(
      <HuggingFaceOptionalServicesStep
        value={createHuggingFaceOptionalServicesForm()}
        errors={{}}
        onChange={onChange}
      />,
    );

    fireEvent.click(getServiceCardCheckbox('Embeddings'));
    fireEvent.click(getServiceCardCheckbox('Image Generation'));
    fireEvent.click(getServiceCardCheckbox('Speech Transcription'));
    fireEvent.click(getServiceCardCheckbox('Speech Synthesis'));

    expect(onChange).toHaveBeenCalledWith({ enableEmbeddings: true });
    expect(onChange).toHaveBeenCalledWith({ enableImages: true });
    expect(onChange).toHaveBeenCalledWith({ enableSpeechTranscription: true });
    expect(onChange).toHaveBeenCalledWith({ enableSpeechSynthesis: true });
  });

  it('edits all hugging face optional fields when every service is enabled', () => {
    render(
      <HuggingFaceOptionalServicesStep
        value={createHuggingFaceOptionalServicesForm({
          enableEmbeddings: true,
          enableImages: true,
          enableSpeechTranscription: true,
          enableSpeechSynthesis: true,
        })}
        errors={{}}
        onChange={onChange}
      />,
    );

    fireEvent.change(screen.getByLabelText('Embedding Model ID'), { target: { value: 'custom/embed-model' } });
    fireEvent.change(screen.getByLabelText('Text-to-Image Model ID'), { target: { value: 'custom/t2i-model' } });
    fireEvent.change(screen.getByLabelText('Image-to-Image Model ID'), { target: { value: 'custom/i2i-model' } });
    fireEvent.change(screen.getByLabelText('Transcription Model ID'), { target: { value: 'custom/asr-model' } });
    fireEvent.change(screen.getByLabelText('TTS Model ID'), { target: { value: 'custom/tts-model' } });

    expect(onChange).toHaveBeenCalledWith({ embeddingsModelId: 'custom/embed-model' });
    expect(onChange).toHaveBeenCalledWith({ imagesTextToImageModelId: 'custom/t2i-model' });
    expect(onChange).toHaveBeenCalledWith({ imagesImageToImageModelId: 'custom/i2i-model' });
    expect(onChange).toHaveBeenCalledWith({ speechTranscriptionModelId: 'custom/asr-model' });
    expect(onChange).toHaveBeenCalledWith({ speechSynthesisModelId: 'custom/tts-model' });
  });

  it('edits timeout fields and shows errors for every hugging face service', () => {
    render(
      <HuggingFaceOptionalServicesStep
        value={createHuggingFaceOptionalServicesForm({
          enableEmbeddings: true,
          enableImages: true,
          enableSpeechTranscription: true,
          enableSpeechSynthesis: true,
        })}
        errors={{
          embeddingsTimeoutSeconds: 'Invalid',
          imagesTimeoutSeconds: 'Invalid',
          speechTranscriptionTimeoutSeconds: 'Invalid',
          speechSynthesisTimeoutSeconds: 'Invalid',
        }}
        onChange={onChange}
      />,
    );

    expect(screen.getAllByText('Invalid')).toHaveLength(4);
    const timeouts = screen.getAllByLabelText('Timeout Seconds');
    fireEvent.change(timeouts[0]!, { target: { value: '90' } });
    fireEvent.change(timeouts[1]!, { target: { value: '120' } });
    fireEvent.change(timeouts[2]!, { target: { value: '150' } });
    fireEvent.change(timeouts[3]!, { target: { value: '180' } });

    expect(onChange).toHaveBeenCalledWith({ embeddingsTimeoutSeconds: '90' });
    expect(onChange).toHaveBeenCalledWith({ imagesTimeoutSeconds: '120' });
    expect(onChange).toHaveBeenCalledWith({ speechTranscriptionTimeoutSeconds: '150' });
    expect(onChange).toHaveBeenCalledWith({ speechSynthesisTimeoutSeconds: '180' });
  });

  it('toggles each hugging face optional service off', () => {
    render(
      <HuggingFaceOptionalServicesStep
        value={createHuggingFaceOptionalServicesForm({
          enableEmbeddings: true,
          enableImages: true,
          enableSpeechTranscription: true,
          enableSpeechSynthesis: true,
        })}
        errors={{}}
        onChange={onChange}
      />,
    );

    fireEvent.click(getServiceCardCheckbox('Embeddings'));
    fireEvent.click(getServiceCardCheckbox('Image Generation'));
    fireEvent.click(getServiceCardCheckbox('Speech Transcription'));
    fireEvent.click(getServiceCardCheckbox('Speech Synthesis'));

    expect(onChange).toHaveBeenCalledWith({ enableEmbeddings: false });
    expect(onChange).toHaveBeenCalledWith({ enableImages: false });
    expect(onChange).toHaveBeenCalledWith({ enableSpeechTranscription: false });
    expect(onChange).toHaveBeenCalledWith({ enableSpeechSynthesis: false });
  });
});

describe('OpenRouterOptionalServicesStep', () => {
  const onChange = vi.fn();

  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('renders optional openrouter services', () => {
    render(
      <OpenRouterOptionalServicesStep
        value={createOpenRouterOptionalServicesForm()}
        errors={{}}
        onChange={onChange}
      />,
    );

    expect(screen.getByText('Optional OpenRouter services')).toBeInTheDocument();
  });

  it('enables images and edits image model id', () => {
    render(
      <OpenRouterOptionalServicesStep
        value={createOpenRouterOptionalServicesForm({ enableImages: true })}
        errors={{}}
        onChange={onChange}
      />,
    );

    fireEvent.change(screen.getByLabelText('Image Model ID'), {
      target: { value: 'openai/gpt-5-image' },
    });
    expect(onChange).toHaveBeenCalledWith({ imagesModelId: 'openai/gpt-5-image' });
    expect(screen.getByText(/one image model id for both generation/)).toBeInTheDocument();
  });

  it('toggles speech transcription service', () => {
    render(
      <OpenRouterOptionalServicesStep
        value={createOpenRouterOptionalServicesForm({ enableSpeechTranscription: true })}
        errors={{}}
        onChange={onChange}
      />,
    );

    fireEvent.change(screen.getByLabelText('Transcription Model ID'), { target: { value: 'openai/whisper-1' } });
    expect(onChange).toHaveBeenCalledWith({ speechTranscriptionModelId: 'openai/whisper-1' });
  });

  it('enables embeddings and speech synthesis services', () => {
    render(
      <OpenRouterOptionalServicesStep
        value={createOpenRouterOptionalServicesForm({
          enableEmbeddings: true,
          enableSpeechSynthesis: true,
        })}
        errors={{}}
        onChange={onChange}
      />,
    );

    fireEvent.change(screen.getByLabelText('Embedding Model ID'), { target: { value: 'openai/text-embedding-3-large' } });
    fireEvent.change(screen.getByLabelText('TTS Model ID'), { target: { value: 'hexgrad/kokoro-82m-v2' } });
    expect(onChange).toHaveBeenCalledWith({ embeddingsModelId: 'openai/text-embedding-3-large' });
    expect(onChange).toHaveBeenCalledWith({ speechSynthesisModelId: 'hexgrad/kokoro-82m-v2' });
  });

  it('toggles all openrouter optional services on', () => {
    render(
      <OpenRouterOptionalServicesStep
        value={createOpenRouterOptionalServicesForm()}
        errors={{}}
        onChange={onChange}
      />,
    );

    fireEvent.click(getServiceCardCheckbox('Embeddings'));
    fireEvent.click(getServiceCardCheckbox('Image Generation'));
    fireEvent.click(getServiceCardCheckbox('Speech Transcription'));
    fireEvent.click(getServiceCardCheckbox('Speech Synthesis'));

    expect(onChange).toHaveBeenCalledWith({ enableEmbeddings: true });
    expect(onChange).toHaveBeenCalledWith({ enableImages: true });
    expect(onChange).toHaveBeenCalledWith({ enableSpeechTranscription: true });
    expect(onChange).toHaveBeenCalledWith({ enableSpeechSynthesis: true });
  });

  it('edits all openrouter optional fields when every service is enabled', () => {
    render(
      <OpenRouterOptionalServicesStep
        value={createOpenRouterOptionalServicesForm({
          enableEmbeddings: true,
          enableImages: true,
          enableSpeechTranscription: true,
          enableSpeechSynthesis: true,
        })}
        errors={{}}
        onChange={onChange}
      />,
    );

    fireEvent.change(screen.getByLabelText('Embedding Model ID'), { target: { value: 'custom/embed-model' } });
    fireEvent.change(screen.getByLabelText('Image Model ID'), { target: { value: 'custom/image-model' } });
    fireEvent.change(screen.getByLabelText('Transcription Model ID'), { target: { value: 'custom/asr-model' } });
    fireEvent.change(screen.getByLabelText('TTS Model ID'), { target: { value: 'custom/tts-model' } });

    expect(onChange).toHaveBeenCalledWith({ embeddingsModelId: 'custom/embed-model' });
    expect(onChange).toHaveBeenCalledWith({ imagesModelId: 'custom/image-model' });
    expect(onChange).toHaveBeenCalledWith({ speechTranscriptionModelId: 'custom/asr-model' });
    expect(onChange).toHaveBeenCalledWith({ speechSynthesisModelId: 'custom/tts-model' });
  });

  it('edits timeout fields and shows errors for every openrouter service', () => {
    render(
      <OpenRouterOptionalServicesStep
        value={createOpenRouterOptionalServicesForm({
          enableEmbeddings: true,
          enableImages: true,
          enableSpeechTranscription: true,
          enableSpeechSynthesis: true,
        })}
        errors={{
          embeddingsTimeoutSeconds: 'Invalid',
          imagesTimeoutSeconds: 'Invalid',
          speechTranscriptionTimeoutSeconds: 'Invalid',
          speechSynthesisTimeoutSeconds: 'Invalid',
        }}
        onChange={onChange}
      />,
    );

    expect(screen.getAllByText('Invalid')).toHaveLength(4);
    const timeouts = screen.getAllByLabelText('Timeout Seconds');
    fireEvent.change(timeouts[0]!, { target: { value: '45' } });
    fireEvent.change(timeouts[1]!, { target: { value: '60' } });
    fireEvent.change(timeouts[2]!, { target: { value: '75' } });
    fireEvent.change(timeouts[3]!, { target: { value: '90' } });

    expect(onChange).toHaveBeenCalledWith({ embeddingsTimeoutSeconds: '45' });
    expect(onChange).toHaveBeenCalledWith({ imagesTimeoutSeconds: '60' });
    expect(onChange).toHaveBeenCalledWith({ speechTranscriptionTimeoutSeconds: '75' });
    expect(onChange).toHaveBeenCalledWith({ speechSynthesisTimeoutSeconds: '90' });
  });

  it('toggles each openrouter optional service off', () => {
    render(
      <OpenRouterOptionalServicesStep
        value={createOpenRouterOptionalServicesForm({
          enableEmbeddings: true,
          enableImages: true,
          enableSpeechTranscription: true,
          enableSpeechSynthesis: true,
        })}
        errors={{}}
        onChange={onChange}
      />,
    );

    fireEvent.click(getServiceCardCheckbox('Embeddings'));
    fireEvent.click(getServiceCardCheckbox('Image Generation'));
    fireEvent.click(getServiceCardCheckbox('Speech Transcription'));
    fireEvent.click(getServiceCardCheckbox('Speech Synthesis'));

    expect(onChange).toHaveBeenCalledWith({ enableEmbeddings: false });
    expect(onChange).toHaveBeenCalledWith({ enableImages: false });
    expect(onChange).toHaveBeenCalledWith({ enableSpeechTranscription: false });
    expect(onChange).toHaveBeenCalledWith({ enableSpeechSynthesis: false });
  });
});

describe('GeminiOptionalServicesStep', () => {
  const onChange = vi.fn();

  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('renders optional gemini services', () => {
    render(
      <GeminiOptionalServicesStep
        value={createGeminiOptionalServicesForm()}
        errors={{}}
        onChange={onChange}
      />,
    );

    expect(screen.getByText('Optional Google Gemini services')).toBeInTheDocument();
  });

  it('enables embeddings and edits model id', () => {
    render(
      <GeminiOptionalServicesStep
        value={createGeminiOptionalServicesForm({ enableEmbeddings: true })}
        errors={{ embeddingsModelId: 'Invalid model' }}
        onChange={onChange}
      />,
    );

    fireEvent.change(screen.getByLabelText('Embedding Model ID'), {
      target: { value: 'gemini-embedding-custom' },
    });
    expect(onChange).toHaveBeenCalledWith({ embeddingsModelId: 'gemini-embedding-custom' });
    expect(screen.getByText('Invalid model')).toBeInTheDocument();
  });

  it('toggles speech synthesis and edits voice name', () => {
    render(
      <GeminiOptionalServicesStep
        value={createGeminiOptionalServicesForm({ enableSpeechSynthesis: true })}
        errors={{}}
        onChange={onChange}
      />,
    );

    fireEvent.change(screen.getByLabelText('Voice Name'), { target: { value: 'Puck' } });
    expect(onChange).toHaveBeenCalledWith({ speechSynthesisVoiceName: 'Puck' });
  });

  it('toggles image generation service', () => {
    render(
      <GeminiOptionalServicesStep
        value={createGeminiOptionalServicesForm({ enableImages: true })}
        errors={{ imagesModelId: 'Required' }}
        onChange={onChange}
      />,
    );

    fireEvent.change(screen.getByLabelText('Image Model ID'), { target: { value: 'gemini-2.0-flash-image-preview' } });
    expect(onChange).toHaveBeenCalledWith({ imagesModelId: 'gemini-2.0-flash-image-preview' });
    expect(screen.getByText('Required')).toBeInTheDocument();
  });

  it('enables speech transcription and edits model id', () => {
    render(
      <GeminiOptionalServicesStep
        value={createGeminiOptionalServicesForm({ enableSpeechTranscription: true })}
        errors={{}}
        onChange={onChange}
      />,
    );

    fireEvent.change(screen.getByLabelText('Transcription Model ID'), { target: { value: 'gemini-2.0-flash-lite' } });
    expect(onChange).toHaveBeenCalledWith({ speechTranscriptionModelId: 'gemini-2.0-flash-lite' });
  });

  it('edits all gemini optional fields when every service is enabled', () => {
    render(
      <GeminiOptionalServicesStep
        value={createGeminiOptionalServicesForm({
          enableEmbeddings: true,
          enableImages: true,
          enableSpeechTranscription: true,
          enableSpeechSynthesis: true,
        })}
        errors={{}}
        onChange={onChange}
      />,
    );

    fireEvent.change(screen.getByLabelText('Embedding Model ID'), { target: { value: 'custom-embed-model' } });
    fireEvent.change(screen.getByLabelText('Image Model ID'), { target: { value: 'custom-image-model' } });
    fireEvent.change(screen.getByLabelText('Transcription Model ID'), { target: { value: 'custom-asr-model' } });
    fireEvent.change(screen.getByLabelText('TTS Model ID'), { target: { value: 'custom-tts-model' } });

    expect(onChange).toHaveBeenCalledWith({ embeddingsModelId: 'custom-embed-model' });
    expect(onChange).toHaveBeenCalledWith({ imagesModelId: 'custom-image-model' });
    expect(onChange).toHaveBeenCalledWith({ speechTranscriptionModelId: 'custom-asr-model' });
    expect(onChange).toHaveBeenCalledWith({ speechSynthesisModelId: 'custom-tts-model' });
  });

  it('edits timeout fields and shows errors for every gemini service', () => {
    render(
      <GeminiOptionalServicesStep
        value={createGeminiOptionalServicesForm({
          enableEmbeddings: true,
          enableImages: true,
          enableSpeechTranscription: true,
          enableSpeechSynthesis: true,
        })}
        errors={{
          embeddingsTimeoutSeconds: 'Invalid',
          imagesTimeoutSeconds: 'Invalid',
          speechTranscriptionTimeoutSeconds: 'Invalid',
          speechSynthesisTimeoutSeconds: 'Invalid',
          speechSynthesisVoiceName: 'Required',
        }}
        onChange={onChange}
      />,
    );

    expect(screen.getAllByText('Invalid')).toHaveLength(4);
    expect(screen.getByText('Required')).toBeInTheDocument();
    const timeouts = screen.getAllByLabelText('Timeout Seconds');
    fireEvent.change(timeouts[0]!, { target: { value: '30' } });
    fireEvent.change(timeouts[1]!, { target: { value: '45' } });
    fireEvent.change(timeouts[2]!, { target: { value: '60' } });
    fireEvent.change(timeouts[3]!, { target: { value: '75' } });

    expect(onChange).toHaveBeenCalledWith({ embeddingsTimeoutSeconds: '30' });
    expect(onChange).toHaveBeenCalledWith({ imagesTimeoutSeconds: '45' });
    expect(onChange).toHaveBeenCalledWith({ speechTranscriptionTimeoutSeconds: '60' });
    expect(onChange).toHaveBeenCalledWith({ speechSynthesisTimeoutSeconds: '75' });
  });

  it('toggles each gemini optional service off', () => {
    render(
      <GeminiOptionalServicesStep
        value={createGeminiOptionalServicesForm({
          enableEmbeddings: true,
          enableImages: true,
          enableSpeechTranscription: true,
          enableSpeechSynthesis: true,
        })}
        errors={{}}
        onChange={onChange}
      />,
    );

    fireEvent.click(getServiceCardCheckbox('Embeddings'));
    fireEvent.click(getServiceCardCheckbox('Image Generation'));
    fireEvent.click(getServiceCardCheckbox('Speech Transcription'));
    fireEvent.click(getServiceCardCheckbox('Speech Synthesis'));

    expect(onChange).toHaveBeenCalledWith({ enableEmbeddings: false });
    expect(onChange).toHaveBeenCalledWith({ enableImages: false });
    expect(onChange).toHaveBeenCalledWith({ enableSpeechTranscription: false });
    expect(onChange).toHaveBeenCalledWith({ enableSpeechSynthesis: false });
  });
});
