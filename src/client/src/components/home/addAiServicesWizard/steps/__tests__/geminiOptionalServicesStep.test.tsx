import { describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import { GeminiOptionalServicesStep } from '../GeminiOptionalServicesStep';

const emptyValue = {
  enableEmbeddings: false,
  embeddingsModelId: '',
  embeddingsTimeoutSeconds: '30',
  enableImages: false,
  imagesModelId: '',
  imagesTimeoutSeconds: '60',
  enableSpeechTranscription: false,
  speechTranscriptionModelId: '',
  speechTranscriptionTimeoutSeconds: '120',
  enableSpeechSynthesis: false,
  speechSynthesisModelId: '',
  speechSynthesisVoiceName: '',
  speechSynthesisTimeoutSeconds: '60',
};

describe('GeminiOptionalServicesStep', () => {
  it('renders all optional service cards', () => {
    render(
      <GeminiOptionalServicesStep value={emptyValue} errors={{}} onChange={vi.fn()} />
    );
    expect(screen.getByText(/optional google gemini services/i)).toBeInTheDocument();
    expect(screen.getByText('Embeddings')).toBeInTheDocument();
    expect(screen.getByText('Image Generation')).toBeInTheDocument();
    expect(screen.getByText('Speech Transcription')).toBeInTheDocument();
    expect(screen.getByText('Speech Synthesis')).toBeInTheDocument();
  });

  it('enables embeddings fields when toggled on', () => {
    const onChange = vi.fn();
    render(
      <GeminiOptionalServicesStep value={emptyValue} errors={{}} onChange={onChange} />
    );
    const toggles = screen.getAllByLabelText('Configure now');
    fireEvent.click(toggles[0]);
    expect(onChange).toHaveBeenCalledWith({ enableEmbeddings: true });
  });

  it('shows field errors when provided', () => {
    render(
      <GeminiOptionalServicesStep
        value={{ ...emptyValue, enableEmbeddings: true }}
        errors={{ embeddingsModelId: 'Required' }}
        onChange={vi.fn()}
      />
    );
    expect(screen.getByText('Required')).toBeInTheDocument();
  });

  it('updates embedding model id', () => {
    const onChange = vi.fn();
    render(
      <GeminiOptionalServicesStep
        value={{ ...emptyValue, enableEmbeddings: true, embeddingsModelId: '' }}
        errors={{}}
        onChange={onChange}
      />
    );
    fireEvent.change(screen.getByLabelText(/embedding model id/i), {
      target: { value: 'gemini-embedding-2' },
    });
    expect(onChange).toHaveBeenCalledWith({ embeddingsModelId: 'gemini-embedding-2' });
  });

  it('enables TTS fields and updates voice name', () => {
    const onChange = vi.fn();
    render(
      <GeminiOptionalServicesStep
        value={{ ...emptyValue, enableSpeechSynthesis: true }}
        errors={{}}
        onChange={onChange}
      />
    );
    fireEvent.change(screen.getByLabelText(/voice name/i), { target: { value: 'Kore' } });
    expect(onChange).toHaveBeenCalledWith({ speechSynthesisVoiceName: 'Kore' });
  });
});
