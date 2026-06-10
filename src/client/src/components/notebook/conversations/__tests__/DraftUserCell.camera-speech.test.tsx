import React from 'react';
import { render, screen, waitFor } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import userEvent from '@testing-library/user-event';
import '@testing-library/jest-dom';
import DraftUserCell from '../DraftUserCell';

const mockAddPendingAttachment = vi.fn();

vi.mock('../../../../contexts/NotebookContext', () => ({
  useNotebook: () => ({ notebook: { id: 'nb-1', projectId: 'proj-1' } }),
}));

vi.mock('../../../../contexts/ConversationContext', () => ({
  useConversation: () => ({
    pendingAttachments: [],
    addPendingAttachment: mockAddPendingAttachment,
    removePendingAttachment: vi.fn(),
  }),
}));

vi.mock('../../../../services/notebookFiles', () => ({
  notebookFilesApi: {
    uploadFiles: vi.fn().mockResolvedValue([{ id: 'cam-1', fileName: 'camera-capture.jpg' }]),
  },
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
  default: ({
    isOpen,
    onClose,
    onCapture,
  }: {
    isOpen: boolean;
    onClose: () => void;
    onCapture: (blob: Blob, fileName: string) => Promise<void>;
  }) =>
    isOpen ? (
      <div data-testid="camera-modal">
        <button type="button" onClick={onClose}>
          Close camera
        </button>
        <button
          type="button"
          onClick={() => onCapture(new Blob(['img'], { type: 'image/jpeg' }), 'camera-capture.jpg')}
        >
          Capture
        </button>
      </div>
    ) : null,
}));

describe('DraftUserCell – camera & speech', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    Object.defineProperty(navigator, 'mediaDevices', {
      configurable: true,
      writable: true,
      value: { getUserMedia: vi.fn() },
    });
  });

  it('opens camera modal and uploads captured photo', async () => {
    const user = userEvent.setup();
    const { notebookFilesApi } = await import('../../../../services/notebookFiles');

    render(<DraftUserCell value="" onChange={vi.fn()} onSend={vi.fn()} />);

    await user.click(screen.getByRole('button', { name: 'Take photo' }));
    expect(screen.getByTestId('camera-modal')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Capture' }));

    await waitFor(() => {
      expect(notebookFilesApi.uploadFiles).toHaveBeenCalled();
      expect(mockAddPendingAttachment).toHaveBeenCalledWith(
        expect.objectContaining({ notebookFileId: 'cam-1', uploadType: 'image' })
      );
    });
  });

  it('renders voice input control when speech is supported', () => {
    render(<DraftUserCell value="" onChange={vi.fn()} onSend={vi.fn()} />);
    expect(screen.getByRole('button', { name: 'Start voice input' })).toBeInTheDocument();
  });
});
