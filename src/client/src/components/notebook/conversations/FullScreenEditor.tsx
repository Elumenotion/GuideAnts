import { useState, useRef, useEffect } from 'react';
import { FaCompress } from 'react-icons/fa';
import { createPortal } from 'react-dom';
import { convertAbsoluteToRelative } from '../../../utils/markdownUrlConverter';
import LexicalEditor, { LexicalEditorRef } from './LexicalEditor';

interface FullScreenEditorProps {
  content: string;
  onSave: (content: string) => void;
  onCancel: (currentContent?: string) => void;
  mode: 'edit' | 'compose';
  title?: string;
  placeholder?: string;
  submitLabel?: string;
  isLoading?: boolean;
  error?: string;
  projectId?: string;
  notebookId?: string;
  resolveProjectFilePath?: (relativePath: string) => string | undefined;
  /** Base path for resolving relative URLs (directory of the markdown file, e.g., "Output") */
  basePath?: string;
}

/**
 * Unified full screen editor component for all editing scenarios.
 * Provides consistent UX for assistant editing, user message editing, and draft composition.
 */
export default function FullScreenEditor({
  content,
  onSave,
  onCancel,
  mode,
  title,
  placeholder = "Type your message...",
  submitLabel = mode === 'compose' ? 'Send' : 'Save',
  isLoading = false,
  error,
  projectId,
  notebookId,
  resolveProjectFilePath,
  basePath
}: FullScreenEditorProps) {
  const editorRef = useRef<LexicalEditorRef>(null);
  const [currentContent, setCurrentContent] = useState(content);
  const [editorReady, setEditorReady] = useState(false);

  // Load content into the editor once the ref is ready (mount) and whenever the
  // `content` prop changes thereafter. The first render of this component
  // happens before <LexicalEditor> mounts, so `editorRef.current` will be null
  // the first time this effect runs. We therefore keep a small state flag to
  // guarantee we only push the initial markdown **once** the editor is ready.
  const [isEditorEmpty, setIsEditorEmpty] = useState<boolean>(!content.trim());
  const [valueError, setValueError] = useState<string | null>(null);

  // Single initialization/update effect gated by explicit editorReady signal
  useEffect(() => {
    if (!editorReady || !editorRef.current) return;
    // Convert any absolute notebook media URLs to relative before loading
    const portable = convertAbsoluteToRelative(content, projectId, notebookId);
    editorRef.current.setValue(portable);
    setCurrentContent(portable);
    try {
      setIsEditorEmpty(editorRef.current.getIsEmpty());
    } catch {
      // Non-fatal: emptiness check can fail briefly during mount
    }
  }, [editorReady, content]);

  // Keep local state in sync with editor updates
  useEffect(() => {
    if (!editorRef.current) return;
    const unregister = editorRef.current.registerChangeListener((markdown) => {
      setCurrentContent(markdown);
      if (editorRef.current) {
        try {
          setIsEditorEmpty(editorRef.current.getIsEmpty());
        } catch {
          // ignore
        }
      }
    });
    return unregister;
  }, []);

  const handleKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === 'Escape') {
      e.preventDefault();
      handleExitFullScreen();
    } else if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) {
      e.preventDefault();
      handleSave();
    }
  };

  const getLatestContent = (): string => {
    if (editorRef.current) {
      return editorRef.current.getValue();
    }
    return currentContent;
  };

  const handleSave = () => {
    let latest = currentContent;
    try {
      latest = getLatestContent();
      setValueError(null);
    } catch {
      setValueError('Failed to read editor content. Please try again.');
      return; // Block save on error
    }
    if (!isLoading) {
      // Ensure we store relative paths
      const portable = convertAbsoluteToRelative(latest, projectId, notebookId);
      onSave(portable);
    }
  };

  // Exit full screen and sync current content back
  const handleExitFullScreen = () => {
    try {
      onCancel(getLatestContent());
    } catch {
      onCancel(currentContent);
    }
  };

  return createPortal(
    <div className="fixed inset-0 z-50 bg-gray-50 flex flex-col">
      {/* Header with controls */}
      <div className="flex-shrink-0 flex items-center justify-between p-4 bg-white border-b border-gray-200">
        <div className="flex items-center space-x-4">
          <h2 className="text-lg font-semibold text-gray-900">
            {title || (mode === 'compose' ? 'Compose Message' : 'Edit Message')}
          </h2>
          <div className="text-sm text-gray-500">
            <kbd className="px-2 py-1 bg-gray-100 rounded text-xs">Ctrl+Enter</kbd> to {submitLabel.toLowerCase()}, <kbd className="px-2 py-1 bg-gray-100 rounded text-xs">Esc</kbd> to cancel
          </div>
        </div>
        <div className="flex items-center space-x-2">
          {isLoading && (
            <div className="flex items-center space-x-2 mr-4">
              <div className="w-4 h-4 border-2 border-blue-600 border-t-transparent rounded-full animate-spin"></div>
              <span className="text-sm text-gray-600">{mode === 'compose' ? 'Sending...' : 'Saving...'}</span>
            </div>
          )}
          <button
            onClick={handleExitFullScreen}
            className="p-2 bg-gray-100 hover:bg-gray-200 rounded-lg text-gray-500 hover:text-gray-700 transition-colors"
            aria-label="Exit full screen"
            title="Exit full screen (Esc)"
          >
            <FaCompress className="w-4 h-4" />
          </button>
        </div>
      </div>

      {/* Error banner */}
      {(error || valueError) && (
        <div className="flex-shrink-0 m-4 p-3 bg-red-50 border border-red-200 rounded text-sm text-red-700">
          {error || valueError}
        </div>
      )}

      {/* Main content area with scrolling */}
      <div className="flex-1 overflow-hidden p-4">
        <div className="h-full bg-white rounded-lg shadow-lg border border-gray-200 focus-within:border-blue-400 flex flex-col">
          {/* Capture keydown early to suppress newline before it is inserted. */}
          <div className="flex-1 overflow-hidden" onKeyDownCapture={handleKeyDown}>
            <LexicalEditor
              ref={editorRef}
              placeholder={placeholder}
              autoFocus={true}
              showToolbar={true}
              className="h-full flex flex-col"
              readOnly={isLoading}
              onReady={() => setEditorReady(true)}
              projectId={projectId}
              notebookId={notebookId}
              resolveProjectFilePath={resolveProjectFilePath}
              basePath={basePath}
              submitButton={{
                label: submitLabel,
                onClick: handleSave,
                disabled: isEditorEmpty || isLoading,
                isLoading: isLoading
              }}
              cancelButton={mode === 'edit' ? {
                label: 'Cancel',
                onClick: () => handleExitFullScreen()
              } : undefined}
            />
          </div>
        </div>
      </div>
    </div>,
    document.body
  );
} 