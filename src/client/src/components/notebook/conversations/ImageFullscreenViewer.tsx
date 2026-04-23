import { useEffect, useCallback } from 'react';
import { createPortal } from 'react-dom';
import { FaTimes, FaDownload } from 'react-icons/fa';

interface ImageFullscreenViewerProps {
  /** Image source URL (can be authenticated API URL or regular URL) */
  src: string;
  /** Alt text for accessibility */
  alt?: string;
  /** Callback when viewer is closed */
  onClose: () => void;
  /** Optional filename for download */
  fileName?: string;
}

/**
 * ImageFullscreenViewer provides a lightbox-style fullscreen viewer for images.
 * Supports authenticated API images, keyboard navigation (ESC to close), and download functionality.
 */
export default function ImageFullscreenViewer({ src, alt, onClose, fileName }: ImageFullscreenViewerProps) {
  
  // Handle ESC key to close
  useEffect(() => {
    const handleEscape = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        onClose();
      }
    };
    
    window.addEventListener('keydown', handleEscape);
    return () => window.removeEventListener('keydown', handleEscape);
  }, [onClose]);

  // Prevent body scroll when viewer is open
  useEffect(() => {
    const originalOverflow = document.body.style.overflow;
    document.body.style.overflow = 'hidden';
    return () => {
      document.body.style.overflow = originalOverflow;
    };
  }, []);

  // Handle backdrop click (click outside image)
  const handleBackdropClick = useCallback((e: React.MouseEvent<HTMLDivElement>) => {
    if (e.target === e.currentTarget) {
      onClose();
    }
  }, [onClose]);

  // Handle download
  const handleDownload = useCallback(() => {
    const link = document.createElement('a');
    link.href = src;
    link.download = fileName || alt || 'image';
    document.body.appendChild(link);
    link.click();
    document.body.removeChild(link);
  }, [src, fileName, alt]);

  return createPortal(
    <div 
      className="fixed inset-0 z-[60] bg-white flex items-center justify-center p-4 animate-fade-in"
      onClick={handleBackdropClick}
      role="dialog"
      aria-modal="true"
      aria-label="Image fullscreen viewer"
    >
      {/* Control buttons */}
      <div className="absolute top-4 right-4 flex items-center space-x-2 z-10">
        <button
          onClick={handleDownload}
          className="p-3 bg-black bg-opacity-10 hover:bg-opacity-20 rounded-lg text-black transition-all duration-200 backdrop-blur-sm"
          aria-label="Download image"
          title="Download image"
        >
          <FaDownload className="w-5 h-5" />
        </button>
        <button
          onClick={onClose}
          className="p-3 bg-black bg-opacity-10 hover:bg-opacity-20 rounded-lg text-black transition-all duration-200 backdrop-blur-sm"
          aria-label="Close viewer"
          title="Close (ESC)"
        >
          <FaTimes className="w-5 h-5" />
        </button>
      </div>

      {/* Image container with max constraints to prevent overflow */}
      <div 
        className="relative max-w-[95vw] max-h-[95vh] flex items-center justify-center"
        onClick={(e) => e.stopPropagation()}
      >
        <img
          src={src}
          alt={alt}
          className="max-w-full max-h-[95vh] object-contain rounded-lg shadow-2xl cursor-default"
          style={{ userSelect: 'none' }}
        />
      </div>

      {/* Instructions hint */}
      <div className="absolute bottom-4 left-1/2 transform -translate-x-1/2 text-black text-sm opacity-75 bg-white bg-opacity-50 px-4 py-2 rounded-full backdrop-blur-sm">
        Press ESC or click outside to close
      </div>
    </div>,
    document.body
  );
}

