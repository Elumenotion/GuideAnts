import { useEffect, type ReactNode } from 'react';
import { createPortal } from 'react-dom';

interface SettingsModalProps {
  isOpen: boolean;
  title: string;
  onClose: () => void;
  children: ReactNode;
  /** Optional footer (typically Cancel + primary action buttons). */
  footer?: ReactNode;
  /** Optional tailwind max-width class. Defaults to max-w-2xl. */
  maxWidthClass?: string;
  /** If true, Esc and overlay click are disabled (for commit-in-progress forms). */
  disableDismiss?: boolean;
}

/**
 * Small in-app dialog wrapper used by the service editor managers. The
 * intent is to keep local model download / load / confirmation flows inside
 * the app UI — browser `window.prompt` and `window.confirm` are explicitly
 * rejected for these flows.
 */
export function SettingsModal({
  isOpen,
  title,
  onClose,
  children,
  footer,
  maxWidthClass = 'max-w-2xl',
  disableDismiss = false,
}: SettingsModalProps) {
  useEffect(() => {
    if (!isOpen) {
      return;
    }
    const handleKeyDown = (event: KeyboardEvent) => {
      if (disableDismiss) {
        return;
      }
      if (event.key === 'Escape') {
        event.preventDefault();
        onClose();
      }
    };
    window.addEventListener('keydown', handleKeyDown);
    return () => window.removeEventListener('keydown', handleKeyDown);
  }, [isOpen, disableDismiss, onClose]);

  if (!isOpen) {
    return null;
  }

  return createPortal(
    <div
      className="fixed inset-0 z-[9999] flex items-start justify-center overflow-y-auto bg-black bg-opacity-50 px-4 py-10"
      role="dialog"
      aria-modal="true"
      aria-label={title}
      onMouseDown={(event) => {
        if (event.target === event.currentTarget && !disableDismiss) {
          onClose();
        }
      }}
    >
      <div className={`w-full ${maxWidthClass} rounded-lg bg-white shadow-xl`}>
        <div className="flex items-center justify-between border-b border-gray-200 px-5 py-3">
          <h2 className="text-base font-semibold text-gray-900">{title}</h2>
          <button
            type="button"
            onClick={() => {
              if (!disableDismiss) {
                onClose();
              }
            }}
            disabled={disableDismiss}
            className="text-gray-400 hover:text-gray-600 disabled:opacity-40"
            aria-label="Close"
          >
            <svg className="h-5 w-5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
            </svg>
          </button>
        </div>
        <div className="px-5 py-4">{children}</div>
        {footer ? (
          <div className="flex justify-end gap-2 border-t border-gray-200 px-5 py-3">{footer}</div>
        ) : null}
      </div>
    </div>,
    document.body
  );
}
