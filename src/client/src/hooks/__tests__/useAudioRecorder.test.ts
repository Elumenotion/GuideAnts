import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import { useAudioRecorder } from '../useAudioRecorder';
import { transcribeAudio } from '../../services/speechApi';

vi.mock('../../services/speechApi', () => ({
  transcribeAudio: vi.fn(),
}));

function createMockStream() {
  const track = { stop: vi.fn() };
  return {
    getTracks: () => [track],
  } as unknown as MediaStream;
}

async function flushPromises() {
  await Promise.resolve();
  await Promise.resolve();
}

describe('useAudioRecorder', () => {
  const getUserMedia = vi.fn();

  beforeEach(() => {
    vi.useFakeTimers();
    vi.clearAllMocks();
    getUserMedia.mockResolvedValue(createMockStream());
    Object.defineProperty(navigator, 'mediaDevices', {
      configurable: true,
      value: { getUserMedia },
    });
    (transcribeAudio as ReturnType<typeof vi.fn>).mockResolvedValue({ text: 'Hello world' });
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('reports browser support when APIs are available', () => {
    const { result } = renderHook(() => useAudioRecorder());
    expect(result.current.isSupported).toBe(true);
  });

  it('starts and stops recording then transcribes', async () => {
    const onTranscriptionComplete = vi.fn();
    const { result } = renderHook(() =>
      useAudioRecorder({
        silenceTimeoutSeconds: 0,
        onTranscriptionComplete,
      })
    );

    await act(async () => {
      await result.current.startRecording();
    });

    expect(result.current.isRecording).toBe(true);
    expect(getUserMedia).toHaveBeenCalledWith({ audio: true });

    await act(async () => {
      vi.advanceTimersByTime(1100);
    });

    await act(async () => {
      await result.current.stopRecording();
      await flushPromises();
    });

    expect(transcribeAudio).toHaveBeenCalled();
    expect(onTranscriptionComplete).toHaveBeenCalledWith('Hello world');
    expect(result.current.isRecording).toBe(false);
    expect(result.current.isProcessing).toBe(false);
    expect(result.current.error).toBeNull();
  });

  it('sets error when recording is too short', async () => {
    const onError = vi.fn();
    const { result } = renderHook(() =>
      useAudioRecorder({ silenceTimeoutSeconds: 0, onError })
    );

    await act(async () => {
      await result.current.startRecording();
    });

    await act(async () => {
      await result.current.stopRecording();
      await flushPromises();
    });

    expect(result.current.error).toBe('Recording too short. Please try again.');
    expect(onError).toHaveBeenCalledWith('Recording too short. Please try again.');
    expect(transcribeAudio).not.toHaveBeenCalled();
  });

  it('cancelRecording cleans up without transcribing', async () => {
    const { result } = renderHook(() =>
      useAudioRecorder({ silenceTimeoutSeconds: 0 })
    );

    await act(async () => {
      await result.current.startRecording();
    });

    act(() => {
      result.current.cancelRecording();
    });

    await act(async () => {
      await flushPromises();
    });

    expect(result.current.isRecording).toBe(false);
    expect(result.current.duration).toBe(0);
    expect(transcribeAudio).not.toHaveBeenCalled();
  });

  it('handles microphone permission denied', async () => {
    const onError = vi.fn();
    const denied = new DOMException('Permission denied', 'NotAllowedError');
    getUserMedia.mockRejectedValue(denied);

    const { result } = renderHook(() =>
      useAudioRecorder({ onError })
    );

    await act(async () => {
      await result.current.startRecording();
    });

    expect(result.current.error).toBe(
      'Microphone access denied. Please enable in browser settings.'
    );
    expect(onError).toHaveBeenCalled();
    expect(result.current.isRecording).toBe(false);
  });

  it('handles empty transcription result', async () => {
    const onError = vi.fn();
    (transcribeAudio as ReturnType<typeof vi.fn>).mockResolvedValue({ text: '   ' });

    const { result } = renderHook(() =>
      useAudioRecorder({ silenceTimeoutSeconds: 0, onError })
    );

    await act(async () => {
      await result.current.startRecording();
    });

    await act(async () => {
      vi.advanceTimersByTime(1100);
    });

    await act(async () => {
      await result.current.stopRecording();
      await flushPromises();
    });

    expect(result.current.error).toBe('No speech detected. Please try again.');
    expect(onError).toHaveBeenCalledWith('No speech detected. Please try again.');
  });

  it('reports unsupported browser', async () => {
    const onError = vi.fn();
    Object.defineProperty(navigator, 'mediaDevices', {
      configurable: true,
      value: undefined,
    });

    const { result } = renderHook(() =>
      useAudioRecorder({ onError })
    );

    expect(result.current.isSupported).toBeFalsy();

    await act(async () => {
      await result.current.startRecording();
    });

    expect(result.current.error).toBe('Voice recording not supported in this browser.');
    expect(onError).toHaveBeenCalled();
  });

  it('updates duration while recording', async () => {
    const { result } = renderHook(() =>
      useAudioRecorder({ silenceTimeoutSeconds: 0 })
    );

    await act(async () => {
      await result.current.startRecording();
    });

    await act(async () => {
      vi.advanceTimersByTime(2100);
    });

    expect(result.current.duration).toBeGreaterThanOrEqual(2);
  });

  it('returns null when stopRecording is called while idle', async () => {
    const { result } = renderHook(() => useAudioRecorder());

    let stopResult: string | null = 'pending';
    await act(async () => {
      stopResult = await result.current.stopRecording();
    });

    expect(stopResult).toBeNull();
  });

  it('ignores startRecording when already recording', async () => {
    const { result } = renderHook(() =>
      useAudioRecorder({ silenceTimeoutSeconds: 0 })
    );

    await act(async () => {
      await result.current.startRecording();
    });

    await act(async () => {
      await result.current.startRecording();
    });

    expect(getUserMedia).toHaveBeenCalledTimes(1);
  });

  it('handles microphone not found', async () => {
    const onError = vi.fn();
    getUserMedia.mockRejectedValue(new DOMException('No device', 'NotFoundError'));

    const { result } = renderHook(() =>
      useAudioRecorder({ onError })
    );

    await act(async () => {
      await result.current.startRecording();
    });

    expect(result.current.error).toBe('No microphone found on this device.');
    expect(onError).toHaveBeenCalledWith('No microphone found on this device.');
  });

  it('handles transcription API failures', async () => {
    const onError = vi.fn();
    (transcribeAudio as ReturnType<typeof vi.fn>).mockRejectedValue(new Error('Service unavailable'));

    const { result } = renderHook(() =>
      useAudioRecorder({ silenceTimeoutSeconds: 0, onError })
    );

    await act(async () => {
      await result.current.startRecording();
    });

    await act(async () => {
      vi.advanceTimersByTime(1100);
    });

    await act(async () => {
      await result.current.stopRecording();
      await flushPromises();
    });

    expect(result.current.error).toBe('Service unavailable');
    expect(onError).toHaveBeenCalledWith('Service unavailable');
  });

  it('auto-stops at max duration', async () => {
    const onTranscriptionComplete = vi.fn();
    const { result } = renderHook(() =>
      useAudioRecorder({
        maxDurationSeconds: 1,
        silenceTimeoutSeconds: 0,
        onTranscriptionComplete,
      })
    );

    await act(async () => {
      await result.current.startRecording();
    });

    await act(async () => {
      vi.advanceTimersByTime(1100);
    });

    await act(async () => {
      await flushPromises();
    });

    expect(transcribeAudio).toHaveBeenCalled();
    expect(onTranscriptionComplete).toHaveBeenCalledWith('Hello world');
    expect(result.current.isRecording).toBe(false);
  });

  it('handles MediaRecorder errors', async () => {
    const onError = vi.fn();
    const OriginalMediaRecorder = global.MediaRecorder;

    class ErrorMediaRecorder extends OriginalMediaRecorder {
      start() {
        super.start();
        this.onerror?.(new Event('error'));
      }
    }

    // @ts-expect-error test override
    global.MediaRecorder = ErrorMediaRecorder;

    const { result } = renderHook(() =>
      useAudioRecorder({ silenceTimeoutSeconds: 0, onError })
    );

    await act(async () => {
      await result.current.startRecording();
    });

    expect(result.current.error).toBe('Recording error occurred.');
    expect(onError).toHaveBeenCalledWith('Recording error occurred.');

    global.MediaRecorder = OriginalMediaRecorder;
  });

  it('continues recording when silence detection setup fails', async () => {
    const warnSpy = vi.spyOn(console, 'warn').mockImplementation(() => {});
    const OriginalAudioContext = global.AudioContext;

    // @ts-expect-error test override
    global.AudioContext = class {
      constructor() {
        throw new Error('AudioContext unavailable');
      }
    };

    const { result } = renderHook(() =>
      useAudioRecorder({ silenceTimeoutSeconds: 2 })
    );

    await act(async () => {
      await result.current.startRecording();
    });

    expect(result.current.isRecording).toBe(true);
    expect(warnSpy).toHaveBeenCalled();

    act(() => {
      result.current.cancelRecording();
    });

    global.AudioContext = OriginalAudioContext;
    warnSpy.mockRestore();
  });

  it('cleans up on unmount', async () => {
    const { result, unmount } = renderHook(() =>
      useAudioRecorder({ silenceTimeoutSeconds: 0 })
    );

    await act(async () => {
      await result.current.startRecording();
    });

    unmount();
    expect(result.current.isRecording).toBe(true);
  });
});
