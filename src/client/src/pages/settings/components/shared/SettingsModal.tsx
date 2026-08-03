import { useEffect, useRef, type ReactNode } from 'react';
import { createPortal } from 'react-dom';

export type SettingsModalSize = 'sm' | 'md' | 'lg' | 'xl';

/** Width only — height is always content-driven up to the viewport. */
const SIZE_MAX_WIDTH: Record<SettingsModalSize, string> = {
  sm: 'max-w-2xl',
  md: 'max-w-4xl',
  lg: 'max-w-6xl',
  xl: 'max-w-[min(96rem,calc(100vw-1.5rem))]',
};

/**
 * Depth bookkeeping so Esc only closes the top-most dialog. Sequence numbers are
 * handed out during render, which runs parent-before-child, so a nested dialog
 * always outranks the one it opened from.
 */
let nextModalSequence = 0;
const openModalSequences = new Set<number>();

function isTopMostModal(sequence: number): boolean {
  for (const other of openModalSequences) {
    if (other > sequence) {
      return false;
    }
  }
  return true;
}

interface SettingsModalProps {
  isOpen: boolean;
  title: string;
  onClose: () => void;
  children: ReactNode;
  /** Optional footer (typically Cancel + primary action buttons). */
  footer?: ReactNode;
  /**
   * Width preset only. Height follows content up to the viewport.
   * - sm: short steps (provider pick)
   * - md: standard forms (default)
   * - lg: multi-section editors
   * - xl: dense grids (curated catalog)
   */
  size?: SettingsModalSize;
  /** Escape hatch — overrides `size` when a one-off width is required. */
  maxWidthClass?: string;
  /** If true, Esc and overlay click are disabled (for commit-in-progress forms). */
  disableDismiss?: boolean;
  /** If true, clicking the overlay/backdrop will not close the modal. */
  disableOverlayDismiss?: boolean;
}

/**
 * In-app dialog for Settings / onboarding.
 * Width from `size`; height follows content (scrolls when taller than the viewport).
 */
export function SettingsModal({
  isOpen,
  title,
  onClose,
  children,
  footer,
  size = 'md',
  maxWidthClass,
  disableDismiss = false,
  disableOverlayDismiss = false,
}: SettingsModalProps) {
  const sequenceRef = useRef<number | null>(null);
  if (sequenceRef.current === null) {
    nextModalSequence += 1;
    sequenceRef.current = nextModalSequence;
  }
  const sequence = sequenceRef.current;

  useEffect(() => {
    if (!isOpen) {
      return;
    }
    openModalSequences.add(sequence);
    return () => {
      openModalSequences.delete(sequence);
    };
  }, [isOpen, sequence]);

  useEffect(() => {
    if (!isOpen) {
      return;
    }
    const handleKeyDown = (event: KeyboardEvent) => {
      if (disableDismiss) {
        return;
      }
      if (event.key === 'Escape' && isTopMostModal(sequence)) {
        event.preventDefault();
        onClose();
      }
    };
    window.addEventListener('keydown', handleKeyDown);
    return () => window.removeEventListener('keydown', handleKeyDown);
  }, [isOpen, disableDismiss, onClose, sequence]);

  if (!isOpen) {
    return null;
  }

  const widthClass = maxWidthClass ?? SIZE_MAX_WIDTH[size];

  return createPortal(
    <div
      className="fixed inset-0 z-[9999] flex items-start justify-center overflow-y-auto bg-black bg-opacity-50 px-4 py-6"
      role="dialog"
      aria-modal="true"
      aria-label={title}
      onMouseDown={(event) => {
        if (event.target === event.currentTarget && !disableDismiss && !disableOverlayDismiss) {
          onClose();
        }
      }}
    >
      <div className={`my-auto flex max-h-[calc(100vh-3rem)] w-full flex-col ${widthClass} rounded-lg bg-white shadow-xl`}>
        <div className="flex shrink-0 items-center justify-between border-b border-gray-200 px-5 py-3">
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
        <div className="flex-1 overflow-y-auto px-5 py-4">{children}</div>
        {footer ? (
          <div className="flex shrink-0 justify-end gap-2 border-t border-gray-200 px-5 py-3">{footer}</div>
        ) : null}
      </div>
    </div>,
    document.body
  );
}
