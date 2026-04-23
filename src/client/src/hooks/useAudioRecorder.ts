import { useState, useCallback, useRef, useEffect } from 'react';
import { transcribeAudio } from '../services/speechApi';

export interface UseAudioRecorderOptions {
  maxDurationSeconds?: number;
  /** Seconds of silence before auto-stopping. Set to 0 to disable. Default: 2 */
  silenceTimeoutSeconds?: number;
  /** Audio level threshold (0-1) below which is considered silence. Default: 0.03 */
  silenceThreshold?: number;
  onTranscriptionComplete?: (text: string) => void;
  onError?: (error: string) => void;
}

export interface UseAudioRecorderResult {
  // State
  isRecording: boolean;
  isProcessing: boolean;
  duration: number;
  error: string | null;
  isSupported: boolean;

  // Actions
  startRecording: () => Promise<void>;
  stopRecording: () => Promise<string | null>;
  cancelRecording: () => void;
}

/**
 * Hook for managing audio recording and transcription.
 * Uses MediaRecorder API to capture audio and sends it to the server for transcription.
 * Includes silence detection to auto-stop when no speech is detected.
 */
export function useAudioRecorder(options: UseAudioRecorderOptions = {}): UseAudioRecorderResult {
  const {
    maxDurationSeconds = 60,
    silenceTimeoutSeconds = 2,
    silenceThreshold = 0.03,
    onTranscriptionComplete,
    onError,
  } = options;

  const [isRecording, setIsRecording] = useState(false);
  const [isProcessing, setIsProcessing] = useState(false);
  const [duration, setDuration] = useState(0);
  const [error, setError] = useState<string | null>(null);

  const mediaRecorderRef = useRef<MediaRecorder | null>(null);
  const audioChunksRef = useRef<Blob[]>([]);
  const streamRef = useRef<MediaStream | null>(null);
  const durationIntervalRef = useRef<NodeJS.Timeout | null>(null);
  const startTimeRef = useRef<number>(0);
  const maxDurationTimeoutRef = useRef<NodeJS.Timeout | null>(null);
  
  // Silence detection refs
  const audioContextRef = useRef<AudioContext | null>(null);
  const analyserRef = useRef<AnalyserNode | null>(null);
  const silenceStartRef = useRef<number | null>(null);
  const silenceCheckIntervalRef = useRef<NodeJS.Timeout | null>(null);
  
  // Flag to track intentional stops (to avoid processing cancelled recordings)
  const shouldStopRef = useRef<boolean>(false);

  // Check browser support
  const isSupported = typeof navigator !== 'undefined' 
    && navigator.mediaDevices 
    && typeof navigator.mediaDevices.getUserMedia === 'function'
    && typeof MediaRecorder !== 'undefined';

  // Cleanup function
  const cleanup = useCallback(() => {
    if (durationIntervalRef.current) {
      clearInterval(durationIntervalRef.current);
      durationIntervalRef.current = null;
    }
    if (maxDurationTimeoutRef.current) {
      clearTimeout(maxDurationTimeoutRef.current);
      maxDurationTimeoutRef.current = null;
    }
    if (silenceCheckIntervalRef.current) {
      clearInterval(silenceCheckIntervalRef.current);
      silenceCheckIntervalRef.current = null;
    }
    if (audioContextRef.current) {
      audioContextRef.current.close().catch(() => {});
      audioContextRef.current = null;
    }
    analyserRef.current = null;
    silenceStartRef.current = null;
    if (streamRef.current) {
      streamRef.current.getTracks().forEach(track => track.stop());
      streamRef.current = null;
    }
    mediaRecorderRef.current = null;
    audioChunksRef.current = [];
  }, []);

  // Cleanup on unmount
  useEffect(() => {
    return () => {
      cleanup();
    };
  }, [cleanup]);

  const startRecording = useCallback(async () => {
    if (!isSupported) {
      const msg = 'Voice recording not supported in this browser.';
      setError(msg);
      onError?.(msg);
      return;
    }

    if (isRecording || isProcessing) {
      return;
    }

    setError(null);
    audioChunksRef.current = [];

    try {
      const stream = await navigator.mediaDevices.getUserMedia({ audio: true });
      streamRef.current = stream;

      // Determine best supported MIME type
      const mimeTypes = [
        'audio/webm;codecs=opus',
        'audio/webm',
        'audio/ogg;codecs=opus',
        'audio/ogg',
        'audio/mp4',
      ];
      
      let selectedMimeType = '';
      for (const mimeType of mimeTypes) {
        if (MediaRecorder.isTypeSupported(mimeType)) {
          selectedMimeType = mimeType;
          break;
        }
      }

      const mediaRecorder = new MediaRecorder(stream, {
        mimeType: selectedMimeType || undefined,
      });
      mediaRecorderRef.current = mediaRecorder;

      mediaRecorder.ondataavailable = (event) => {
        if (event.data.size > 0) {
          audioChunksRef.current.push(event.data);
        }
      };

      mediaRecorder.onerror = () => {
        const msg = 'Recording error occurred.';
        setError(msg);
        onError?.(msg);
        setIsRecording(false);
        cleanup();
      };

      // Set up onstop handler here so it works for both manual and auto-stop
      mediaRecorder.onstop = async () => {
        setIsRecording(false);
        
        // Clear all timers and silence detection
        if (durationIntervalRef.current) {
          clearInterval(durationIntervalRef.current);
          durationIntervalRef.current = null;
        }
        if (maxDurationTimeoutRef.current) {
          clearTimeout(maxDurationTimeoutRef.current);
          maxDurationTimeoutRef.current = null;
        }
        if (silenceCheckIntervalRef.current) {
          clearInterval(silenceCheckIntervalRef.current);
          silenceCheckIntervalRef.current = null;
        }
        if (audioContextRef.current) {
          audioContextRef.current.close().catch(() => {});
          audioContextRef.current = null;
        }
        analyserRef.current = null;

        // Stop all tracks
        if (streamRef.current) {
          streamRef.current.getTracks().forEach(track => track.stop());
          streamRef.current = null;
        }

        const chunks = audioChunksRef.current;
        const recordedDuration = Math.floor((Date.now() - startTimeRef.current) / 1000);
        
        if (chunks.length === 0 || recordedDuration < 1) {
          const msg = 'Recording too short. Please try again.';
          setError(msg);
          onError?.(msg);
          audioChunksRef.current = [];
          mediaRecorderRef.current = null;
          return;
        }

        const audioBlob = new Blob(chunks, { type: mediaRecorder.mimeType || 'audio/webm' });
        audioChunksRef.current = [];
        mediaRecorderRef.current = null;

        // Transcribe
        setIsProcessing(true);
        try {
          const result = await transcribeAudio({ audioBlob });
          
          if (!result.text || result.text.trim() === '') {
            const msg = 'No speech detected. Please try again.';
            setError(msg);
            onError?.(msg);
          } else {
            setError(null);
            onTranscriptionComplete?.(result.text);
          }
        } catch (err) {
          const msg = err instanceof Error ? err.message : 'Transcription failed. Please try again.';
          setError(msg);
          onError?.(msg);
        } finally {
          setIsProcessing(false);
        }
      };

      mediaRecorder.start(100); // Collect data every 100ms
      setIsRecording(true);
      setDuration(0);
      startTimeRef.current = Date.now();
      silenceStartRef.current = null;

      // Update duration every 100ms
      durationIntervalRef.current = setInterval(() => {
        const elapsed = Math.floor((Date.now() - startTimeRef.current) / 1000);
        setDuration(elapsed);
      }, 100);

      // Auto-stop at max duration - directly stop MediaRecorder to avoid stale closure
      maxDurationTimeoutRef.current = setTimeout(() => {
        if (mediaRecorderRef.current?.state === 'recording') {
          shouldStopRef.current = true;
          mediaRecorderRef.current.stop();
        }
      }, maxDurationSeconds * 1000);

      // Set up silence detection using Web Audio API
      if (silenceTimeoutSeconds > 0) {
        try {
          const audioContext = new AudioContext();
          audioContextRef.current = audioContext;
          
          const source = audioContext.createMediaStreamSource(stream);
          const analyser = audioContext.createAnalyser();
          analyser.fftSize = 256;
          analyser.smoothingTimeConstant = 0.3; // Lower smoothing for faster response
          source.connect(analyser);
          analyserRef.current = analyser;

          const dataArray = new Uint8Array(analyser.frequencyBinCount);
          let logCounter = 0;

          console.log('[AudioRecorder] Silence detection initialized', { silenceTimeoutSeconds, silenceThreshold });
          
          // Start silence timer immediately
          silenceStartRef.current = Date.now();

          // Check audio level every 100ms
          silenceCheckIntervalRef.current = setInterval(() => {
            if (!analyserRef.current || !mediaRecorderRef.current || mediaRecorderRef.current.state !== 'recording') {
              return;
            }

            analyserRef.current.getByteFrequencyData(dataArray);
            
            // Calculate average audio level (0-255 -> 0-1)
            const average = dataArray.reduce((acc, val) => acc + val, 0) / dataArray.length / 255;
            const isSilent = average < silenceThreshold;

            // Log every 500ms (every 5 intervals) for better visibility
            logCounter++;
            if (logCounter % 5 === 0) {
              const silenceElapsed = silenceStartRef.current ? ((Date.now() - silenceStartRef.current) / 1000).toFixed(1) : '0';
              console.log(`[AudioRecorder] level=${average.toFixed(3)} silent=${isSilent} silenceTimer=${silenceElapsed}s`);
            }

            if (!isSilent) {
              // Speech detected - reset silence timer
              silenceStartRef.current = Date.now();
            } else {
              // Silent - check if we've exceeded the timeout
              const silenceDuration = (Date.now() - silenceStartRef.current!) / 1000;
              if (silenceDuration >= silenceTimeoutSeconds) {
                console.log('[AudioRecorder] >>> AUTO-STOPPING after', silenceDuration.toFixed(1), 's of silence <<<');
                if (mediaRecorderRef.current?.state === 'recording') {
                  mediaRecorderRef.current.stop();
                }
              }
            }
          }, 100);
        } catch (err) {
          // Silence detection is optional - continue without it if AudioContext fails
          console.warn('[AudioRecorder] Silence detection unavailable:', err);
        }
      }

    } catch (err) {
      let msg = 'Failed to access microphone.';
      if (err instanceof DOMException) {
        if (err.name === 'NotAllowedError' || err.name === 'PermissionDeniedError') {
          msg = 'Microphone access denied. Please enable in browser settings.';
        } else if (err.name === 'NotFoundError') {
          msg = 'No microphone found on this device.';
        }
      }
      setError(msg);
      onError?.(msg);
      cleanup();
    }
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [isSupported, isRecording, isProcessing, maxDurationSeconds, silenceTimeoutSeconds, silenceThreshold, onError, cleanup]);

  const stopRecording = useCallback(async (): Promise<string | null> => {
    if (!mediaRecorderRef.current || mediaRecorderRef.current.state !== 'recording') {
      return null;
    }
    
    // Simply stop the recorder - the onstop handler set during startRecording will handle everything
    mediaRecorderRef.current.stop();
    return null; // Note: transcription happens asynchronously via the onstop handler
  }, []);

  const cancelRecording = useCallback(() => {
    if (mediaRecorderRef.current?.state === 'recording') {
      mediaRecorderRef.current.stop();
    }
    setIsRecording(false);
    setIsProcessing(false);
    setDuration(0);
    setError(null);
    cleanup();
  }, [cleanup]);

  return {
    isRecording,
    isProcessing,
    duration,
    error,
    isSupported,
    startRecording,
    stopRecording,
    cancelRecording,
  };
}

export default useAudioRecorder;

