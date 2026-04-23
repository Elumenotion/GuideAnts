import { useRef, useEffect } from 'react';
import { useCameraCapture } from '../../hooks/useCameraCapture';

interface CameraCaptureProps {
  isOpen: boolean;
  onClose: () => void;
  onCapture: (blob: Blob, fileName: string) => Promise<void>;
}

/**
 * Camera capture modal component for taking photos.
 * Uses the device camera to capture still images.
 */
export default function CameraCapture({ isOpen, onClose, onCapture }: CameraCaptureProps) {
  const {
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
  } = useCameraCapture({
    maxWidth: 1920,
    maxHeight: 1080,
    imageQuality: 0.85,
    preferredFacingMode: 'environment',
  });

  const isUploading = useRef(false);
  const modalRef = useRef<HTMLDivElement>(null);

  // Open camera when modal opens
  useEffect(() => {
    if (isOpen) {
      openCamera();
    } else {
      close();
    }
  }, [isOpen, openCamera, close]);

  // Handle escape key
  useEffect(() => {
    const handleKeyDown = (e: KeyboardEvent) => {
      if (e.key === 'Escape' && isOpen) {
        handleClose();
      }
    };

    window.addEventListener('keydown', handleKeyDown);
    return () => window.removeEventListener('keydown', handleKeyDown);
  }, [isOpen]);

  // Focus trap
  useEffect(() => {
    if (isOpen && modalRef.current) {
      modalRef.current.focus();
    }
  }, [isOpen]);

  const handleClose = () => {
    close();
    onClose();
  };

  const handleConfirm = async () => {
    if (isUploading.current) return;

    const blob = await getImageBlob();
    if (!blob) {
      return;
    }

    isUploading.current = true;
    try {
      const fileName = `camera-capture-${Date.now()}.jpg`;
      await onCapture(blob, fileName);
      handleClose();
    } catch (err) {
      console.error('Failed to upload captured image:', err);
    } finally {
      isUploading.current = false;
    }
  };

  if (!isOpen) return null;

  if (!isSupported) {
    return (
      <div
        ref={modalRef}
        className="fixed inset-0 z-[10000] bg-black/90 flex items-center justify-center p-4"
        tabIndex={-1}
      >
        <div className="bg-white rounded-lg p-6 max-w-sm text-center">
          <p className="text-gray-700 mb-4">Camera capture is not supported in this browser.</p>
          <button
            onClick={handleClose}
            className="px-4 py-2 bg-blue-600 text-white rounded hover:bg-blue-700"
          >
            Close
          </button>
        </div>
      </div>
    );
  }

  return (
    <div
      ref={modalRef}
      className="fixed inset-0 z-[10000] bg-black/90 flex flex-col items-center justify-center p-4"
      tabIndex={-1}
      role="dialog"
      aria-modal="true"
      aria-label="Camera capture"
    >
      {/* Close button */}
      <button
        onClick={handleClose}
        className="absolute top-4 right-4 text-white hover:text-gray-300 p-2"
        aria-label="Close camera"
      >
        <svg xmlns="http://www.w3.org/2000/svg" className="h-8 w-8" fill="none" viewBox="0 0 24 24" stroke="currentColor">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
        </svg>
      </button>

      {/* Error display */}
      {error && (
        <div className="absolute top-4 left-4 right-16 bg-red-600 text-white px-4 py-2 rounded" role="alert">
          {error}
        </div>
      )}

      {/* Camera preview / captured image */}
      <div className="relative max-w-full max-h-[60vh] overflow-hidden rounded-lg">
        {capturedImage ? (
          <img
            src={capturedImage}
            alt="Captured photo"
            className="max-w-full max-h-[60vh] object-contain"
          />
        ) : (
          <video
            ref={videoRef}
            autoPlay
            playsInline
            muted
            className="max-w-full max-h-[60vh] object-contain"
            aria-label="Camera preview"
          />
        )}
      </div>

      {/* Hidden canvas for capture */}
      <canvas ref={canvasRef} className="hidden" />

      {/* Camera selector (if multiple cameras) */}
      {!capturedImage && availableCameras.length > 1 && (
        <div className="mt-4 flex items-center gap-2">
          <label htmlFor="camera-select" className="text-white text-sm">Camera:</label>
          <select
            id="camera-select"
            value={selectedCameraId || ''}
            onChange={(e) => switchCamera(e.target.value)}
            className="px-3 py-1 rounded bg-white/20 text-white border border-white/30"
          >
            {availableCameras.map((camera) => (
              <option key={camera.deviceId} value={camera.deviceId} className="text-black">
                {camera.label || `Camera ${availableCameras.indexOf(camera) + 1}`}
              </option>
            ))}
          </select>
        </div>
      )}

      {/* Controls */}
      <div className="mt-6 flex items-center gap-4">
        {capturedImage ? (
          <>
            {/* Retake button */}
            <button
              onClick={retake}
              className="px-6 py-3 bg-white/20 text-white rounded-lg hover:bg-white/30 transition-colors"
            >
              Retake
            </button>

            {/* Confirm button */}
            <button
              onClick={handleConfirm}
              disabled={isUploading.current}
              className="px-6 py-3 bg-blue-600 text-white rounded-lg hover:bg-blue-700 disabled:opacity-50 transition-colors"
            >
              {isUploading.current ? 'Uploading...' : 'Use Photo'}
            </button>
          </>
        ) : (
          /* Capture button */
          <button
            onClick={captureImage}
            disabled={isCapturing || !!error}
            className="w-16 h-16 rounded-full bg-white border-4 border-blue-500 hover:bg-blue-50 disabled:opacity-50 transition-colors"
            aria-label="Capture photo"
          >
            {isCapturing && (
              <div className="w-full h-full flex items-center justify-center">
                <div className="w-8 h-8 border-2 border-blue-600 border-t-transparent rounded-full animate-spin" />
              </div>
            )}
          </button>
        )}
      </div>

      {/* Instructions */}
      <p className="mt-4 text-white/60 text-sm text-center">
        {capturedImage 
          ? 'Review your photo and tap "Use Photo" to add it, or "Retake" to try again'
          : 'Position your shot and tap the button to capture'}
      </p>
    </div>
  );
}

