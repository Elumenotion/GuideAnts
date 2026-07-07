import { describe, expect, it, vi } from 'vitest';
import userEvent from '@testing-library/user-event';
import { render, screen } from '@testing-library/react';
import '@testing-library/jest-dom';
import { ServicesTab } from '../ServicesTab';

vi.mock('../../editors/embeddings/EmbeddingsEditor', () => ({
  EmbeddingsEditor: () => <div>embeddings-editor</div>,
}));
vi.mock('../../editors/image-generation/ImageGenerationEditor', () => ({
  ImageGenerationEditor: () => <div>image-generation-editor</div>,
}));
vi.mock('../../editors/document-intelligence/DocumentIntelligenceEditor', () => ({
  DocumentIntelligenceEditor: () => <div>document-intelligence-editor</div>,
}));
vi.mock('../../editors/speech-transcription/SpeechTranscriptionEditor', () => ({
  SpeechTranscriptionEditor: () => <div>speech-transcription-editor</div>,
}));
vi.mock('../../editors/speech-synthesis/SpeechSynthesisEditor', () => ({
  SpeechSynthesisEditor: () => <div>speech-synthesis-editor</div>,
}));

describe('ServicesTab', () => {
  it('switches between service editors', async () => {
    const user = userEvent.setup();
    const onFocusedServiceHandled = vi.fn();

    render(
      <ServicesTab
        focusedService="Embeddings"
        onFocusedServiceHandled={onFocusedServiceHandled}
      />,
    );

    expect(screen.getByText('embeddings-editor')).toBeInTheDocument();
    expect(onFocusedServiceHandled).toHaveBeenCalled();

    await user.click(screen.getByRole('button', { name: 'Speech Synthesis' }));
    expect(screen.getByText('speech-synthesis-editor')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Image Generation' }));
    expect(screen.getByText('image-generation-editor')).toBeInTheDocument();
  });
});
