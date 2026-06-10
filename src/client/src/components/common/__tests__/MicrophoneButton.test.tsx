import React from 'react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import MicrophoneButton from '../MicrophoneButton';

describe('MicrophoneButton', () => {
  const defaultProps = {
    isRecording: false,
    isProcessing: false,
    duration: 0,
    isSupported: true,
    onStartRecording: vi.fn(),
    onStopRecording: vi.fn(),
  };

  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('renders nothing when not supported', () => {
    const { container } = render(
      <MicrophoneButton {...defaultProps} isSupported={false} />
    );
    expect(container).toBeEmptyDOMElement();
  });

  it('starts recording on click in idle state', async () => {
    const user = userEvent.setup();
    render(<MicrophoneButton {...defaultProps} />);

    await user.click(screen.getByRole('button', { name: 'Start voice input' }));
    expect(defaultProps.onStartRecording).toHaveBeenCalledTimes(1);
    expect(defaultProps.onStopRecording).not.toHaveBeenCalled();
  });

  it('does not start when disabled', async () => {
    const user = userEvent.setup();
    render(<MicrophoneButton {...defaultProps} disabled />);

    await user.click(screen.getByRole('button', { name: 'Start voice input' }));
    expect(defaultProps.onStartRecording).not.toHaveBeenCalled();
  });

  it('shows duration and stop control while recording', async () => {
    const user = userEvent.setup();
    render(<MicrophoneButton {...defaultProps} isRecording duration={65} />);

    expect(screen.getByText('1:05')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Stop listening' }));
    expect(defaultProps.onStopRecording).toHaveBeenCalledTimes(1);
  });

  it('shows transcribing state and ignores clicks', async () => {
    const user = userEvent.setup();
    render(<MicrophoneButton {...defaultProps} isProcessing />);

    expect(screen.getByText('Transcribing...')).toBeInTheDocument();
    expect(screen.queryByRole('button')).not.toBeInTheDocument();

    const container = screen.getByText('Transcribing...').closest('div');
    if (container) {
      await user.click(container);
    }
    expect(defaultProps.onStartRecording).not.toHaveBeenCalled();
    expect(defaultProps.onStopRecording).not.toHaveBeenCalled();
  });
});
