import { useEffect } from 'react';

export interface UseSidebarKeyboardShortcutsOptions {
  onDelete?: () => void;
  onRename?: () => void;
  onSelectAll?: () => void;
  onClearSelection?: () => void;
  onCopy?: () => void;
  onPaste?: () => void;
  isActive: boolean; // Only process if section is active/focused
}

export function useSidebarKeyboardShortcuts({
  onDelete,
  onRename,
  onSelectAll,
  onClearSelection,
  onCopy,
  onPaste,
  isActive
}: UseSidebarKeyboardShortcutsOptions) {
  useEffect(() => {
    if (!isActive) return;

    const handleKeyDown = (event: KeyboardEvent) => {
      // Don't trigger if user is typing in an input
      if (
        event.target instanceof HTMLInputElement || 
        event.target instanceof HTMLTextAreaElement || 
        (event.target instanceof HTMLElement && event.target.isContentEditable)
      ) {
        return;
      }

      if (event.key === 'Delete' || event.key === 'Backspace') {
        // Backspace is sometimes navigate back, but often delete in file managers.
        // Let's stick to Delete for safety, or check platform.
        // Plan says: "Delete key triggers delete confirmation".
        if (event.key === 'Delete' && onDelete) {
          event.preventDefault();
          onDelete();
        }
      } else if (event.key === 'F2') {
        if (onRename) {
          event.preventDefault();
          onRename();
        }
      } else if ((event.ctrlKey || event.metaKey) && event.key === 'a') {
        if (onSelectAll) {
          event.preventDefault();
          onSelectAll();
        }
      } else if (event.key === 'Escape') {
        if (onClearSelection) {
          event.preventDefault();
          onClearSelection();
        }
      } else if ((event.ctrlKey || event.metaKey) && event.key === 'c') {
         if (onCopy) {
             event.preventDefault();
             onCopy();
         }
      } else if ((event.ctrlKey || event.metaKey) && event.key === 'v') {
         if (onPaste) {
             event.preventDefault();
             onPaste();
         }
      }
    };

    window.addEventListener('keydown', handleKeyDown);
    return () => window.removeEventListener('keydown', handleKeyDown);
  }, [isActive, onDelete, onRename, onSelectAll, onClearSelection, onCopy, onPaste]);
}

