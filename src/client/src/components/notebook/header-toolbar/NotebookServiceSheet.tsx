import type { ReactNode } from 'react';

interface NotebookServiceSheetProps {
  open: boolean;
  onClose: () => void;
  children: ReactNode;
}

export function NotebookServiceSheet({ open, onClose, children }: NotebookServiceSheetProps) {
  if (!open) return null;

  return (
    <div
      className="fixed inset-x-0 bottom-0 z-50 flex flex-col justify-end bg-black/40"
      style={{ top: '3.25rem' }}
      role="dialog"
      aria-modal="true"
      onMouseDown={(event) => {
        if (event.target === event.currentTarget) {
          onClose();
        }
      }}
    >
      <div
        className="bg-white rounded-t-xl border-t border-x max-h-[85vh] overflow-auto p-4"
        onMouseDown={(e) => e.stopPropagation()}
      >
        <div className="flex justify-end mb-2">
          <button
            type="button"
            className="text-xs text-slate-600 hover:text-slate-800"
            onClick={onClose}
            aria-label="Close services"
          >
            Close
          </button>
        </div>
        {children}
      </div>
    </div>
  );
}
