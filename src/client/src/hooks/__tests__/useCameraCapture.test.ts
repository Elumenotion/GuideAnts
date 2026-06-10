import { describe, it, expect, vi, beforeEach } from 'vitest';
import { renderHook, act, waitFor } from '@testing-library/react';
import { useCameraCapture } from '../useCameraCapture';

function createMockStream(deviceId = 'cam-1') {
  const track = {
    stop: vi.fn(),
    getSettings: () => ({ deviceId }),
  };
  return {
    getTracks: () => [track],
    getVideoTracks: () => [track],
  } as unknown as MediaStream;
}

function attachVideoAndCanvas(result: ReturnType<typeof renderHook<typeof useCameraCapture>>['result']) {
  const video = document.createElement('video');
  Object.defineProperty(video, 'videoWidth', { value: 640, configurable: true });
  Object.defineProperty(video, 'videoHeight', { value: 480, configurable: true });
  video.play = vi.fn().mockResolvedValue(undefined);

  const ctx = {
    drawImage: vi.fn(),
  };
  const canvas = document.createElement('canvas');
  canvas.getContext = vi.fn().mockReturnValue(ctx);
  canvas.toDataURL = vi.fn().mockReturnValue('data:image/jpeg;base64,abc');
  canvas.toBlob = vi.fn((cb: BlobCallback) => {
    cb(new Blob(['jpeg'], { type: 'image/jpeg' }));
  });

  result.current.videoRef.current = video;
  result.current.canvasRef.current = canvas;

  return { video, canvas, ctx };
}

describe('useCameraCapture', () => {
  const getUserMedia = vi.fn();
  const enumerateDevices = vi.fn();

  beforeEach(() => {
    vi.clearAllMocks();
    getUserMedia.mockResolvedValue(createMockStream());
    enumerateDevices.mockResolvedValue([
      { kind: 'videoinput', deviceId: 'cam-1', label: 'Front' },
      { kind: 'videoinput', deviceId: 'cam-2', label: 'Back' },
      { kind: 'audioinput', deviceId: 'mic-1', label: 'Mic' },
    ] as MediaDeviceInfo[]);

    Object.defineProperty(navigator, 'mediaDevices', {
      configurable: true,
      value: { getUserMedia, enumerateDevices },
    });
  });

  it('reports support when getUserMedia is available', () => {
    const { result } = renderHook(() => useCameraCapture());
    expect(result.current.isSupported).toBe(true);
  });

  it('opens camera and loads available devices', async () => {
    const { result } = renderHook(() => useCameraCapture());
    attachVideoAndCanvas(result);

    await act(async () => {
      await result.current.openCamera();
    });

    expect(result.current.isOpen).toBe(true);
    expect(getUserMedia).toHaveBeenCalled();
    expect(result.current.availableCameras).toHaveLength(2);
    expect(result.current.selectedCameraId).toBe('cam-1');
    expect(result.current.error).toBeNull();
  });

  it('captures an image from the video stream', async () => {
    const { result } = renderHook(() => useCameraCapture({ maxWidth: 320 }));
    const { canvas, ctx } = attachVideoAndCanvas(result);

    await act(async () => {
      await result.current.openCamera();
    });

    act(() => {
      result.current.captureImage();
    });

    expect(ctx.drawImage).toHaveBeenCalled();
    expect(canvas.toDataURL).toHaveBeenCalledWith('image/jpeg', 0.85);
    expect(result.current.capturedImage).toBe('data:image/jpeg;base64,abc');

    const blob = await result.current.getImageBlob();
    expect(blob).toBeInstanceOf(Blob);
  });

  it('retake clears capture and restarts stream', async () => {
    const { result } = renderHook(() => useCameraCapture());
    attachVideoAndCanvas(result);

    await act(async () => {
      await result.current.openCamera();
    });

    act(() => {
      result.current.captureImage();
    });

    expect(result.current.capturedImage).not.toBeNull();

    await act(async () => {
      await result.current.retake();
    });

    expect(result.current.capturedImage).toBeNull();
    expect(getUserMedia.mock.calls.length).toBeGreaterThan(1);
  });

  it('switchCamera requests stream for selected device', async () => {
    const { result } = renderHook(() => useCameraCapture());
    attachVideoAndCanvas(result);

    await act(async () => {
      await result.current.openCamera();
    });

    await act(async () => {
      await result.current.switchCamera('cam-2');
    });

    expect(getUserMedia).toHaveBeenCalledWith({
      video: { deviceId: { exact: 'cam-2' } },
    });
  });

  it('close stops stream and resets state', async () => {
    const { result } = renderHook(() => useCameraCapture());
    attachVideoAndCanvas(result);

    await act(async () => {
      await result.current.openCamera();
    });

    act(() => {
      result.current.close();
    });

    expect(result.current.isOpen).toBe(false);
    expect(result.current.capturedImage).toBeNull();
    expect(result.current.selectedCameraId).toBeNull();
  });

  it('handles camera permission denied', async () => {
    const onError = vi.fn();
    getUserMedia.mockRejectedValue(new DOMException('Denied', 'NotAllowedError'));

    const { result } = renderHook(() => useCameraCapture({ onError }));
    attachVideoAndCanvas(result);

    await act(async () => {
      await result.current.openCamera();
    });

    await waitFor(() => {
      expect(result.current.error).toBe(
        'Camera access denied. Please enable in browser settings.'
      );
    });
    expect(onError).toHaveBeenCalled();
  });

  it('reports unsupported browser on open', async () => {
    const onError = vi.fn();
    Object.defineProperty(navigator, 'mediaDevices', {
      configurable: true,
      value: undefined,
    });

    const { result } = renderHook(() => useCameraCapture({ onError }));

    await act(async () => {
      await result.current.openCamera();
    });

    expect(result.current.error).toBe('Camera capture not supported in this browser.');
    expect(onError).toHaveBeenCalled();
  });
});
