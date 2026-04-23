import { useState, useCallback, useRef, useEffect } from 'react';

export interface UseCameraCaptureOptions {
  maxWidth?: number;
  maxHeight?: number;
  imageQuality?: number;
  preferredFacingMode?: 'user' | 'environment';
  onError?: (error: string) => void;
}

export interface UseCameraCaptureResult {
  // State
  isOpen: boolean;
  isCapturing: boolean;
  capturedImage: string | null;
  availableCameras: MediaDeviceInfo[];
  selectedCameraId: string | null;
  error: string | null;
  isSupported: boolean;

  // Refs for video/canvas
  videoRef: React.RefObject<HTMLVideoElement | null>;
  canvasRef: React.RefObject<HTMLCanvasElement | null>;

  // Actions
  openCamera: () => Promise<void>;
  captureImage: () => void;
  retake: () => void;
  getImageBlob: () => Promise<Blob | null>;
  close: () => void;
  switchCamera: (deviceId: string) => Promise<void>;
}

/**
 * Hook for managing camera capture for still images.
 * Uses getUserMedia API to access the device camera.
 */
export function useCameraCapture(options: UseCameraCaptureOptions = {}): UseCameraCaptureResult {
  const {
    maxWidth = 1920,
    maxHeight = 1080,
    imageQuality = 0.85,
    preferredFacingMode = 'environment',
    onError,
  } = options;

  const [isOpen, setIsOpen] = useState(false);
  const [isCapturing, setIsCapturing] = useState(false);
  const [capturedImage, setCapturedImage] = useState<string | null>(null);
  const [availableCameras, setAvailableCameras] = useState<MediaDeviceInfo[]>([]);
  const [selectedCameraId, setSelectedCameraId] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const videoRef = useRef<HTMLVideoElement>(null);
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const streamRef = useRef<MediaStream | null>(null);
  const capturedBlobRef = useRef<Blob | null>(null);

  // Check browser support
  const isSupported = typeof navigator !== 'undefined'
    && navigator.mediaDevices
    && typeof navigator.mediaDevices.getUserMedia === 'function';

  // Load available cameras
  const loadCameras = useCallback(async () => {
    if (!isSupported) return;

    try {
      // First request permission to get real device labels
      const tempStream = await navigator.mediaDevices.getUserMedia({ video: true });
      tempStream.getTracks().forEach(track => track.stop());

      const devices = await navigator.mediaDevices.enumerateDevices();
      const cameras = devices.filter(d => d.kind === 'videoinput');
      setAvailableCameras(cameras);
    } catch (err) {
      console.error('Failed to enumerate cameras:', err);
    }
  }, [isSupported]);

  // Stop the camera stream
  const stopStream = useCallback(() => {
    if (streamRef.current) {
      streamRef.current.getTracks().forEach(track => track.stop());
      streamRef.current = null;
    }
  }, []);

  // Start the camera stream
  const startStream = useCallback(async (deviceId?: string) => {
    if (!isSupported) {
      const msg = 'Camera capture not supported in this browser.';
      setError(msg);
      onError?.(msg);
      return;
    }

    try {
      stopStream();

      const constraints: MediaStreamConstraints = {
        video: deviceId
          ? { deviceId: { exact: deviceId } }
          : { facingMode: preferredFacingMode },
      };

      const stream = await navigator.mediaDevices.getUserMedia(constraints);
      streamRef.current = stream;

      if (videoRef.current) {
        videoRef.current.srcObject = stream;
        await videoRef.current.play();
      }

      // Update selected camera
      const videoTrack = stream.getVideoTracks()[0];
      if (videoTrack) {
        const settings = videoTrack.getSettings();
        if (settings.deviceId) {
          setSelectedCameraId(settings.deviceId);
        }
      }

      setError(null);
    } catch (err) {
      let msg = 'Failed to access camera.';
      if (err instanceof DOMException) {
        if (err.name === 'NotAllowedError' || err.name === 'PermissionDeniedError') {
          msg = 'Camera access denied. Please enable in browser settings.';
        } else if (err.name === 'NotFoundError') {
          msg = 'No camera found on this device.';
        } else if (err.name === 'NotReadableError') {
          msg = 'Camera is being used by another application.';
        }
      }
      setError(msg);
      onError?.(msg);
    }
  }, [isSupported, preferredFacingMode, onError, stopStream]);

  // Open camera modal
  const openCamera = useCallback(async () => {
    if (!isSupported) {
      const msg = 'Camera capture not supported in this browser.';
      setError(msg);
      onError?.(msg);
      return;
    }

    setIsOpen(true);
    setCapturedImage(null);
    capturedBlobRef.current = null;
    setError(null);

    await loadCameras();
    await startStream();
  }, [isSupported, onError, loadCameras, startStream]);

  // Capture image from video
  const captureImage = useCallback(() => {
    if (!videoRef.current || !canvasRef.current) return;

    setIsCapturing(true);

    try {
      const video = videoRef.current;
      const canvas = canvasRef.current;
      const ctx = canvas.getContext('2d');

      if (!ctx) {
        throw new Error('Failed to get canvas context');
      }

      // Calculate scaled dimensions
      let width = video.videoWidth;
      let height = video.videoHeight;

      if (width > maxWidth) {
        height = (height * maxWidth) / width;
        width = maxWidth;
      }
      if (height > maxHeight) {
        width = (width * maxHeight) / height;
        height = maxHeight;
      }

      canvas.width = width;
      canvas.height = height;

      // Draw video frame to canvas
      ctx.drawImage(video, 0, 0, width, height);

      // Get data URL for preview
      const dataUrl = canvas.toDataURL('image/jpeg', imageQuality);
      setCapturedImage(dataUrl);

      // Convert to blob for upload
      canvas.toBlob(
        (blob) => {
          capturedBlobRef.current = blob;
        },
        'image/jpeg',
        imageQuality
      );

      // Stop the stream after capture
      stopStream();
    } catch (err) {
      const msg = err instanceof Error ? err.message : 'Failed to capture image.';
      setError(msg);
      onError?.(msg);
    } finally {
      setIsCapturing(false);
    }
  }, [maxWidth, maxHeight, imageQuality, onError, stopStream]);

  // Retake photo
  const retake = useCallback(async () => {
    setCapturedImage(null);
    capturedBlobRef.current = null;
    setError(null);
    await startStream(selectedCameraId || undefined);
  }, [startStream, selectedCameraId]);

  // Get the captured image as blob
  const getImageBlob = useCallback(async (): Promise<Blob | null> => {
    return capturedBlobRef.current;
  }, []);

  // Close camera modal
  const close = useCallback(() => {
    stopStream();
    setIsOpen(false);
    setCapturedImage(null);
    capturedBlobRef.current = null;
    setError(null);
    setSelectedCameraId(null);
  }, [stopStream]);

  // Switch to a different camera
  const switchCamera = useCallback(async (deviceId: string) => {
    setError(null);
    await startStream(deviceId);
  }, [startStream]);

  // Cleanup on unmount
  useEffect(() => {
    return () => {
      stopStream();
    };
  }, [stopStream]);

  return {
    isOpen,
    isCapturing,
    capturedImage,
    availableCameras,
    selectedCameraId,
    error,
    isSupported,
    videoRef,
    canvasRef,
    openCamera,
    captureImage,
    retake,
    getImageBlob,
    close,
    switchCamera,
  };
}

export default useCameraCapture;

