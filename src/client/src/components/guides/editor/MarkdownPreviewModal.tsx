import { useState, useEffect } from 'react';
import { FaTimes, FaDownload, FaSpinner, FaExclamationCircle } from 'react-icons/fa';
import { api } from '../../../services/api';
import MarkdownViewer from '../../common/MarkdownViewer';

interface MarkdownPreviewModalProps {
  isOpen: boolean;
  onClose: () => void;
  fileId: string;
  assistantId?: string;
  guideId?: string;
}

export function MarkdownPreviewModal({ isOpen, onClose, fileId, assistantId, guideId }: MarkdownPreviewModalProps) {
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [markdownContent, setMarkdownContent] = useState<string>('');
  const [fileName, setFileName] = useState<string>('');

  useEffect(() => {
    if (isOpen && (assistantId || guideId)) {
      loadMarkdownContent();
    }
  }, [isOpen, fileId, assistantId, guideId]);

  useEffect(() => {
    if (!isOpen) return;
    const handleKeyDown = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose();
    };
    window.addEventListener('keydown', handleKeyDown);
    return () => window.removeEventListener('keydown', handleKeyDown);
  }, [isOpen, onClose]);

  const loadMarkdownContent = async () => {
    setLoading(true);
    setError(null);

    try {
      const entityId = assistantId || guideId!;
      const result = await api.guides.assistants.getFileMarkdownContent(entityId, fileId);
      
      // Read text from blob
      const text = await result.blob.text();
      setMarkdownContent(text);
      setFileName(result.fileName || 'Unknown');
    } catch (err: any) {
      console.error('Failed to load markdown content:', err);
      setError(err.message || 'Failed to load markdown content');
    } finally {
      setLoading(false);
    }
  };

  const handleDownload = () => {
    const blob = new Blob([markdownContent], { type: 'text/markdown' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = `${fileName.replace(/\.[^/.]+$/, '')}-extracted.md`;
    a.click();
    URL.revokeObjectURL(url);
  };

  if (!isOpen) return null;

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black bg-opacity-50">
      <div 
        className="bg-white rounded-lg shadow-xl w-full max-w-4xl max-h-[90vh] flex flex-col"
        tabIndex={-1}
      >
        {/* Header */}
        <div className="flex items-center justify-between px-6 py-4 border-b border-gray-200">
          <div className="flex-1">
            <h2 className="text-xl font-semibold text-gray-900">Extracted Content Preview</h2>
            <p className="text-sm text-gray-500 mt-1">{fileName}</p>
          </div>
          <div className="flex items-center gap-2">
            {!loading && !error && (
              <button
                type="button"
                onClick={handleDownload}
                className="flex items-center gap-2 px-3 py-2 text-sm text-purple-600 border border-purple-300 rounded-md hover:bg-purple-50"
                title="Download extracted markdown"
              >
                <FaDownload className="w-4 h-4" />
                Download
              </button>
            )}
            <button
              type="button"
              onClick={onClose}
              className="text-gray-400 hover:text-gray-600 p-2"
              title="Close"
            >
              <FaTimes className="w-5 h-5" />
            </button>
          </div>
        </div>

        {/* Content */}
        <div className="flex-1 overflow-auto px-6 py-4">
          {loading && (
            <div className="flex flex-col items-center justify-center h-64 text-gray-500">
              <FaSpinner className="w-8 h-8 animate-spin mb-4" />
              <p>Loading extracted content...</p>
            </div>
          )}

          {error && (
            <div className="flex flex-col items-center justify-center h-64 text-red-600">
              <FaExclamationCircle className="w-8 h-8 mb-4" />
              <p className="text-lg font-medium">Failed to load content</p>
              <p className="text-sm text-gray-600 mt-2">{error}</p>
            </div>
          )}

          {!loading && !error && (
            <div className="prose prose-sm max-w-none">
              <MarkdownViewer
                text={markdownContent}
                inlineMode={true}
                className="text-gray-700"
              />
            </div>
          )}
        </div>

        {/* Footer */}
        <div className="flex items-center justify-end px-6 py-4 border-t border-gray-200">
          <button
            type="button"
            onClick={onClose}
            className="px-4 py-2 text-sm text-gray-700 bg-gray-100 rounded-md hover:bg-gray-200"
          >
            Close
          </button>
        </div>
      </div>
    </div>
  );
}

