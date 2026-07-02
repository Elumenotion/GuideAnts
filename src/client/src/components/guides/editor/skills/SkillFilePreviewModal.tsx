import { useEffect, useState } from 'react';
import { FaDownload, FaExclamationCircle, FaSpinner, FaTimes } from 'react-icons/fa';
import MarkdownViewer from '../../../common/MarkdownViewer';
import { API_BASE_URL } from '../../../../config/apiConfig';
import { isSkillFilePreviewable } from './skillFileTreeModel';

interface SkillFilePreviewModalProps {
  isOpen: boolean;
  onClose: () => void;
  fileName: string;
  relativePath: string;
  initialContent?: string;
  assistantId?: string;
  fileId?: string;
}

function isMarkdownFile(relativePath: string): boolean {
  const extension = relativePath.split('.').pop()?.toLowerCase() ?? '';
  return extension === 'md' || extension === 'markdown';
}

export function SkillFilePreviewModal({
  isOpen,
  onClose,
  fileName,
  relativePath,
  initialContent,
  assistantId,
  fileId,
}: SkillFilePreviewModalProps) {
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [content, setContent] = useState('');

  useEffect(() => {
    if (!isOpen) {
      return;
    }

    if (initialContent !== undefined) {
      setContent(initialContent);
      setError(null);
      setLoading(false);
      return;
    }

    if (!assistantId || !fileId || fileId.startsWith('pending-')) {
      setError('Save the guide before previewing this file.');
      setLoading(false);
      return;
    }

    let cancelled = false;
    const load = async () => {
      setLoading(true);
      setError(null);

      try {
        const response = await fetch(
          `${API_BASE_URL}/assistants/${assistantId}/files/${fileId}/download`,
        );
        if (!response.ok) {
          throw new Error('Failed to load file content.');
        }

        const text = await response.text();
        if (!cancelled) {
          setContent(text);
        }
      } catch (loadError) {
        if (!cancelled) {
          setError(loadError instanceof Error ? loadError.message : 'Failed to load file content.');
        }
      } finally {
        if (!cancelled) {
          setLoading(false);
        }
      }
    };

    void load();
    return () => {
      cancelled = true;
    };
  }, [assistantId, fileId, initialContent, isOpen]);

  useEffect(() => {
    if (!isOpen) {
      return;
    }

    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        onClose();
      }
    };

    window.addEventListener('keydown', handleKeyDown);
    return () => window.removeEventListener('keydown', handleKeyDown);
  }, [isOpen, onClose]);

  const handleDownload = () => {
    const blob = new Blob([content], { type: 'text/plain;charset=utf-8' });
    const url = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = url;
    link.download = fileName;
    link.click();
    URL.revokeObjectURL(url);
  };

  if (!isOpen) {
    return null;
  }

  const previewable = isSkillFilePreviewable(relativePath);

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/50 p-4">
      <div className="flex max-h-[90vh] w-full max-w-4xl flex-col rounded-lg bg-white shadow-xl">
        <div className="flex items-center justify-between border-b border-gray-200 px-6 py-4">
          <div>
            <h2 className="text-xl font-semibold text-gray-900">Skill file preview</h2>
            <p className="mt-1 text-sm text-gray-500">{fileName}</p>
          </div>
          <div className="flex items-center gap-2">
            {!loading && !error && content && (
              <button
                type="button"
                onClick={handleDownload}
                className="flex items-center gap-2 rounded-md border border-blue-300 px-3 py-2 text-sm text-blue-700 hover:bg-blue-50"
              >
                <FaDownload className="h-4 w-4" />
                Download
              </button>
            )}
            <button
              type="button"
              onClick={onClose}
              className="p-2 text-gray-400 hover:text-gray-600"
              title="Close"
            >
              <FaTimes className="h-5 w-5" />
            </button>
          </div>
        </div>

        <div className="flex-1 overflow-auto px-6 py-4">
          {loading && (
            <div className="flex h-64 flex-col items-center justify-center text-gray-500">
              <FaSpinner className="mb-4 h-8 w-8 animate-spin" />
              <p>Loading file content...</p>
            </div>
          )}

          {error && (
            <div className="flex h-64 flex-col items-center justify-center text-red-600">
              <FaExclamationCircle className="mb-4 h-8 w-8" />
              <p className="text-lg font-medium">Failed to load content</p>
              <p className="mt-2 text-sm text-gray-600">{error}</p>
            </div>
          )}

          {!loading && !error && !previewable && (
            <p className="text-sm text-gray-600">
              This file type cannot be previewed in the browser. Use download from the file list instead.
            </p>
          )}

          {!loading && !error && previewable && isMarkdownFile(relativePath) && (
            <div className="prose prose-sm max-w-none">
              <MarkdownViewer text={content} inlineMode className="text-gray-700" />
            </div>
          )}

          {!loading && !error && previewable && !isMarkdownFile(relativePath) && (
            <pre className="overflow-auto rounded-md bg-gray-50 p-4 text-sm text-gray-800">
              <code>{content}</code>
            </pre>
          )}
        </div>

        <div className="flex justify-end border-t border-gray-200 px-6 py-4">
          <button
            type="button"
            onClick={onClose}
            className="rounded-md bg-gray-100 px-4 py-2 text-sm text-gray-700 hover:bg-gray-200"
          >
            Close
          </button>
        </div>
      </div>
    </div>
  );
}
