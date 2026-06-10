import React from 'react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen } from '../../../test/test-utils';
import userEvent from '@testing-library/user-event';
import CameraCapture from '../CameraCapture';

const mockHook = {
  isCapturing: false,
  capturedImage: null as string | null,
  availableCameras: [] as MediaDeviceInfo[],
  selectedCameraId: null as string | null,
  error: null as string | null,
  isSupported: true,
  videoRef: { current: null },
  canvasRef: { current: null },
  openCamera: vi.fn(),
  captureImage: vi.fn(),
  retake: vi.fn(),
  getImageBlob: vi.fn().mockResolvedValue(new Blob(['img'], { type: 'image/jpeg' })),
  close: vi.fn(),
  switchCamera: vi.fn(),
};

vi.mock('../../../hooks/useCameraCapture', () => ({
  useCameraCapture: () => mockHook,
}));

describe('CameraCapture', () => {
  const onClose = vi.fn();
  const onCapture = vi.fn().mockResolvedValue(undefined);

  beforeEach(() => {
    vi.clearAllMocks();
    mockHook.isSupported = true;
    mockHook.capturedImage = null;
    mockHook.availableCameras = [];
    mockHook.selectedCameraId = null;
    mockHook.error = null;
    mockHook.isCapturing = false;
    mockHook.getImageBlob.mockResolvedValue(new Blob(['img'], { type: 'image/jpeg' }));
  });

  it('renders nothing when closed', () => {
    const { container } = render(
      <CameraCapture isOpen={false} onClose={onClose} onCapture={onCapture} />
    );
    expect(container).toBeEmptyDOMElement();
    expect(mockHook.openCamera).not.toHaveBeenCalled();
  });

  it('opens camera when modal becomes visible', () => {
    render(<CameraCapture isOpen onClose={onClose} onCapture={onCapture} />);
    expect(mockHook.openCamera).toHaveBeenCalledTimes(1);
    expect(screen.getByRole('dialog', { name: 'Camera capture' })).toBeInTheDocument();
    expect(screen.getByLabelText('Camera preview')).toBeInTheDocument();
  });

  it('closes camera when modal hides', () => {
    const { rerender } = render(
      <CameraCapture isOpen onClose={onClose} onCapture={onCapture} />
    );

    rerender(<CameraCapture isOpen={false} onClose={onClose} onCapture={onCapture} />);
    expect(mockHook.close).toHaveBeenCalled();
  });

  it('shows unsupported message when camera unavailable', () => {
    mockHook.isSupported = false;
    render(<CameraCapture isOpen onClose={onClose} onCapture={onCapture} />);

    expect(
      screen.getByText('Camera capture is not supported in this browser.')
    ).toBeInTheDocument();
  });

  it('captures photo from preview controls', async () => {
    const user = userEvent.setup();
    render(<CameraCapture isOpen onClose={onClose} onCapture={onCapture} />);

    await user.click(screen.getByRole('button', { name: 'Capture photo' }));
    expect(mockHook.captureImage).toHaveBeenCalledTimes(1);
  });

  it('confirms captured image and uploads', async () => {
    const user = userEvent.setup();
    mockHook.capturedImage = 'data:image/jpeg;base64,preview';

    render(<CameraCapture isOpen onClose={onClose} onCapture={onCapture} />);

    expect(screen.getByAltText('Captured photo')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Use Photo' }));

    expect(mockHook.getImageBlob).toHaveBeenCalled();
    expect(onCapture).toHaveBeenCalledWith(
      expect.any(Blob),
      expect.stringMatching(/^camera-capture-\d+\.jpg$/)
    );
    expect(mockHook.close).toHaveBeenCalled();
    expect(onClose).toHaveBeenCalled();
  });

  it('retakes after capture', async () => {
    const user = userEvent.setup();
    mockHook.capturedImage = 'data:image/jpeg;base64,preview';

    render(<CameraCapture isOpen onClose={onClose} onCapture={onCapture} />);

    await user.click(screen.getByRole('button', { name: 'Retake' }));
    expect(mockHook.retake).toHaveBeenCalledTimes(1);
  });

  it('shows camera selector when multiple cameras exist', async () => {
    const user = userEvent.setup();
    mockHook.availableCameras = [
      { deviceId: 'cam-1', kind: 'videoinput', label: 'Front', groupId: 'g', toJSON: () => ({}) },
      { deviceId: 'cam-2', kind: 'videoinput', label: 'Back', groupId: 'g', toJSON: () => ({}) },
    ] as MediaDeviceInfo[];
    mockHook.selectedCameraId = 'cam-1';

    render(<CameraCapture isOpen onClose={onClose} onCapture={onCapture} />);

    const select = screen.getByLabelText('Camera:');
    await user.selectOptions(select, 'cam-2');
    expect(mockHook.switchCamera).toHaveBeenCalledWith('cam-2');
  });

  it('closes on escape and close button', async () => {
    const user = userEvent.setup();
    render(<CameraCapture isOpen onClose={onClose} onCapture={onCapture} />);

    await user.click(screen.getByRole('button', { name: 'Close camera' }));
    expect(mockHook.close).toHaveBeenCalled();
    expect(onClose).toHaveBeenCalled();
  });

  it('displays error alert when hook reports error', () => {
    mockHook.error = 'Camera access denied. Please enable in browser settings.';
    render(<CameraCapture isOpen onClose={onClose} onCapture={onCapture} />);

    expect(screen.getByRole('alert')).toHaveTextContent('Camera access denied');
  });

  it('closes on Escape key', async () => {
    const user = userEvent.setup();
    render(<CameraCapture isOpen onClose={onClose} onCapture={onCapture} />);

    await user.keyboard('{Escape}');
    expect(mockHook.close).toHaveBeenCalled();
    expect(onClose).toHaveBeenCalled();
  });

  it('does not upload when getImageBlob returns null', async () => {
    const user = userEvent.setup();
    mockHook.capturedImage = 'data:image/jpeg;base64,preview';
    mockHook.getImageBlob.mockResolvedValue(null);

    render(<CameraCapture isOpen onClose={onClose} onCapture={onCapture} />);
    await user.click(screen.getByRole('button', { name: 'Use Photo' }));

    expect(onCapture).not.toHaveBeenCalled();
    expect(onClose).not.toHaveBeenCalled();
  });

  it('logs upload failures without closing modal', async () => {
    const user = userEvent.setup();
    const consoleSpy = vi.spyOn(console, 'error').mockImplementation(() => {});
    mockHook.capturedImage = 'data:image/jpeg;base64,preview';
    onCapture.mockRejectedValueOnce(new Error('upload failed'));

    render(<CameraCapture isOpen onClose={onClose} onCapture={onCapture} />);
    await user.click(screen.getByRole('button', { name: 'Use Photo' }));

    await vi.waitFor(() => {
      expect(consoleSpy).toHaveBeenCalled();
    });
    expect(onClose).not.toHaveBeenCalled();
    consoleSpy.mockRestore();
  });

  it('shows capture spinner while isCapturing is true', () => {
    mockHook.isCapturing = true;
    render(<CameraCapture isOpen onClose={onClose} onCapture={onCapture} />);

    expect(screen.getByRole('button', { name: 'Capture photo' })).toBeDisabled();
  });

  it('closes unsupported browser dialog via Close button', async () => {
    const user = userEvent.setup();
    mockHook.isSupported = false;

    render(<CameraCapture isOpen onClose={onClose} onCapture={onCapture} />);
    await user.click(screen.getByRole('button', { name: 'Close' }));

    expect(mockHook.close).toHaveBeenCalled();
    expect(onClose).toHaveBeenCalled();
  });

  it('shows review instructions after capture', () => {
    mockHook.capturedImage = 'data:image/jpeg;base64,preview';
    render(<CameraCapture isOpen onClose={onClose} onCapture={onCapture} />);

    expect(
      screen.getByText(/Review your photo and tap "Use Photo"/)
    ).toBeInTheDocument();
  });
});
