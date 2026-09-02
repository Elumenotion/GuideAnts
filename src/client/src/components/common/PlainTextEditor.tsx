import { useCallback, useEffect, useState } from 'react';
import { createPortal } from 'react-dom';
import { ConfirmationDialog } from './ConfirmationDialog';

interface PlainTextEditorProps {
  value: string;
  onSave: (value: string) => void;
  onCancel: () => void;
  canEdit?: boolean;
  title?: string;
  isLoading?: boolean;
  error?: string;
}

/**
 * Byte-faithful plain-text editor (single textarea, no markdown parsing).
 */
export default function PlainTextEditor({
  value,
  onSave,
  onCancel,
  canEdit = true,
  title = 'Edit text',
  isLoading = false,
  error,
}: PlainTextEditorProps) {
  const [draft, setDraft] = useState(value);
  const [showDiscardConfirm, setShowDiscardConfirm] = useState(false);

  useEffect(() => {
    setDraft(value);
  }, [value]);

  const isDirty = draft !== value;

  const requestCancel = useCallback(() => {
    if (isDirty) {
      setShowDiscardConfirm(true);
      return;
    }
    onCancel();
  }, [isDirty, onCancel]);

  useEffect(() => {
    const onKeyDown = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        e.preventDefault();
        requestCancel();
      }
      if ((e.metaKey || e.ctrlKey) && e.key === 's') {
        e.preventDefault();
        if (canEdit && !isLoading && isDirty) {
          onSave(draft);
        }
      }
    };
    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, [canEdit, draft, isDirty, isLoading, onSave, requestCancel]);

  const editor = (
    <div className="fixed inset-0 z-[60] flex flex-col bg-white">
      <header className="flex items-center justify-between border-b px-4 py-3 bg-gray-50">
        <h2 className="text-lg font-semibold truncate pr-4">{title}</h2>
        <div className="flex items-center gap-2">
          <button
            type="button"
            onClick={requestCancel}
            className="px-4 py-2 text-sm border rounded hover:bg-gray-100"
            disabled={isLoading}
          >
            Cancel
          </button>
          <button
            type="button"
            onClick={() => onSave(draft)}
            className="px-4 py-2 text-sm rounded bg-blue-600 text-white hover:bg-blue-700 disabled:opacity-50"
            disabled={!canEdit || isLoading || !isDirty}
          >
            {isLoading ? 'Saving…' : 'Save'}
          </button>
        </div>
      </header>
      {error && (
        <p className="px-4 py-2 text-sm text-red-600 border-b bg-red-50" role="alert">
          {error}
        </p>
      )}
      <textarea
        className="flex-1 min-h-0 w-full resize-none font-mono text-sm p-4 outline-none whitespace-pre-wrap break-words"
        spellCheck={false}
        value={draft}
        onChange={(e) => setDraft(e.target.value)}
        readOnly={!canEdit || isLoading}
        aria-label={title}
      />
      <ConfirmationDialog
        isOpen={showDiscardConfirm}
        onClose={() => setShowDiscardConfirm(false)}
        onConfirm={() => {
          setShowDiscardConfirm(false);
          onCancel();
        }}
        title="Discard changes?"
        message="You have unsaved edits. Discard them and close the editor?"
        confirmText="Discard"
        cancelText="Keep editing"
        confirmButtonClass="bg-red-600 hover:bg-red-700 text-white"
      />
    </div>
  );

  return createPortal(editor, document.body);
}
