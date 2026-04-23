interface MicrophoneButtonProps {
  isRecording: boolean;
  isProcessing: boolean;
  duration: number;
  isSupported: boolean;
  disabled?: boolean;
  onStartRecording: () => void;
  onStopRecording: () => void;
  className?: string;
}

/**
 * Formats seconds into MM:SS format
 */
function formatDuration(seconds: number): string {
  const mins = Math.floor(seconds / 60);
  const secs = seconds % 60;
  return `${mins}:${secs.toString().padStart(2, '0')}`;
}

/**
 * Reusable microphone button component for voice recording.
 * Shows different states: idle, recording (with duration), and processing.
 */
export default function MicrophoneButton({
  isRecording,
  isProcessing,
  duration,
  isSupported,
  disabled = false,
  onStartRecording,
  onStopRecording,
  className = '',
}: MicrophoneButtonProps) {
  if (!isSupported) {
    return null;
  }

  const handleClick = () => {
    if (isProcessing) return;
    
    if (isRecording) {
      onStopRecording();
    } else {
      onStartRecording();
    }
  };

  // Processing state - show spinner
  if (isProcessing) {
    return (
      <div className={`flex items-center gap-2 ${className}`}>
        <div className="flex items-center gap-1 text-sm text-gray-500">
          <div className="w-4 h-4 border-2 border-gray-400 border-t-transparent rounded-full animate-spin"></div>
          <span>Transcribing...</span>
        </div>
      </div>
    );
  }

  // Recording state - show ear icon with duration
  if (isRecording) {
    return (
      <div className={`flex items-center gap-2 ${className}`}>
        {/* Listening indicator */}
        <div className="flex items-center gap-1.5 text-sm text-blue-600" aria-live="polite">
          <span className="animate-pulse" aria-hidden="true">👂</span>
          <span>{formatDuration(duration)}</span>
        </div>
        
        {/* Stop button */}
        <button
          type="button"
          onClick={handleClick}
          className="p-1.5 rounded hover:bg-gray-100 text-gray-600 transition-colors"
          title="Stop listening"
          aria-label="Stop listening"
        >
          <svg
            xmlns="http://www.w3.org/2000/svg"
            viewBox="0 0 24 24"
            fill="currentColor"
            className="w-5 h-5"
          >
            <rect x="6" y="6" width="12" height="12" rx="1" />
          </svg>
        </button>
      </div>
    );
  }

  // Idle state - show microphone button
  return (
    <button
      type="button"
      onClick={handleClick}
      disabled={disabled}
      className={`p-1.5 rounded hover:bg-gray-100 text-gray-600 transition-colors disabled:opacity-50 disabled:cursor-not-allowed ${className}`}
      title="Start voice input"
      aria-label="Start voice input"
    >
      <svg
        xmlns="http://www.w3.org/2000/svg"
        viewBox="0 0 24 24"
        fill="currentColor"
        className="w-5 h-5"
      >
        <path d="M12 1a4 4 0 0 0-4 4v6a4 4 0 0 0 8 0V5a4 4 0 0 0-4-4z" />
        <path d="M19 10v1a7 7 0 0 1-14 0v-1a1 1 0 0 1 2 0v1a5 5 0 0 0 10 0v-1a1 1 0 0 1 2 0z" />
        <path d="M12 19a1 1 0 0 0-1 1v2a1 1 0 0 0 2 0v-2a1 1 0 0 0-1-1z" />
        <path d="M8 23h8a1 1 0 0 0 0-2H8a1 1 0 0 0 0 2z" />
      </svg>
    </button>
  );
}

