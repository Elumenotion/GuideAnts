import React from 'react';
import { render, screen, waitFor, fireEvent } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach, beforeAll } from 'vitest';
import userEvent from '@testing-library/user-event';
import '@testing-library/jest-dom';
import DraftUserCell from '../DraftUserCell';

vi.mock('../../../../contexts/NotebookContext', () => ({
  useNotebook: () => ({ notebook: { id: 'nb-1', projectId: 'proj-1' } }),
}));

vi.mock('../../../../contexts/ConversationContext', () => ({
  useConversation: () => ({
    pendingAttachments: [],
    addPendingAttachment: vi.fn(),
    removePendingAttachment: vi.fn(),
  }),
}));

vi.mock('../../../../services/notebookFiles', () => ({
  notebookFilesApi: { uploadFiles: vi.fn().mockResolvedValue([]) },
}));

vi.mock('../../../../hooks/useAudioRecorder', () => ({
  useAudioRecorder: () => ({
    isRecording: false,
    isProcessing: false,
    duration: 0,
    isSupported: true,
    startRecording: vi.fn(),
    stopRecording: vi.fn(),
  }),
}));

vi.mock('../../../common/CameraCapture', () => ({
  default: ({ isOpen, onClose }: { isOpen: boolean; onClose: () => void }) =>
    isOpen ? (
      <div data-testid="camera-modal">
        <button onClick={onClose}>Close camera</button>
      </div>
    ) : null,
}));

const assistants = [
  { name: 'Alpha Guide', model: 'gpt-4' },
  { name: 'Beta Guide', model: 'gpt-4' },
];

describe('DraftUserCell – keyboard & fullscreen flows', () => {
  beforeAll(() => {
    Object.defineProperty(navigator, 'mediaDevices', {
      configurable: true,
      value: { getUserMedia: vi.fn().mockResolvedValue({ getTracks: () => [] }) },
    });
  });

  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('filters assistants while typing in selector', async () => {
    const user = userEvent.setup();
    render(
      <DraftUserCell
        value=""
        onChange={vi.fn()}
        onSend={vi.fn()}
        assistants={assistants}
        onAssistantSelect={vi.fn()}
      />
    );

    const compose = screen.getByRole('group', { name: 'Compose message' });
    await user.click(screen.getByRole('textbox'));
    await user.type(screen.getByRole('textbox'), '@');

    await waitFor(() => expect(screen.getByText(/Select Guide/)).toBeInTheDocument());

    const editorWrapper = compose.querySelector('.p-2') as HTMLElement;
    fireEvent.keyDown(editorWrapper, { key: 'b', code: 'KeyB' });

    await waitFor(() => {
      expect(screen.getByText('Beta Guide')).toBeInTheDocument();
    });
  });

  it('closes assistant selector on Escape', async () => {
    const user = userEvent.setup();
    render(
      <DraftUserCell
        value=""
        onChange={vi.fn()}
        onSend={vi.fn()}
        assistants={assistants}
      />
    );

    const compose = screen.getByRole('group', { name: 'Compose message' });
    await user.click(screen.getByRole('textbox'));
    await user.type(screen.getByRole('textbox'), '@');
    await waitFor(() => expect(screen.getByText(/Select Guide/)).toBeInTheDocument());

    const editorWrapper = compose.querySelector('.p-2') as HTMLElement;
    fireEvent.keyDown(editorWrapper, { key: 'Escape', code: 'Escape' });

    await waitFor(() => {
      expect(screen.queryByText(/Select Guide/)).not.toBeInTheDocument();
    });
  });

  it('opens and closes camera capture modal', async () => {
    const user = userEvent.setup();
    render(
      <DraftUserCell value="" onChange={vi.fn()} onSend={vi.fn()} />
    );

    await user.click(screen.getByLabelText('Take photo'));
    expect(screen.getByTestId('camera-modal')).toBeInTheDocument();
    await user.click(screen.getByText('Close camera'));
    expect(screen.queryByTestId('camera-modal')).not.toBeInTheDocument();
  });

  it('shows microphone button when speech is supported', () => {
    render(
      <DraftUserCell value="" onChange={vi.fn()} onSend={vi.fn()} />
    );
    expect(screen.getByLabelText('Start voice input')).toBeInTheDocument();
  });
});
